using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Panel = System.Windows.Controls.Panel;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace JungleLeopardPoc;

public partial class LcdStudioWindow : Window
{
    private readonly LcdStudioBackend _controller;
    private readonly Dictionary<string, UIElement> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _placementSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly TextBlock _sidebarStatus = new();
    private readonly Dictionary<string, Button> _navigationButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<string> _panelItems = [];
    private string _panelCanvasSignature = string.Empty;
    private readonly Dictionary<string, TextBlock> _sensorValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _deviceValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _sensorSourceSelectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _panelList = new();
    private TextBlock? _multiScreenStatus;
    private TextBlock? _multiCompositionStatus;
    private Button? _startCaptureButton;
    private Button? _stopCaptureButton;
    private ComboBox? _compositionLayout;
    private ComboBox? _desktopDisplay;
    private CheckBox? _includeDesktopCursor;
    private CheckBox? _customCropEnabled;
    private ComboBox? _panelRotation;
    private TextBox? _cropX;
    private TextBox? _cropY;
    private TextBox? _cropWidth;
    private TextBox? _cropHeight;
    private TextBox? _layoutX;
    private TextBox? _layoutY;
    private WpfPanelCompositionSurface? _compositionSurface;
    private TextBlock? _firmwareStatus;
    private TextBlock? _mediaStatus;
    private ProgressBar? _mediaProgress;
    private Button? _stopTransmissionButton;
    private Button? _restartUsbButton;
    private TextBlock? _restartUsbStatus;
    private ComboBox? _temperatureUnit;
    private ComboBox? _renderMode;
    private ComboBox? _diskInterval;
    private CheckBox? _disableDiskSensors;
    private CheckBox? _keepPlayback;
    private CheckBox? _launchAtLogin;
    private CheckBox? _startHidden;
    private CheckBox? _automaticSteamCover;
    private bool _syncingSettings;
    private bool _syncingIntegrations;
    private bool _syncingCrop;
    private bool _exitFromTray;
    private CoverStudioWindow? _coverStudio;
    private HtThemeEditorWindow? _themeEditor;

