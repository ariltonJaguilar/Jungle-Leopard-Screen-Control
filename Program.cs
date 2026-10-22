using System.ComponentModel;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using Window = System.Windows.Window;
using MessageBox = JungleLeopardPoc.BackendMessageBox;
using WindowInteropHelper = System.Windows.Interop.WindowInteropHelper;

namespace JungleLeopardPoc;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2
            && args[0].Equals("--setup-cpu-sensor", StringComparison.Ordinal)
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int sensorSetupParentProcessId))
        {
            CpuSensorSetupResult setup = CpuSensorSupport.InstallAndConfigure(sensorSetupParentProcessId);
            if (!setup.Succeeded)
            {
                MessageBox.Show(
                    $"Não foi possível configurar o acesso aos sensores de hardware.\n\n{setup.Error}",
                    "Sensores de hardware não configurados",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            string taskStatus = setup.MatchingStartupTasks == 0
                ? "Não encontrei uma tarefa de logon que execute este arquivo. Se você o inicia pelo Agendador, abra a tarefa e marque ‘Executar com privilégios mais altos’ na guia Geral."
                : setup.ElevatedStartupTasks < setup.MatchingStartupTasks
                    ? "Algumas tarefas correspondentes não puderam ser atualizadas. Confira na guia Geral da tarefa se ‘Executar com privilégios mais altos’ está marcado."
                    : "A tarefa de logon correspondente foi configurada para executar com privilégios mais altos.";
            string rebootStatus = setup.RebootRequired
                ? "\n\nO Windows informou que é necessário reiniciar para ativar o driver."
                : "";
            MessageBox.Show(
                $"Suporte a sensores de hardware configurado. O PawnIO está instalado e o aplicativo usa o LibreHardwareMonitor integrado; o Jungle Leopard Display não é necessário.\n\n{taskStatus}{rebootStatus}",
                "Suporte a sensores de hardware",
                MessageBoxButtons.OK,
                setup.MatchingStartupTasks > 0 && setup.ElevatedStartupTasks == setup.MatchingStartupTasks
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        }

        if (args.Length == 3
            && args[0].Equals("--fps-broker", StringComparison.Ordinal)
            && args[1].StartsWith("JungleLeopardFps_", StringComparison.Ordinal)
            && int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentProcessId)
            && parentProcessId > 0)
        {
            ForegroundFpsSensor.RunBroker(args[1], parentProcessId);
            return;
        }

        using var instanceMutex = new Mutex(initiallyOwned: true, name: @"Local\JungleLeopardStudio", createdNew: out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "O Jungle Leopard Screen Control já está aberto nesta sessão do Windows. Use a janela existente para evitar disputa pela porta do painel LCD.",
                "Jungle Leopard Screen Control já está em execução",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var application = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose
        };
        LcdStudioBackend? controller = null;
        application.Startup += (_, _) =>
        {
            controller = new LcdStudioBackend();
            var window = new LcdStudioWindow(controller);
            new WindowInteropHelper(window).EnsureHandle();
            controller.SetStudioWindow(window);
            controller.RestoreWindowRequested += window.RestoreFromTray;
            controller.ExitApplicationRequested += window.CloseFromTray;
            application.MainWindow = window;
            window.Show();
            controller.StartBackend();
        };
        application.Run();
        controller?.ShutdownBackend();
    }
}

internal sealed class LcdStudioBackend
{
    private const int PanelWidth = 960;
    private const int PanelHeight = 480;
    private const int MaxJpegBytes = 80 * 1024;
    private const int MaximumPanelFrameRate = 60;
    private const int StreamLoopIntervalMilliseconds = 1000 / MaximumPanelFrameRate;
    private const double PanelBrightnessRenderRatio = 1.0;
    private const string FirmwareUpdateBaseUrl = "https://download.htssl.com/Jungle_Leopard_Display";
    private const int FirmwareDownloadLimitBytes = 256 * 1024 * 1024;
    private static readonly string[] DeviceUsbKeys =
    [
        @"SYSTEM\CurrentControlSet\Enum\USB\VID_33C3&PID_7788",
        @"SYSTEM\CurrentControlSet\Enum\USB\VID_33C3&PID_7792",
        @"SYSTEM\CurrentControlSet\Enum\USB\VID_33C3&PID_7815"
    ];
    private const string VendorProcessName = "Jungle Leopard Display";
    private static readonly System.Net.Http.HttpClient FirmwareHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static BackendChoiceState CreateChoiceState(params string[] items)
    {
        var state = new BackendChoiceState();
        foreach (string item in items) state.Items.Add(item);
        state.SelectedIndex = items.Length == 0 ? -1 : 0;
        return state;
    }

    private readonly BackendTextState _panelDetection = new("Procurando tela USB…");
    private string? _panelConnectionError;
    private readonly BackendTimer _deviceDetectionTimer = new() { Interval = 1500 };
    private readonly BackendTimer _gameDetectionTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource _appShutdown = new();
    private readonly object _themeRendererGate = new();
    private readonly ConcurrentDictionary<string, PanelIdentity> _panelIdentitiesByPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly BackendPreviewState _preview = new();
    private readonly ScreenPlaceholderPreview _previewPlaceholder = new();
    private readonly BackendChoiceState _rotationSelector = CreateChoiceState("0°", "90° horário", "180°", "90° anti-horário");
    private readonly BackendChoiceState _fitSelector = CreateChoiceState("Manter proporção", "Preencher e cortar");
    private readonly BackendNumberState _brightnessSelector = new() { Minimum = 0, Maximum = 100, Value = 100 };
    private readonly BackendTextState _brightnessValue = new();
    private readonly BackendToggleState _autoSteamCover = new();
    private readonly BackendTextState _imageInfo = new("Nenhuma imagem selecionada");
    private readonly BackendTextState _status = new("Pronto. Selecione uma imagem para começar.");
    private readonly BackendTextState _gameStatus = new("Capa Steam automática desativada.");
    private readonly BackendActionState _showButton = new() { Enabled = false };
    private readonly BackendActionState _stopButton = new() { Enabled = false };
    private readonly BackendActionState _closeOriginalButton = new();
    private readonly BackendActionState _restoreOriginalButton = new();
    private readonly BackendActionState _chooseStartupImageButton = new();
    private readonly BackendActionState _uploadStartupImageButton = new() { Enabled = false };
    private readonly BackendActionState _chooseStandbyImageButton = new();
    private readonly BackendActionState _uploadStandbyImageButton = new() { Enabled = false };
    private readonly BackendActionState _checkFirmwareButton = new();
    private readonly BackendActionState _installFirmwareButton = new() { Enabled = false };
    private readonly BackendTextState _startupImageName = new("Nenhuma mídia selecionada");
    private readonly BackendTextState _standbyImageName = new("Nenhuma mídia selecionada");
    private readonly BackendProgressState _animationUploadProgress = new();
    private readonly BackendTextState _animationUploadStatus = new("Nenhum envio de mídia em andamento.");
    private readonly BackendProgressState _firmwareProgress = new();
    private readonly BackendTextState _firmwareStatus = new("Consulte o firmware do painel para buscar atualizações.");
    private readonly HtSensorReader _telemetrySensors = new();
    private readonly BackendTimer _telemetryTimer = new() { Interval = 1000 };
    private readonly BackendTimer _themePreviewTimer = new() { Interval = 250 };
    private readonly BackendTextState _sidebarDeviceStatus = new();
    private readonly BackendTextState _monitorCpuValue = new("—");
    private readonly BackendTextState _monitorMemoryValue = new("—");
    private readonly BackendTextState _monitorGpuValue = new("—");
    private readonly BackendTextState _monitorCpuTemperatureValue = new("—");
    private readonly BackendTextState _monitorGpuTemperatureValue = new("—");
    private readonly Dictionary<string, BackendChoiceState> _sensorSelectionControls = new(StringComparer.OrdinalIgnoreCase);
    private string? _sensorSelectionOptionsSignature;
    private static readonly SensorSelectionSpec[] SensorSelectionSpecs =
    [
        new("CPU", "Uso", "cpuLayout_utilization", "CPU", "Load"),
        new("CPU", "Temperatura", "cpuLayout_temperature", "CPU", "Temperature"),
        new("CPU", "Clock", "cpuLayout_currentRefreshRate", "CPU", "Clock"),
        new("CPU", "Potência", "cpuLayout_powerDraw", "CPU", "Power"),
        new("CPU", "Tensão", "cpuLayout_vid", "CPU", "Voltage"),
        new("CPU", "Ventoinha", "cpuLayout_fanSpeed", "CPU", "Fan"),
        new("Gráficos", "Uso", "graphics_utilization", "GPU", "Load"),
        new("Gráficos", "Temperatura", "graphics_temperature", "GPU", "Temperature"),
        new("Gráficos", "Clock", "graphics_currentRefreshRate", "GPU", "Clock"),
        new("Gráficos", "Potência", "graphics_powerDraw", "GPU", "Power"),
        new("Gráficos", "Tensão", "graphics_vid", "GPU", "Voltage"),
        new("Gráficos", "Ventoinha", "graphics_fanSpeed", "GPU", "Fan"),
        new("Memória", "Uso", "memLayout_utilization", "Memory", "Load"),
        new("Memória", "Temperatura", "memLayout_temperature", "Memory", "Temperature"),
        new("Memória", "Clock", "memLayout_currentRefreshRate", "Memory", "Clock"),
        new("Memória", "Potência", "memLayout_powerDraw", "Memory", "Power"),
        new("Memória", "Tensão", "memLayout_vid", "Memory", "Voltage"),
        new("Memória", "Ventoinha", "memLayout_fanSpeed", "Memory", "Fan"),
        new("Disco", "Uso", "diskLayout_utilization", "Disk", "Load"),
        new("Disco", "Temperatura", "diskLayout_temperature", "Disk", "Temperature"),
        new("Disco", "Clock", "diskLayout_currentRefreshRate", "Disk", "Clock"),
        new("Disco", "Potência", "diskLayout_powerDraw", "Disk", "Power"),
        new("Disco", "Tensão", "diskLayout_vid", "Disk", "Voltage"),
        new("Disco", "Ventoinha", "diskLayout_fanSpeed", "Disk", "Fan"),
        new("Ventoinhas", "Placa-mãe", "mainBordLayout_fanSpeed", "Mainboard", "Fan"),
        new("Ventoinhas", "Sistema", "fanLayout_fanSpeed", "Any", "Fan"),
        new("Ventoinhas", "Refrigeração", "coolLayout_fanSpeed", "Any", "Fan")
    ];
    private readonly BackendToggleState _launchAtLogin = new();
    private readonly BackendToggleState _startHidden = new();
    private readonly BackendToggleState _keepPlayWhenScreenOff = new();
    private readonly BackendChoiceState _temperatureUnitSelector = CreateChoiceState("Celsius (°C)", "Fahrenheit (°F)");
    private readonly BackendChoiceState _renderModeSelector = CreateChoiceState("Alta performance", "Equilibrado", "Alta qualidade");
    private readonly BackendToggleState _disableDiskSensors = new();
    private readonly BackendChoiceState _diskSensorIntervalSelector = CreateChoiceState("1 segundo", "2 segundos", "5 segundos", "10 segundos");
    private readonly BackendTextState _ffmpegPathBox = new();
    private readonly BackendTextState _ffmpegStatus = new();
    private readonly BackendListState _multiScreenDevices = new();
    private readonly BackendTextState _multiScreenStatus = new();
    private readonly BackendActionState _composeCurrentVisualButton = new();
    private readonly BackendActionState _stopMultiScreenCompositionButton = new();
    private readonly BackendTextState _multiScreenCompositionStatus = new();
    private readonly BackendChoiceState _desktopDisplaySelector = new();
    private readonly BackendChoiceState _desktopLayoutSelector = CreateChoiceState("Espelhar todos", "Compor na horizontal", "Compor na vertical", "Compor livremente");
    private readonly BackendToggleState _includeDesktopCursor = new();
    private readonly BackendToggleState _customPanelCropEnabled = new();
    private readonly BackendNumberState _panelCropX = new() { Minimum = 0, Maximum = 100, Value = 0 };
    private readonly BackendNumberState _panelCropY = new() { Minimum = 0, Maximum = 100, Value = 0 };
    private readonly BackendNumberState _panelCropWidth = new() { Minimum = 1, Maximum = 100, Value = 100 };
    private readonly BackendNumberState _panelCropHeight = new() { Minimum = 1, Maximum = 100, Value = 100 };
    private readonly BackendNumberState _panelLayoutX = new() { Minimum = -1920, Maximum = 1920, Enabled = false };
    private readonly BackendNumberState _panelLayoutY = new() { Minimum = -960, Maximum = 960, Enabled = false };
    private readonly BackendChoiceState _panelRotationSelector = CreateChoiceState("Padrão", "0°", "90°", "180°", "270°");
    private readonly BackendActionState _startDesktopCaptureButton = new() { Enabled = false };
    private readonly BackendActionState _stopDesktopCaptureButton = new() { Enabled = false };
    private readonly BackendTimer _desktopCaptureTimer = new() { Interval = 100 };
    private string _temperatureUnit = "C";
    private string _renderMode = "highPerformance";
    private NativeTrayIcon? _trayIcon;
    private bool _trayIconVisible;
    private bool _exitRequested;
    private bool _sessionLocked;
    private bool _systemSuspended;
    private bool _systemPlaybackPaused;
    private bool _resumePlaybackAfterSystemState;
    private int _systemResumeGeneration;
    private int _motionBeforeOffScreenOnStop;
    private string _panelInfoDescription = "Aguardando a conexão automática com o painel.";
    private HtThemeRenderer? _activeThemeRenderer;

    private byte[]? _jpegBytes;
    private byte[]? _displayPreviewBytes;
    private Dictionary<string, byte[]> _panelJpegFrames = new(StringComparer.OrdinalIgnoreCase);
    private Image? _sourceImage;
    private string? _selectedImageName;
    private string? _lastImagePath;
    private string? _detectedPort;
    private string[] _detectedPorts = [];
    private List<string> _panelOrder = [];
    private readonly Dictionary<string, PanelCropSettings> _panelCrops = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _panelCompositionSettingsGate = new();
    private bool _loadingPanelCompositionSettings;
    private Rectangle? _desktopCaptureBounds;
    private bool _desktopCaptureActive;
    private bool _multiScreenCompositionActive;
    private int _themePreviewRenderInProgress;
    private int _currentRotationIndex;
    private int _compositionLayoutIndex;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private int _skipLiveEndMarkerOnNextStop;
    private int _abortStreamSessionOnStop;
    private int _panelUsbResetInProgress;
    private string _usbResetStatus = "Clique em Reiniciar porta USB para recuperar o painel.";
    private int _transmissionStartInProgress;
    private bool _steamDetectionBusy;
    private bool _steamDetectionRefreshRequested;
    private bool _autoSteamSessionSuppressed;
    private uint? _loadedSteamAppId;
    private bool? _loadedSteamCoverPortrait;
    private byte[]? _loadedSteamCoverBytes;
    private uint? _foregroundSteamAppId;
    private uint? _steamCoverAttemptAppId;
    private bool? _steamCoverAttemptPortrait;
    private DateTime _nextSteamCoverRetryUtc;
    private DateTime _nextAutoConnectAttemptUtc;
    private DateTime _nextPanelReconnectAttemptUtc;
    private int _panelBrightness = 100;
    private PanelIdentity? _panelIdentity;
    private string? _pendingFirmwareVersion;
    private string? _pendingFirmwareName;
    private string? _startupImagePath;
    private string? _standbyImagePath;
    private string? _ffmpegPath = LoadSavedFfmpegPath();
    private bool _deviceMaintenanceInProgress;
    private Window? _studioWindow;
    private bool _isShuttingDown;
    private Window? NativeDialogOwner => _studioWindow;
    private bool HasStudioWindow => _studioWindow is not null;
    private bool IsOnUiThread => _studioWindow?.Dispatcher.CheckAccess() == true;

    private void PostToUi(Action action)
    {
        if (_studioWindow is { } window && !window.Dispatcher.HasShutdownStarted)
            _ = window.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private void RunOnUiThread(Action action)
    {
        if (_studioWindow is { } window && !window.Dispatcher.HasShutdownStarted)
            window.Dispatcher.Invoke(action);
    }

    internal event Action? RestoreWindowRequested;
    internal event Action? ExitApplicationRequested;

    internal int RotationIndex => Math.Clamp(_rotationSelector.SelectedIndex, 0, 3);
    internal int Brightness => (int)_brightnessSelector.Value;
    internal bool AutomaticSteamCover => _autoSteamCover.Checked;
    internal bool StartHidden => _startHidden.Checked;
    internal bool TransmissionActive => _streamTask is { IsCompleted: false };
    internal bool CanSendImage => _jpegBytes is not null && _detectedPort is not null;
    internal bool HasPreview => Volatile.Read(ref _jpegBytes) is not null;
    internal byte[]? CurrentPreviewBytes => Volatile.Read(ref _displayPreviewBytes);
    internal int PreviewWidth => GetDisplaySize(CurrentRotation).Width;
    internal int PreviewHeight => GetDisplaySize(CurrentRotation).Height;
    internal int NativePanelWidth => GetNativePanelSize().Width;
    internal int NativePanelHeight => GetNativePanelSize().Height;
    internal string PanelDetectionText => Volatile.Read(ref _panelConnectionError) is { Length: > 0 } error
        ? $"Erro de conexão: {error}"
        : _panelDetection.Text;
    internal bool PanelConnectionHasError => Volatile.Read(ref _panelConnectionError) is { Length: > 0 };
    internal string StatusMessage => _status.Text;
    internal string GameStatusMessage => _gameStatus.Text;
    internal string ImageDescription => _imageInfo.Text;
    internal string PanelDescription => _panelInfoDescription;
    internal string FirmwareDescription => _firmwareStatus.Text;
    internal string StartupMediaDescription => _startupImageName.Text;
    internal string StandbyMediaDescription => _standbyImageName.Text;
    internal string? StartupMediaPath => _startupImagePath;
    internal string? StandbyMediaPath => _standbyImagePath;
    internal string MediaUploadStatus => _animationUploadStatus.Text;
    internal int MediaUploadProgress => _animationUploadProgress.Value;
    internal string PanelStatusDescription => _sidebarDeviceStatus.Text;
    internal string CpuSensorText => _monitorCpuValue.Text;
    internal string MemorySensorText => _monitorMemoryValue.Text;
    internal string GpuSensorText => _monitorGpuValue.Text;
    internal string CpuTemperatureText => _monitorCpuTemperatureValue.Text;
    internal string GpuTemperatureText => _monitorGpuTemperatureValue.Text;
    internal string MultiScreenStatusText => _multiScreenStatus.Text;
    internal string MultiScreenCompositionStatusText => _multiScreenCompositionStatus.Text;
    internal string[] DetectedPanelPorts => Volatile.Read(ref _detectedPorts).ToArray();
    internal string[] MultiScreenPanelPorts => _multiScreenDevices.Items
        .Cast<object>().Select(GetPortFromListItem).Where(port => port is not null).Select(port => port!).ToArray();
    internal int CompositionLayoutIndex => Math.Clamp(_desktopLayoutSelector.SelectedIndex, 0, 3);
    internal bool DesktopCaptureActive => _desktopCaptureActive;
    internal bool CanStartDesktopCapture => _startDesktopCaptureButton.Enabled;
    internal int DesktopDisplayIndex => Math.Max(0, _desktopDisplaySelector.SelectedIndex);
    internal bool IncludeDesktopCursor => _includeDesktopCursor.Checked;
    internal string[] DesktopDisplays => EnumerateDisplays()
        .Select(display => $"{display.DeviceName}  ·  {display.Bounds.Width} × {display.Bounds.Height}")
        .ToArray();
    internal IReadOnlyList<(string Group, string Label, string Key, string[] Options, int SelectedIndex)> SensorSourceSelections
        => SensorSelectionSpecs
            .Where(spec => _sensorSelectionControls.ContainsKey(spec.Key))
            .Select(spec =>
            {
                BackendChoiceState selector = _sensorSelectionControls[spec.Key];
                return (AppLanguage.Translate(spec.Group), AppLanguage.Translate(spec.Label), spec.Key,
                    selector.Items.Cast<object>().Select(item => selector.GetItemText(item) ?? string.Empty).ToArray(),
                    Math.Max(0, selector.SelectedIndex));
            })
            .ToArray();
    internal (bool Enabled, int X, int Y, int Width, int Height, int RotationIndex, int LayoutX, int LayoutY) SelectedPanelCrop
        => (_customPanelCropEnabled.Checked, (int)_panelCropX.Value, (int)_panelCropY.Value,
            (int)_panelCropWidth.Value, (int)_panelCropHeight.Value,
            _panelRotationSelector.SelectedIndex <= 0 ? -1 : _panelRotationSelector.SelectedIndex - 1,
            (int)_panelLayoutX.Value, (int)_panelLayoutY.Value);
    internal PanelCanvasItem[] PanelCompositionItems
    {
        get
        {
            string[] ports = Volatile.Read(ref _detectedPorts);
            int layout = Volatile.Read(ref _compositionLayoutIndex);
            int displayLayout = layout == 0 ? 1 : layout;
            return ports.Select((port, index) =>
            {
                (int x, int y) = GetPanelLayoutPosition(port, index, displayLayout, ports);
                Size size = GetPanelCanvasSize(port);
                return new PanelCanvasItem(port, x, y, size.Width, size.Height, (int)GetPanelRotation(port) * 90);
            }).ToArray();
        }
    }
    internal int TemperatureUnitIndex => Math.Max(0, _temperatureUnitSelector.SelectedIndex);
    internal int RenderModeIndex => Math.Max(0, _renderModeSelector.SelectedIndex);
    internal int DiskSensorIntervalIndex => Math.Max(0, _diskSensorIntervalSelector.SelectedIndex);
    internal bool DiskSensorsDisabled => _disableDiskSensors.Checked;
    internal bool KeepPlaybackWhenScreenOff => _keepPlayWhenScreenOff.Checked;
    internal bool LaunchAtLogin => _launchAtLogin.Checked;
    internal bool StartMinimizedToTray => _startHidden.Checked;
    internal string FfmpegPathDescription => _ffmpegStatus.Text;
    internal bool HasMultiplePanels => Volatile.Read(ref _detectedPorts).Length > 1;
    internal bool CanComposeCurrentVisual => _composeCurrentVisualButton.Enabled;
    internal bool IsMultiScreenCompositionActive => _multiScreenCompositionActive;
    internal bool DeviceMaintenanceInProgress => _deviceMaintenanceInProgress;
    internal string UsbResetStatus => Volatile.Read(ref _usbResetStatus);

    internal void SetScreenOptions(int rotation, int brightness, bool automaticSteamCover)
    {
        int safeRotation = Math.Clamp(rotation, 0, 3);
        int safeBrightness = Math.Clamp(brightness, 0, 100);
        bool changed = _rotationSelector.SelectedIndex != safeRotation
            || _brightnessSelector.Value != safeBrightness || _autoSteamCover.Checked != automaticSteamCover;
        _rotationSelector.SelectedIndex = safeRotation;
        _brightnessSelector.Value = safeBrightness;
        _autoSteamCover.Checked = automaticSteamCover;
        _currentRotationIndex = safeRotation;
        _panelBrightness = safeBrightness;
        _brightnessValue.Text = $"{safeBrightness}%";
        if (changed)
        {
            SaveSettings(safeRotation, automaticSteamCover);
            RefreshPreparedImage();
        }
    }

    internal ThemePackage? LoadSteamLayoutPackage(bool portrait)
    {
        string path = GetSteamLayoutPath(portrait);
        return File.Exists(path) ? ThemePackage.Read(path) : null;
    }

    internal void SaveSteamLayoutPackage(byte[] packageBytes, bool portrait)
    {
        ThemePackage package = ThemePackage.Read(packageBytes, portrait ? "Steam - Retrato" : "Steam - Paisagem");
        string path = GetSteamLayoutPath(portrait);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, package.ToBytes());
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        if (_loadedSteamAppId is not null && _loadedSteamCoverPortrait == portrait && _loadedSteamCoverBytes is { Length: > 0 } cover)
            ApplySteamLayout(package, _selectedImageName?.Replace(" (Steam)", "", StringComparison.Ordinal) ?? "Jogo Steam", cover);
    }

    internal void ChooseImageFromStudio() => ChooseImageClicked(this, EventArgs.Empty);
    internal void ShowImageFromStudio() => StartStreaming(automatic: false);
    internal void StopFromStudio() => StopClicked(this, EventArgs.Empty);
    internal void RestartUsbFromStudio() => _ = RestartPanelUsbAsync();
    internal void CloseVendorFromStudio() => CloseOriginalClicked(this, EventArgs.Empty);
    internal void RestoreVendorFromStudio() => RestoreOriginalClicked(this, EventArgs.Empty);
    internal void ChooseStartupMediaFromStudio() => ChoosePanelMediaFile(standby: false);
    internal void UploadStartupMediaFromStudio() => _ = UploadPanelMediaFileAsync(standby: false);
    internal void ChooseStandbyMediaFromStudio() => ChoosePanelMediaFile(standby: true);
    internal void UploadStandbyMediaFromStudio() => _ = UploadPanelMediaFileAsync(standby: true);
    internal void CheckFirmwareFromStudio() => _ = CheckFirmwareUpdatesAsync();
    internal void InstallFirmwareFromStudio() => _ = InstallFirmwareUpdateAsync();
    internal void ApplyThemeFromStudio(ThemePackage package, bool fitToScreen) =>
        ApplyCoverTheme(package, persist: true, startStreaming: true, fitToScreen: fitToScreen);
    internal void SetStudioWindow(Window window)
    {
        _studioWindow = window;
        _trayIcon = new NativeTrayIcon(window, () => RestoreWindowRequested?.Invoke(), () => ExitApplicationRequested?.Invoke());
        _trayIcon.RestoreRequested += () => RestoreWindowRequested?.Invoke();
        _trayIconVisible = _startHidden.Checked && _trayIcon.SetVisible(true);
    }
    internal void ComposeCurrentVisualFromStudio() => ComposeCurrentVisualClicked();
    internal void StopMultiScreenFromStudio() => StopMultiScreenComposition();
    internal void MoveSelectedPanelFromStudio(int direction) => MoveSelectedPanel(direction);
    internal void SelectPanelFromStudio(int index)
    {
        if (index >= 0 && index < _multiScreenDevices.Items.Count)
            _multiScreenDevices.SelectedIndex = index;
    }
    internal void SelectPanelPortFromStudio(string port)
    {
        for (int index = 0; index < _multiScreenDevices.Items.Count; index++)
        {
            if (!string.Equals(GetPortFromListItem(_multiScreenDevices.Items[index]), port, StringComparison.OrdinalIgnoreCase)) continue;
            _multiScreenDevices.SelectedIndex = index;
            return;
        }
    }

    private static CaptureDisplayChoice[] EnumerateDisplays()
        => DesktopDisplayCapture.Enumerate().Select(display => new CaptureDisplayChoice(display.DeviceName, display.Bounds)).ToArray();

    private static void DrawDesktopCursor(Graphics graphics, Rectangle bounds)
        => DesktopDisplayCapture.DrawCursor(graphics, bounds);
    internal void SetGeneralSettingsFromStudio(int temperatureUnit, int renderMode, bool disableDiskSensors,
        int diskSensorInterval, bool keepPlayback, bool launchAtLogin, bool startHidden)
    {
        bool launchAtLoginChanged = _launchAtLogin.Checked != launchAtLogin;
        if (_temperatureUnitSelector.Items.Count > 0) _temperatureUnitSelector.SelectedIndex = Math.Clamp(temperatureUnit, 0, _temperatureUnitSelector.Items.Count - 1);
        if (_renderModeSelector.Items.Count > 0) _renderModeSelector.SelectedIndex = Math.Clamp(renderMode, 0, _renderModeSelector.Items.Count - 1);
        if (_diskSensorIntervalSelector.Items.Count > 0) _diskSensorIntervalSelector.SelectedIndex = Math.Clamp(diskSensorInterval, 0, _diskSensorIntervalSelector.Items.Count - 1);
        _disableDiskSensors.Checked = disableDiskSensors;
        _keepPlayWhenScreenOff.Checked = keepPlayback;
        _launchAtLogin.Checked = launchAtLogin;
        _startHidden.Checked = startHidden;
        _temperatureUnit = temperatureUnit == 1 ? "F" : "C";
        AppTemperatureUnit.UseFahrenheit = _temperatureUnit == "F";
        _renderMode = renderMode switch { 1 => "balanced", 2 => "highQuality", _ => "highPerformance" };
        AppRenderSettings.Mode = _renderMode;
        AppSensorSettings.DisableDiskSensorScan = disableDiskSensors;
        AppSensorSettings.DiskSensorScanIntervalMs = _diskSensorIntervalSelector.SelectedIndex switch { 0 => 1000, 1 => 2000, 2 => 5000, _ => 10000 };
        
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        if (launchAtLoginChanged) UpdateLaunchAtLogin(launchAtLogin);
        UpdateTrayVisibility();
    }
    internal void SetCompositionLayoutFromStudio(int index)
    {
        if (_desktopLayoutSelector.Items.Count > 0)
            _desktopLayoutSelector.SelectedIndex = Math.Clamp(index, 0, _desktopLayoutSelector.Items.Count - 1);
        Volatile.Write(ref _compositionLayoutIndex, _desktopLayoutSelector.SelectedIndex);
        LoadSelectedPanelCrop();
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
    }
    internal void SetDesktopDisplayFromStudio(int index)
    {
        if (_desktopDisplaySelector.Items.Count > 0)
            _desktopDisplaySelector.SelectedIndex = Math.Clamp(index, 0, _desktopDisplaySelector.Items.Count - 1);
    }
    internal void SetDesktopCursorFromStudio(bool include) => _includeDesktopCursor.Checked = include;
    internal void SetSelectedPanelCropFromStudio(bool enabled, int x, int y, int width, int height, int rotationIndex, int layoutX, int layoutY)
    {
        if (_multiScreenDevices.SelectedIndex < 0) return;
        _loadingPanelCompositionSettings = true;
        try
        {
            _customPanelCropEnabled.Checked = enabled;
            _panelCropX.Value = Math.Clamp(x, (int)_panelCropX.Minimum, (int)_panelCropX.Maximum);
            _panelCropY.Value = Math.Clamp(y, (int)_panelCropY.Minimum, (int)_panelCropY.Maximum);
            _panelCropWidth.Value = Math.Clamp(width, (int)_panelCropWidth.Minimum, (int)_panelCropWidth.Maximum);
            _panelCropHeight.Value = Math.Clamp(height, (int)_panelCropHeight.Minimum, (int)_panelCropHeight.Maximum);
            _panelRotationSelector.SelectedIndex = rotationIndex < 0 ? 0 : Math.Clamp(rotationIndex + 1, 1, 4);
            _panelLayoutX.Value = Math.Clamp(layoutX, (int)_panelLayoutX.Minimum, (int)_panelLayoutX.Maximum);
            _panelLayoutY.Value = Math.Clamp(layoutY, (int)_panelLayoutY.Minimum, (int)_panelLayoutY.Maximum);
        }
        finally { _loadingPanelCompositionSettings = false; }
        PanelCropSettingsChanged(this, EventArgs.Empty);
    }
    internal void SetPanelPositionFromStudio(string port, int x, int y) => SetPanelCompositionPosition(port, x, y);
    internal void StartDesktopCaptureFromStudio() => StartDesktopCapture();
    internal void StopDesktopCaptureFromStudio() => StopDesktopCapture(restoreSavedVisual: true);
    internal void ResetSettingsFromStudio() => ResetSettingsClicked(this, EventArgs.Empty);
    internal void ChooseFfmpegFromStudio() => ChooseFfmpegClicked(this, EventArgs.Empty);

    internal void StartBackend()
    {
        UpdateTrayVisibility();
        if (_startHidden.Checked && _trayIconVisible) _studioWindow?.Hide();
        SubscribeSystemPlaybackEvents();
        RefreshDesktopDisplays();
        RestoreSavedVisual();
        EnsureStartupVisual();
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        _launchAtLogin.Checked = IsRegisteredForStartup();
        RefreshPanelDetection();
        _deviceDetectionTimer.Start();
        _gameDetectionTimer.Start();
        _telemetryTimer.Start();
    }