    internal LcdStudioWindow(LcdStudioBackend controller)
    {
        InitializeComponent();
        _controller = controller;
        RestoreWindowPlacement();
        _sidebarStatus = (TextBlock)FindName("SidebarDeviceStatus");
        _navigationButtons["devices"] = (Button)FindName("DevicesNavigation");
        _navigationButtons["multiscreen"] = (Button)FindName("MultiScreenNavigation");
        _navigationButtons["themes"] = (Button)FindName("ThemesNavigation");
        _navigationButtons["sensors"] = (Button)FindName("SensorsNavigation");
        _navigationButtons["integrations"] = (Button)FindName("IntegrationsNavigation");
        _navigationButtons["settings"] = (Button)FindName("SettingsNavigation");
        _pages["devices"] = BuildDevicesPage();
        _pages["multiscreen"] = BuildMultiScreenPage();
        _pages["sensors"] = BuildSensorsPage();
        _pages["integrations"] = BuildIntegrationsPage();
        _pages["settings"] = BuildSettingsPage();
        ShowCoverStudio();
        foreach (UIElement page in _pages.Values)
            AppLanguage.Apply(page);
        AppLanguage.Apply(this);
        _statusTimer.Tick += (_, _) => RefreshSharedStatus();
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
        };
        StateChanged += (_, _) =>
        {
            SaveWindowPlacement();
            ScheduleWindowPlacementSave();
        };
        LocationChanged += (_, _) => ScheduleWindowPlacementSave();
        SizeChanged += (_, _) => ScheduleWindowPlacementSave();
        Loaded += (_, _) =>
        {
            RefreshSharedStatus();
            _statusTimer.Start();
            if (_controller.StartMinimizedToTray)
                Dispatcher.BeginInvoke(Hide, DispatcherPriority.Background);
        };
        Closing += (_, e) =>
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
            if (!_exitFromTray) _controller.OnWindowClosing(e);
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _placementSaveTimer.Stop();
            _controller.ShutdownBackend();
        };
        SourceInitialized += (_, _) => Win11WindowChrome.Apply(new WindowInteropHelper(this).Handle);
    }

    private void FitWindowToWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        double availableWidth = Math.Max(320, workArea.Width - 16);
        double availableHeight = Math.Max(240, workArea.Height - 16);

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private void RestoreWindowPlacement()
    {
        FitWindowToWorkArea();
        SavedWindowPlacement? saved = WindowPlacementSettings.Load();
        if (saved is null) return;

        Rect virtualScreen = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
            virtualScreen = SystemParameters.WorkArea;

        double maxWidth = Math.Max(MinWidth, virtualScreen.Width - 16);
        double maxHeight = Math.Max(MinHeight, virtualScreen.Height - 16);
        Width = Math.Clamp(saved.Width, MinWidth, maxWidth);
        Height = Math.Clamp(saved.Height, MinHeight, maxHeight);

        const double visibleWidth = 160;
        const double visibleHeight = 100;
        double minLeft = virtualScreen.Left - Width + visibleWidth;
        double maxLeft = virtualScreen.Right - visibleWidth;
        double minTop = virtualScreen.Top - Height + visibleHeight;
        double maxTop = virtualScreen.Bottom - visibleHeight;
        Left = Math.Clamp(saved.Left, minLeft, maxLeft);
        Top = Math.Clamp(saved.Top, minTop, maxTop);
        WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void SaveWindowPlacement()
    {
        if (WindowState == WindowState.Minimized) return;
        Rect bounds = WindowState == WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);
        if (bounds.Width <= 0 || bounds.Height <= 0
            || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            return;

        WindowPlacementSettings.Save(new SavedWindowPlacement(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized));
    }

    private void ScheduleWindowPlacementSave()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized) return;
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void RefreshSharedStatus()
    {
        _sidebarStatus.Text = _controller.PanelStatusDescription;
        if (_pages["devices"] is FrameworkElement devicePage && devicePage.IsLoaded)
        {
            _deviceValues["connection"].Text = _controller.PanelDetectionText;
            _deviceValues["description"].Text = _controller.PanelDescription;
            _firmwareStatus!.Text = _controller.FirmwareDescription;
            _mediaStatus!.Text = _controller.MediaUploadStatus;
            _mediaProgress!.Value = _controller.MediaUploadProgress;
            _stopTransmissionButton!.IsEnabled = _controller.TransmissionActive;
            _restartUsbButton!.IsEnabled = !_controller.DeviceMaintenanceInProgress;
            _restartUsbStatus!.Text = _controller.UsbResetStatus;
        }
        if (_pages["multiscreen"] is FrameworkElement multiPage && multiPage.IsLoaded)
        {
            _multiScreenStatus!.Text = _controller.MultiScreenStatusText;
            _multiCompositionStatus!.Text = _controller.MultiScreenCompositionStatusText;
            string[] ports = _controller.MultiScreenPanelPorts;
            string panelLabel = AppLanguage.Translate("Painel LCD Jungle Leopard");
            string[] items = ports.Select(port => $"{port}  ·  {panelLabel}").ToArray();
            if (!_panelItems.SequenceEqual(items))
            {
                int selected = Math.Clamp(_panelList.SelectedIndex, 0, Math.Max(0, items.Length - 1));
                _panelItems.Clear();
                foreach (string item in items) _panelItems.Add(item);
                if (items.Length > 0) _panelList.SelectedIndex = selected;
            }
            string selectedPort = _panelList.SelectedItem?.ToString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            string canvasSignature = string.Join(";", _controller.PanelCompositionItems.Select(item => $"{item.Port},{item.X},{item.Y},{item.Width},{item.Height},{item.RotationDegrees}"))
                + $"|{selectedPort}|{_controller.CompositionLayoutIndex}";
            if (!canvasSignature.Equals(_panelCanvasSignature, StringComparison.Ordinal))
            {
                _panelCanvasSignature = canvasSignature;
                _compositionSurface!.SetPanels(_controller.PanelCompositionItems, selectedPort, _controller.CompositionLayoutIndex == 3);
            }
            if (_compositionLayout is not null && _compositionLayout.SelectedIndex != _controller.CompositionLayoutIndex)
                _compositionLayout.SelectedIndex = _controller.CompositionLayoutIndex;
            if (_startCaptureButton is not null) _startCaptureButton.IsEnabled = _controller.CanStartDesktopCapture;
            if (_stopCaptureButton is not null) _stopCaptureButton.IsEnabled = _controller.DesktopCaptureActive;
        }
        if (_pages["sensors"] is FrameworkElement sensorPage && sensorPage.IsLoaded)
        {
            _sensorValues["cpu"].Text = _controller.CpuSensorText;
            _sensorValues["memory"].Text = _controller.MemorySensorText;
            _sensorValues["gpu"].Text = _controller.GpuSensorText;
            _sensorValues["cpu-temp"].Text = _controller.CpuTemperatureText;
            _sensorValues["gpu-temp"].Text = _controller.GpuTemperatureText;
            foreach (var option in _controller.SensorSourceSelections)
            {
                if (!_sensorSourceSelectors.TryGetValue(option.Key, out ComboBox? selector)) continue;
                string[] current = selector.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty).ToArray();
                if (!current.SequenceEqual(option.Options))
                {
                    selector.Items.Clear();
                    foreach (string choice in option.Options) selector.Items.Add(choice);
                }
                if (selector.SelectedIndex != option.SelectedIndex) selector.SelectedIndex = option.SelectedIndex;
            }
        }
    }

    private UIElement BuildDevicesPage()
    {
        var content = NewPage("Painel LCD e conexão", "Detecção USB automática e mídias de inicialização do Pro Flow 360.");
        _deviceValues["connection"] = AddTextCard(content, "TELA USB", _controller.PanelDetectionText);
        _deviceValues["description"] = AddTextCard(content, "IDENTIFICAÇÃO DO PAINEL", _controller.PanelDescription);

        var originalProgramActions = new WrapPanel();
        _stopTransmissionButton = ActionButton("Parar transmissão", (_, _) => _controller.StopFromStudio());
        _stopTransmissionButton.IsEnabled = _controller.TransmissionActive;
        _restartUsbButton = ActionButton("Reiniciar porta USB", (_, _) => _controller.RestartUsbFromStudio());
        _restartUsbButton.IsEnabled = !_controller.DeviceMaintenanceInProgress;
        _restartUsbStatus = new TextBlock
        {
            Text = _controller.UsbResetStatus,
            Foreground = Brush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        originalProgramActions.Children.Add(_stopTransmissionButton);
        originalProgramActions.Children.Add(_restartUsbButton);
        originalProgramActions.Children.Add(ActionButton("Fechar Jungle Leopard", (_, _) => _controller.CloseVendorFromStudio()));
        originalProgramActions.Children.Add(ActionButton("Parar e reabrir original", (_, _) => _controller.RestoreVendorFromStudio()));
        AddCard(content, new StackPanel
        {
            Children =
            {
                SectionLabel("CONTROLE DO PAINEL"),
                originalProgramActions,
                _restartUsbStatus
            }
        });

        var firmwareCard = new StackPanel();
        firmwareCard.Children.Add(SectionLabel("FIRMWARE"));
        _firmwareStatus = new TextBlock
        {
            Text = _controller.FirmwareDescription,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        };
        firmwareCard.Children.Add(_firmwareStatus);
        var firmwareActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        firmwareActions.Children.Add(ActionButton("Verificar atualizações", (_, _) => _controller.CheckFirmwareFromStudio()));
        firmwareActions.Children.Add(ActionButton("Instalar atualização", (_, _) => _controller.InstallFirmwareFromStudio()));
        firmwareCard.Children.Add(firmwareActions);
        AddCard(content, firmwareCard);

        var media = new StackPanel();
        media.Children.Add(SectionLabel("MÍDIA DE INICIALIZAÇÃO"));
        media.Children.Add(ActionButton("Escolher arquivo…", (_, _) => _controller.ChooseStartupMediaFromStudio()));
        media.Children.Add(ActionButton("Enviar ao painel", (_, _) => _controller.UploadStartupMediaFromStudio()));
        media.Children.Add(SectionLabel("MÍDIA DE ESPERA"));
        media.Children.Add(ActionButton("Escolher arquivo…", (_, _) => _controller.ChooseStandbyMediaFromStudio()));
        media.Children.Add(ActionButton("Enviar ao painel", (_, _) => _controller.UploadStandbyMediaFromStudio()));
        _mediaStatus = new TextBlock { Text = _controller.MediaUploadStatus, Foreground = Brush("MutedBrush"), Margin = new Thickness(0, 10, 0, 0) };
        _mediaProgress = new ProgressBar { Minimum = 0, Maximum = 100, Value = _controller.MediaUploadProgress, Height = 5, Margin = new Thickness(0, 8, 0, 0) };
        media.Children.Add(_mediaStatus);
        media.Children.Add(_mediaProgress);
        AddCard(content, media);
        return Scroll(content);
    }

    private UIElement BuildMultiScreenPage()
    {
        var content = NewPage("Múltiplas telas LCD", "Organize os painéis detectados e envie o conteúdo atual para todos eles.");
        _panelList.ItemsSource = _panelItems;
        _panelList.MinHeight = 90;
        _panelList.MaxHeight = 220;
        _panelList.SelectionChanged += (_, _) =>
        {
            _controller.SelectPanelFromStudio(_panelList.SelectedIndex);
            LoadCropSettingsFromController();
            _panelCanvasSignature = string.Empty;
        };
        AddCard(content, new StackPanel
        {
            Children = { SectionLabel("PAINÉIS COMPATÍVEIS DETECTADOS"), _panelList }
        });

        _compositionLayout = new ComboBox { MinWidth = 280, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        foreach (string layout in new[] { "Espelhar todos", "Compor na horizontal", "Compor na vertical", "Compor livremente" })
            _compositionLayout.Items.Add(AppLanguage.Translate(layout));
        _compositionLayout.SelectedIndex = _controller.CompositionLayoutIndex;
        _compositionLayout.SelectionChanged += (_, _) =>
        {
            if (_compositionLayout.SelectedIndex < 0) return;
            _controller.SetCompositionLayoutFromStudio(_compositionLayout.SelectedIndex);
            LoadCropSettingsFromController();
            _panelCanvasSignature = string.Empty;
        };
        var compositionOptions = new Grid();
        compositionOptions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        compositionOptions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var layoutStack = new StackPanel();
        layoutStack.Children.Add(SectionLabel("ARRANJO DOS PAINÉIS"));
        layoutStack.Children.Add(_compositionLayout);
        compositionOptions.Children.Add(layoutStack);
        var orderActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 0) };
        orderActions.Children.Add(ActionButton("Mover para cima", (_, _) => _controller.MoveSelectedPanelFromStudio(-1)));
        orderActions.Children.Add(ActionButton("Mover para baixo", (_, _) => _controller.MoveSelectedPanelFromStudio(1)));
        Grid.SetColumn(orderActions, 1);
        compositionOptions.Children.Add(orderActions);
        AddCard(content, compositionOptions);

        var cropStack = new StackPanel();
        cropStack.Children.Add(SectionLabel("RECORTE PERSONALIZADO POR PAINEL"));
        _customCropEnabled = new CheckBox { Content = "Usar recorte personalizado neste painel", Margin = new Thickness(0, 0, 0, 10) };
        _customCropEnabled.Checked += (_, _) => ApplyCropSettingsFromView();
        _customCropEnabled.Unchecked += (_, _) => ApplyCropSettingsFromView();
        cropStack.Children.Add(_customCropEnabled);
        var cropGrid = new Grid();
        for (int i = 0; i < 6; i++) cropGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _cropX = AddTextInput(cropGrid, "X (%)", 0);
        _cropY = AddTextInput(cropGrid, "Y (%)", 1);
        _cropWidth = AddTextInput(cropGrid, "Largura (%)", 2);
        _cropHeight = AddTextInput(cropGrid, "Altura (%)", 3);
        _layoutX = AddTextInput(cropGrid, "Posição X (px)", 4);
        _layoutY = AddTextInput(cropGrid, "Posição Y (px)", 5);
        cropStack.Children.Add(cropGrid);
        var rotationStack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        rotationStack.Children.Add(SectionLabel("ORIENTAÇÃO DESTE PAINEL"));
        _panelRotation = new ComboBox { MinWidth = 260, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        foreach (string rotation in new[] { "Usar orientação global", "0°", "90° horário", "180°", "270° horário" })
            _panelRotation.Items.Add(AppLanguage.Translate(rotation));
        _panelRotation.SelectionChanged += (_, _) => ApplyCropSettingsFromView();
        rotationStack.Children.Add(_panelRotation);
        cropStack.Children.Add(rotationStack);
        cropStack.Children.Add(ActionButton("Aplicar recorte", (_, _) => ApplyCropSettingsFromView(), primary: true));
        AddCard(content, cropStack);

        _compositionSurface = new WpfPanelCompositionSurface { Height = 300 };
        _compositionSurface.PanelSelected += port =>
        {
            int index = Array.FindIndex(_controller.MultiScreenPanelPorts, item => item.Equals(port, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < _panelItems.Count) _panelList.SelectedIndex = index;
        };
        _compositionSurface.PanelMoved += (port, x, y) =>
        {
            _controller.SetPanelPositionFromStudio(port, x, y);
            _panelCanvasSignature = string.Empty;
        };
        AddCard(content, new StackPanel
        {
            Children = { SectionLabel("POSIÇÃO DOS PAINÉIS NO CANVAS"), _compositionSurface }
        });

        var capture = new StackPanel();
        capture.Children.Add(SectionLabel("CAPTURAR UMA TELA DO WINDOWS"));
        _desktopDisplay = new ComboBox { MinWidth = 300, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        foreach (string display in _controller.DesktopDisplays) _desktopDisplay.Items.Add(display);
        if (_desktopDisplay.Items.Count > 0) _desktopDisplay.SelectedIndex = _controller.DesktopDisplayIndex;
        _desktopDisplay.SelectionChanged += (_, _) => _controller.SetDesktopDisplayFromStudio(_desktopDisplay.SelectedIndex);
        capture.Children.Add(new TextBlock { Text = "Monitor", Foreground = Brush("MutedBrush"), Margin = new Thickness(0, 0, 0, 6) });
        capture.Children.Add(_desktopDisplay);
        _includeDesktopCursor = new CheckBox { Content = "Incluir cursor na captura", IsChecked = _controller.IncludeDesktopCursor, Margin = new Thickness(0, 12, 0, 10) };
        _includeDesktopCursor.Checked += (_, _) => _controller.SetDesktopCursorFromStudio(true);
        _includeDesktopCursor.Unchecked += (_, _) => _controller.SetDesktopCursorFromStudio(false);
        capture.Children.Add(_includeDesktopCursor);
        _startCaptureButton = ActionButton("Iniciar captura", (_, _) => _controller.StartDesktopCaptureFromStudio(), primary: true);
        _stopCaptureButton = ActionButton("Parar captura", (_, _) => _controller.StopDesktopCaptureFromStudio());
        capture.Children.Add(new WrapPanel { Children = { _startCaptureButton, _stopCaptureButton } });
        _multiCompositionStatus = new TextBlock { Text = _controller.MultiScreenCompositionStatusText, Foreground = Brush("MutedBrush"), Margin = new Thickness(0, 8, 0, 0) };
        capture.Children.Add(_multiCompositionStatus);
        AddCard(content, capture);

        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("Compor imagem/tema atual", (_, _) => _controller.ComposeCurrentVisualFromStudio(), primary: true));
        actions.Children.Add(ActionButton("Parar composição", (_, _) => _controller.StopMultiScreenFromStudio()));
        AddCard(content, actions);
        _multiScreenStatus = AddTextCard(content, "DETECÇÃO USB", _controller.MultiScreenStatusText);
        LoadCropSettingsFromController();
        return Scroll(content);
    }

    private TextBox AddTextInput(Grid grid, string label, int column)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 9, 8) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = Brush("MutedBrush"), Margin = new Thickness(0, 0, 0, 5) });
        var input = new TextBox { Text = "0", MinWidth = 75 };
        stack.Children.Add(input);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
        return input;
    }

    private void LoadCropSettingsFromController()
    {
        if (_customCropEnabled is null || _cropX is null || _cropY is null || _cropWidth is null || _cropHeight is null
            || _layoutX is null || _layoutY is null || _panelRotation is null) return;
        var crop = _controller.SelectedPanelCrop;
        _syncingCrop = true;
        try
        {
            _customCropEnabled.IsChecked = crop.Enabled;
            _cropX.Text = crop.X.ToString();
            _cropY.Text = crop.Y.ToString();
            _cropWidth.Text = crop.Width.ToString();
            _cropHeight.Text = crop.Height.ToString();
            _layoutX.Text = crop.LayoutX.ToString();
            _layoutY.Text = crop.LayoutY.ToString();
            _panelRotation.SelectedIndex = crop.RotationIndex < 0 ? 0 : crop.RotationIndex + 1;
        }
        finally { _syncingCrop = false; }
    }

    private void ApplyCropSettingsFromView()
    {
        if (_syncingCrop || _customCropEnabled is null || _cropX is null || _cropY is null || _cropWidth is null || _cropHeight is null
            || _layoutX is null || _layoutY is null || _panelRotation is null) return;
        static int Read(TextBox box, int fallback) => int.TryParse(box.Text, out int value) ? value : fallback;
        _controller.SetSelectedPanelCropFromStudio(
            _customCropEnabled.IsChecked == true,
            Read(_cropX, 0), Read(_cropY, 0), Read(_cropWidth, 100), Read(_cropHeight, 100),
            _panelRotation.SelectedIndex <= 0 ? -1 : _panelRotation.SelectedIndex - 1,
            Read(_layoutX, 0), Read(_layoutY, 0));
        LoadCropSettingsFromController();
        _panelCanvasSignature = string.Empty;
    }

    private UIElement BuildSensorsPage()
    {
        var content = NewPage("Dados dos temas", "Sensores que cenas .ht podem mostrar ao vivo. Valores indisponíveis aparecem como —.");
        var metrics = new WrapPanel();
        AddSensorMetric(metrics, "Processador", "cpu", _controller.CpuSensorText, "Utilização atual");
        AddSensorMetric(metrics, "Memória", "memory", _controller.MemorySensorText, "Memória física");
        AddSensorMetric(metrics, "Placa de vídeo", "gpu", _controller.GpuSensorText, "Utilização atual");
        AddSensorMetric(metrics, "Temperatura da CPU", "cpu-temp", _controller.CpuTemperatureText, "Sensor disponível");
        AddSensorMetric(metrics, "Temperatura da GPU", "gpu-temp", _controller.GpuTemperatureText, "Sensor disponível");
        AddCard(content, metrics);
        foreach (IGrouping<string, (string Group, string Label, string Key, string[] Options, int SelectedIndex)> group
            in _controller.SensorSourceSelections.GroupBy(option => option.Group))
        {
            var choices = new StackPanel();
            choices.Children.Add(SectionLabel(group.Key.ToUpperInvariant()));
            foreach (var option in group)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = option.Label, Foreground = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
                var selector = new ComboBox { MinWidth = 300, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
                foreach (string choice in option.Options) selector.Items.Add(choice);
                selector.SelectedIndex = option.SelectedIndex;
                selector.SelectionChanged += (_, _) =>
                {
                    if (selector.SelectedIndex >= 0) _controller.SetSensorSourceFromStudio(option.Key, selector.SelectedIndex);
                };
                _sensorSourceSelectors[option.Key] = selector;
                Grid.SetColumn(selector, 1);
                row.Children.Add(selector);
                choices.Children.Add(row);
            }
            AddCard(content, choices);
        }
        var note = new TextBlock
        {
            Text = "Os temas atualizam os mostradores no painel. Fontes de sensor específicas podem ser escolhidas em Configurações do LCD.",
            Foreground = Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 18)
        };
        content.Children.Add(note);
        return Scroll(content);
    }

    private UIElement BuildSettingsPage()
    {
        var content = NewPage("Configurações do LCD", "Preferências de renderização, sensores e execução do Jungle Leopard Screen Control.");
        _temperatureUnit = AddOptionCard(content, "Unidade de temperatura", ["Celsius (°C)", "Fahrenheit (°F)"]);
        _renderMode = AddOptionCard(content, "Modo de renderização", ["Alta performance", "Equilibrado", "Alta qualidade"]);
        _diskInterval = AddOptionCard(content, "Intervalo de leitura do disco", ["1 segundo", "2 segundos", "5 segundos", "10 segundos"]);
        _disableDiskSensors = AddCheckCard(content, "Desativar leitura de sensores de disco");
        _keepPlayback = AddCheckCard(content, "Manter reprodução durante bloqueio ou suspensão do Windows");
        _launchAtLogin = AddCheckCard(content, "Iniciar junto com o Windows");
        _startHidden = AddCheckCard(content, "Iniciar oculto na bandeja e minimizar ao fechar");
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("Selecionar ffmpeg.exe", (_, _) => _controller.ChooseFfmpegFromStudio()));
        actions.Children.Add(ActionButton("Restaurar padrões", (_, _) => _controller.ResetSettingsFromStudio()));
        AddCard(content, actions);
        content.Children.Add(new TextBlock { Text = _controller.FfmpegPathDescription, Foreground = Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 10) });

        _temperatureUnit.SelectedIndex = _controller.TemperatureUnitIndex;
        _renderMode.SelectedIndex = _controller.RenderModeIndex;
        _diskInterval.SelectedIndex = _controller.DiskSensorIntervalIndex;
        _disableDiskSensors.IsChecked = _controller.DiskSensorsDisabled;
        _keepPlayback.IsChecked = _controller.KeepPlaybackWhenScreenOff;
        _launchAtLogin.IsChecked = _controller.LaunchAtLogin;
        _startHidden.IsChecked = _controller.StartMinimizedToTray;
        _syncingSettings = false;
        System.Windows.Controls.SelectionChangedEventHandler selectionChanged = (_, _) => SaveSettingsFromView();
        RoutedEventHandler settingChanged = (_, _) => SaveSettingsFromView();
        _temperatureUnit.SelectionChanged += selectionChanged;
        _renderMode.SelectionChanged += selectionChanged;
        _diskInterval.SelectionChanged += selectionChanged;
        _disableDiskSensors.Checked += settingChanged;
        _disableDiskSensors.Unchecked += settingChanged;
        _keepPlayback.Checked += settingChanged;
        _keepPlayback.Unchecked += settingChanged;
        _launchAtLogin.Checked += settingChanged;
        _launchAtLogin.Unchecked += settingChanged;
        _startHidden.Checked += settingChanged;
        _startHidden.Unchecked += settingChanged;
        return Scroll(content);
    }

    private UIElement BuildIntegrationsPage()
    {
        var content = NewPage("Integra\u00E7\u00F5es", "Configure conex\u00F5es com servi\u00E7os e recursos externos.");
        var card = new Grid();
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var settings = new StackPanel();
        settings.Children.Add(SectionLabel("STEAM"));
        _automaticSteamCover = new CheckBox
        {
            Content = "Capa Steam autom\u00E1tica",
            IsChecked = _controller.AutomaticSteamCover,
            Style = (Style)FindResource("Win11CheckBox"),
            Margin = new Thickness(4, 0, 4, 8)
        };
        _automaticSteamCover.Checked += AutomaticSteamCoverChanged;
        _automaticSteamCover.Unchecked += AutomaticSteamCoverChanged;
        settings.Children.Add(_automaticSteamCover);
        settings.Children.Add(new TextBlock
        {
            Text = "Detecta o jogo Steam em primeiro plano e atualiza automaticamente a capa exibida no painel.",
            Foreground = Brush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 4, 4, 2)
        });
        card.Children.Add(settings);
        var editButton = ActionButton("Editar layout", EditSteamLayoutClicked, primary: true);
        editButton.VerticalAlignment = VerticalAlignment.Center;
        editButton.Margin = new Thickness(18, 8, 0, 0);
        Grid.SetColumn(editButton, 1);
        card.Children.Add(editButton);
        AddCard(content, card);
        return Scroll(content);
    }

    private void EditSteamLayoutClicked(object sender, RoutedEventArgs e)
    {
        bool portrait = _controller.PreviewHeight > _controller.PreviewWidth;

        int shortSide = Math.Min(_controller.NativePanelWidth, _controller.NativePanelHeight);
        int longSide = Math.Max(_controller.NativePanelWidth, _controller.NativePanelHeight);
        ThemePackage package;
        try
        {
            package = _controller.LoadSteamLayoutPackage(portrait)
                ?? ThemePackage.Read(ThemePackage.CreateSteamLayout(
                    portrait ? "Steam - Retrato" : "Steam - Paisagem",
                    portrait ? shortSide : longSide,
                    portrait ? longSide : shortSide));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            AppMessageBox.Show(this, "N\u00E3o foi poss\u00EDvel abrir o layout Steam: " + ex.Message,
                "Integra\u00E7\u00E3o Steam", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var editor = new HtThemeEditorWindow(package, steamLayoutMode: true);
        editor.SaveRequested += (_, _) =>
        {
            if (editor.PackageBytes is not null)
            {
                try
                {
                    _controller.SaveSteamLayoutPackage(editor.PackageBytes, portrait);
                    PageHost.Content = _pages["integrations"];
                    UpdateNavigation("integrations");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    AppMessageBox.Show(this, "N\u00E3o foi poss\u00EDvel salvar o layout Steam: " + ex.Message,
                        "Integra\u00E7\u00E3o Steam", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            _themeEditor = null;
        };
        editor.CancelRequested += (_, _) =>
        {
            _themeEditor = null;
            PageHost.Content = _pages["integrations"];
            UpdateNavigation("integrations");
        };
        _themeEditor = editor;
        PageHost.Content = editor;
        UpdateNavigation("integrations");
    }

    private void AutomaticSteamCoverChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingIntegrations || _automaticSteamCover is null) return;
        _controller.SetScreenOptions(_controller.RotationIndex, _controller.Brightness,
            _automaticSteamCover.IsChecked == true);
    }

    private void RefreshIntegrationsPage()
    {
        if (_automaticSteamCover is null) return;
        _syncingIntegrations = true;
        _automaticSteamCover.IsChecked = _controller.AutomaticSteamCover;
        _syncingIntegrations = false;
    }

    private void SaveSettingsFromView()
    {
        if (_syncingSettings || _temperatureUnit is null || _renderMode is null || _diskInterval is null) return;
        _controller.SetGeneralSettingsFromStudio(
            Math.Max(0, _temperatureUnit.SelectedIndex),
            Math.Max(0, _renderMode.SelectedIndex),
            _disableDiskSensors?.IsChecked == true,
            Math.Max(0, _diskInterval.SelectedIndex),
            _keepPlayback?.IsChecked == true,
            _launchAtLogin?.IsChecked == true,
            _startHidden?.IsChecked == true);
    }

    private void AddSensorMetric(Panel panel, string title, string key, string value, string detail)
    {
        var card = new Border { Width = 240, MinHeight = 132, Background = Brush("PanelBrush"), BorderBrush = Brush("PanelBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Foreground = Brush("MutedBrush") });
        var valueBlock = new TextBlock { Text = value, FontSize = 29, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 3) };
        _sensorValues[key] = valueBlock;
        stack.Children.Add(valueBlock);
        stack.Children.Add(new TextBlock { Text = detail, Foreground = Brush("MutedBrush"), FontSize = 12 });
        card.Child = stack;
        panel.Children.Add(card);
    }

    private ComboBox AddOptionCard(Panel parent, string label, string[] options)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, Foreground = Brush("MutedBrush"), Margin = new Thickness(0, 0, 0, 7) });
        var combo = new ComboBox { MinWidth = 260, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        foreach (string option in options) combo.Items.Add(new ComboBoxItem { Content = AppLanguage.Translate(option) });
        stack.Children.Add(combo);
        AddCard(parent, stack);
        return combo;
    }

    private CheckBox AddCheckCard(Panel parent, string label)
    {
        var check = new CheckBox { Content = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 6, 3, 6) };
        AddCard(parent, check);
        return check;
    }

    private TextBlock AddTextCard(Panel parent, string heading, string value)
    {
        var stack = new StackPanel();
        stack.Children.Add(SectionLabel(heading));
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        stack.Children.Add(text);
        AddCard(parent, stack);
        return text;
    }

    private StackPanel NewPage(string title, string description)
    {
        var page = new StackPanel { Margin = new Thickness(2, 0, 6, 10) };
        page.Children.Add(new TextBlock { Text = title, FontSize = 29, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = description, Foreground = Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 18) });
        return page;
    }

    private static ScrollViewer Scroll(UIElement child) => new() { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private void AddCard(Panel parent, UIElement child)
    {
        parent.Children.Add(new Border { Background = Brush("PanelBrush"), BorderBrush = Brush("PanelBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 14), Child = child });
    }

    private TextBlock SectionLabel(string title) => new() { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Brush("MutedBrush"), Margin = new Thickness(0, 8, 0, 8) };

    private Button ActionButton(string title, RoutedEventHandler click, bool primary = false)
    {
        var button = new Button { Content = title, Style = (Style)FindResource(primary ? "Win11PrimaryButton" : "Win11Button"), Margin = new Thickness(0, 0, 8, 8) };
        button.Click += click;
        return button;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
    private void DevicesClicked(object sender, RoutedEventArgs e) => ShowPage("devices");
    private void MultiScreenClicked(object sender, RoutedEventArgs e) => ShowPage("multiscreen");
    private void SensorsClicked(object sender, RoutedEventArgs e) => ShowPage("sensors");
    private void IntegrationsClicked(object sender, RoutedEventArgs e)
    {
        RefreshIntegrationsPage();
        ShowPage("integrations");
    }
    private void SettingsClicked(object sender, RoutedEventArgs e) => ShowPage("settings");

    private void ThemesClicked(object sender, RoutedEventArgs e)
        => ShowCoverStudio();

    private void ShowCoverStudio()
    {
        if (_coverStudio is null)
        {
            _coverStudio = new CoverStudioWindow(_controller.ApplyThemeFromStudio,
                () => _controller.RotationIndex,
                () => (_controller.RotationIndex, _controller.Brightness),
                (rotation, brightness) => _controller.SetScreenOptions(rotation, brightness, _controller.AutomaticSteamCover));
            _coverStudio.EditRequested += OpenThemeEditor;
        }
        _pages["themes"] = _coverStudio;
        PageHost.Content = _coverStudio;
        _coverStudio.RefreshPanelOrientation();
        UpdateNavigation("themes");
    }

    private void OpenThemeEditor(ThemePackage package, bool isNew)
    {
        if (_coverStudio is null) return;
        var editor = new HtThemeEditorWindow(package);
        editor.SaveRequested += (_, _) =>
        {
            if (editor.PackageBytes is not null)
                _coverStudio.SaveEditedPackage(editor.PackageBytes, editor.ThemeName, isNew);
            _themeEditor = null;
            PageHost.Content = _coverStudio;
            _coverStudio.RefreshPanelOrientation();
            UpdateNavigation("themes");
        };
        editor.CancelRequested += (_, _) =>
        {
            _themeEditor = null;
            PageHost.Content = _coverStudio;
            _coverStudio.RefreshPanelOrientation();
            UpdateNavigation("themes");
        };
        _themeEditor = editor;
        PageHost.Content = editor;
        UpdateNavigation("themes");
    }

    private void ShowPage(string key)
    {
        if (_pages.TryGetValue(key, out UIElement? page)) PageHost.Content = page;
        else return;
        UpdateNavigation(key);
    }

    private void UpdateNavigation(string key)
    {
        foreach ((string page, Button button) in _navigationButtons)
        {
            bool selected = page.Equals(key, StringComparison.OrdinalIgnoreCase);
            button.Tag = selected ? "selected" : null;
            button.Background = Brushes.Transparent;
            button.Foreground = selected ? Brush("TextBrush") : Brush("MutedBrush");
        }
    }

    internal void RestoreFromTray()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ShowInTaskbar = true;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });
    }

    internal void CloseFromTray() => Dispatcher.BeginInvoke(new Action(() =>
    {
        _exitFromTray = true;
        Close();
    }));
}