    internal void ShutdownBackend()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _exitRequested = true;
        UnsubscribeSystemPlaybackEvents();
        _deviceDetectionTimer.Stop();
        _gameDetectionTimer.Stop();
        _telemetryTimer.Stop();
        _desktopCaptureTimer.Stop();
        _themePreviewTimer.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayIconVisible = false;
        _appShutdown.Cancel();
        StopStreamingForExit();
        StopDesktopCapture(restoreSavedVisual: false);
        _sourceImage?.Dispose();
        _sourceImage = null;
        _preview.Image?.Dispose();
        _preview.Image = null;
        _activeThemeRenderer?.Dispose();
        _activeThemeRenderer = null;
        _appShutdown.Dispose();
    }

    internal void OnWindowClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _startHidden.Checked && _trayIconVisible)
        {
            e.Cancel = true;
            _studioWindow?.Hide();
            return;
        }
        ShutdownBackend();
    }

    internal void SetStartHiddenFromStudio(bool enabled)
    {
        _startHidden.Checked = enabled;
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        UpdateTrayVisibility();
    }

    internal void SetPlaybackWhenScreenOffFromStudio(bool enabled)
    {
        _keepPlayWhenScreenOff.Checked = enabled;
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
    }

    internal void SetLaunchAtLoginFromStudio(bool enabled)
    {
        _launchAtLogin.Checked = enabled;
        UpdateLaunchAtLogin(enabled);
    }

    private void UpdateLaunchAtLogin(bool enabled)
    {
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey is null) throw new UnauthorizedAccessException("Não foi possível abrir as configurações de inicialização do usuário.");
            if (enabled)
            {
                string executable = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new IOException("Não foi possível localizar o executável do Jungle Leopard Screen Control.");
                runKey.SetValue("JungleLeopardStudio", $"\"{executable}\"");
            }
            else runKey.DeleteValue("JungleLeopardStudio", throwOnMissingValue: false);
            SetStatus(enabled ? "O aplicativo será iniciado junto com o Windows." : "A inicialização automática foi desativada.");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(NativeDialogOwner, $"Não consegui salvar essa preferência: {ex.Message}", "Configuração não aplicada", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal void ExitFromTray()
    {
        _exitRequested = true;
        _studioWindow?.Close();
    }

    private void UpdateTrayVisibility()
    {
        _trayIconVisible = _startHidden.Checked && (_trayIcon?.SetVisible(true) ?? false);
        if (!_startHidden.Checked)
        {
            _trayIcon?.SetVisible(false);
            if (_studioWindow is { IsVisible: false }) RestoreWindowRequested?.Invoke();
        }
    }

    internal void SetSensorSourceFromStudio(string key, int index)
    {
        if (!_sensorSelectionControls.TryGetValue(key, out BackendChoiceState? selector)
            || index < 0 || index >= selector.Items.Count) return;
        selector.SelectedIndex = index;
        if (selector.SelectedItem is not SensorSelectionChoice choice) return;
        if (choice.Name is null) AppSensorSettings.SensorSelections.TryRemove(key, out _);
        else AppSensorSettings.SensorSelections[key] = choice.Name;
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
    }

    internal bool StartCpuSensorSetupFromStudio(out string? error)
        => CpuSensorSupport.StartSetupProcess(out error);

    public LcdStudioBackend()
    {
        _temperatureUnit = LoadSavedTemperatureUnit();
        HtVideoTranscoder.ConfiguredPath = _ffmpegPath;
        _lastImagePath = LoadSavedImagePath();
        int savedRotation = LoadSavedRotationIndex();
        int savedFitMode = LoadSavedFitModeIndex();
        int savedBrightness = LoadSavedBrightness();
        _rotationSelector.SelectedIndex = savedRotation;
        _fitSelector.SelectedIndex = savedFitMode;
        _brightnessSelector.Value = savedBrightness;
        _brightnessValue.Text = $"{savedBrightness}%";
        _autoSteamCover.Checked = LoadSavedAutoSteamCover();
        _currentRotationIndex = savedRotation;
        _panelBrightness = savedBrightness;
        _startHidden.Checked = LoadSavedStartHidden();
        _keepPlayWhenScreenOff.Checked = LoadSavedKeepPlayWhenScreenOff();
        _launchAtLogin.Checked = IsRegisteredForStartup();
        
        LoadMultiScreenSettings();
        AppTemperatureUnit.UseFahrenheit = _temperatureUnit.Equals("F", StringComparison.OrdinalIgnoreCase);
        _temperatureUnitSelector.SelectedIndex = _temperatureUnit.Equals("F", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _renderMode = LoadSavedRenderMode();
        _renderModeSelector.SelectedIndex = _renderMode switch { "balanced" => 1, "highQuality" => 2, _ => 0 };
        AppRenderSettings.Mode = _renderMode;
        AppSensorSettings.DisableDiskSensorScan = LoadSavedDisableDiskSensorScan();
        _disableDiskSensors.Checked = AppSensorSettings.DisableDiskSensorScan;
        AppSensorSettings.DiskSensorScanIntervalMs = LoadSavedDiskSensorScanInterval();
        _diskSensorIntervalSelector.SelectedIndex = AppSensorSettings.DiskSensorScanIntervalMs switch { <= 1000 => 0, <= 2000 => 1, <= 5000 => 2, _ => 3 };
        _ffmpegPathBox.Text = _ffmpegPath ?? "Busca automática: pasta do app, variável FFMPEG_PATH ou PATH do Windows";
        _ffmpegStatus.Text = _ffmpegPath is not null
            ? "FFmpeg selecionado: " + _ffmpegPath
            : HtVideoTranscoder.FindExecutable() is null
                ? "Sem FFmpeg: vídeos que o Windows não decodifica podem ficar sem imagem. Selecione ffmpeg.exe para habilitar o fallback."
                : "FFmpeg encontrado por busca automática.";
        AppSensorSettings.SensorSelections.Clear();
        foreach ((string key, string sensorName) in LoadSavedSensorSelections())
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(sensorName))
                AppSensorSettings.SensorSelections[key] = sensorName;
        foreach (SensorSelectionSpec spec in SensorSelectionSpecs)
            _sensorSelectionControls[spec.Key] = new BackendChoiceState();
        _panelRotationSelector.SelectedIndex = 0;
        _desktopLayoutSelector.SelectedIndex = Math.Clamp(_compositionLayoutIndex, 0, 3);
        _deviceDetectionTimer.Tick += (_, _) => RefreshPanelDetection();
        _gameDetectionTimer.Tick += SteamDetectionTimerTick;
        _telemetryTimer.Tick += (_, _) => RefreshTelemetry();
        _themePreviewTimer.Tick += async (_, _) => await RefreshIdleThemePreviewAsync();
        _desktopCaptureTimer.Tick += (_, _) => CaptureDesktopFrameSafely();
    }


    private string? GetSelectedPanelPort() => GetPortFromListItem(_multiScreenDevices.SelectedItem);

    private static string? GetPortFromListItem(object? item)
    {
        string? value = item?.ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        string port = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? port : null;
    }

    private bool NormalizePanelOrder(string[] discoveredPorts)
    {
        bool changed = false;
        foreach (string port in discoveredPorts.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (_panelOrder.Contains(port, StringComparer.OrdinalIgnoreCase)) continue;
            _panelOrder.Add(port);
            changed = true;
        }
        return changed;
    }

    private void MoveSelectedPanel(int delta)
    {
        string? port = GetSelectedPanelPort();
        if (port is null) return;
        int activeIndex = Array.FindIndex(_detectedPorts, value => value.Equals(port, StringComparison.OrdinalIgnoreCase));
        int nextActiveIndex = activeIndex + delta;
        if (activeIndex < 0 || nextActiveIndex < 0 || nextActiveIndex >= _detectedPorts.Length) return;
        int index = _panelOrder.FindIndex(value => value.Equals(_detectedPorts[activeIndex], StringComparison.OrdinalIgnoreCase));
        int next = _panelOrder.FindIndex(value => value.Equals(_detectedPorts[nextActiveIndex], StringComparison.OrdinalIgnoreCase));
        if (index < 0 || next < 0) return;
        (_panelOrder[index], _panelOrder[next]) = (_panelOrder[next], _panelOrder[index]);
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        RefreshPanelDetection();
    }

    private void LoadSelectedPanelCrop()
    {
        string? port = GetSelectedPanelPort();
        PanelCropSettings settings = PanelCropSettings.Default;
        if (port is not null && _panelCrops.TryGetValue(port, out PanelCropSettings? saved) && saved is not null)
            settings = saved;
        (int defaultX, int defaultY) = port is null
            ? (0, 0)
            : GetDefaultPanelLayoutPosition(port, Volatile.Read(ref _compositionLayoutIndex));
        _loadingPanelCompositionSettings = true;
        try
        {
            
            _customPanelCropEnabled.Enabled = port is not null;
            _panelCropX.Enabled = _panelCropY.Enabled = _panelCropWidth.Enabled = _panelCropHeight.Enabled = port is not null && settings.Enabled;
            _panelRotationSelector.Enabled = port is not null;
            _panelRotationSelector.SelectedIndex = settings.RotationIndex < 0 ? 0 : Math.Clamp(settings.RotationIndex + 1, 1, 4);
            _customPanelCropEnabled.Checked = settings.Enabled;
            _panelCropX.Value = Math.Clamp(settings.X, 0, 100);
            _panelCropY.Value = Math.Clamp(settings.Y, 0, 100);
            _panelCropWidth.Value = Math.Clamp(settings.Width, 1, 100);
            _panelCropHeight.Value = Math.Clamp(settings.Height, 1, 100);
            bool freeLayout = port is not null && Volatile.Read(ref _compositionLayoutIndex) == 3;
            _panelLayoutX.Enabled = _panelLayoutY.Enabled = freeLayout;
            _panelLayoutX.Value = Math.Clamp(settings.LayoutX ?? defaultX, (int)_panelLayoutX.Minimum, (int)_panelLayoutX.Maximum);
            _panelLayoutY.Value = Math.Clamp(settings.LayoutY ?? defaultY, (int)_panelLayoutY.Minimum, (int)_panelLayoutY.Maximum);
        }
        finally { _loadingPanelCompositionSettings = false; }
    }

    private void PanelCropSettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingPanelCompositionSettings) return;
        string? port = GetSelectedPanelPort();
        if (port is null) return;
        bool enabled = _customPanelCropEnabled.Checked;
        var settings = new PanelCropSettings(enabled, (int)_panelCropX.Value, (int)_panelCropY.Value,
            Math.Max(1, (int)_panelCropWidth.Value), Math.Max(1, (int)_panelCropHeight.Value),
            _panelRotationSelector.SelectedIndex <= 0 ? -1 : _panelRotationSelector.SelectedIndex - 1,
            _compositionLayoutIndex == 3 ? (int)_panelLayoutX.Value : GetPanelCropSettings(port).LayoutX,
            _compositionLayoutIndex == 3 ? (int)_panelLayoutY.Value : GetPanelCropSettings(port).LayoutY);
        lock (_panelCompositionSettingsGate)
            _panelCrops[port] = settings;
        _panelCropX.Enabled = _panelCropY.Enabled = _panelCropWidth.Enabled = _panelCropHeight.Enabled = enabled;
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        if (_multiScreenCompositionActive)
            RefreshMultiScreenComposition();
        else if (_desktopCaptureActive)
            CaptureDesktopFrameSafely();
    }

    private void RefreshDesktopDisplays()
    {
        string? previousDeviceName = (_desktopDisplaySelector.SelectedItem as CaptureDisplayChoice)?.DeviceName;
        _desktopDisplaySelector.Items.Clear();
        foreach (CaptureDisplayChoice screen in EnumerateDisplays())
            _desktopDisplaySelector.Items.Add(screen);

        int previousIndex = -1;
        for (int i = 0; i < _desktopDisplaySelector.Items.Count; i++)
        {
            if (_desktopDisplaySelector.Items[i] is CaptureDisplayChoice choice
                && string.Equals(choice.DeviceName, previousDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                previousIndex = i;
                break;
            }
        }
        _desktopDisplaySelector.SelectedIndex = previousIndex >= 0 ? previousIndex : (_desktopDisplaySelector.Items.Count > 0 ? 0 : -1);
        _startDesktopCaptureButton.Enabled = !_desktopCaptureActive
            && _detectedPorts.Length > 0
            && _desktopDisplaySelector.SelectedItem is CaptureDisplayChoice;
    }

    private async void StartDesktopCapture()
    {
        if (_desktopCaptureActive)
            return;
        if (IsVendorRunning() && !await ReleaseVendorForStudioAsync())
        {
            SetStatus("Não foi possível assumir a tela. Feche o Jungle Leopard Display e tente novamente.");
            return;
        }
        if (_detectedPorts.Length == 0)
        {
            SetStatus("Conecte um painel Pro Flow antes de iniciar a captura do desktop.");
            return;
        }
        if (_desktopDisplaySelector.SelectedItem is not CaptureDisplayChoice display)
        {
            SetStatus("O Windows não informou um monitor disponível para capturar.");
            return;
        }

        _desktopCaptureBounds = display.Bounds;
        _desktopCaptureActive = true;
        _sourceImage?.Dispose();
        _sourceImage = null;
        ClearActiveTheme();
        _loadedSteamAppId = null;
        _loadedSteamCoverPortrait = null;
        _selectedImageName = $"Captura do desktop — {display.DeviceName}";
        try
        {
            CaptureDesktopFrame();
            StartStreaming(automatic: false);
            _desktopCaptureTimer.Start();
            _startDesktopCaptureButton.Enabled = false;
            _stopDesktopCaptureButton.Enabled = true;
            SetStatus($"Capturando {display.DeviceName} e transmitindo para {_detectedPorts.Length} painel(is) Pro Flow.");
        }
        catch (Exception ex)
        {
            StopDesktopCapture(restoreSavedVisual: false);
            SetStatus($"Falha ao capturar o monitor: {ex.Message}");
        }
    }

    private void CaptureDesktopFrameSafely()
    {
        try
        {
            CaptureDesktopFrame();
        }
        catch (Exception ex)
        {
            StopDesktopCapture(restoreSavedVisual: false);
            SetStatus($"Captura encerrada: {ex.Message}");
        }
    }

    private void CaptureDesktopFrame()
    {
        if (!_desktopCaptureActive || _desktopCaptureBounds is not Rectangle bounds || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using var frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(frame))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            if (_includeDesktopCursor.Checked) DrawDesktopCursor(graphics, bounds);
        }

        string[] ports = _detectedPorts.ToArray();
        bool hasCustomCrop = ports.Any(port => _panelCrops.TryGetValue(port, out PanelCropSettings? crop) && crop?.Enabled == true);
        bool hasCustomRotation = ports.Any(port => _panelCrops.TryGetValue(port, out PanelCropSettings? settings)
            && settings is not null && settings.RotationIndex >= 0);
        if ((_desktopLayoutSelector.SelectedIndex == 0 || ports.Length < 2) && !hasCustomCrop && !hasCustomRotation && ports.Length < 2)
        {
            PreparedImage prepared = PrepareImage(frame, CurrentRotation, CurrentFitMode, CurrentRenderBrightness, minimumQuality: 3);
            DisplayPreparedImage(prepared);
            Volatile.Write(ref _panelJpegFrames, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        if (_desktopLayoutSelector.SelectedIndex == 0 || ports.Length < 2)
        {
            var mirrorFrames = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            PreparedImage? mirrorPreview = null;
            foreach (string port in ports)
            {
                ImageRotation panelRotation = GetPanelRotation(port);
                PreparedImage prepared;
                if (_panelCrops.TryGetValue(port, out PanelCropSettings? crop) && crop?.Enabled == true)
                {
                    using Bitmap cropped = CropCaptureRegion(frame, crop);
                    prepared = PrepareImage(cropped, panelRotation, ImageFitMode.Stretch, CurrentRenderBrightness, minimumQuality: 3,
                        nativePanelSize: GetNativePanelSize(port), maximumJpegBytes: GetPanelJpegMaxBytes(port));
                }
                else
                {
                    prepared = PrepareImage(frame, panelRotation, CurrentFitMode, CurrentRenderBrightness, minimumQuality: 3,
                        nativePanelSize: GetNativePanelSize(port), maximumJpegBytes: GetPanelJpegMaxBytes(port));
                }
                mirrorFrames[port] = prepared.Bytes;
                mirrorPreview ??= prepared;
            }
            if (mirrorPreview is not null)
            {
                DisplayPreparedImage(mirrorPreview);
                Volatile.Write(ref _panelJpegFrames, mirrorFrames);
            }
            return;
        }

        if (_desktopLayoutSelector.SelectedIndex == 3)
        {
            Size freeCanvasSize = GetCompositionSize(ports, 3);
            using var freeCanvas = new Bitmap(freeCanvasSize.Width, freeCanvasSize.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(freeCanvas))
                DrawDesktopOnCanvas(graphics, frame, freeCanvasSize);
            (int originX, int originY) = GetCompositionOrigin(ports, 3);
            var freeFrames = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            PreparedImage? freePreview = null;
            for (int index = 0; index < ports.Length; index++)
            {
                ImageRotation panelRotation = GetPanelRotation(ports[index]);
                PreparedImage prepared;
                if (_panelCrops.TryGetValue(ports[index], out PanelCropSettings? crop) && crop?.Enabled == true)
                {
                    using Bitmap cropped = CropCaptureRegion(frame, crop);
                    prepared = PrepareImage(cropped, panelRotation, ImageFitMode.Stretch, CurrentRenderBrightness, minimumQuality: 3,
                        nativePanelSize: GetNativePanelSize(ports[index]), maximumJpegBytes: GetPanelJpegMaxBytes(ports[index]));
                }
                else
                {
                    (int panelX, int panelY) = GetPanelLayoutPosition(ports[index], index, 3, ports);
                    Size viewport = GetPanelCanvasSize(ports[index]);
                    using Bitmap panelCanvas = freeCanvas.Clone(new Rectangle(panelX - originX, panelY - originY, viewport.Width, viewport.Height), PixelFormat.Format32bppArgb);
                    prepared = PrepareImage(panelCanvas, panelRotation, ImageFitMode.Stretch, CurrentRenderBrightness, minimumQuality: 3,
                        nativePanelSize: GetNativePanelSize(ports[index]), maximumJpegBytes: GetPanelJpegMaxBytes(ports[index]));
                }
                freeFrames[ports[index]] = prepared.Bytes;
                freePreview ??= prepared;
            }
            if (freePreview is not null)
            {
                DisplayPreparedImage(freePreview);
                Volatile.Write(ref _panelJpegFrames, freeFrames);
            }
            return;
        }

        int layout = _desktopLayoutSelector.SelectedIndex;
        Size compositionSize = GetCompositionSize(ports, layout);
        using var composition = new Bitmap(compositionSize.Width, compositionSize.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(composition))
            DrawDesktopOnCanvas(graphics, frame, compositionSize);
        (int compositionOriginX, int compositionOriginY) = GetCompositionOrigin(ports, layout);

        var panelFrames = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        PreparedImage? preview = null;
        for (int index = 0; index < ports.Length; index++)
        {
            ImageRotation panelRotation = GetPanelRotation(ports[index]);
            PreparedImage prepared;
            if (_panelCrops.TryGetValue(ports[index], out PanelCropSettings? crop) && crop?.Enabled == true)
            {
                using Bitmap cropped = CropCaptureRegion(frame, crop);
                prepared = PrepareImage(cropped, panelRotation, ImageFitMode.Stretch, CurrentRenderBrightness, minimumQuality: 3,
                    nativePanelSize: GetNativePanelSize(ports[index]), maximumJpegBytes: GetPanelJpegMaxBytes(ports[index]));
            }
            else
            {
                (int panelX, int panelY) = GetPanelLayoutPosition(ports[index], index, layout, ports);
                Size viewport = GetPanelCanvasSize(ports[index]);
                int x = layout == 3 ? panelX - compositionOriginX : panelX;
                int y = layout == 3 ? panelY - compositionOriginY : panelY;
                using Bitmap panelCanvas = composition.Clone(new Rectangle(x, y, viewport.Width, viewport.Height), PixelFormat.Format32bppArgb);
                prepared = PrepareImage(panelCanvas, panelRotation, ImageFitMode.Stretch, CurrentRenderBrightness, minimumQuality: 3,
                    nativePanelSize: GetNativePanelSize(ports[index]), maximumJpegBytes: GetPanelJpegMaxBytes(ports[index]));
            }
            panelFrames[ports[index]] = prepared.Bytes;
            preview ??= prepared;
        }

        if (preview is not null)
        {
            DisplayPreparedImage(preview);
            Volatile.Write(ref _panelJpegFrames, panelFrames);
        }
    }

    private PanelCropSettings GetPanelCropSettings(string port)
    {
        lock (_panelCompositionSettingsGate)
            return _panelCrops.TryGetValue(port, out PanelCropSettings? settings) && settings is not null
                ? settings
                : PanelCropSettings.Default;
    }

    private (int X, int Y) GetDefaultPanelLayoutPosition(string port, int layout, string[]? orderedPorts = null)
    {
        string[] ports = orderedPorts ?? Volatile.Read(ref _detectedPorts);
        int index = Array.FindIndex(ports, value => value.Equals(port, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return (0, 0);
        if (layout == 0) return (0, 0);
        int x = 0, y = 0;
        for (int i = 0; i < index; i++)
        {
            Size size = GetPanelCanvasSize(ports[i]);
            if (layout == 2) y += size.Height;
            else x += size.Width;
        }
        return (x, y);
    }

    private (int X, int Y) GetPanelLayoutPosition(string port, int index, int layout, string[]? orderedPorts = null)
    {
        if (layout is 1 or 2)
            return GetDefaultPanelLayoutPosition(port, layout, orderedPorts);
        if (layout != 3)
            return (0, 0);
        PanelCropSettings settings = GetPanelCropSettings(port);
        (int fallbackX, int fallbackY) = GetDefaultPanelLayoutPosition(port, layout, orderedPorts);
        return (settings.LayoutX ?? fallbackX, settings.LayoutY ?? fallbackY);
    }

    private Size GetPanelCanvasSize(string port) => GetDisplaySize(GetPanelRotation(port), GetNativePanelSize(port));

    private void SetPanelCompositionPosition(string port, int x, int y)
    {
        if (_desktopLayoutSelector.SelectedIndex != 3)
            _desktopLayoutSelector.SelectedIndex = 3;
        Volatile.Write(ref _compositionLayoutIndex, 3);
        PanelCropSettings current = GetPanelCropSettings(port);
        lock (_panelCompositionSettingsGate)
            _panelCrops[port] = current with { LayoutX = x, LayoutY = y };
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        if (string.Equals(GetSelectedPanelPort(), port, StringComparison.OrdinalIgnoreCase))
            LoadSelectedPanelCrop();
        if (_multiScreenCompositionActive)
            RefreshMultiScreenComposition();
        else if (_desktopCaptureActive)
            CaptureDesktopFrameSafely();
    }



    private ImageRotation GetPanelRotation(string port)
    {
        PanelCropSettings settings = GetPanelCropSettings(port);
        return settings.RotationIndex >= 0
            ? (ImageRotation)Math.Clamp(settings.RotationIndex, 0, 3)
            : CurrentRotation;
    }

    private void DrawDesktopOnCanvas(Graphics graphics, Image source, Size canvasSize)
    {
        graphics.Clear(Color.Black);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (CurrentFitMode == ImageFitMode.Stretch)
        {
            graphics.DrawImage(source, new Rectangle(Point.Empty, canvasSize));
            return;
        }

        double scaleX = (double)canvasSize.Width / source.Width;
        double scaleY = (double)canvasSize.Height / source.Height;
        double scale = CurrentFitMode == ImageFitMode.Cover ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var destination = new Rectangle((canvasSize.Width - width) / 2, (canvasSize.Height - height) / 2, width, height);
        if (CurrentFitMode == ImageFitMode.Contain)
            DrawSolidEdgeExtensions(graphics, source, destination, canvasSize);
        else
            graphics.SetClip(new Rectangle(Point.Empty, canvasSize));
        graphics.DrawImage(source, destination);
        graphics.ResetClip();
    }

    private static Bitmap CropCaptureRegion(Image source, PanelCropSettings crop)
    {
        int left = Math.Clamp((int)Math.Round(source.Width * crop.X / 100d), 0, Math.Max(0, source.Width - 1));
        int top = Math.Clamp((int)Math.Round(source.Height * crop.Y / 100d), 0, Math.Max(0, source.Height - 1));
        int width = Math.Clamp((int)Math.Round(source.Width * crop.Width / 100d), 1, source.Width - left);
        int height = Math.Clamp((int)Math.Round(source.Height * crop.Height / 100d), 1, source.Height - top);
        Rectangle sourceRectangle = new(left, top, width, height);
        var cropped = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(cropped))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(Point.Empty, cropped.Size), sourceRectangle, GraphicsUnit.Pixel);
        }
        return cropped;
    }

    private void ComposeCurrentVisualClicked()
    {
        if (_desktopCaptureActive)
        {
            _multiScreenCompositionStatus.Text = "Pare a captura do Windows antes de compor o conteúdo selecionado.";
            return;
        }
        if (_detectedPorts.Length < 2)
        {
            _multiScreenCompositionStatus.Text = "Conecte pelo menos dois painéis Pro Flow para compor uma imagem entre telas.";
            return;
        }
        if (_sourceImage is null && !HasActiveTheme)
        {
            _multiScreenCompositionStatus.Text = "Aplique um tema pelo Studio de temas primeiro.";
            return;
        }
        if (!RefreshMultiScreenComposition())
            return;
        StartStreaming(automatic: false);
    }

    private void StopMultiScreenComposition()
    {
        Volatile.Write(ref _multiScreenCompositionActive, false);
        Volatile.Write(ref _panelJpegFrames, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
        _stopMultiScreenCompositionButton.Enabled = false;
        RefreshPreparedImage();
        _multiScreenCompositionStatus.Text = "Composição parada; o conteúdo voltou ao enquadramento normal da tela.";
    }

    private bool RefreshMultiScreenComposition()
    {
        string[] ports = _detectedPorts.ToArray();
        if (ports.Length < 2 || (_sourceImage is null && !HasActiveTheme))
            return false;

        PanelCompositionOutput? output;
        try
        {
            output = HasActiveTheme
                ? RenderActiveThemeComposition(ports)
                : ComposeImageAcrossPanels(_sourceImage!, ports);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            _multiScreenCompositionStatus.Text = $"Não foi possível compor os painéis: {ex.Message}";
            return false;
        }
        if (output is null) return false;

        DisplayPreparedImage(output.Preview, preservePanelFrames: true);
        Volatile.Write(ref _panelJpegFrames, output.Frames);
        Volatile.Write(ref _multiScreenCompositionActive, true);
        _stopMultiScreenCompositionButton.Enabled = true;
        _multiScreenCompositionStatus.Text = $"Composição ativa: {ports.Length} painéis, arranjo {GetCompositionLayoutName(Volatile.Read(ref _compositionLayoutIndex))}.";
        return true;
    }

    private PanelCompositionOutput? ComposeImageAcrossPanels(Image source, string[] ports)
    {
        int layout = Volatile.Read(ref _compositionLayoutIndex);
        if (layout == 0)
        {
            var mirroredFrames = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            PreparedImage? mirroredPreview = null;
            foreach (string port in ports)
            {
                ImageRotation rotation = GetPanelRotation(port);
                Size nativeSize = GetNativePanelSize(port);
                PreparedImage frame;
                if (_panelCrops.TryGetValue(port, out PanelCropSettings? crop) && crop?.Enabled == true)
                {
                    using Bitmap cropped = CropCaptureRegion(source, crop);
                    frame = PrepareImage(cropped, rotation, ImageFitMode.Stretch, CurrentRenderBrightness,
                        minimumQuality: 3, nativePanelSize: nativeSize, maximumJpegBytes: GetPanelJpegMaxBytes(port));
                }
                else
                {
                    frame = PrepareImage(source, rotation, CurrentFitMode, CurrentRenderBrightness,
                        minimumQuality: 3, nativePanelSize: nativeSize, maximumJpegBytes: GetPanelJpegMaxBytes(port));
                }
                mirroredFrames[port] = frame.Bytes;
                mirroredPreview ??= frame;
            }
            return mirroredPreview is null ? null : new PanelCompositionOutput(mirroredPreview, mirroredFrames);
        }

        Size outputSize = GetCompositionSize(ports, layout);
        using var composite = new Bitmap(outputSize.Width, outputSize.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(composite))
            DrawDesktopOnCanvas(graphics, source, outputSize);
        return BuildPanelCompositionOutput(composite, ports, layout);
    }

    private PanelCompositionOutput? RenderActiveThemeComposition(string[]? requestedPorts = null)
    {
        string[] ports = requestedPorts ?? Volatile.Read(ref _detectedPorts).ToArray();
        if (ports.Length < 2) return null;
        int layout = Volatile.Read(ref _compositionLayoutIndex);
        if (layout == 0)
            return RenderMirroredThemeFrames(ports);
        Size outputSize = GetCompositionSize(ports, layout);
        using Bitmap? composite = RenderActiveThemeScene(outputSize);
        return composite is null ? null : BuildPanelCompositionOutput(composite, ports, layout);
    }

    private Bitmap? RenderActiveThemeScene(Size outputSize)
    {
        lock (_themeRendererGate)
            return _activeThemeRenderer?.Render(outputSize);
    }

    private PanelCompositionOutput? BuildPanelCompositionOutput(Bitmap composite, string[] ports, int layout)
    {
        if (ports.Length == 0) return null;
        var frames = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        PreparedImage? preview = null;
        (int originX, int originY) = GetCompositionOrigin(ports, layout);
        for (int index = 0; index < ports.Length; index++)
        {
            PanelCropSettings crop = GetPanelCropSettings(ports[index]);
            Bitmap panelImage;
            if (crop.Enabled)
            {
                panelImage = CropCaptureRegion(composite, crop);
            }
            else
            {
                (int panelX, int panelY) = GetPanelLayoutPosition(ports[index], index, layout, ports);
                int x = layout == 3 ? panelX - originX : panelX;
                int y = layout == 3 ? panelY - originY : panelY;
                Size viewport = GetPanelCanvasSize(ports[index]);
                panelImage = composite.Clone(new Rectangle(x, y, viewport.Width, viewport.Height), PixelFormat.Format32bppArgb);
            }

            using (panelImage)
            {
                PreparedImage prepared = PrepareImage(panelImage, GetPanelRotation(ports[index]), ImageFitMode.Stretch,
                    CurrentRenderBrightness, minimumQuality: 3, nativePanelSize: GetNativePanelSize(ports[index]),
                    maximumJpegBytes: GetPanelJpegMaxBytes(ports[index]));
                frames[ports[index]] = prepared.Bytes;
                preview ??= prepared;
            }
        }
        return preview is null ? null : new PanelCompositionOutput(preview, frames);
    }

    private Size GetCompositionSize(string[] ports, int layout)
    {
        if (layout is 1 or 2 or 3 && ports.Length > 1)
        {
            (int minX, int minY, int maxX, int maxY) = GetCompositionBounds(ports, layout);
            int width = Math.Max(1, maxX - minX);
            int height = Math.Max(1, maxY - minY);
            if (width > 8192 || height > 4096 || (long)width * height > 33_554_432)
                throw new InvalidOperationException("O canvas personalizado ultrapassa 8192 × 4096 pixels.");
            return new Size(width, height);
        }
        if (ports.Length < 2 || layout == 0)
            return ports.Length == 1 ? GetPanelCanvasSize(ports[0]) : GetDisplaySize(CurrentRotation);
        return new Size(PanelWidth, PanelHeight);
    }

    private (int X, int Y) GetCompositionOrigin(string[] ports, int layout)
    {
        if (layout != 3 || ports.Length < 2) return (0, 0);
        (int minX, int minY, _, _) = GetCompositionBounds(ports, layout);
        return (minX, minY);
    }

    private (int MinX, int MinY, int MaxX, int MaxY) GetCompositionBounds(string[] ports, int layout)
    {
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        for (int index = 0; index < ports.Length; index++)
        {
            (int x, int y) = GetPanelLayoutPosition(ports[index], index, layout, ports);
            Size size = GetPanelCanvasSize(ports[index]);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + size.Width);
            maxY = Math.Max(maxY, y + size.Height);
        }
        return (minX, minY, maxX, maxY);
    }

    private static string GetCompositionLayoutName(int layout) => layout switch
    {
        1 => AppLanguage.Translate("horizontal"),
        2 => AppLanguage.Translate("vertical"),
        _ => AppLanguage.Translate("espelhado")
    };

    private void StopDesktopCapture(bool restoreSavedVisual)
    {
        if (!_desktopCaptureActive)
            return;
        _desktopCaptureTimer.Stop();
        _desktopCaptureActive = false;
        _desktopCaptureBounds = null;
        _stopDesktopCaptureButton.Enabled = false;
        _startDesktopCaptureButton.Enabled = _detectedPorts.Length > 0 && _desktopDisplaySelector.SelectedItem is CaptureDisplayChoice;
        if (restoreSavedVisual)
            RestoreSavedVisual();
    }


    private void ResetSettingsClicked(object? sender, EventArgs e)
    {
        DialogResult answer = MessageBox.Show(
            _studioWindow,
            "Restaurar orientação, enquadramento, brilho e preferências locais para os valores padrão? As mídias de inicialização/espera já gravadas no painel serão preservadas.",
            "Restaurar configurações",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _rotationSelector.SelectedIndex = 0;
        _fitSelector.SelectedIndex = 0;
        _brightnessSelector.Value = 100;
        _autoSteamCover.Checked = false;
        AppSensorSettings.SensorSelections.Clear();
        _sensorSelectionOptionsSignature = null;
        _keepPlayWhenScreenOff.Checked = false;
        _temperatureUnitSelector.SelectedIndex = 0;
        _renderModeSelector.SelectedIndex = 0;
        _disableDiskSensors.Checked = false;
        _diskSensorIntervalSelector.SelectedIndex = 1;
        _ffmpegPath = null;
        HtVideoTranscoder.ConfiguredPath = null;
        _ffmpegPathBox.Text = "Busca automática: pasta do app, variável FFMPEG_PATH ou PATH do Windows";
        _ffmpegStatus.Text = HtVideoTranscoder.FindExecutable() is null
            ? "Sem FFmpeg: vídeos que o Windows não decodifica podem ficar sem imagem. Selecione ffmpeg.exe para habilitar o fallback."
            : "FFmpeg encontrado por busca automática.";
        _startHidden.Checked = false;
        SetLaunchAtLoginFromStudio(false);
        
        SaveSettings(0, false);
        RefreshPreparedImage();
        SetStatus("Preferências locais restauradas para os valores padrão.");
    }

    private async Task CheckFirmwareUpdatesAsync()
    {
        if (_deviceMaintenanceInProgress) return;
        _checkFirmwareButton.Enabled = false;
        _installFirmwareButton.Enabled = false;
        _pendingFirmwareVersion = null;
        _pendingFirmwareName = null;
        _firmwareProgress.Value = 0;
        _firmwareStatus.Text = "Consultando o manifesto de firmware do fabricante…";
        try
        {
            PanelIdentity identity = await GetPanelIdentityForFirmwareAsync();
            SetPanelIdentity(identity);

            using JsonDocument modelVersions = await DownloadFirmwareManifestAsync("modelVersion.json");
            using JsonDocument availableUpdates = await DownloadFirmwareManifestAsync("availableUpdates.json");
            string? latestVersion = ReadManifestValue(availableUpdates.RootElement, $"{identity.Model}-v{identity.Version}")
                ?? ReadManifestValue(modelVersions.RootElement, identity.Model);
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                _firmwareStatus.Text = $"O fabricante não publicou uma versão de firmware para {identity.Model}.";
                return;
            }

            if (CompareFirmwareVersions(latestVersion, identity.Version) <= 0)
            {
                _firmwareStatus.Text = $"Firmware atualizado: versão {identity.Version} (última publicada: {latestVersion}).";
                return;
            }

            _pendingFirmwareVersion = latestVersion;
            _pendingFirmwareName = BuildFirmwareFileName(identity.Model, latestVersion);
            _installFirmwareButton.Enabled = true;
            _firmwareStatus.Text = $"Firmware {latestVersion} disponível para {identity.Model}; o painel está na versão {identity.Version}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or HttpRequestException or TaskCanceledException or InvalidOperationException
            or SecurityException or ArgumentException)
        {
            _firmwareStatus.Text = $"Não foi possível verificar o firmware: {ex.Message}";
        }
        finally
        {
            _checkFirmwareButton.Enabled = true;
        }
    }

    private async Task InstallFirmwareUpdateAsync()
    {
        if (_deviceMaintenanceInProgress
            || string.IsNullOrWhiteSpace(_pendingFirmwareVersion)
            || string.IsNullOrWhiteSpace(_pendingFirmwareName))
            return;

        PanelIdentity? expectedIdentity = _panelIdentity;
        if (expectedIdentity is null) return;
        DialogResult answer = MessageBox.Show(
            _studioWindow,
            $"Instalar o firmware {_pendingFirmwareVersion} em {expectedIdentity.Model}?\n\n" +
            "A atualização interrompe a transmissão. Não desligue o computador nem desconecte o USB até a confirmação de conclusão.",
            "Atualização de firmware",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        if (IsVendorRunning() && !await ReleaseVendorForStudioAsync())
        {
            _firmwareStatus.Text = "Não foi possível assumir o painel para atualizar o firmware.";
            return;
        }

        bool resumeStreaming = _streamTask is { IsCompleted: false };
        bool streamingPausedForMaintenance = false;
        string? port = _detectedPort ?? DetectPanelPort();
        if (string.IsNullOrWhiteSpace(port))
        {
            _firmwareStatus.Text = "O painel não foi detectado na porta USB.";
            return;
        }

        _deviceMaintenanceInProgress = true;
        _checkFirmwareButton.Enabled = false;
        _installFirmwareButton.Enabled = false;
        _showButton.Enabled = false;
        _stopButton.Enabled = false;
        _closeOriginalButton.Enabled = false;
        _restoreOriginalButton.Enabled = false;
        _firmwareProgress.Value = 0;
        try
        {
            await StopStreamingAsync();
            streamingPausedForMaintenance = true;
            PanelIdentity currentIdentity = await Task.Run(() => ReadPanelIdentity(port));
            if (!string.Equals(currentIdentity.Model, expectedIdentity.Model, StringComparison.Ordinal)
                || !string.Equals(currentIdentity.Version, expectedIdentity.Version, StringComparison.Ordinal))
            {
                SetPanelIdentity(currentIdentity);
                _firmwareStatus.Text = "O painel mudou desde a consulta. Verifique as atualizações novamente antes de instalar.";
                return;
            }

            string firmwarePath = await DownloadFirmwareFileAsync(
                _pendingFirmwareName,
                percent => SetFirmwareProgressFromWorker(percent, "Baixando firmware…"));
            _firmwareStatus.Text = "Gravando firmware no painel. Não interrompa a conexão USB…";
            await Task.Run(() => WriteFirmwareToPanel(port, firmwarePath, expectedIdentity.Model,
                percent => SetFirmwareProgressFromWorker(50 + percent / 2, "Gravando firmware…")));

            _firmwareProgress.Value = 100;
            _firmwareStatus.Text = "Firmware enviado. Aguardando o painel reiniciar e confirmar a versão…";
            PanelIdentity? updatedIdentity = await WaitForFirmwareVersionAsync(port, currentIdentity.Model, _pendingFirmwareVersion);
            if (updatedIdentity is null)
            {
                _firmwareStatus.Text = "O arquivo foi enviado, mas o painel não confirmou a nova versão. Não reenvie o firmware; confira a conexão e o software original.";
                return;
            }

            SetPanelIdentity(updatedIdentity);
            _pendingFirmwareVersion = null;
            _pendingFirmwareName = null;
            _firmwareStatus.Text = $"Firmware atualizado e confirmado: versão {updatedIdentity.Version}.";
            if (resumeStreaming && _jpegBytes is not null)
                StartStreaming(automatic: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or HttpRequestException or TaskCanceledException or InvalidOperationException
            or SecurityException or ArgumentException or Win32Exception)
        {
            _firmwareStatus.Text = $"Falha na atualização de firmware: {ex.Message}";
        }
        finally
        {
            _deviceMaintenanceInProgress = false;
            if (resumeStreaming && streamingPausedForMaintenance && _jpegBytes is not null
                && _streamTask is not { IsCompleted: false })
            {
                try
                {
                    await Task.Delay(1500);
                    RefreshPanelDetection();
                    if (_detectedPort is not null)
                    {
                        StartStreaming(automatic: true);
                        if (!string.IsNullOrWhiteSpace(_pendingFirmwareVersion))
                            _firmwareStatus.Text += " A transmissão anterior foi retomada.";
                    }
                    else
                    {
                        _firmwareStatus.Text += " A transmissão anterior não foi retomada porque o painel não está disponível.";
                    }
                }
                catch (Exception restoreError) when (restoreError is IOException or InvalidOperationException or Win32Exception)
                {
                    _firmwareStatus.Text += $" Não foi possível retomar a transmissão anterior: {restoreError.Message}";
                }
            }
            _checkFirmwareButton.Enabled = true;
            _installFirmwareButton.Enabled = !string.IsNullOrWhiteSpace(_pendingFirmwareVersion);
            _showButton.Enabled = _jpegBytes is not null && _detectedPort is not null;
            _closeOriginalButton.Enabled = true;
            _restoreOriginalButton.Enabled = true;
        }
    }

    private async Task<PanelIdentity> GetPanelIdentityForFirmwareAsync()
    {
        if (_streamTask is { IsCompleted: false } && _panelIdentity is not null)
            return _panelIdentity;
        if (IsVendorRunning() && !await ReleaseVendorForStudioAsync())
            throw new InvalidOperationException("Não foi possível assumir a porta do painel do software original.");
        string? port = _detectedPort ?? DetectPanelPort();
        if (string.IsNullOrWhiteSpace(port))
            throw new InvalidOperationException("O painel Pro Flow 360 não foi detectado no USB.");
        PanelIdentity identity = await Task.Run(() => ReadPanelIdentity(port));
        SetPanelIdentity(identity);
        return identity;
    }

    private static PanelIdentity ReadPanelIdentity(string port)
    {
        using var device = new PanelConnection(port);
        device.Open();
        // The original client clears pending/live image data before requesting
        // device information. Keep the standalone firmware query on the same
        // serial-session sequence used when starting live playback.
        device.FlushImagePipeline();
        device.SendControl(0x06);
        byte[]? response = device.ReadControlResponse(TimeSpan.FromSeconds(2));
        string description = DescribePanelResponse(response, out PanelIdentity? identity);
        ClosePanelSessionIfSupported(device, identity);
        return identity ?? throw new InvalidDataException(description);
    }

    private static void ClosePanelSessionIfSupported(PanelConnection device, PanelIdentity? identity)
    {
        if (Version.TryParse(identity?.Version, out Version? firmwareVersion)
            && firmwareVersion >= new Version(3, 1))
            device.SendControlAndDrain(0x21);
    }

    private static void ReleasePanelSession(PanelConnection device, PanelIdentity? identity)
    {
        try { device.StopLive(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception) { }
        try { ClosePanelSessionIfSupported(device, identity); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception) { }
        device.Dispose();
    }

    private static async Task<JsonDocument> DownloadFirmwareManifestAsync(string relativePath)
    {
        string url = FirmwareUpdateBaseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/');
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        using System.Net.Http.HttpResponseMessage response = await FirmwareHttpClient.SendAsync(
            request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > 2 * 1024 * 1024)
            throw new InvalidDataException("O manifesto de firmware excede 2 MiB.");
        await using Stream content = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(content);
    }

    private static async Task<string> DownloadFirmwareFileAsync(string fileName, Action<int> progress)
    {
        if (Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("O nome do firmware publicado é inválido.");
        string url = FirmwareUpdateBaseUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(fileName);
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        using System.Net.Http.HttpResponseMessage response = await FirmwareHttpClient.SendAsync(
            request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length
            && (length < 16 || length > FirmwareDownloadLimitBytes))
            throw new InvalidDataException("O tamanho informado para o firmware é inválido.");

        string directory = Path.Combine(AppContext.BaseDirectory, "Firmware");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, fileName);
        string temporary = destination + ".download";
        try
        {
            await using Stream input = await response.Content.ReadAsStreamAsync();
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                byte[] buffer = new byte[64 * 1024];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    written += read;
                    if (written > FirmwareDownloadLimitBytes)
                        throw new InvalidDataException("O arquivo de firmware excede o limite de 256 MiB.");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    if (response.Content.Headers.ContentLength is long total && total > 0)
                        progress((int)Math.Clamp(written * 100 / total, 0, 100));
                }
                await output.FlushAsync();
            }

            var fileInfo = new FileInfo(temporary);
            if (fileInfo.Length < 16 || fileInfo.Length > FirmwareDownloadLimitBytes)
                throw new InvalidDataException("O arquivo baixado não parece ser um firmware válido.");
            byte[] header = new byte[16];
            using (var check = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                _ = check.Read(header, 0, header.Length);
            string headerText = System.Text.Encoding.ASCII.GetString(header).TrimStart();
            if (headerText.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || headerText.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O servidor retornou uma página HTML no lugar do firmware.");
            File.Move(temporary, destination, overwrite: true);
            progress(100);
            return destination;
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    private static void WriteFirmwareToPanel(string port, string firmwarePath, string model, Action<int> progress)
    {
        using var device = new PanelConnection(port);
        device.Open();
        long fileLength = new FileInfo(firmwarePath).Length;
        if (fileLength <= 0 || fileLength > uint.MaxValue)
            throw new InvalidDataException("O tamanho do arquivo de firmware não cabe no protocolo do painel.");

        byte[] startPayload =
        [
            0xF2, 0xFF,
            (byte)(fileLength & 0xFF),
            (byte)((fileLength >> 8) & 0xFF),
            (byte)((fileLength >> 16) & 0xFF),
            (byte)((fileLength >> 24) & 0xFF),
            0, 0, 0, 0
        ];
        device.SendControl(0x0C, startPayload);
        if (device.ReadControlResponse(TimeSpan.FromSeconds(5)) is null)
            throw new IOException("O painel não confirmou o início da atualização de firmware.");

        using var file = new FileStream(firmwarePath, FileMode.Open, FileAccess.Read, FileShare.Read, 20 * 1024, useAsync: false);
        byte[] buffer = new byte[20 * 1024];
        long sent = 0;
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            device.SendRawAndDrain(buffer.AsSpan(0, read));
            sent += read;
            progress((int)Math.Clamp(sent * 100 / fileLength, 0, 100));
        }

        if (model.Contains("D215", StringComparison.OrdinalIgnoreCase))
            device.SendRawAndDrain(new byte[4]);
    }

    private async Task<PanelIdentity?> WaitForFirmwareVersionAsync(string originalPort, string model, string expectedVersion)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1500);
            string? port = DetectPanelPort() ?? originalPort;
            try
            {
                PanelIdentity identity = await Task.Run(() => ReadPanelIdentity(port));
                if (string.Equals(identity.Model, model, StringComparison.Ordinal)
                    && CompareFirmwareVersions(identity.Version, expectedVersion) >= 0)
                    return identity;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or Win32Exception) { }
        }
        return null;
    }

    private void SetFirmwareProgressFromWorker(int progress, string status)
    {
        if (_isShuttingDown) return;
        try
        {
            void Update()
            {
                _firmwareProgress.Value = Math.Clamp(progress, _firmwareProgress.Minimum, _firmwareProgress.Maximum);
                _firmwareStatus.Text = status;
            }
            if (!IsOnUiThread) PostToUi((Action)Update);
            else Update();
        }
        catch (InvalidOperationException) { }
    }

    private static string? ReadManifestValue(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out JsonElement value))
            return null;
        string result = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private static int CompareFirmwareVersions(string left, string right)
    {
        if (Version.TryParse(left, out Version? leftVersion) && Version.TryParse(right, out Version? rightVersion))
            return leftVersion.CompareTo(rightVersion);
        if (decimal.TryParse(left, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal leftNumber)
            && decimal.TryParse(right, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal rightNumber))
            return leftNumber.CompareTo(rightNumber);
        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static string BuildFirmwareFileName(string model, string version)
    {
        if (string.IsNullOrWhiteSpace(model)
            || model.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new InvalidDataException("O modelo retornado pelo painel não pode ser usado como nome de firmware.");
        bool qz = model.Contains("F101", StringComparison.OrdinalIgnoreCase)
            || model.Contains("QZ", StringComparison.OrdinalIgnoreCase);
        return $"APP_OTA_{model}-v{version}.{(qz ? "fex" : "bin")}";
    }

    private void ChoosePanelMediaFile(bool standby)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = AppLanguage.Translate(standby ? "Escolha a mídia de espera" : "Escolha a mídia de inicialização"),
            Filter = AppLanguage.Translate("Mídia do painel|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.rgb565|Todos os arquivos|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };
        StudioFileDialogDefaults.Configure(dialog, standby ? _standbyImagePath : _startupImagePath);
        if (dialog.ShowDialog(NativeDialogOwner) != true) return;

        try
        {
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Length is <= 0 or > uint.MaxValue)
                throw new InvalidDataException("O arquivo precisa ter entre 1 byte e 4 GiB.");
            if (standby)
            {
                _standbyImagePath = dialog.FileName;
                _standbyImageName.Text = $"{Path.GetFileName(dialog.FileName)} · {fileInfo.Length:N0} bytes";
                _animationUploadStatus.Text = "Arquivo selecionado; ainda não foi enviado ao painel.";
                _uploadStandbyImageButton.Enabled = true;
            }
            else
            {
                _startupImagePath = dialog.FileName;
                _startupImageName.Text = $"{Path.GetFileName(dialog.FileName)} · {fileInfo.Length:N0} bytes";
                _animationUploadStatus.Text = "Arquivo selecionado; ainda não foi enviado ao painel.";
                _uploadStartupImageButton.Enabled = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException or UnauthorizedAccessException)
        {
            MessageBox.Show(NativeDialogOwner, ex.Message, "Não foi possível abrir o arquivo de mídia", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task UploadPanelMediaFileAsync(bool standby)
    {
        string? mediaPath = standby ? _standbyImagePath : _startupImagePath;
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
        {
            MessageBox.Show(NativeDialogOwner, "Escolha um arquivo de mídia primeiro.", "Mídia necessária", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (IsVendorRunning() && !await ReleaseVendorForStudioAsync())
        {
            MessageBox.Show(NativeDialogOwner, "Não foi possível assumir o painel para gravar a mídia.", "Tela em uso", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? port = _detectedPort ?? DetectPanelPort();
        if (string.IsNullOrWhiteSpace(port))
        {
            MessageBox.Show(NativeDialogOwner, "Não encontrei um painel LCD Jungle Leopard compatível no USB. Confira o cabo.", "Tela não detectada", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long mediaLength;
        try
        {
            mediaLength = new FileInfo(mediaPath).Length;
            if (mediaLength is <= 0 or > uint.MaxValue)
                throw new InvalidDataException("O arquivo precisa ter entre 1 byte e 4 GiB.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(NativeDialogOwner, ex.Message, "Arquivo de mídia inválido", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool resumeStreaming = _streamTask is { IsCompleted: false };
        bool streamingPausedForMaintenance = false;
        string? maintenanceFailure = null;
        _deviceMaintenanceInProgress = true;
        _chooseStartupImageButton.Enabled = false;
        _chooseStandbyImageButton.Enabled = false;
        _uploadStartupImageButton.Enabled = false;
        _uploadStandbyImageButton.Enabled = false;
        _showButton.Enabled = false;
        _closeOriginalButton.Enabled = false;
        _restoreOriginalButton.Enabled = false;
        _animationUploadProgress.Value = 0;
        _animationUploadStatus.Text = "Parando a transmissão…";

        try
        {
            Interlocked.Exchange(ref _skipLiveEndMarkerOnNextStop, 1);
            try { await StopStreamingAsync(); }
            finally { Interlocked.Exchange(ref _skipLiveEndMarkerOnNextStop, 0); }
            streamingPausedForMaintenance = true;
            _animationUploadStatus.Text = "Enviando arquivo original…";
            await Task.Run(() => UploadPanelMediaFile(port, mediaPath, standby, SetAnimationUploadProgressFromWorker));
            _animationUploadProgress.Value = 100;
            _animationUploadStatus.Text = "Envio concluído; reinicialização solicitada. Exibição no painel não verificada.";
            SetStatus("Mídia enviada ao controlador; exibição no painel não verificada.");
        }
        catch (Exception ex)
        {
            maintenanceFailure = ex.Message;
            _animationUploadStatus.Text = "Falha no envio.";
            SetStatus($"Falha ao gravar a mídia no controlador: {ex.Message}");
            MessageBox.Show(NativeDialogOwner, ex.Message, "Falha ao gravar a mídia", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _deviceMaintenanceInProgress = false;
            if (resumeStreaming && streamingPausedForMaintenance && _jpegBytes is not null)
            {
                try
                {
                    await Task.Delay(1500);
                    RefreshPanelDetection();
                    if (_detectedPort is not null)
                    {
                        StartStreaming(automatic: true);
                        SetStatus(maintenanceFailure is null
                            ? "Envio da mídia finalizado; transmissão anterior retomada. Exibição não verificada."
                            : $"A gravação falhou ({maintenanceFailure}), mas a transmissão anterior foi retomada.");
                    }
                    else
                    {
                        SetStatus(maintenanceFailure is null
                            ? "Envio da mídia finalizado; abra a transmissão novamente quando o painel estiver livre. Exibição não verificada."
                            : $"A gravação falhou ({maintenanceFailure}) e a transmissão não pôde ser retomada porque o painel não está disponível.");
                    }
                }
                catch (Exception restoreError)
                {
                    SetStatus(maintenanceFailure is null
                        ? $"Mídia gravada, mas a transmissão anterior não foi retomada: {restoreError.Message}"
                        : $"A gravação falhou ({maintenanceFailure}) e a transmissão anterior também não foi retomada: {restoreError.Message}");
                }
            }
            _chooseStartupImageButton.Enabled = true;
            _chooseStandbyImageButton.Enabled = true;
            _uploadStartupImageButton.Enabled = !string.IsNullOrWhiteSpace(_startupImagePath);
            _uploadStandbyImageButton.Enabled = !string.IsNullOrWhiteSpace(_standbyImagePath);
            _closeOriginalButton.Enabled = true;
            _restoreOriginalButton.Enabled = true;
            _showButton.Enabled = _jpegBytes is not null && _detectedPort is not null;
        }
    }

    private static void UploadPanelMediaFile(string port, string filePath, bool standby, Action<int> progress)
    {
        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        long fileLength = input.Length;
        if (fileLength is <= 0 or > uint.MaxValue)
            throw new InvalidDataException("O arquivo precisa ter entre 1 byte e 4 GiB.");

        using var device = new PanelConnection(port);
        device.Open();
        // The original setStartUpMedia path stops live mode with one JPEG end marker.
        // FlushImagePipeline is for starting a new live stream and appends a four-byte
        // empty frame, which is not part of the startup/standby media protocol.
        device.SendLiveEndMarker();
        var sizePayload = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sizePayload, (uint)fileLength);
        device.SendControl(standby ? (byte)0x13 : (byte)0x12, sizePayload);
        if (device.ReadControlResponse(TimeSpan.FromSeconds(2)) is null)
            throw new IOException("O controlador não confirmou o início da gravação.");

        // Jungle Leopard's writeFile sends 20 KiB at a time and drains after each chunk.
        const int chunkSize = 20 * 1024;
        byte[] buffer = new byte[chunkSize];
        long uploaded = 0;
        while (uploaded < fileLength)
        {
            int requested = (int)Math.Min(buffer.Length, fileLength - uploaded);
            int count = input.Read(buffer, 0, requested);
            if (count == 0)
                throw new EndOfStreamException("O arquivo foi alterado ou truncado durante o envio.");
            device.SendRawAndDrain(buffer.AsSpan(0, count));
            uploaded += count;
            progress((int)Math.Round(uploaded * 100d / fileLength));
        }

        if (input.ReadByte() != -1)
            throw new IOException("O arquivo mudou de tamanho durante o envio; o controlador não foi reiniciado.");

        Thread.Sleep(200);
        device.SendControl(0x01);
        _ = device.ReadControlResponse(TimeSpan.FromSeconds(1));
    }

    private void SetAnimationUploadProgressFromWorker(int value)
    {
        if (_isShuttingDown || !HasStudioWindow) return;
        try { PostToUi((Action)(() => _animationUploadProgress.Value = Math.Clamp(value, 0, 100))); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void RefreshTelemetry()
    {
        if (_isShuttingDown) return;
        SensorSnapshot snapshot = _telemetrySensors.Read();
        string cpu = snapshot.CpuUtilizationReady ? $"{snapshot.CpuUtilization:F0}%" : "\u2014";
        string memory = !snapshot.MemoryTelemetryReady || snapshot.MemoryTotalMb <= 0
            ? "\u2014"
            : $"{snapshot.MemoryUtilization:F0}%";
        string gpu = snapshot.GpuUtilization is double gpuValue ? $"{gpuValue:F0}%" : "—";
        string temperatureUnit = AppTemperatureUnit.UseFahrenheit ? "°F" : "°C";
        double ConvertTemperature(double celsius) => AppTemperatureUnit.UseFahrenheit ? celsius * 9d / 5d + 32d : celsius;
        string cpuTemperature = snapshot.CpuTemperature is double cpuTemp ? $"{ConvertTemperature(cpuTemp):F0} {temperatureUnit}" : "—";
        string gpuTemperature = snapshot.GpuTemperature is double gpuTemp ? $"{ConvertTemperature(gpuTemp):F0} {temperatureUnit}" : "—";
        _monitorCpuValue.Text = cpu;
        _monitorMemoryValue.Text = snapshot.MemoryTotalMb <= 0 ? "—" : $"{snapshot.MemoryUsedMb:F0} / {snapshot.MemoryTotalMb:F0} MB · {memory}";
        _monitorGpuValue.Text = gpu;
        _monitorCpuTemperatureValue.Text = cpuTemperature;
        _monitorGpuTemperatureValue.Text = gpuTemperature;
        RefreshSensorSelectionOptions();
    }

    private void RefreshSensorSelectionOptions()
    {
        IReadOnlyList<HtSensorReader.MonitorSensor> sensors = HtSensorReader.GetAvailableSensors();
        string signature = string.Join("\n", sensors.Select(sensor =>
            $"{sensor.Type}\0{sensor.Name}\0{HtSensorReader.IsUsableSensorValue(sensor.Type, sensor.Value)}"));
        if (string.Equals(signature, _sensorSelectionOptionsSignature, StringComparison.Ordinal)) return;

        _sensorSelectionOptionsSignature = signature;
        try
        {
            foreach (SensorSelectionSpec spec in SensorSelectionSpecs)
            {
                if (!_sensorSelectionControls.TryGetValue(spec.Key, out BackendChoiceState? selector)) continue;
                string? selectedName = AppSensorSettings.GetSensorSelection(spec.Key);
                var choices = sensors
                    .Where(sensor => sensor.Type.Equals(spec.SensorType, StringComparison.OrdinalIgnoreCase)
                        && HtSensorReader.MonitorNameMatches(sensor.Name, spec.Device)
                        && HtSensorReader.IsUsableSensorValue(sensor.Type, sensor.Value))
                    .Select(sensor => sensor.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(name => new SensorSelectionChoice(name, name))
                    .ToList();

                selector.BeginUpdate();
                selector.Items.Clear();
                selector.Items.Add(new SensorSelectionChoice(null, "Automático"));
                foreach (SensorSelectionChoice choice in choices)
                    selector.Items.Add(choice);
                if (selectedName is not null && !choices.Any(choice => choice.Name?.Equals(selectedName, StringComparison.OrdinalIgnoreCase) == true))
                    selector.Items.Add(new SensorSelectionChoice(selectedName, $"{selectedName} (indisponível)"));
                int selectedIndex = 0;
                if (selectedName is not null)
                {
                    for (int index = 1; index < selector.Items.Count; index++)
                        if (selector.Items[index] is SensorSelectionChoice choice
                            && choice.Name?.Equals(selectedName, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            selectedIndex = index;
                            break;
                        }
                }
                selector.SelectedIndex = selectedIndex;
                selector.EndUpdate();
            }
        }
        finally { }
    }


    private void ChooseFfmpegClicked(object? sender, EventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = AppLanguage.Translate("Selecionar FFmpeg para normalizar vídeos de temas"),
            Filter = AppLanguage.Translate("Executável FFmpeg|ffmpeg.exe|Executáveis|*.exe|Todos os arquivos|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };
        StudioFileDialogDefaults.Configure(dialog, _ffmpegPath);
        if (dialog.ShowDialog(NativeDialogOwner) != true) return;

        _ffmpegPath = Path.GetFullPath(dialog.FileName);
        HtVideoTranscoder.ConfiguredPath = _ffmpegPath;
        _ffmpegPathBox.Text = _ffmpegPath;
        _ffmpegStatus.Text = "FFmpeg selecionado: " + _ffmpegPath;
        SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        SetStatus("Caminho do FFmpeg salvo para a normalização de vídeos em temas .ht.");
    }

    private static string LoadSavedTemperatureUnit()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return "C";
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (settings.RootElement.TryGetProperty(nameof(SavedSettings.TemperatureUnit), out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString()?.Equals("F", StringComparison.OrdinalIgnoreCase) == true)
                return "F";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException) { }
        return "C";
    }

    private static string LoadSavedRenderMode()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return "highPerformance";
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (settings.RootElement.TryGetProperty(nameof(SavedSettings.RenderMode), out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() switch
                {
                    "balanced" => "balanced",
                    "highQuality" => "highQuality",
                    _ => "highPerformance"
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException) { }
        return "highPerformance";
    }

    private static bool LoadSavedDisableDiskSensorScan()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return false;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return settings.RootElement.TryGetProperty(nameof(SavedSettings.DisableDiskSensorScan), out JsonElement value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException) { }
        return false;
    }

    private static int LoadSavedDiskSensorScanInterval()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return 2000;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (settings.RootElement.TryGetProperty(nameof(SavedSettings.DiskSensorScanIntervalMs), out JsonElement value)
                && value.TryGetInt32(out int interval))
                return interval switch { <= 1000 => 1000, <= 2000 => 2000, <= 5000 => 5000, _ => 10000 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException) { }
        return 2000;
    }

    private static Dictionary<string, string> LoadSavedSensorSelections()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return new(StringComparer.OrdinalIgnoreCase);
            SavedSettings? settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsPath));
            return settings?.SensorSelections is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(settings.SensorSelections, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool LoadSavedStartHidden()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return false;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return settings.RootElement.TryGetProperty(nameof(SavedSettings.StartHidden), out JsonElement value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException) { }
        return false;
    }

    private static bool LoadSavedKeepPlayWhenScreenOff()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return false;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return settings.RootElement.TryGetProperty(nameof(SavedSettings.KeepPlayWhenScreenOff), out JsonElement value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException) { }
        return false;
    }

    private static string? LoadSavedFfmpegPath()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null) return null;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return settings.RootElement.TryGetProperty(nameof(SavedSettings.FfmpegPath), out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException) { }
        return null;
    }

    private static bool IsRegisteredForStartup()
    {
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            return runKey?.GetValue("JungleLeopardStudio") is string command
                && command.Contains((Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }


    private void RefreshPanelDetection()
    {
        string[] discoveredPorts = DetectPanelPorts();
        bool panelOrderChanged = NormalizePanelOrder(discoveredPorts);
        var discovered = discoveredPorts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string knownPort in _panelIdentitiesByPort.Keys)
            if (!discovered.Contains(knownPort))
                _panelIdentitiesByPort.TryRemove(knownPort, out _);
        string[] ports = _panelOrder.Where(discovered.Contains).ToArray();
        bool changed = !_detectedPorts.SequenceEqual(ports, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _detectedPorts, ports);
        string? detectedPort = ports.FirstOrDefault();
        _detectedPort = detectedPort;
        _panelIdentity = detectedPort is not null && _panelIdentitiesByPort.TryGetValue(detectedPort, out PanelIdentity? detectedIdentity)
            ? detectedIdentity
            : detectedPort is null ? null : _panelIdentity;

        string? selectedPanel = GetSelectedPanelPort();
        _loadingPanelCompositionSettings = true;
        _multiScreenDevices.BeginUpdate();
        _multiScreenDevices.Items.Clear();
        foreach (string port in ports)
            _multiScreenDevices.Items.Add($"{port}  ·  Painel LCD Jungle Leopard");
        if (selectedPanel is not null)
        {
            for (int index = 0; index < _multiScreenDevices.Items.Count; index++)
                if (string.Equals(GetPortFromListItem(_multiScreenDevices.Items[index]), selectedPanel, StringComparison.OrdinalIgnoreCase))
                {
                    _multiScreenDevices.SelectedIndex = index;
                    break;
                }
        }
        if (_multiScreenDevices.SelectedIndex < 0 && _multiScreenDevices.Items.Count > 0)
            _multiScreenDevices.SelectedIndex = 0;
        _multiScreenDevices.EndUpdate();
        _loadingPanelCompositionSettings = false;
        LoadSelectedPanelCrop();
        _multiScreenStatus.Text = ports.Length switch
        {
            0 => "Nenhum painel LCD Jungle Leopard compatível foi detectado. Conecte o painel por USB.",
            1 => "1 painel detectado. Inicie uma imagem, tema ou captura do desktop na página Tela LCD.",
            _ => $"{ports.Length} painéis detectados. É possível espelhar o quadro ou compor a captura entre eles."
        };

        if (ports.Length == 0)
            SetPanelConnectionError(null);

        _panelDetection.Text = ports.Length switch
        {
            0 => "Tela não encontrada. Conecte a tela Jungle Leopard por USB.",
            1 => $"Tela Jungle Leopard detectada na porta USB {ports[0]}.",
            _ => $"{ports.Length} telas Jungle Leopard detectadas nas portas USB {string.Join(", ", ports)}."
        };
        Size nativePanelSize = detectedPort is not null ? GetNativePanelSize(detectedPort) : new Size(PanelWidth, PanelHeight);
        _sidebarDeviceStatus.Text = ports.Length switch
        {
            0 => "Painel desconectado",
            1 => $"Painel conectado\r\n{ports[0]} · {nativePanelSize.Width} × {nativePanelSize.Height}",
            _ => $"{ports.Length} painéis conectados\r\n{string.Join(", ", ports)}"
        };
        _showButton.Enabled = _jpegBytes is not null && ports.Length > 0;
        _composeCurrentVisualButton.Enabled = ports.Length > 1 && (_sourceImage is not null || HasActiveTheme);
        _stopMultiScreenCompositionButton.Enabled = _multiScreenCompositionActive;
        _startDesktopCaptureButton.Enabled = !_desktopCaptureActive
            && ports.Length > 0
            && _desktopDisplaySelector.SelectedItem is CaptureDisplayChoice;
        _stopDesktopCaptureButton.Enabled = _desktopCaptureActive;

        if (panelOrderChanged)
            SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);

        if (changed && _streamTask is { IsCompleted: false } && ports.Length == 0)
            SetStatus("A tela USB foi desconectada. A transmissão será interrompida quando a porta reportar a falha.");
        else if (changed && ports.Length > 0 && _jpegBytes is not null)
        {
            SetStatus($"{ports.Length} painel(is) detectado(s) automaticamente: {string.Join(", ", ports)}.");
            _nextPanelReconnectAttemptUtc = DateTime.MinValue;
        }

        TryReconnectPanel(ports);

        if (changed && _multiScreenCompositionActive)
        {
            if (ports.Length > 1)
                RefreshMultiScreenComposition();
            else
            {
                Volatile.Write(ref _multiScreenCompositionActive, false);
                Volatile.Write(ref _panelJpegFrames, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
                _stopMultiScreenCompositionButton.Enabled = false;
                RefreshPreparedImage();
                _multiScreenCompositionStatus.Text = "A composição foi parada porque são necessários pelo menos dois painéis conectados.";
            }
        }
    }

    private void TryReconnectPanel(string[] ports)
    {
        if (ports.Length == 0 || _jpegBytes is null || _deviceMaintenanceInProgress
            || _autoSteamSessionSuppressed || _desktopCaptureActive || IsSystemPlaybackBlocked()
            || _streamTask is { IsCompleted: false }
            || Volatile.Read(ref _transmissionStartInProgress) != 0
            || DateTime.UtcNow < _nextPanelReconnectAttemptUtc)
            return;

        _nextPanelReconnectAttemptUtc = DateTime.UtcNow.AddSeconds(5);
        StartStreaming(automatic: true);
    }

    private void SchedulePanelReconnect()
    {
        if (!_isShuttingDown)
            _nextPanelReconnectAttemptUtc = DateTime.UtcNow.AddSeconds(5);
    }

    private static string[] DetectPanelPorts()
    {
        try
        {
            var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string deviceUsbKey in DeviceUsbKeys)
            {
                using var usbRoot = Registry.LocalMachine.OpenSubKey(deviceUsbKey);
                if (usbRoot is null) continue;
                foreach (string instanceName in usbRoot.GetSubKeyNames())
                {
                    using var deviceParams = usbRoot.OpenSubKey($@"{instanceName}\Device Parameters");
                    if (deviceParams?.GetValue("PortName") is string port
                        && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        ports.Add(port);
                }
            }

            return ports.OrderBy(port => int.TryParse(port.AsSpan(3), out int number) ? number : int.MaxValue)
                .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (SecurityException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string? DetectPanelPort() => DetectPanelPorts().FirstOrDefault();

    private static string? FindPanelDeviceInstanceId(string port)
    {
        const string enumRegistryPrefix = @"SYSTEM\CurrentControlSet\Enum\";
        foreach (string deviceUsbKey in DeviceUsbKeys)
        {
            if (!deviceUsbKey.StartsWith(enumRegistryPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            using var usbRoot = Registry.LocalMachine.OpenSubKey(deviceUsbKey);
            if (usbRoot is null) continue;
            foreach (string instanceName in usbRoot.GetSubKeyNames())
            {
                using var deviceParams = usbRoot.OpenSubKey($@"{instanceName}\Device Parameters");
                if (deviceParams?.GetValue("PortName") is string devicePort
                    && string.Equals(devicePort, port, StringComparison.OrdinalIgnoreCase))
                    return deviceUsbKey[enumRegistryPrefix.Length..] + "\\" + instanceName;
            }
        }

        return null;
    }

    private void SetPanelInfoDescriptionFromWorker(string description)
    {
        if (_isShuttingDown) return;
        try
        {
            if (!IsOnUiThread)
                PostToUi((Action)(() => SetPanelInfoDescription(description)));
            else
                SetPanelInfoDescription(description);
        }
        catch (InvalidOperationException) { }
    }

    private void SetPanelInfoDescription(string description)
    {
        _panelInfoDescription = description;
    }

    private void SetPanelIdentityFromWorker(PanelIdentity? identity)
    {
        if (_isShuttingDown) return;
        try
        {
            if (!IsOnUiThread)
                PostToUi((Action)(() => SetPanelIdentity(identity)));
            else
                SetPanelIdentity(identity);
        }
        catch (InvalidOperationException) { }
    }

    private void SetPanelIdentity(PanelIdentity? identity)
    {
        _panelIdentity = identity;
        if (identity is not null && _detectedPort is not null)
            _panelIdentitiesByPort[_detectedPort] = identity;
        _firmwareStatus.Text = identity is null
            ? "Não foi possível ler modelo e firmware do painel."
            : $"Painel: {identity.Model}  ·  firmware {identity.Version}  ·  {identity.Width} × {identity.Height}";
        if (identity is not null && _detectedPort is not null)
            _sidebarDeviceStatus.Text = $"Painel conectado\r\n{_detectedPort} · {identity.Width} × {identity.Height}";
    }

    private void ChooseImageClicked(object? sender, EventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = AppLanguage.Translate("Escolha uma imagem para a tela"),
            Filter = AppLanguage.Translate("Imagens|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Todos os arquivos|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };
        StudioFileDialogDefaults.Configure(dialog, _lastImagePath);

        if (dialog.ShowDialog(NativeDialogOwner) != true)
            return;

        StopDesktopCapture(restoreSavedVisual: false);

        try
        {
            Image loadedImage;
            using (Image imageFile = Image.FromFile(dialog.FileName))
                loadedImage = new Bitmap(imageFile);

            PreparedImage result;
            try
            {
                result = PrepareImage(loadedImage, CurrentRotation, CurrentFitMode, CurrentRenderBrightness);
            }
            catch
            {
                loadedImage.Dispose();
                throw;
            }

            _sourceImage?.Dispose();
            ClearActiveTheme();
            _sourceImage = loadedImage;
            _selectedImageName = Path.GetFileName(dialog.FileName);
            _lastImagePath = dialog.FileName;
            DeleteActiveThemePackage();
            _loadedSteamAppId = null;
            _loadedSteamCoverPortrait = null;
            _ = result;
            RefreshPreparedImage();
            SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show(NativeDialogOwner, ex.Message, "Não foi possível preparar a imagem", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private ImageRotation CurrentRotation => (ImageRotation)Math.Clamp(Volatile.Read(ref _currentRotationIndex), 0, 3);

    private ImageFitMode CurrentFitMode => (ImageFitMode)Math.Clamp(_fitSelector.SelectedIndex, 0, 1);

    private bool IsPlaceholderVisual => _selectedImageName is "Aguardando imagem" or "Aguardando jogo Steam em primeiro plano";

    private int CurrentBrightness => Volatile.Read(ref _panelBrightness);

    private double CurrentRenderBrightness => CurrentBrightness * PanelBrightnessRenderRatio;

    private bool PrefersPortraitCover => CurrentRotation is ImageRotation.Clockwise90 or ImageRotation.Clockwise270;

    private Size GetNativePanelSize(string? port = null)
    {
        if (!string.IsNullOrWhiteSpace(port) && _panelIdentitiesByPort.TryGetValue(port, out PanelIdentity? panelIdentity))
            return panelIdentity.NativeSize;
        return _panelIdentity?.NativeSize ?? new Size(PanelWidth, PanelHeight);
    }

    private int GetPanelJpegMaxBytes(string? port = null)
    {
        PanelIdentity? identity = !string.IsNullOrWhiteSpace(port)
            && _panelIdentitiesByPort.TryGetValue(port, out PanelIdentity? panelIdentity)
                ? panelIdentity
                : _panelIdentity;
        if (identity is null)
            return MaxJpegBytes;
        int maxDimension = Math.Max(identity.Width, identity.Height);
        bool newerFirmware = Version.TryParse(identity.Version, out Version? version) && version > new Version(2, 8);
        int maxKilobytes = newerFirmware ? 80 : 50;
        if (identity.Model.Contains("9.16", StringComparison.OrdinalIgnoreCase))
            maxKilobytes = newerFirmware ? 120 : 90;
        if (identity.Model.Contains("6.67", StringComparison.OrdinalIgnoreCase) || maxDimension >= 1024)
            maxKilobytes = 260;
        if (identity.Model.Contains("10.26", StringComparison.OrdinalIgnoreCase))
            maxKilobytes = 350;
        return maxKilobytes * 1024;
    }

    private int GetPanelFrameRate(string? port = null)
    {
        PanelIdentity? identity = !string.IsNullOrWhiteSpace(port)
            && _panelIdentitiesByPort.TryGetValue(port, out PanelIdentity? panelIdentity)
                ? panelIdentity
                : _panelIdentity;
        if (identity is null)
            return 30;
        bool newerFirmware = Version.TryParse(identity.Version, out Version? version) && version > new Version(2, 8);
        if (identity.Model.Contains("9.16", StringComparison.OrdinalIgnoreCase))
            return newerFirmware ? 60 : 30;
        if (identity.Model.Contains("6.67", StringComparison.OrdinalIgnoreCase)
            || identity.Model.Contains("10.26", StringComparison.OrdinalIgnoreCase)
            || Math.Max(identity.Width, identity.Height) >= 1024)
            return 60;
        return 30;
    }

    private Size GetDisplaySize(ImageRotation rotation) => GetDisplaySize(rotation, GetNativePanelSize());

    private static Size GetDisplaySize(ImageRotation rotation, Size nativePanelSize) => rotation is ImageRotation.Clockwise90 or ImageRotation.Clockwise270
        ? new Size(nativePanelSize.Height, nativePanelSize.Width)
        : nativePanelSize;

    private static int LoadSavedRotationIndex()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null)
                return 0;

            SavedSettings? settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsPath));
            return settings?.RotationIndex is >= 0 and <= 3 ? settings.RotationIndex : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            return 0;
        }
    }

    private static int LoadSavedFitModeIndex()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null)
                return 0;

            SavedSettings? settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsPath));
            return settings?.FitModeIndex switch
            {
                2 => 1,
                _ => 0
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            return 0;
        }
    }

    private static bool LoadSavedAutoSteamCover()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null)
                return false;

            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (settings.RootElement.TryGetProperty(nameof(SavedSettings.AutoSteamCover), out JsonElement savedValue))
                return savedValue.ValueKind == JsonValueKind.True;

            // Existing settings files predate this option. Preserve the checked state from the user's current setup.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static int LoadSavedBrightness()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null)
                return 100;

            SavedSettings? settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsPath));
            return Math.Clamp(settings?.Brightness ?? 100, 0, 100);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            return 100;
        }
    }

    private static string? LoadSavedImagePath()
    {
        try
        {
            string? settingsPath = FindExistingSettingsPath();
            if (settingsPath is null)
                return null;

            SavedSettings? settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsPath));
            return string.IsNullOrWhiteSpace(settings?.LastImagePath) ? null : settings.LastImagePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private void SaveSettings(int rotationIndex, bool autoSteamCover)
    {
        try
        {
            string settingsPath = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new SavedSettings(
                rotationIndex,
                autoSteamCover,
                _temperatureUnit,
                _startHidden.Checked,
                CurrentBrightness,
                _renderMode,
                AppSensorSettings.DisableDiskSensorScan,
                AppSensorSettings.DiskSensorScanIntervalMs,
                _lastImagePath,
                _fitSelector.SelectedIndex,
                _panelOrder.ToArray(),
                new Dictionary<string, PanelCropSettings>(_panelCrops, StringComparer.OrdinalIgnoreCase),
                Volatile.Read(ref _compositionLayoutIndex),
                _ffmpegPath,
                _keepPlayWhenScreenOff.Checked,
                new Dictionary<string, string>(AppSensorSettings.SensorSelections, StringComparer.OrdinalIgnoreCase))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            // Keep the selected settings active for this session even if they cannot be saved.
        }
    }

    private void LoadMultiScreenSettings()
    {
        try
        {
            string? path = FindExistingSettingsPath();
            if (path is null) return;
            SavedSettings? settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path));
            if (settings?.CompositionLayoutIndex is >= 0 and <= 3)
                _compositionLayoutIndex = settings.CompositionLayoutIndex.Value;
            if (settings?.PanelOrder is { Length: > 0 })
                _panelOrder = settings.PanelOrder.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (settings?.PanelCrops is not null)
                foreach ((string port, PanelCropSettings crop) in settings.PanelCrops)
                    if (!string.IsNullOrWhiteSpace(port)) _panelCrops[port] = crop;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            _panelOrder.Clear();
            _panelCrops.Clear();
        }
    }

    private static string? FindExistingSettingsPath()
    {
        string userSettingsPath = GetSettingsPath();
        if (File.Exists(userSettingsPath))
            return userSettingsPath;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JungleLeopardPoc.csproj")))
            {
                string developmentSettingsPath = Path.Combine(directory.FullName, "settings.json");
                if (File.Exists(developmentSettingsPath))
                    return developmentSettingsPath;
                break;
            }
            directory = directory.Parent;
        }

        string legacyPath = GetLegacySettingsPath();
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JungleLeopardStudio",
        "settings.json");

    private static string GetActiveThemePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JungleLeopardStudio",
        "active-theme.ht");

    private static string GetSteamLayoutPath(bool portrait) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JungleLeopardStudio",
        portrait ? "steam-layout-portrait.ht" : "steam-layout-landscape.ht");

    private static void SaveActiveThemePackage(ThemePackage package)
    {
        string path = GetActiveThemePath();
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, package.ToBytes());
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void DeleteActiveThemePackage()
    {
        string path = GetActiveThemePath();
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string GetLegacySettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JungleLeopardPoc",
        "settings.json");

    private void RefreshPreparedImage()
    {
        if (_multiScreenCompositionActive && RefreshMultiScreenComposition())
            return;
        if (_selectedImageName is null)
            return;

        try
        {
            string[] ports = Volatile.Read(ref _detectedPorts);
            if (ports.Length > 1)
            {
                var frames = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                PreparedImage? preview = null;
                foreach (string port in ports)
                {
                    PreparedImage? panelFrame = RenderPanelVisual(port);
                    if (panelFrame is null) continue;
                    frames[port] = panelFrame.Bytes;
                    preview ??= panelFrame;
                }
                if (preview is not null)
                {
                    DisplayPreparedImage(preview, preservePanelFrames: true);
                    Volatile.Write(ref _panelJpegFrames, frames);
                    return;
                }
            }

            PreparedImage? themeFrame = RenderActiveThemeFrame();
            if (themeFrame is not null)
            {
                DisplayPreparedImage(themeFrame);
                return;
            }
            if (_sourceImage is not null)
            {
                if (IsPlaceholderVisual)
                {
                    using Bitmap placeholder = _previewPlaceholder.CreateSourceBitmap(GetDisplaySize(CurrentRotation));
                    DisplayPreparedImage(PrepareImage(placeholder, CurrentRotation, ImageFitMode.Stretch, CurrentRenderBrightness));
                }
                else
                    DisplayPreparedImage(PrepareImage(_sourceImage, CurrentRotation, CurrentFitMode, CurrentRenderBrightness,
                        blackContainBackground: IsSteamCoverVisual));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(NativeDialogOwner, ex.Message, "Não foi possível preparar a imagem", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private PreparedImage? RenderPanelVisual(string port)
    {
        ImageRotation rotation = GetPanelRotation(port);
        Size nativeSize = GetNativePanelSize(port);
        if (IsPlaceholderVisual)
        {
            using Bitmap placeholder = _previewPlaceholder.CreateSourceBitmap(GetDisplaySize(rotation, nativeSize));
            return PrepareImage(placeholder, rotation, ImageFitMode.Stretch, CurrentRenderBrightness,
                nativePanelSize: nativeSize, maximumJpegBytes: GetPanelJpegMaxBytes(port));
        }
        if (_sourceImage is not null)
            return PrepareImage(_sourceImage, rotation, CurrentFitMode, CurrentRenderBrightness, nativePanelSize: nativeSize,
                maximumJpegBytes: GetPanelJpegMaxBytes(port), blackContainBackground: IsSteamCoverVisual);

        lock (_themeRendererGate)
        {
            if (_activeThemeRenderer is null)
                return null;
            using Bitmap scene = _activeThemeRenderer.Render(GetDisplaySize(rotation, nativeSize));
            return PrepareImage(scene, rotation, ImageFitMode.Stretch, CurrentRenderBrightness, nativePanelSize: nativeSize,
                maximumJpegBytes: GetPanelJpegMaxBytes(port));
        }
    }

    private void DisplayPreparedImage(PreparedImage result, bool preservePanelFrames = false)
    {
        if (!preservePanelFrames)
        {
            Volatile.Write(ref _multiScreenCompositionActive, false);
            _stopMultiScreenCompositionButton.Enabled = false;
            Volatile.Write(ref _panelJpegFrames, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
        }
        SetCurrentPreparedImage(result);
        _imageInfo.Text = $"{_selectedImageName}  •  prévia {result.DisplaySize.Width} × {result.DisplaySize.Height}  •  JPEG 4:2:0  •  {result.Bytes.Length / 1024.0:F1} KB";
        _showButton.Enabled = _detectedPort is not null;
        _composeCurrentVisualButton.Enabled = Volatile.Read(ref _detectedPorts).Length > 1 && (_sourceImage is not null || HasActiveTheme);

        _preview.Image?.Dispose();
        using var previewStream = new MemoryStream(result.PreviewBytes, writable: false);
        using var decodedPreview = Image.FromStream(previewStream);
        _preview.Image = new Bitmap(decodedPreview);
        _preview.Visible = true;
        _previewPlaceholder.Visible = false;
        Size nativePanelSize = GetNativePanelSize();

        if (_desktopCaptureActive)
            SetStatus($"Capturando {_selectedImageName} e preparando o próximo quadro USB.");
        else if (_streamTask is { IsCompleted: false })
            SetStatus($"Ajustes atualizados. Prévia {result.DisplaySize.Width} × {result.DisplaySize.Height}; quadro nativo do painel {nativePanelSize.Width} × {nativePanelSize.Height}.");
        else
            SetStatus($"Prévia {result.DisplaySize.Width} × {result.DisplaySize.Height}; quadro USB em {nativePanelSize.Width} × {nativePanelSize.Height}. Aplique um tema pelo Studio de temas para enviar conteúdo.");
    }

    private void SetCurrentPreparedImage(PreparedImage prepared)
    {
        Volatile.Write(ref _jpegBytes, prepared.Bytes);
        Volatile.Write(ref _displayPreviewBytes, prepared.PreviewBytes);
    }

    private async void SteamDetectionTimerTick(object? sender, EventArgs e)
    {
        if (!_autoSteamCover.Checked || _steamDetectionBusy || _deviceMaintenanceInProgress || _desktopCaptureActive || IsSystemPlaybackBlocked())
            return;

        _steamDetectionBusy = true;
        try
        {
            SteamGameInfo? game = await Task.Run(SteamGameDetector.GetForegroundGame, _appShutdown.Token);
            if (game is null)
            {
                ObserveForegroundGame(null);
                return;
            }

            ObserveForegroundGame(game);
            _gameStatus.Text = $"Jogo em primeiro plano: {game.Name} (AppID {game.AppId}).";
            bool preferPortrait = PrefersPortraitCover;
            bool needsCover = _loadedSteamAppId != game.AppId || _loadedSteamCoverPortrait != preferPortrait;
            bool shouldLoadCover = needsCover
                && (_steamCoverAttemptAppId != game.AppId
                    || _steamCoverAttemptPortrait != preferPortrait
                    || DateTime.UtcNow >= _nextSteamCoverRetryUtc);
            if (shouldLoadCover)
            {
                _steamCoverAttemptAppId = game.AppId;
                _steamCoverAttemptPortrait = preferPortrait;
                _nextSteamCoverRetryUtc = DateTime.UtcNow.AddSeconds(30);
                _gameStatus.Text = $"Buscando a capa {(preferPortrait ? "vertical" : "horizontal")} de {game.Name}…";

                byte[] coverBytes = await SteamCoverProvider.GetCoverAsync(
                    game,
                    preferPortrait,
                    _appShutdown.Token);

                if (_isShuttingDown || !_autoSteamCover.Checked)
                    return;

                if (preferPortrait != PrefersPortraitCover)
                {
                    _steamCoverAttemptAppId = null;
                    _steamCoverAttemptPortrait = null;
                    _nextSteamCoverRetryUtc = DateTime.MinValue;
                    RequestSteamDetection();
                    return;
                }

                SteamGameInfo? foregroundGame = await Task.Run(SteamGameDetector.GetForegroundGame, _appShutdown.Token);
                if (foregroundGame?.AppId != game.AppId)
                {
                    ObserveForegroundGame(foregroundGame);
                    return;
                }

                ThemePackage? steamLayout = LoadSteamLayoutPackage(preferPortrait);
                if (steamLayout is not null)
                {
                    ApplySteamLayout(steamLayout, game.Name, coverBytes);
                }
                else
                {
                    Image newSource;
                    using (var coverStream = new MemoryStream(coverBytes, writable: false))
                    using (Image decodedCover = Image.FromStream(coverStream))
                        newSource = new Bitmap(decodedCover);

                    try
                    {
                        _ = PrepareImage(newSource, CurrentRotation, CurrentFitMode, CurrentRenderBrightness,
                            blackContainBackground: true);
                    }
                    catch
                    {
                        newSource.Dispose();
                        throw;
                    }

                    _sourceImage?.Dispose();
                    ClearActiveTheme();
                    _sourceImage = newSource;
                    _selectedImageName = $"{game.Name} (Steam)";
                    RefreshPreparedImage();
                }
                _loadedSteamAppId = game.AppId;
                _loadedSteamCoverPortrait = preferPortrait;
                _loadedSteamCoverBytes = (byte[])coverBytes.Clone();
                _nextSteamCoverRetryUtc = DateTime.MinValue;
                _gameStatus.Text = $"Capa {(preferPortrait ? "vertical" : "horizontal")} carregada: {game.Name}. A transmissão acompanha o jogo em foco.";
                _nextAutoConnectAttemptUtc = DateTime.MinValue;
            }

            if (_loadedSteamAppId == game.AppId)
                EnsureAutomaticSteamTransmission(game);
        }
        catch (OperationCanceledException) when (_appShutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _gameStatus.Text = $"Não consegui obter a capa Steam: {ex.Message}";
        }
        finally
        {
            _steamDetectionBusy = false;
            if (_steamDetectionRefreshRequested && _autoSteamCover.Checked && !_isShuttingDown)
            {
                _steamDetectionRefreshRequested = false;
                SteamDetectionTimerTick(this, EventArgs.Empty);
            }
        }
    }

    private void ApplySteamLayout(ThemePackage layout, string gameName, byte[] coverBytes)
    {
        JsonObject definition = layout.CreateEditableDefinition();
        var dynamicAssets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ThemePackage.SteamBackgroundAssetPath] = ThemePackage.CreateSteamBackgroundAsset(
                layout.DeviceWidth, layout.DeviceHeight),
            [ThemePackage.SteamCoverAssetPath] = coverBytes
        };
        var renderer = new HtThemeRenderer(layout.WithDefinition(definition, dynamicAssets), fitToScreen: true);
        PreparedImage prepared;
        try
        {
            using Bitmap scene = renderer.Render(GetDisplaySize(CurrentRotation));
            prepared = PrepareImage(scene, CurrentRotation, ImageFitMode.Stretch, CurrentRenderBrightness);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }

        lock (_themeRendererGate)
        {
            _activeThemeRenderer?.Dispose();
            _activeThemeRenderer = renderer;
        }
        _sourceImage?.Dispose();
        _sourceImage = null;
        _selectedImageName = $"{gameName} (Steam)";
        DisplayPreparedImage(prepared);
        _themePreviewTimer.Start();
    }

    private void RequestSteamDetection()
    {
        if (!_autoSteamCover.Checked || _isShuttingDown)
            return;

        if (_steamDetectionBusy)
        {
            _steamDetectionRefreshRequested = true;
            return;
        }

        SteamDetectionTimerTick(this, EventArgs.Empty);
    }

    private void ObserveForegroundGame(SteamGameInfo? game)
    {
        if (game is null)
        {
            _foregroundSteamAppId = null;
            _autoSteamSessionSuppressed = false;
            _steamCoverAttemptAppId = null;
            _steamCoverAttemptPortrait = null;
            _nextSteamCoverRetryUtc = DateTime.MinValue;

            if (_loadedSteamAppId is not null)
                ClearSteamGameDisplay("Nenhum jogo compatível da Biblioteca Steam está em primeiro plano.");
            else
                _gameStatus.Text = "Nenhum jogo compatível da Biblioteca Steam está em primeiro plano.";
            return;
        }

        bool changed = _foregroundSteamAppId != game.AppId;
        _foregroundSteamAppId = game.AppId;
        if (changed)
            _autoSteamSessionSuppressed = false;
        if (changed && _loadedSteamAppId is not null && _loadedSteamAppId != game.AppId)
        {
            ClearSteamGameDisplay($"Jogo em primeiro plano: {game.Name}. Removendo a capa anterior enquanto busco a nova…");
        }
    }

    private void ClearSteamGameDisplay(string status)
    {
        if (_loadedSteamAppId is null)
        {
            _gameStatus.Text = status;
            return;
        }

        _loadedSteamAppId = null;
        _loadedSteamCoverPortrait = null;
        _loadedSteamCoverBytes = null;
        RestoreSavedVisual();
        if (IsSteamCoverVisual || _jpegBytes is null)
            ShowWaitingVisual();
        _gameStatus.Text = status;
    }

    private void EnsureAutomaticSteamTransmission(SteamGameInfo game)
    {
        if (_deviceMaintenanceInProgress || _autoSteamSessionSuppressed || _desktopCaptureActive)
            return;

        if (_streamTask is { IsCompleted: false })
            return;

        if (_detectedPort is null)
        {
            _gameStatus.Text = $"Capa de {game.Name} pronta; tela Jungle Leopard não detectada.";
            return;
        }

        if (DateTime.UtcNow < _nextAutoConnectAttemptUtc)
            return;

        _nextAutoConnectAttemptUtc = DateTime.UtcNow.AddSeconds(5);
        StartStreaming(automatic: true);
    }

    private void ShowImageClicked(object? sender, EventArgs e)
        => StartStreaming(automatic: false);

    private void StartStreaming(bool automatic)
    {
        if (_isShuttingDown || _deviceMaintenanceInProgress)
            return;
        if (IsSystemPlaybackBlocked())
        {
            SetStatus("A reprodução está pausada porque o Windows está bloqueado ou suspenso.");
            return;
        }
        if (!automatic)
            _autoSteamSessionSuppressed = false;
        if (Interlocked.CompareExchange(ref _transmissionStartInProgress, 1, 0) != 0)
            return;

        _ = StartStreamingWithPriorityAsync(automatic);
    }

    private async Task StartStreamingWithPriorityAsync(bool automatic)
    {
        try
        {
            if (_isShuttingDown)
                return;
            if (_jpegBytes is null || _streamTask is { IsCompleted: false })
            {
                StartStreamingCore(automatic);
                return;
            }

            if (IsVendorRunning())
            {
                SetStatus("A Studio está assumindo o controle do painel e liberando a porta USB…");
                if (!await ReleaseVendorForStudioAsync())
                {
                    const string message = "Não foi possível liberar o painel do Jungle Leopard Display. Feche o programa original ou execute ambos com a mesma permissão e tente novamente.";
                    SetPanelConnectionError(message);
                    SetStatus(message);
                    if (!automatic)
                        MessageBox.Show(NativeDialogOwner, message, "Não foi possível assumir a tela", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (_isShuttingDown)
                    return;
                await Task.Delay(300);
            }

            if (_isShuttingDown)
                return;

            if (IsVendorRunning())
            {
                const string message = "O programa original voltou a abrir e ainda está usando o painel. Feche-o e tente novamente.";
                SetPanelConnectionError(message);
                SetStatus(message);
                return;
            }

            StartStreamingCore(automatic);
        }
        catch (Exception ex)
        {
            SetPanelConnectionError(ex.Message);
            SetStatus($"Não foi possível iniciar o controle da tela: {ex.Message}");
            if (automatic)
                SchedulePanelReconnect();
        }
        finally
        {
            Interlocked.Exchange(ref _transmissionStartInProgress, 0);
        }
    }

    private void StartStreamingCore(bool automatic)
    {
        if (_isShuttingDown)
            return;
        if (_jpegBytes is null)
        {
            if (!automatic)
                MessageBox.Show(NativeDialogOwner, "Aplique um tema pelo Studio de temas antes de iniciar a transmissão.", "Tema necessário", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string[] detectedPorts = Volatile.Read(ref _detectedPorts);
        string[] ports = detectedPorts.Length > 0 ? detectedPorts.ToArray() : DetectPanelPorts();
        string? port = ports.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(port))
        {
            if (!automatic)
                MessageBox.Show(NativeDialogOwner, "Não encontrei um painel LCD Jungle Leopard compatível no USB. Confira o cabo USB.", "Tela não detectada", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _detectedPort = port;
        Volatile.Write(ref _detectedPorts, ports);

        if (_streamTask is { IsCompleted: false })
        {
            if (!automatic)
                SetStatus($"Enviando “{_selectedImageName}” para {port}. A imagem será substituída no próximo quadro.");
            return;
        }

        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();
        CancellationToken token = _streamCts.Token;
        _streamTask = Task.Run(() => StreamImageLoop(ports, token));
        _stopButton.Enabled = true;
        SetStatus(automatic
            ? $"Conectando a {port} e transmitindo o conteúdo atual."
            : $"Abrindo {port} e iniciando a transmissão. Se o original estiver ativo, use “Fechar Jungle Leopard” primeiro.");
    }

    private async void StopClicked(object? sender, EventArgs e)
    {
        _autoSteamSessionSuppressed = true;
        _resumePlaybackAfterSystemState = false;
        await StopStreamingAsync();
        SetStatus("Transmissão parada e porta liberada.");
    }

    private async void CloseOriginalClicked(object? sender, EventArgs e)
    {
        _resumePlaybackAfterSystemState = false;
        _closeOriginalButton.Enabled = false;
        try
        {
            await StopStreamingAsync();
            await CloseOriginalAsync();
        }
        finally
        {
            _closeOriginalButton.Enabled = true;
            _showButton.Enabled = _jpegBytes is not null && _detectedPort is not null;
        }
    }


    private void ApplyCoverTheme(ThemePackage package)
        => ApplyCoverTheme(package, persist: true, startStreaming: true);

    private void ApplyCoverTheme(ThemePackage package, bool persist, bool startStreaming, bool fitToScreen = false)
    {
        StopDesktopCapture(restoreSavedVisual: false);
        try
        {
            HtThemeRenderer renderer = new(package, fitToScreen);
            PreparedImage prepared;
            lock (_themeRendererGate)
            {
                _activeThemeRenderer?.Dispose();
                _activeThemeRenderer = renderer;
                using Bitmap scene = renderer.Render(GetDisplaySize(CurrentRotation));
                prepared = PrepareImage(scene, CurrentRotation, ImageFitMode.Stretch, CurrentRenderBrightness);
            }
            _sourceImage?.Dispose();
            _sourceImage = null;
            _selectedImageName = package.Name;
            _lastImagePath = null;
            _loadedSteamAppId = null;
            _loadedSteamCoverPortrait = null;
            if (persist)
            {
                try
                {
                    SaveActiveThemePackage(package);
                    SaveSettings(_rotationSelector.SelectedIndex, _autoSteamCover.Checked);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
                {
                    SetStatus("Tema aplicado, mas não foi possível salvá-lo para a próxima abertura.");
                }
            }
            DisplayPreparedImage(prepared);
            if (_detectedPorts.Length > 1)
                RefreshPreparedImage();
            _themePreviewTimer.Start();
            SetStatus("Tema .ht ativo: relógio, utilização de CPU/memória/GPU e sensores de temperatura são atualizados ao vivo; sensores indisponíveis aparecem como —.");
            if (startStreaming)
                StartStreaming(automatic: false);
            else
                SetStatus("Último tema .ht restaurado na prévia. Aplique-o pelo Studio de temas para iniciar a transmissão.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(NativeDialogOwner, ex.Message, "Não foi possível renderizar o tema .ht", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreSavedVisual()
    {
        if (!string.IsNullOrWhiteSpace(_lastImagePath) && File.Exists(_lastImagePath))
        {
            Image? loadedImage = null;
            try
            {
                using (Image source = Image.FromFile(_lastImagePath))
                    loadedImage = new Bitmap(source);
                PreparedImage prepared = PrepareImage(loadedImage, CurrentRotation, CurrentFitMode, CurrentRenderBrightness);
                _sourceImage?.Dispose();
                ClearActiveTheme();
                _sourceImage = loadedImage;
                loadedImage = null;
                _selectedImageName = Path.GetFileName(_lastImagePath);
                _loadedSteamAppId = null;
                _loadedSteamCoverPortrait = null;
                DisplayPreparedImage(prepared);
                if (_detectedPorts.Length > 1)
                    RefreshPreparedImage();
                SetStatus($"Imagem restaurada: {_selectedImageName}.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
                SetStatus($"A última imagem não pôde ser restaurada: {ex.Message}");
            }
            finally
            {
                loadedImage?.Dispose();
            }
        }
        else if (!string.IsNullOrWhiteSpace(_lastImagePath))
        {
            _lastImagePath = null;
        }

        string savedThemePath = GetActiveThemePath();
        if (!File.Exists(savedThemePath)) return;
        try
        {
            ThemePackage package = ThemePackage.Read(savedThemePath);
            ApplyCoverTheme(package, persist: false, startStreaming: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            SetStatus($"O último tema .ht não pôde ser restaurado: {ex.Message}");
        }
    }

    private void EnsureStartupVisual()
    {
        if (_jpegBytes is not null)
            return;

        ShowWaitingVisual();
    }

    private void ShowWaitingVisual()
    {
        Bitmap placeholder = _previewPlaceholder.CreateSourceBitmap(GetDisplaySize(CurrentRotation));
        PreparedImage prepared;
        try
        {
            prepared = PrepareImage(placeholder, CurrentRotation, ImageFitMode.Stretch, CurrentRenderBrightness);
        }
        catch
        {
            placeholder.Dispose();
            throw;
        }

        _sourceImage?.Dispose();
        ClearActiveTheme();
        _sourceImage = placeholder;
        _selectedImageName = "Aguardando imagem";
        DisplayPreparedImage(prepared);
    }

    private void ClearActiveTheme()
    {
        _themePreviewTimer.Stop();
        lock (_themeRendererGate)
        {
            _activeThemeRenderer?.Dispose();
            _activeThemeRenderer = null;
        }
    }

    private PreparedImage? RenderActiveThemeFrame(string? port = null)
    {
        ImageRotation rotation = port is null ? CurrentRotation : GetPanelRotation(port);
        Size nativeSize = GetNativePanelSize(port);
        lock (_themeRendererGate)
        {
            if (_activeThemeRenderer is null)
                return null;
            using Bitmap scene = _activeThemeRenderer.Render(GetDisplaySize(rotation, nativeSize));
            return PrepareImage(scene, rotation, ImageFitMode.Stretch, CurrentRenderBrightness, nativePanelSize: nativeSize,
                maximumJpegBytes: GetPanelJpegMaxBytes(port));
        }
    }

    private PanelCompositionOutput? RenderMirroredThemeFrames(string[] ports)
    {
        var frames = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        PreparedImage? preview = null;
        foreach (string port in ports)
        {
            PreparedImage? frame = RenderActiveThemeFrame(port);
            if (frame is null) continue;
            frames[port] = frame.Bytes;
            preview ??= frame;
        }
        return preview is null ? null : new PanelCompositionOutput(preview, frames);
    }

    private bool HasActiveTheme
    {
        get { lock (_themeRendererGate) return _activeThemeRenderer is not null; }
    }

    private void QueueLiveThemePreview(PreparedImage frame)
    {
        if (_isShuttingDown || !HasStudioWindow) return;
        try
        {
            PostToUi((Action)(() =>
            {
                if (_isShuttingDown || !HasActiveTheme) return;
                _preview.Image?.Dispose();
                using var previewStream = new MemoryStream(frame.PreviewBytes, writable: false);
                using var decoded = Image.FromStream(previewStream);
                _preview.Image = new Bitmap(decoded);
                _preview.Visible = true;
                _previewPlaceholder.Visible = false;
            }));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private async Task RefreshIdleThemePreviewAsync()
    {
        if (_streamTask is { IsCompleted: false } || !HasActiveTheme
            || Interlocked.Exchange(ref _themePreviewRenderInProgress, 1) != 0)
            return;

        try
        {
            bool composing = Volatile.Read(ref _multiScreenCompositionActive);
            if (composing)
            {
                PanelCompositionOutput? output = await Task.Run(() => RenderActiveThemeComposition());
                if (output is null || _streamTask is { IsCompleted: false } || !HasActiveTheme) return;
                SetCurrentPreparedImage(output.Preview);
                Volatile.Write(ref _panelJpegFrames, output.Frames);
                QueueLiveThemePreview(output.Preview);
            }
            else
            {
                PreparedImage? frame = await Task.Run(() => RenderActiveThemeFrame());
                if (frame is null || _streamTask is { IsCompleted: false } || !HasActiveTheme) return;
                SetCurrentPreparedImage(frame);
                QueueLiveThemePreview(frame);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Falha ao atualizar a prévia dinâmica do tema: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _themePreviewRenderInProgress, 0);
        }
    }

    private async void RestoreOriginalClicked(object? sender, EventArgs e)
    {
        _restoreOriginalButton.Enabled = false;
        try
        {
            await StopStreamingAsync();
            await RestoreOriginalAsync();
        }
        finally
        {
            _restoreOriginalButton.Enabled = true;
        }
    }

    private async Task<bool> ReleaseVendorForStudioAsync()
    {
        Process[] processes = GetVendorProcesses();
        if (processes.Length == 0)
            return true;

        foreach (Process process in processes)
        {
            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        DisposeProcesses(processes);
        if (await WaitForVendorExitAsync(TimeSpan.FromSeconds(4)))
            return true;

        var elevatedPids = new List<int>();
        processes = GetVendorProcesses();
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception)
            {
                try { elevatedPids.Add(process.Id); }
                catch (InvalidOperationException) { }
            }
        }
        DisposeProcesses(processes);

        if (await WaitForVendorExitAsync(TimeSpan.FromSeconds(3)))
            return true;
        if (elevatedPids.Count == 0)
            return false;

        try
        {
            string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = taskkill,
                Arguments = "/F /T " + string.Join(" ", elevatedPids.Select(id => $"/PID {id}")),
                UseShellExecute = true,
                Verb = "runas"
            };
            using Process? elevatedTaskkill = Process.Start(startInfo);
            if (elevatedTaskkill is null)
                return false;
            await elevatedTaskkill.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Win32Exception) { return false; }
        catch (TimeoutException) { return false; }

        return await WaitForVendorExitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task CloseOriginalAsync()
    {
        Process[] processes = GetVendorProcesses();
        if (processes.Length == 0)
        {
            SetStatus("O Jungle Leopard Display já está fechado; a porta está livre para a Studio.");
            return;
        }

        bool sentClose = false;
        foreach (Process process in processes)
        {
            try { sentClose |= process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        DisposeProcesses(processes);

        SetStatus(sentClose
            ? "Solicitei o fechamento normal do software original. Aguardando a porta ser liberada…"
            : "O software original está ativo sem uma janela principal visível. Verificando se ele encerra…");

        if (await WaitForVendorExitAsync(TimeSpan.FromSeconds(5)))
        {
            SetStatus("Jungle Leopard Display fechado. A porta da tela está livre para a Studio.");
            return;
        }

        DialogResult answer = MessageBox.Show(
            _studioWindow,
            "O Jungle Leopard Display continuou ativo após o pedido de fechamento normal.\n\n" +
            "Encerrá-lo à força pode descartar ajustes que ainda não foram salvos. Quer encerrar os processos restantes para liberar a tela?",
            "Confirmar encerramento",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            SetStatus("O software original continua ativo. A Studio só poderá usar a tela depois que ele for encerrado.");
            return;
        }

        bool needsElevation = false;
        processes = GetVendorProcesses();
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception ex)
            {
                needsElevation = true;
                SetStatus($"Não consegui encerrar o processo {process.Id}: {ex.Message}");
            }
        }
        DisposeProcesses(processes);

        if (needsElevation && IsVendorRunning())
        {
            try
            {
                string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                Process.Start(new ProcessStartInfo
                {
                    FileName = taskkill,
                    Arguments = "/F /IM \"Jungle Leopard Display.exe\"",
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception ex)
            {
                SetStatus($"Não consegui pedir ao Windows para encerrar o software elevado: {ex.Message}");
                return;
            }
        }

        if (await WaitForVendorExitAsync(TimeSpan.FromSeconds(5)))
            SetStatus("Jungle Leopard Display encerrado. A porta da tela está livre para a Studio.");
        else
            SetStatus("Ainda há processos do software original ativos. Feche-os pelo Windows e tente novamente.");
    }

    private async Task RestoreOriginalAsync()
    {
        string? executable = FindVendorExecutable();
        if (executable is null || !File.Exists(executable))
        {
            MessageBox.Show(NativeDialogOwner,
                "Não encontrei o executável do Jungle Leopard Display. Instale-o ou abra-o manualmente para devolver o controle da tela.",
                "Software original não encontrado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus("Transmissão da Studio parada. Não encontrei o programa original para reabri-lo.");
            return;
        }

        if (IsVendorRunning())
        {
            SetStatus("O software original está ativo na bandeja. Solicitando que ele mostre a janela…");
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true
            });

            if (await WaitForVendorWindowAsync(TimeSpan.FromSeconds(8)))
            {
                SetStatus("Jungle Leopard Display reaberto e trazido para a frente para retomar a tela.");
                return;
            }

            SetStatus("O Jungle Leopard Display não mostrou uma janela; reiniciando a instância oculta para devolver o controle da tela.");
            if (!await ReleaseVendorForStudioAsync())
            {
                const string message = "O Jungle Leopard Display continua ativo, mas não foi possível reabri-lo. Feche-o pelo ícone da bandeja ou execute a Studio com a mesma permissão e tente novamente.";
                SetStatus(message);
                MessageBox.Show(NativeDialogOwner, message, "Não foi possível restaurar a tela", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true
            });
            SetStatus("Iniciando Jungle Leopard Display…");
            if (await WaitForVendorReadyAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)))
            {
                SetStatus("Jungle Leopard Display reaberto. Ele deve retomar o conteúdo configurado no próprio software.");
                return;
            }

            SetStatus("A abertura normal não manteve o programa ativo. Tentando com a permissão elevada usada pela inicialização do software…");
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (await WaitForVendorReadyAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3)))
            {
                SetStatus("Jungle Leopard Display reaberto. Ele deve retomar o conteúdo configurado no próprio software.");
                return;
            }

            SetStatus("O Windows iniciou o pedido, mas o software original não permaneceu aberto. Confira se a solicitação de acesso foi aprovada.");
            MessageBox.Show(NativeDialogOwner,
                "O Jungle Leopard Display não permaneceu aberto. Se apareceu uma solicitação do Windows, aprove-a; caso contrário, abra o software pelo menu Iniciar.",
                "Não foi possível retomar a tela", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(NativeDialogOwner, $"Não consegui reabrir o software original:\n{ex.Message}", "Falha ao abrir", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Transmissão da Studio parada; reabra o Jungle Leopard Display manualmente para retomar a tela.");
        }
    }

    private async Task StopStreamingAsync(bool abortSessionWithoutProtocol = false)
    {
        StopDesktopCapture(restoreSavedVisual: false);
        CancellationTokenSource? source = _streamCts;
        Task? task = _streamTask;
        if (source is null)
        {
            if (abortSessionWithoutProtocol)
                Interlocked.Exchange(ref _abortStreamSessionOnStop, 0);
            _stopButton.Enabled = false;
            return;
        }

        if (abortSessionWithoutProtocol)
            Interlocked.Exchange(ref _abortStreamSessionOnStop, task is { IsCompleted: false } ? 1 : 0);
        source.Cancel();
        _stopButton.Enabled = false;
        SetStatus("Parando a transmissão e liberando a porta serial…");
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }

        if (ReferenceEquals(source, _streamCts))
        {
            _streamCts = null;
            _streamTask = null;
            source.Dispose();
        }
    }

    private (CancellationTokenSource? Source, Task? Task) RequestStreamingStopForUsbReset()
    {
        StopDesktopCapture(restoreSavedVisual: false);
        CancellationTokenSource? source = _streamCts;
        Task? task = _streamTask;
        if (source is null)
            return (null, task);

        Interlocked.Exchange(ref _abortStreamSessionOnStop, task is { IsCompleted: false } ? 1 : 0);
        source.Cancel();
        _stopButton.Enabled = false;
        SetStatus("Cancelando a transmissao; o Windows vai reiniciar o dispositivo para liberar a porta...");
        return (source, task);
    }

    private void ReleaseCompletedStreamStop(CancellationTokenSource? source, Task? task)
    {
        if (source is null || task is { IsCompleted: false } || !ReferenceEquals(source, _streamCts))
            return;

        _streamCts = null;
        _streamTask = null;
        source.Dispose();
    }

    private async Task RestartPanelUsbAsync()
    {
        if (Interlocked.CompareExchange(ref _panelUsbResetInProgress, 1, 0) != 0)
            return;

        bool restartSucceeded = false;
        bool rebootRequired = false;
        CancellationTokenSource? stoppingSource = null;
        Task? stoppingTask = null;
        try
        {
            _deviceMaintenanceInProgress = true;
            Volatile.Write(ref _usbResetStatus, "Preparando o reinicio da conexao USB...");
            string? port = _detectedPort ?? DetectPanelPort();
            if (string.IsNullOrWhiteSpace(port))
                throw new InvalidOperationException("Nenhum painel Jungle Leopard foi encontrado para reiniciar.");

            string? instanceId = FindPanelDeviceInstanceId(port);
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new InvalidOperationException($"Nao encontrei o dispositivo USB correspondente a porta {port}.");
            if (Environment.OSVersion.Version.Build < 19041)
                throw new PlatformNotSupportedException("O reinicio seletivo do dispositivo requer Windows 10 versao 2004 ou posterior.");

            if (IsVendorRunning())
            {
                Volatile.Write(ref _usbResetStatus, "Fechando o Jungle Leopard Display para liberar a porta...");
                Process[] vendorProcesses = GetVendorProcesses();
                foreach (Process vendorProcess in vendorProcesses)
                {
                    try { vendorProcess.CloseMainWindow(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
                DisposeProcesses(vendorProcesses);
                if (!await WaitForVendorExitAsync(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("O Jungle Leopard Display continua aberto. Feche-o pelo botao 'Fechar Jungle Leopard' e tente novamente.");
            }

            (stoppingSource, stoppingTask) = RequestStreamingStopForUsbReset();
            Volatile.Write(ref _usbResetStatus, $"Solicitando ao Windows o reinicio de {port} para destravar a comunicacao...");
            SetStatus($"Reiniciando somente o dispositivo USB do painel em {port}...");

            if (PanelPnpControl.IsCurrentProcessAdministrator())
            {
                Volatile.Write(ref _usbResetStatus, $"Desativando e reativando o dispositivo {port} pelo Windows...");
                await Task.Run(() => PanelPnpControl.RestartDevice(instanceId)).WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add("/restart-device");
                startInfo.ArgumentList.Add(instanceId);
                Volatile.Write(ref _usbResetStatus, "Aguardando a resposta do Windows para reiniciar o dispositivo...");
                await Task.Yield();
                using Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("O Windows nao iniciou a ferramenta de reinicio do dispositivo.");
                Volatile.Write(ref _usbResetStatus, "O Windows aceitou o pedido. Aguardando o dispositivo USB...");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                if (process.ExitCode == 3010)
                    rebootRequired = true;
                else if (process.ExitCode != 0)
                    throw new InvalidOperationException($"O Windows nao conseguiu reiniciar o dispositivo USB (codigo {process.ExitCode}).");
            }

            restartSucceeded = true;
            SetPanelConnectionError(null);
            Volatile.Write(ref _usbResetStatus, rebootRequired
                ? $"O Windows reiniciou {port}, mas informou que ha uma operacao pendente para reinicializacao. Testando a reconexao agora..."
                : $"Dispositivo USB reiniciado. Aguardando {port} reaparecer...");
            SetStatus(rebootRequired
                ? $"O Windows reportou reinicio pendente apos reiniciar {port}. Tentando reconectar sem reiniciar o computador..."
                : $"Dispositivo USB em {port} reiniciado. Aguardando a reconexao do painel...");

            if (stoppingTask is { IsCompleted: false })
            {
                Volatile.Write(ref _usbResetStatus, "USB reiniciado. Aguardando a transmissao antiga liberar a porta...");
                await stoppingTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            ReleaseCompletedStreamStop(stoppingSource, stoppingTask);
            await Task.Delay(2500);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
            or Win32Exception or TimeoutException or ArgumentException or SecurityException or PlatformNotSupportedException)
        {
            string message = $"Nao foi possivel reiniciar a conexao USB: {ex.Message}";
            SetPanelConnectionError(message);
            Volatile.Write(ref _usbResetStatus, message);
            SetStatus(message);
        }
        finally
        {
            ReleaseCompletedStreamStop(stoppingSource, stoppingTask);
            _deviceMaintenanceInProgress = false;
            Interlocked.Exchange(ref _panelUsbResetInProgress, 0);
            if (!_isShuttingDown)
            {
                RefreshPanelDetection();
                if (restartSucceeded && _jpegBytes is not null && _detectedPort is not null
                    && _streamTask is not { IsCompleted: false })
                    StartStreaming(automatic: true);
            }
        }
    }

    private void SubscribeSystemPlaybackEvents()
    {
        SystemEvents.SessionSwitch += SystemSessionSwitch;
        SystemEvents.PowerModeChanged += SystemPowerModeChanged;
    }

    private void UnsubscribeSystemPlaybackEvents()
    {
        SystemEvents.SessionSwitch -= SystemSessionSwitch;
        SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
    }

    private void SystemSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock))
            return;
        QueueSystemPlaybackStateChange(() =>
        {
            _sessionLocked = e.Reason == SessionSwitchReason.SessionLock;
            UpdateSystemPlaybackPauseState();
        });
    }

    private void SystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.Suspend or PowerModes.Resume))
            return;
        QueueSystemPlaybackStateChange(() =>
        {
            _systemSuspended = e.Mode == PowerModes.Suspend;
            UpdateSystemPlaybackPauseState();
        });
    }

    private void QueueSystemPlaybackStateChange(Action action)
    {
        if (_isShuttingDown || !HasStudioWindow)
            return;
        try
        {
            PostToUi((Action)(() =>
            {
                if (!_isShuttingDown)
                    action();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private bool IsSystemPlaybackBlocked()
        => !_keepPlayWhenScreenOff.Checked && (_sessionLocked || _systemSuspended);

    private void UpdateSystemPlaybackPauseState()
    {
        if (IsSystemPlaybackBlocked())
        {
            if (_systemPlaybackPaused)
                return;

            _systemPlaybackPaused = true;
            _systemResumeGeneration++;
            _resumePlaybackAfterSystemState = _streamTask is { IsCompleted: false };
            if (_desktopCaptureActive)
                _desktopCaptureTimer.Stop();
            if (_resumePlaybackAfterSystemState)
                _ = PauseStreamingForSystemStateAsync();
            return;
        }

        if (!_systemPlaybackPaused)
            return;

        _systemPlaybackPaused = false;
        int generation = ++_systemResumeGeneration;
        if (_resumePlaybackAfterSystemState)
            _ = ResumePlaybackAfterSystemStateAsync(generation);
    }

    private async Task PauseStreamingForSystemStateAsync()
    {
        CancellationTokenSource? source = _streamCts;
        Task? task = _streamTask;
        if (source is null)
            return;

        Volatile.Write(ref _motionBeforeOffScreenOnStop, 1);
        source.Cancel();
        _stopButton.Enabled = false;
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }

        if (ReferenceEquals(source, _streamCts))
        {
            _streamCts = null;
            _streamTask = null;
            source.Dispose();
        }
        if (!_isShuttingDown)
            SetStatus("LCD pausado durante o bloqueio/suspensão do Windows; a reprodução será retomada ao voltar.");
    }

    private async Task ResumePlaybackAfterSystemStateAsync(int generation)
    {
        await Task.Delay(1500);
        if (_isShuttingDown || generation != _systemResumeGeneration || IsSystemPlaybackBlocked())
            return;

        bool shouldResume = _resumePlaybackAfterSystemState;
        _resumePlaybackAfterSystemState = false;
        if (!shouldResume)
            return;

        try
        {
            if (_desktopCaptureActive)
            {
                CaptureDesktopFrame();
                _desktopCaptureTimer.Start();
            }
            RefreshPanelDetection();
            if (_jpegBytes is not null)
                StartStreaming(automatic: true);
        }
        catch (Exception ex)
        {
            SetStatus($"O Windows voltou, mas não foi possível retomar a imagem no LCD: {ex.Message}");
        }
    }

    private void StopStreamingForExit()
    {
        // Closing the app must release COM without sending commands that can
        // change the panel's current display state.
        Interlocked.Exchange(ref _motionBeforeOffScreenOnStop, 0);
        Interlocked.Exchange(ref _abortStreamSessionOnStop,
            _streamTask is { IsCompleted: false } ? 1 : 0);
        try
        {
            _streamCts?.Cancel();
            _streamTask?.Wait(TimeSpan.FromSeconds(12));
        }
        catch (AggregateException) { }
        catch (OperationCanceledException) { }
        _preview.Image?.Dispose();
        ClearActiveTheme();
    }

    private async Task StreamImageLoop(string[] ports, CancellationToken token)
    {
        string? failure = null;
        var connected = new List<(string Port, PanelConnection Device, string Description, PanelIdentity? Identity)>();
        var failedPorts = new List<string>();
        var lastBrightnessByPort = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string port in ports.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var device = new PanelConnection(port);
                PanelIdentity? identity = null;
                try
                {
                    device.Open();
                    device.CancelPendingIoOnCancellation(token,
                        () => _isShuttingDown || Volatile.Read(ref _abortStreamSessionOnStop) != 0);
                    token.ThrowIfCancellationRequested();
                    device.FlushImagePipeline(token);
                    token.ThrowIfCancellationRequested();
                    device.SendControl(0x06);
                    token.ThrowIfCancellationRequested();
                    byte[]? infoResponse = device.ReadControlResponse(TimeSpan.FromSeconds(2));
                    token.ThrowIfCancellationRequested();
                    string description = DescribePanelResponse(infoResponse, out identity);
                    if (identity is not null)
                        _panelIdentitiesByPort[port] = identity;
                    if (connected.Count == 0)
                    {
                        SetPanelIdentityFromWorker(identity);
                        SetPanelInfoDescriptionFromWorker(description);
                    }
                    if (identity is not null && Version.TryParse(identity.Version, out Version? firmwareVersion))
                    {
                        if (firmwareVersion >= new Version(2, 8))
                        {
                            token.ThrowIfCancellationRequested();
                            device.SendControl(0x15, [0, 3]);
                        }
                        if (firmwareVersion >= new Version(4, 1))
                        {
                            token.ThrowIfCancellationRequested();
                            device.SendControl(0x26, [10]);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    device.StartLive();
                    await Task.Delay(500, token).ConfigureAwait(false);
                    connected.Add((port, device, description, identity));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    device.Abort();
                    throw;
                }
                catch (Exception ex)
                {
                    device.Abort();
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);
                    failedPorts.Add($"{port}: {ex.Message}");
                }
            }

            if (connected.Count == 0)
                throw new IOException("Não foi possível abrir nenhum painel detectado. " + string.Join(" | ", failedPorts));

            SetPanelConnectionError(failedPorts.Count == 0 ? null : string.Join(" | ", failedPorts));
            _nextPanelReconnectAttemptUtc = DateTime.MinValue;

            if (!_isShuttingDown && HasStudioWindow)
            {
                try
                {
                    RunOnUiThread((Action)(() =>
                    {
                        if (connected[0].Identity is not null)
                            SetPanelIdentity(connected[0].Identity);
                        RefreshPreparedImage();
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }

            DateTime nextKeepAlive = DateTime.UtcNow.AddMilliseconds(1500);
            DateTime nextThemeFrame = DateTime.MinValue;
            var nextPanelFrame = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            bool firstFrame = true;
            while (!token.IsCancellationRequested)
            {
                DateTime now = DateTime.UtcNow;
                int brightness = CurrentBrightness;
                if (HasActiveTheme && now >= nextThemeFrame)
                {
                    // Rendering a theme also composites the scene, encodes a JPEG,
                    // and creates a preview image. Do not do that faster than the
                    // connected panels can receive the current frame over USB.
                    byte[]? previousImage = Volatile.Read(ref _jpegBytes);
                    Dictionary<string, byte[]> previousPanelFrames = Volatile.Read(ref _panelJpegFrames);
                    int renderInterval = connected.Max(panel =>
                    {
                        byte[]? panelImage = previousPanelFrames.TryGetValue(panel.Port, out byte[]? composedImage)
                            ? composedImage
                            : previousImage;
                        return PanelConnection.GetMinimumImageFrameIntervalMilliseconds(
                            panelImage?.Length ?? 0, GetPanelFrameRate(panel.Port));
                    });
                    nextThemeFrame = now.AddMilliseconds(Math.Max(1, renderInterval));
                    try
                    {
                        if (Volatile.Read(ref _multiScreenCompositionActive))
                        {
                            PanelCompositionOutput? composed = RenderActiveThemeComposition();
                            if (composed is not null)
                            {
                                SetCurrentPreparedImage(composed.Preview);
                                Volatile.Write(ref _panelJpegFrames, composed.Frames);
                                QueueLiveThemePreview(composed.Preview);
                            }
                        }
                        else
                        {
                            PanelCompositionOutput? mirroredFrames = connected.Count > 1
                                ? RenderMirroredThemeFrames(connected.Select(panel => panel.Port).ToArray())
                                : null;
                            PreparedImage? liveFrame = mirroredFrames?.Preview ?? RenderActiveThemeFrame();
                            if (liveFrame is not null)
                            {
                                Volatile.Write(ref _panelJpegFrames, mirroredFrames?.Frames
                                    ?? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
                                SetCurrentPreparedImage(liveFrame);
                                QueueLiveThemePreview(liveFrame);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SetStatusFromWorker($"Falha ao atualizar a cena dinâmica: {ex.Message}");
                    }
                }

                byte[]? image = Volatile.Read(ref _jpegBytes);
                Dictionary<string, byte[]> perPanelFrames = Volatile.Read(ref _panelJpegFrames);
                if (image is not null)
                {
                    bool keepAliveDue = now >= nextKeepAlive;
                    var sendErrors = new ConcurrentQueue<(string Port, Exception Error)>();
                    Parallel.ForEach(connected.ToArray(), panel =>
                    {
                        try
                        {
                            if (!lastBrightnessByPort.TryGetValue(panel.Port, out int lastBrightness)
                                || brightness != lastBrightness)
                            {
                                panel.Device.SendControl(0x03, [(byte)brightness]);
                                lastBrightnessByPort[panel.Port] = brightness;
                            }
                            if (keepAliveDue)
                                panel.Device.StartLive();
                            if (nextPanelFrame.TryGetValue(panel.Port, out DateTime nextFrameTime) && now < nextFrameTime)
                                return;
                            byte[] panelImage = perPanelFrames.TryGetValue(panel.Port, out byte[]? composedImage)
                                ? composedImage
                                : image;
                            int minimumFrameInterval = PanelConnection.GetMinimumImageFrameIntervalMilliseconds(
                                panelImage.Length, GetPanelFrameRate(panel.Port));
                            DateTime frameStarted = DateTime.UtcNow;
                            panel.Device.SendImage(panelImage);
                            double remainingInterval = Math.Max(0,
                                minimumFrameInterval - (DateTime.UtcNow - frameStarted).TotalMilliseconds);
                            nextPanelFrame[panel.Port] = DateTime.UtcNow.AddMilliseconds(remainingInterval);
                        }
                        catch (Exception ex)
                        {
                            sendErrors.Enqueue((panel.Port, ex));
                        }
                    });
                    while (sendErrors.TryDequeue(out var sendError))
                    {
                        int failedIndex = connected.FindIndex(panel => string.Equals(panel.Port, sendError.Port, StringComparison.OrdinalIgnoreCase));
                        if (failedIndex < 0) continue;
                        // A timed-out write leaves the serial queue in an unknown state.
                        // Drop the handle without sending more protocol bytes through it.
                        connected[failedIndex].Device.Abort();
                        connected.RemoveAt(failedIndex);
                        failedPorts.Add($"{sendError.Port}: {sendError.Error.Message}");
                    }

                    token.ThrowIfCancellationRequested();

                    if (failedPorts.Count > 0)
                        SetPanelConnectionError(string.Join(" | ", failedPorts));

                    if (connected.Count == 0)
                        throw new IOException("Todos os painéis perderam a conexão. " + string.Join(" | ", failedPorts));
                    if (keepAliveDue)
                        nextKeepAlive = now.AddMilliseconds(1500);

                    if (firstFrame)
                    {
                        string panelList = string.Join(", ", connected.Select(panel => panel.Port));
                        string unavailable = failedPorts.Count == 0 ? string.Empty : " Falhas: " + string.Join(" | ", failedPorts);
                        SetStatusFromWorker($"{panelList}: {image.Length / 1024.0:F1} KB enviados por USB. O protocolo não confirma cada desenho.{unavailable}");
                        firstFrame = false;
                    }
                }

                await Task.Delay(StreamLoopIntervalMilliseconds, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = $"Falha na transmissão: {ex.Message}";
        }
        finally
        {
            bool setMotionBeforeOffScreen = Interlocked.Exchange(ref _motionBeforeOffScreenOnStop, 0) != 0;
            bool skipLiveEndMarker = Interlocked.Exchange(ref _skipLiveEndMarkerOnNextStop, 0) != 0;
            bool abortStreamSession = _isShuttingDown
                || Interlocked.Exchange(ref _abortStreamSessionOnStop, 0) != 0;

            if (abortStreamSession)
            {
                foreach (var panel in connected)
                    panel.Device.Abort();
            }
            else
            {
                foreach (var panel in connected)
                {
                    try { panel.Device.StopLive(sendEndMarker: !skipLiveEndMarker); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
                    {
                        failure ??= $"Falha ao encerrar a imagem em {panel.Port}: {ex.Message}";
                        panel.Device.Abort();
                    }
                }

                if (setMotionBeforeOffScreen && connected.Count > 0)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    foreach (var panel in connected)
                    {
                        if (!panel.Device.IsOpen) continue;
                        try { panel.Device.SendControl(0x14, [0, 0]); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
                        {
                            failure ??= $"Falha ao solicitar o modo de espera em {panel.Port}: {ex.Message}";
                            panel.Device.Abort();
                        }
                    }
                }

                foreach (var panel in connected)
                {
                    try
                    {
                        if (panel.Device.IsOpen)
                            ClosePanelSessionIfSupported(panel.Device, panel.Identity);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
                    {
                        failure ??= $"Falha ao fechar a sessao do painel em {panel.Port}: {ex.Message}";
                        panel.Device.Abort();
                    }
                    finally { panel.Device.Dispose(); }
                }
            }

            if (failure is not null)
            {
                SetPanelConnectionError(failure);
                SchedulePanelReconnect();
            }
            SetStatusFromWorker(failure ?? "Transmissao encerrada; as portas seriais foram liberadas.");
        }
    }
    private static string DescribePanelResponse(byte[]? frame, out PanelIdentity? identity)
    {
        identity = null;
        if (frame is null)
            return "A porta serial abriu, mas o painel não respondeu à consulta de identificação.";

        if (frame.Length < 7 || frame[0] != 0x55 || frame[1] != 0xAA)
            return "O painel enviou uma resposta serial fora do formato esperado.";

        uint sum = 0;
        for (int i = 0; i < frame.Length - 2; i++)
            sum += frame[i];
        ushort expectedChecksum = (ushort)(sum & 0xFFFF);
        ushort receivedChecksum = (ushort)(frame[^2] | (frame[^1] << 8));
        if (expectedChecksum != receivedChecksum)
            return "O painel respondeu, mas o checksum da resposta não confere.";

        int payloadLength = frame.Length - 7;
        if (payloadLength <= 0)
            return "O painel respondeu à consulta, sem informações de identificação.";

        try
        {
            using JsonDocument document = JsonDocument.Parse(frame.AsMemory(5, payloadLength));
            JsonElement data = document.RootElement;
            if (data.TryGetProperty("data", out JsonElement nestedData) && nestedData.ValueKind == JsonValueKind.Object)
                data = nestedData;

            string model = ReadJsonString(data, "model");
            string version = ReadJsonString(data, "version");
            string uid = ReadJsonString(data, "uid");
            int width = ReadPanelDimension(data, "width", PanelWidth);
            int height = ReadPanelDimension(data, "height", PanelHeight);
            int angle = ReadPanelAngle(data);
            if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(version))
                identity = new PanelIdentity(model, version, uid, width, height, angle);
            string brightness = ReadJsonValue(data, "brightness");
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(model)) details.Add(model);
            if (!string.IsNullOrWhiteSpace(version)) details.Add($"firmware {version}");
            if (width > 0 && height > 0) details.Add($"{width} × {height}");
            if (!string.IsNullOrWhiteSpace(brightness)) details.Add($"brilho {brightness}%");
            string suffix = details.Count == 0 ? "" : $" ({string.Join(", ", details)})";
            return $"Painel respondeu à consulta{suffix}.";
        }
        catch (JsonException)
        {
            return "O painel respondeu à consulta, mas os dados não vieram em JSON reconhecível.";
        }
    }

    private static string ReadJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static string ReadJsonValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";
        return value.ToString();
    }

    private static int ReadPanelDimension(JsonElement data, string propertyName, int fallback)
    {
        string value = ReadJsonValue(data, propertyName);
        return int.TryParse(value, out int dimension) && dimension is >= 16 and <= 8192 ? dimension : fallback;
    }

    private static int ReadPanelAngle(JsonElement data)
    {
        string value = ReadJsonValue(data, "angle");
        return int.TryParse(value, out int angle) ? ((angle % 360) + 360) % 360 : 0;
    }

    private void SetStatusFromWorker(string message)
    {
        if (_isShuttingDown || !HasStudioWindow)
            return;
        try { PostToUi((Action)(() => SetStatus(message))); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void SetPanelConnectionError(string? message)
    {
        string? error = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (error?.Length > 280)
            error = error[..277] + "…";
        Volatile.Write(ref _panelConnectionError, error);
    }

    private void SetStatus(string message) => _status.Text = message;

    private bool IsSteamCoverVisual => _selectedImageName?.EndsWith(" (Steam)", StringComparison.OrdinalIgnoreCase) == true;

    private PreparedImage PrepareImage(Image source, ImageRotation rotation, ImageFitMode fitMode, double brightness = 100,
        long minimumQuality = 18, Size? nativePanelSize = null, int? maximumJpegBytes = null, bool blackContainBackground = false)
    {
        Size displaySize = GetDisplaySize(rotation, nativePanelSize ?? GetNativePanelSize());
        int jpegLimit = maximumJpegBytes ?? GetPanelJpegMaxBytes();
        using var displayCanvas = new Bitmap(displaySize.Width, displaySize.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(displayCanvas))
        {
            graphics.Clear(Color.Black);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle destination;
            if (fitMode == ImageFitMode.Stretch)
            {
                destination = new Rectangle(Point.Empty, displaySize);
            }
            else
            {
                double widthScale = (double)displaySize.Width / source.Width;
                double heightScale = (double)displaySize.Height / source.Height;
                double scale = fitMode == ImageFitMode.Cover
                    ? Math.Max(widthScale, heightScale)
                    : Math.Min(widthScale, heightScale);
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                (int left, int top) = GetAlignedImageOffset(displaySize, width, height, rotation);
                destination = new Rectangle(left, top, width, height);

                if (fitMode == ImageFitMode.Contain && !blackContainBackground
                    && (width < displaySize.Width || height < displaySize.Height))
                    DrawSolidEdgeExtensions(graphics, source, destination, displaySize);
            }

            graphics.DrawImage(source, destination);
            int darkness = (int)Math.Round((100 - Math.Clamp(brightness, 0, 100)) * 255 / 100);
            if (darkness > 0)
            {
                using var shade = new SolidBrush(Color.FromArgb(darkness, Color.Black));
                graphics.FillRectangle(shade, new Rectangle(Point.Empty, displaySize));
            }
        }

        using var nativeCanvas = new Bitmap(displayCanvas);
        nativeCanvas.RotateFlip(GetRotationTransform(rotation, inverse: true));

        ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders().First(codec => codec.MimeType == "image/jpeg");
        for (long quality = 88; quality >= Math.Clamp(minimumQuality, 1, 88); quality -= 5)
        {
            using var output = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            nativeCanvas.Save(output, jpegCodec, parameters);
            byte[] bytes = output.ToArray();
            if (!IsBaseline420Jpeg(bytes))
                throw new InvalidOperationException("O codificador JPEG do Windows não gerou o formato 4:2:0 esperado pelo painel.");
            if (bytes.Length <= jpegLimit)
            {
                using var jpegStream = new MemoryStream(bytes, writable: false);
                using var decodedFrame = Image.FromStream(jpegStream);
                using var displayPreview = new Bitmap(decodedFrame);
                displayPreview.RotateFlip(GetRotationTransform(rotation, inverse: false));
                using var previewStream = new MemoryStream();
                displayPreview.Save(previewStream, ImageFormat.Png);
                return new PreparedImage(bytes, previewStream.ToArray(), displaySize, quality);
            }
        }

        throw new InvalidOperationException($"Esta imagem ainda excede {Math.Max(1, jpegLimit / 1024 - 4)} KB mesmo com compressão alta. Tente uma imagem com menos detalhes ou ruído.");
    }

    private static (int Left, int Top) GetAlignedImageOffset(Size displaySize, int imageWidth, int imageHeight, ImageRotation rotation)
    {
        int centeredLeft = (displaySize.Width - imageWidth) / 2;
        int centeredTop = (displaySize.Height - imageHeight) / 2;
        return rotation switch
        {
            ImageRotation.None => (0, centeredTop),
            ImageRotation.Clockwise90 => (centeredLeft, 0),
            ImageRotation.HalfTurn => (displaySize.Width - imageWidth, centeredTop),
            ImageRotation.Clockwise270 => (centeredLeft, displaySize.Height - imageHeight),
            _ => (centeredLeft, centeredTop)
        };
    }

    private static void DrawSolidEdgeExtensions(Graphics graphics, Image source, Rectangle imageBounds, Size canvasSize)
    {
        using var sharpImage = new Bitmap(canvasSize.Width, canvasSize.Height, PixelFormat.Format24bppRgb);
        using (Graphics sharpGraphics = Graphics.FromImage(sharpImage))
        {
            sharpGraphics.Clear(Color.Black);
            sharpGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            sharpGraphics.DrawImage(source, imageBounds);
        }

        Rectangle content = Rectangle.Intersect(imageBounds, new Rectangle(Point.Empty, canvasSize));
        if (content.Width <= 0 || content.Height <= 0)
            return;

        int stripThickness = Math.Clamp(Math.Min(content.Width, content.Height) / 20, 4, 32);
        if (content.Left > 0)
        {
            var gap = new Rectangle(0, 0, content.Left, canvasSize.Height);
            var edge = new Rectangle(content.Left, content.Top, Math.Min(stripThickness, content.Width), content.Height);
            FillEdgeColor(graphics, sharpImage, edge, gap);
        }
        if (content.Right < canvasSize.Width)
        {
            var gap = new Rectangle(content.Right, 0, canvasSize.Width - content.Right, canvasSize.Height);
            var edge = new Rectangle(content.Right - Math.Min(stripThickness, content.Width), content.Top,
                Math.Min(stripThickness, content.Width), content.Height);
            FillEdgeColor(graphics, sharpImage, edge, gap);
        }
        if (content.Top > 0)
        {
            var gap = new Rectangle(0, 0, canvasSize.Width, content.Top);
            var edge = new Rectangle(content.Left, content.Top, content.Width, Math.Min(stripThickness, content.Height));
            FillEdgeColor(graphics, sharpImage, edge, gap);
        }
        if (content.Bottom < canvasSize.Height)
        {
            var gap = new Rectangle(0, content.Bottom, canvasSize.Width, canvasSize.Height - content.Bottom);
            var edge = new Rectangle(content.Left, content.Bottom - Math.Min(stripThickness, content.Height), content.Width,
                Math.Min(stripThickness, content.Height));
            FillEdgeColor(graphics, sharpImage, edge, gap);
        }
    }

    private static void FillEdgeColor(Graphics graphics, Bitmap image, Rectangle edgeBounds, Rectangle gapBounds)
    {
        if (edgeBounds.Width <= 0 || edgeBounds.Height <= 0 || gapBounds.Width <= 0 || gapBounds.Height <= 0)
            return;

        Rectangle sampleBounds = Rectangle.Intersect(edgeBounds, new Rectangle(Point.Empty, image.Size));
        if (sampleBounds.Width <= 0 || sampleBounds.Height <= 0)
            return;

        // Quantize RGB into 4-bit buckets so a frequently occurring edge color
        // wins over isolated highlights, while retaining the bucket's average.
        var counts = new int[4096];
        var redSums = new long[4096];
        var greenSums = new long[4096];
        var blueSums = new long[4096];
        for (int y = sampleBounds.Top; y < sampleBounds.Bottom; y++)
        {
            for (int x = sampleBounds.Left; x < sampleBounds.Right; x++)
            {
                Color pixel = image.GetPixel(x, y);
                int bucket = ((pixel.R >> 4) << 8) | ((pixel.G >> 4) << 4) | (pixel.B >> 4);
                counts[bucket]++;
                redSums[bucket] += pixel.R;
                greenSums[bucket] += pixel.G;
                blueSums[bucket] += pixel.B;
            }
        }

        int dominantBucket = 0;
        for (int bucket = 1; bucket < counts.Length; bucket++)
        {
            if (counts[bucket] > counts[dominantBucket])
                dominantBucket = bucket;
        }

        int sampleCount = counts[dominantBucket];
        Color fill = Color.FromArgb(
            (int)(redSums[dominantBucket] / sampleCount),
            (int)(greenSums[dominantBucket] / sampleCount),
            (int)(blueSums[dominantBucket] / sampleCount));
        using var brush = new SolidBrush(fill);
        graphics.FillRectangle(brush, gapBounds);
    }

    private static RotateFlipType GetRotationTransform(ImageRotation rotation, bool inverse) => (rotation, inverse) switch
    {
        (ImageRotation.Clockwise90, false) => RotateFlipType.Rotate90FlipNone,
        (ImageRotation.Clockwise90, true) => RotateFlipType.Rotate270FlipNone,
        (ImageRotation.HalfTurn, _) => RotateFlipType.Rotate180FlipNone,
        (ImageRotation.Clockwise270, false) => RotateFlipType.Rotate270FlipNone,
        (ImageRotation.Clockwise270, true) => RotateFlipType.Rotate90FlipNone,
        _ => RotateFlipType.RotateNoneFlipNone
    };

    private static bool IsBaseline420Jpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return false;

        int index = 2;
        while (index < bytes.Length - 8)
        {
            if (bytes[index++] != 0xFF)
                continue;
            while (index < bytes.Length && bytes[index] == 0xFF)
                index++;
            if (index >= bytes.Length)
                return false;

            byte marker = bytes[index++];
            if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7)
                continue;
            if (index + 1 >= bytes.Length)
                return false;

            int segmentLength = (bytes[index] << 8) | bytes[index + 1];
            if (segmentLength < 2 || index + segmentLength > bytes.Length)
                return false;

            if (marker == 0xC0)
            {
                int components = bytes[index + 7];
                if (components != 3 || segmentLength < 17)
                    return false;
                int firstComponent = index + 8;
                return bytes[firstComponent + 1] == 0x22
                    && bytes[firstComponent + 4] == 0x11
                    && bytes[firstComponent + 7] == 0x11;
            }

            if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
                return false;
            index += segmentLength;
        }
        return false;
    }

    private static Process[] GetVendorProcesses() => Process.GetProcessesByName(VendorProcessName);

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
            process.Dispose();
    }

    private static bool IsVendorRunning()
    {
        Process[] processes = GetVendorProcesses();
        try { return processes.Any(process => { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }); }
        finally { DisposeProcesses(processes); }
    }

    private static async Task<bool> WaitForVendorExitAsync(TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (!IsVendorRunning())
                return true;
            await Task.Delay(250);
        }
        return !IsVendorRunning();
    }

    private static async Task<bool> WaitForVendorStartAsync(TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (IsVendorRunning())
                return true;
            await Task.Delay(250);
        }
        return IsVendorRunning();
    }

    private static async Task<bool> WaitForVendorReadyAsync(TimeSpan startupTimeout, TimeSpan stablePeriod)
    {
        if (!await WaitForVendorStartAsync(startupTimeout))
            return false;

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < stablePeriod)
        {
            if (!IsVendorRunning())
                return false;
            await Task.Delay(250);
        }
        return IsVendorRunning();
    }

    private static async Task<bool> WaitForVendorWindowAsync(TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            Process[] processes = GetVendorProcesses();
            try
            {
                if (processes.Any(process =>
                {
                    try { return !process.HasExited && process.MainWindowHandle != IntPtr.Zero; }
                    catch (InvalidOperationException) { return false; }
                    catch (Win32Exception) { return false; }
                }))
                    return true;
            }
            finally { DisposeProcesses(processes); }

            await Task.Delay(250);
        }
        return false;
    }

    private static string? FindVendorExecutable()
    {
        try
        {
            using var uninstallRoot = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallRoot is not null)
            {
                foreach (string keyName in uninstallRoot.GetSubKeyNames())
                {
                    using var key = uninstallRoot.OpenSubKey(keyName);
                    string? displayName = key?.GetValue("DisplayName") as string;
                    if (displayName?.StartsWith(VendorProcessName, StringComparison.OrdinalIgnoreCase) != true)
                        continue;

                    string? icon = key?.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(icon))
                    {
                        string candidate = icon.Split(',')[0].Trim().Trim('"');
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
        }
        catch (SecurityException) { }
        catch (UnauthorizedAccessException) { }

        string conventionalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), VendorProcessName, VendorProcessName + ".exe");
        return File.Exists(conventionalPath) ? conventionalPath : null;
    }

    private sealed record PanelIdentity(string Model, string Version, string Uid, int Width, int Height, int Angle)
    {
        public Size NativeSize => new(Width, Height);
    }

    private sealed record SensorSelectionSpec(string Group, string Label, string Key, string Device, string SensorType);

    private sealed record SensorSelectionChoice(string? Name, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CaptureDisplayChoice(string DeviceName, Rectangle Bounds)
    {
        public override string ToString() => $"{DeviceName}  ·  {Bounds.Width} × {Bounds.Height}";
    }

    private sealed record SavedSettings(
        int RotationIndex,
        bool? AutoSteamCover = null,
        string? TemperatureUnit = "C",
        bool StartHidden = false,
        int Brightness = 100,
        string RenderMode = "highPerformance",
        bool DisableDiskSensorScan = false,
        int DiskSensorScanIntervalMs = 2000,
        string? LastImagePath = null,
        int FitModeIndex = 0,
        string[]? PanelOrder = null,
        Dictionary<string, PanelCropSettings>? PanelCrops = null,
        int? CompositionLayoutIndex = null,
        string? FfmpegPath = null,
        bool KeepPlayWhenScreenOff = false,
        Dictionary<string, string>? SensorSelections = null);

    private sealed record PanelCropSettings(bool Enabled, int X, int Y, int Width, int Height, int RotationIndex = -1,
        int? LayoutX = null, int? LayoutY = null)
    {
        public static PanelCropSettings Default { get; } = new(false, 0, 0, 100, 100, -1, null, null);
    }

    private sealed class ScreenPlaceholderPreview
    {
        public int RotationDegrees { get; set; }
        public bool Visible { get; set; } = true;
        public void Invalidate() { }

        public Bitmap CreateSourceBitmap(Size displaySize)
        {
            int width = Math.Max(1, (int)displaySize.Width);
            int height = Math.Max(1, (int)displaySize.Height);
            float scale = Math.Max(0.25f, Math.Min(width, height) / (float)PanelHeight);
            float margin = 16f * scale;
            var canonical = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics canvas = Graphics.FromImage(canonical);
            using var background = new SolidBrush(Color.FromArgb(35, 35, 39));
            using var border = new Pen(Color.FromArgb(76, 76, 83), 3f);
            using var guide = new Pen(Color.FromArgb(64, 64, 70), 2f);
            using var accent = new SolidBrush(Color.FromArgb(122, 146, 255));
            using var primary = new SolidBrush(Color.FromArgb(245, 243, 248));
            using var secondary = new SolidBrush(Color.FromArgb(184, 181, 190));
            using var titleFont = new Font("Segoe UI Semibold", 30f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", 18f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var arrowFont = new Font("Segoe UI Symbol", 48f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.Clear(Color.FromArgb(30, 29, 33));
            var screen = new RectangleF(margin, margin, Math.Max(1, width - margin * 2), Math.Max(1, height - margin * 2));
            canvas.FillRectangle(background, screen);
            canvas.DrawRectangle(border, screen.X, screen.Y, screen.Width, screen.Height);
            canvas.DrawLine(guide, screen.Left, screen.Top + 78f * scale, screen.Right, screen.Top + 78f * scale);
            canvas.DrawString("↑", arrowFont, accent, new RectangleF(0f, margin, width, 54f * scale), centered);
            canvas.DrawString("TOPO DO PAINEL", smallFont, accent,
                new RectangleF(0f, margin + 48f * scale, width, 32f * scale), centered);

            float centerX = width / 2f;
            float centerY = height * 0.54f;
            float horizontalInset = Math.Min(170f * scale, screen.Width * 0.34f);
            float verticalHalf = Math.Min(55f * scale, screen.Height * 0.08f);
            float dotSize = 12f * scale;
            canvas.DrawLine(guide, screen.Left + horizontalInset, centerY, screen.Right - horizontalInset, centerY);
            canvas.DrawLine(guide, centerX, centerY - verticalHalf, centerX, centerY + verticalHalf);
            canvas.FillEllipse(accent, centerX - dotSize / 2f, centerY - dotSize / 2f, dotSize, dotSize);
            canvas.DrawString("SEM IMAGEM", titleFont, primary,
                new RectangleF(screen.Left + 10f * scale, centerY + 48f * scale, screen.Width - 20f * scale, 48f * scale), centered);
            canvas.DrawString($"{width} × {height}  •  AGUARDANDO IMAGEM", smallFont, secondary,
                new RectangleF(0f, height - margin - 32f * scale, width, 32f * scale), centered);
            return canonical;
        }
    }

    private sealed record PreparedImage(byte[] Bytes, byte[] PreviewBytes, Size DisplaySize, long Quality);

    private sealed record PanelCompositionOutput(PreparedImage Preview, Dictionary<string, byte[]> Frames);

    private enum ImageRotation
    {
        None,
        Clockwise90,
        HalfTurn,
        Clockwise270
    }

    private enum ImageFitMode
    {
        Contain,
        Cover,
        Stretch
    }
}

internal static class PanelPnpControl
{
    private const uint CrSuccess = 0;
    private const uint DeviceStarted = 0x00000008;

    internal static bool IsCurrentProcessAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static void RestartDevice(string instanceId)
    {
        uint result = CM_Locate_DevNodeW(out uint devInst, instanceId, 0);
        EnsureSuccess(result, "Localizar o dispositivo USB");

        result = CM_Disable_DevNode(devInst, 0);
        EnsureSuccess(result, "Desativar o dispositivo USB");

        try
        {
            Thread.Sleep(700);
        }
        finally
        {
            uint enableResult = CM_Enable_DevNode(devInst, 0);
            EnsureSuccess(enableResult, "Reativar o dispositivo USB");
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            uint locateResult = CM_Locate_DevNodeW(out uint currentDevInst, instanceId, 0);
            if (locateResult == CrSuccess)
                devInst = currentDevInst;

            uint statusResult = CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
            if (statusResult == CrSuccess && problem == 0 && (status & DeviceStarted) != 0)
                return;

            Thread.Sleep(250);
        }

        throw new TimeoutException("O Windows nao iniciou novamente o dispositivo USB em 15 segundos.");
    }

    private static void EnsureSuccess(uint result, string operation)
    {
        if (result == CrSuccess)
            return;

        if (result == 0x33)
            throw new UnauthorizedAccessException($"O Windows negou permissao para {operation}. Abra o aplicativo como administrador.");

        uint win32Error = CM_MapCrToWin32Err(result, 0);
        string detail = win32Error == 0 ? string.Empty : $": {new Win32Exception((int)win32Error).Message}";
        throw new IOException($"{operation} falhou (codigo PnP {result}){detail}.");
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Disable_DevNode")]
    private static extern uint CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Enable_DevNode")]
    private static extern uint CM_Enable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_MapCrToWin32Err")]
    private static extern uint CM_MapCrToWin32Err(uint configRet, uint defaultError);
}

internal sealed class PanelConnection : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint PanelBaudRate = 2_000_000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint PurgeRxClear = 0x0008;
    private const uint PurgeTxClear = 0x0004;
    private const uint PurgeRxAbort = 0x0002;
    private const uint PurgeTxAbort = 0x0001;
    private const uint BinaryFlag = 1;
    private const uint ParityFlag = 1 << 1;
    private const uint OutCtsFlowFlag = 1 << 2;
    private const uint OutDsrFlowFlag = 1 << 3;
    private const uint DtrControlMask = 3 << 4;
    private const uint OutXFlag = 1 << 8;
    private const uint InXFlag = 1 << 9;
    private const uint RtsControlMask = 3 << 12;
    private const uint DtrEnable = 1 << 4;
    private const uint RtsEnable = 1 << 12;

    private readonly string _portName;
    private SafeFileHandle? _handle;
    private FileStream? _stream;
    private bool _liveMode;
    private CancellationTokenRegistration _cancelOnRequestedAbort;

    public PanelConnection(string portName) => _portName = portName;
    public bool IsOpen => _stream is not null;

    public static int GetMinimumImageFrameIntervalMilliseconds(int jpegBytes, int targetFrameRate)
    {
        double targetInterval = 1000d / Math.Max(1, targetFrameRate);
        double serialTransferInterval = (jpegBytes + 6) * 10_000d / PanelBaudRate * 1.25;
        return (int)Math.Ceiling(Math.Max(targetInterval, serialTransferInterval));
    }

    public void Open()
    {
        string path = @"\\.\" + _portName;
        _handle = CreateFile(path, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            string detail = new Win32Exception(error).Message;
            throw new IOException($"Não foi possível abrir {_portName} (erro do Windows {error}: {detail}). Outro programa pode estar usando a tela.");
        }

        var dcb = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(_handle, ref dcb))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui ler a configuração da porta serial.");

        dcb.BaudRate = PanelBaudRate;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;
        dcb.Flags = (dcb.Flags | BinaryFlag | DtrEnable | RtsEnable)
            & ~(ParityFlag | OutCtsFlowFlag | OutDsrFlowFlag | DtrControlMask | OutXFlag | InXFlag | RtsControlMask);
        dcb.Flags |= DtrEnable | RtsEnable;

        if (!SetCommState(_handle, ref dcb))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui configurar a porta serial.");

        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = uint.MaxValue,
            ReadTotalTimeoutMultiplier = 0,
            ReadTotalTimeoutConstant = 0,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = 10000
        };
        if (!SetCommTimeouts(_handle, ref timeouts))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui configurar o tempo limite da porta serial.");

        // Clear pending serial I/O on every open so a previous interrupted
        // session cannot leave stale bytes in the device queues.
        if (!PurgeComm(_handle, PurgeRxAbort | PurgeRxClear | PurgeTxAbort | PurgeTxClear))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"NÃ£o consegui limpar a fila serial de {_portName}.");
        _stream = new FileStream(_handle, FileAccess.ReadWrite, 4096, isAsync: false);
    }

    public void CancelPendingIoOnCancellation(CancellationToken token, Func<bool> shouldAbort)
    {
        _cancelOnRequestedAbort = token.Register(() =>
        {
            if (!shouldAbort()) return;
            SafeFileHandle? handle = Volatile.Read(ref _handle);
            if (handle is not null && !handle.IsInvalid && !handle.IsClosed)
                CancelIoEx(handle, IntPtr.Zero);
        });
    }

    public void FlushImagePipeline(CancellationToken cancellationToken = default)
    {
        DelayWithCancellation(500, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Write(new byte[] { 0xFF, 0xD9, 0xFF, 0xD9 });
        DelayWithCancellation(60, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Write(new byte[4]);
        DelayWithCancellation(200, cancellationToken);
    }

    private static void DelayWithCancellation(int milliseconds, CancellationToken token)
    {
        if (token.CanBeCanceled)
        {
            if (token.WaitHandle.WaitOne(milliseconds))
                token.ThrowIfCancellationRequested();
        }
        else
        {
            Thread.Sleep(milliseconds);
        }
    }

    public void StopLive(bool sendEndMarker = true)
    {
        if (_stream is null || !_liveMode)
            return;
        if (sendEndMarker)
            SendLiveEndMarker();
        else
            _liveMode = false;
    }

    public void SendLiveEndMarker()
    {
        if (_stream is null)
            throw new InvalidOperationException("A porta serial não está aberta.");
        Write(new byte[] { 0xFF, 0xD9, 0xFF, 0xD9 });
        _liveMode = false;
    }

    public void StartLive()
    {
        SendControl(0x11);
        _liveMode = true;
    }

    public void SendControl(byte command, byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        var frame = new byte[payload.Length + 7];
        frame[0] = 0x55;
        frame[1] = 0xAA;
        frame[2] = (byte)(frame.Length & 0xFF);
        frame[3] = (byte)((frame.Length >> 8) & 0xFF);
        frame[4] = command;
        Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
        WriteChecksum(frame, frame.Length - 2);
        Write(frame);
    }

    public void SendControlAndDrain(byte command, byte[]? payload = null)
    {
        SendControl(command, payload);
        if (_handle is null || !FlushFileBuffers(_handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível aguardar o envio do comando pela porta serial.");
    }

    public void SendRaw(ReadOnlySpan<byte> bytes)
    {
        if (_stream is null)
            throw new InvalidOperationException("A porta serial não está aberta.");
        _stream.Write(bytes);
        _stream.Flush();
    }

    public void SendRawAndDrain(ReadOnlySpan<byte> bytes)
    {
        SendRaw(bytes);
        if (_handle is null || !FlushFileBuffers(_handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível aguardar o envio dos dados pela porta serial.");
    }

    public byte[]? ReadControlResponse(TimeSpan timeout)
    {
        if (_handle is null || _stream is null)
            throw new InvalidOperationException("A porta serial não está aberta.");

        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = uint.MaxValue,
            ReadTotalTimeoutMultiplier = 0,
            ReadTotalTimeoutConstant = 50,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = 10000
        };
        if (!SetCommTimeouts(_handle, ref timeouts))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui configurar a leitura da resposta do painel.");

        var received = new List<byte>();
        var chunk = new byte[512];
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            int count = _stream.Read(chunk, 0, chunk.Length);
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                    received.Add(chunk[i]);

                byte[]? frame = ExtractControlFrame(received);
                if (frame is not null)
                    return frame;
            }
        }

        return null;
    }

    private static byte[]? ExtractControlFrame(List<byte> received)
    {
        for (int start = 0; start + 1 < received.Count; start++)
        {
            if (received[start] != 0x55 || received[start + 1] != 0xAA)
                continue;
            if (start + 4 > received.Count)
                return null;

            int length = received[start + 2] | (received[start + 3] << 8);
            if (length < 7 || length > 65536)
                continue;
            if (start + length > received.Count)
                return null;

            return received.GetRange(start, length).ToArray();
        }

        if (received.Count > 1)
        {
            byte last = received[^1];
            received.Clear();
            if (last == 0x55)
                received.Add(last);
        }
        return null;
    }

    public void SendImage(byte[] jpeg)
    {
        var frame = new byte[jpeg.Length + 6];
        frame[0] = (byte)(jpeg.Length & 0xFF);
        frame[1] = (byte)((jpeg.Length >> 8) & 0xFF);
        frame[2] = (byte)((jpeg.Length >> 16) & 0xFF);
        frame[3] = (byte)((jpeg.Length >> 24) & 0xFF);
        Buffer.BlockCopy(jpeg, 0, frame, 4, jpeg.Length);
        WriteChecksum(frame, frame.Length - 2);
        Write(frame);
    }

    private static void WriteChecksum(byte[] frame, int checksumOffset)
    {
        uint sum = 0;
        for (int i = 0; i < checksumOffset; i++)
            sum += frame[i];
        ushort checksum = (ushort)(sum & 0xFFFF);
        frame[checksumOffset] = (byte)(checksum & 0xFF);
        frame[checksumOffset + 1] = (byte)(checksum >> 8);
    }

    private void Write(byte[] bytes)
    {
        if (_stream is null)
            throw new InvalidOperationException("A porta serial não está aberta.");
        _stream.Write(bytes, 0, bytes.Length);
        // FileStream buffers small control frames by default. Push each protocol
        // stage to the serial driver before its timing delay or response read.
        _stream.Flush();
    }

    public void Abort()
    {
        _liveMode = false;
        DisposeTransport();
    }

    public void Dispose()
    {
        try { StopLive(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception) { }
        DisposeTransport();
    }

    private void DisposeTransport()
    {
        _cancelOnRequestedAbort.Dispose();
        try { _stream?.Dispose(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception) { }
        finally
        {
            _stream = null;
            try { _handle?.Dispose(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception) { }
            finally { _handle = null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLimit;
        public ushort XoffLimit;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public sbyte EvtChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle handle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle handle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(SafeFileHandle handle, ref CommTimeouts timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PurgeComm(SafeFileHandle handle, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
}

