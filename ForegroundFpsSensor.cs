using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace JungleLeopardPoc;

internal static class ForegroundFpsSensor
{
    private static readonly object Gate = new();
    private static readonly HashSet<object> Requesters = new(ReferenceEqualityComparer.Instance);
    private static readonly CancellationTokenSource Shutdown = new();
    private static NamedPipeServerStream? _pipe;
    private static double? _currentValue;
    private static DateTime _lastUpdateUtc;
    private static bool _started;
    private static string _status = "Nenhum tema solicitou a medição de FPS.";
    private static readonly object DiagnosticGate = new();
    private static readonly Regex PresentMonSessionNamePattern = new(
        @"JungleLeopardFps_[A-Za-z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static ForegroundFpsSensor()
        => AppDomain.CurrentDomain.ProcessExit += (_, _) => StopParentConnection();

    public static double? CurrentValue
    {
        get
        {
            lock (Gate)
                return Requesters.Count > 0 && DateTime.UtcNow - _lastUpdateUtc <= TimeSpan.FromSeconds(2)
                    ? _currentValue
                    : null;
        }
    }

    public static string Status
    {
        get
        {
            lock (Gate)
                return Requesters.Count == 0 ? "Nenhum tema ativo está solicitando FPS." : _status;
        }
    }

    public static void SetRequested(object owner, bool requested)
    {
        ArgumentNullException.ThrowIfNull(owner);
        bool startBroker = false;
        lock (Gate)
        {
            if (requested)
            {
                bool added = Requesters.Add(owner);
                if (added)
                    _status = "Iniciando o leitor PresentMon…";
                if (!_started)
                {
                    _started = true;
                    startBroker = true;
                }
            }
            else if (!Requesters.Remove(owner))
            {
                return;
            }
            else if (Requesters.Count == 0)
            {
                _status = "Nenhum tema ativo está solicitando FPS.";
            }
        }

        if (startBroker)
            _ = Task.Run(StartBrokerAsync);
    }

    private static async Task StartBrokerAsync()
    {
        NamedPipeServerStream? pipe = null;
        bool retry = false;
        try
        {
            string pipeName = "JungleLeopardFps_" + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            lock (Gate)
            {
                _pipe = pipe;
                _status = "Conectando ao leitor PresentMon…";
            }

            string executable = ResolveApplicationExecutable(out string? assemblyArgument);
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            string brokerArguments = assemblyArgument is null ? string.Empty : QuoteArgument(assemblyArgument) + " ";
            brokerArguments += "--fps-broker " + pipeName + " " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            startInfo.Arguments = brokerArguments;

            bool elevated = IsCurrentProcessElevated();
            startInfo.UseShellExecute = !elevated;
            if (elevated)
                startInfo.CreateNoWindow = true;
            else
                startInfo.Verb = "runas";

            using Process broker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("O processo auxiliar de FPS não pôde ser iniciado.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            SetStatus("Leitor PresentMon conectado; aguardando quadros do app em primeiro plano.");
            LogDiagnostic("Broker conectado.");
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(Shutdown.Token);
            Task<string?> readTask = reader.ReadLineAsync(readCancellation.Token).AsTask();
            bool? lastEnabled = null;

            while (!Shutdown.IsCancellationRequested && pipe.IsConnected)
            {
                bool enabled;
                lock (Gate) enabled = Requesters.Count > 0;
                if (lastEnabled != enabled)
                {
                    await writer.WriteLineAsync(enabled ? "1" : "0").ConfigureAwait(false);
                    lastEnabled = enabled;
                }

                Task delay = Task.Delay(200, Shutdown.Token);
                Task completed = await Task.WhenAny(readTask, delay).ConfigureAwait(false);
                if (completed == delay) continue;

                string? line;
                try { line = await readTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (line is null) break;
                int statusSeparator = line.IndexOf('|');
                string valueText = statusSeparator >= 0 ? line[..statusSeparator] : line;
                string brokerStatus = statusSeparator >= 0 ? line[(statusSeparator + 1)..] : "Leitor conectado; aguardando quadros.";
                lock (Gate)
                {
                    _lastUpdateUtc = DateTime.UtcNow;
                    _status = brokerStatus;
                    _currentValue = double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        && double.IsFinite(value) && value > 0 && value <= 2000
                            ? value
                            : null;
                }
                readTask = reader.ReadLineAsync(readCancellation.Token).AsTask();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException
            or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            SetStatus("Não foi possível iniciar a captura de FPS: " + ex.Message);
            LogDiagnostic("Falha no broker: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            lock (Gate)
            {
                if (pipe is not null && ReferenceEquals(_pipe, pipe)) _pipe = null;
                _currentValue = null;
                _lastUpdateUtc = DateTime.MinValue;
                retry = Requesters.Count > 0 && !Shutdown.IsCancellationRequested;
                if (!retry)
                {
                    _started = false;
                    if (!Shutdown.IsCancellationRequested)
                        _status = "Nenhum tema ativo está solicitando FPS.";
                }
                else
                {
                    _status = "Conexão com o leitor perdida; tentando restabelecer…";
                }
            }
            pipe?.Dispose();

            if (retry)
            {
                bool canRetry = true;
                try { await Task.Delay(TimeSpan.FromSeconds(5), Shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    canRetry = false;
                }

                lock (Gate)
                {
                    canRetry &= Requesters.Count > 0 && !Shutdown.IsCancellationRequested;
                    if (!canRetry)
                        _started = false;
                }
                if (canRetry)
                    _ = Task.Run(StartBrokerAsync);
            }
        }
    }

    private static void SetStatus(string status)
    {
        lock (Gate) _status = status;
    }

    private static void LogDiagnostic(string message)
    {
        lock (DiagnosticGate)
        {
            try
            {
                string path = GetDiagnosticLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.WriteAllText(path, string.Empty);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private static string GetDiagnosticLogPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JungleLeopardStudio", "fps-capture.log");

    private static string ResolveApplicationExecutable(out string? assemblyArgument)
    {
        assemblyArgument = null;
        string appHostPath = Path.Combine(AppContext.BaseDirectory, "JungleLeopardScreenControl.exe");
        if (File.Exists(appHostPath)) return appHostPath;

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(assemblyPath))
            {
                assemblyArgument = assemblyPath;
                return processPath;
            }
        }

        return processPath ?? throw new InvalidOperationException("Não foi possível localizar o executável do aplicativo.");
    }

    private static string QuoteArgument(string value)
        => "\"" + value + "\"";

    private static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void StopParentConnection()
    {
        Shutdown.Cancel();
        try { _pipe?.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    public static int RunBroker(string pipeName, int parentProcessId)
    {
        try { RunBrokerAsync(pipeName, parentProcessId).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine("FPS broker stopped: " + ex.Message);
            LogDiagnostic("Broker encerrado: " + ex.GetType().Name + ": " + ex.Message);
        }
        return 0;
    }

    private static async Task RunBrokerAsync(string pipeName, int parentProcessId)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var parent = Process.GetProcessById(parentProcessId);
        await CleanupStalePresentMonSessionsAsync().ConfigureAwait(false);
        var pendingFrames = new ConcurrentQueue<FrameSample>();
        var captureErrors = new ConcurrentQueue<string>();
        var swapChains = new Dictionary<string, Queue<FrameSample>>(StringComparer.OrdinalIgnoreCase);
        Process? capture = null;
        CancellationTokenSource? captureCancellation = null;
        Task? captureReadTask = null;
        string? lastCaptureError = null;
        string? captureSessionName = null;
        uint foregroundProcessId = 0;
        DateTime retryCaptureAfter = DateTime.MinValue;
        bool enabled = false;
        string captureStatus = "Aguardando ativação do sensor de FPS.";
        long nextForegroundCheck = 0;
        Task<string?> commandTask = reader.ReadLineAsync();

        try
        {
            while (!parent.HasExited && pipe.IsConnected)
            {
                if (commandTask.IsCompleted)
                {
                    string? command = await commandTask.ConfigureAwait(false);
                    if (command is null || command.Equals("X", StringComparison.OrdinalIgnoreCase)) break;
                    enabled = command == "1";
                    commandTask = reader.ReadLineAsync();
                    if (!enabled)
                    {
                        await StopCaptureAsync(capture, captureSessionName, captureCancellation, captureReadTask).ConfigureAwait(false);
                        capture = null;
                        captureSessionName = null;
                        captureCancellation = null;
                        captureReadTask = null;
                        foregroundProcessId = 0;
                        swapChains.Clear();
                        while (pendingFrames.TryDequeue(out _)) { }
                        captureStatus = "Nenhum tema ativo está solicitando FPS.";
                    }
                    else
                        captureStatus = "Procurando o app em primeiro plano…";
                }

                while (captureErrors.TryDequeue(out string? captureError))
                    lastCaptureError = captureError;

                if (enabled && Stopwatch.GetTimestamp() >= nextForegroundCheck)
                {
                    nextForegroundCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
                    uint detectedProcessId = GetForegroundProcessId();
                    if (detectedProcessId == 0 || detectedProcessId == (uint)parentProcessId
                        || detectedProcessId == Environment.ProcessId)
                        detectedProcessId = IsProcessRunning(foregroundProcessId) ? foregroundProcessId : 0;

                    if (detectedProcessId != foregroundProcessId)
                    {
                        foregroundProcessId = detectedProcessId;
                        captureStatus = foregroundProcessId == 0
                            ? "Aguardando um jogo ou app gráfico em primeiro plano."
                            : $"Aguardando quadros do processo em primeiro plano {foregroundProcessId}…";
                    }
                }

                bool captureExited = capture is not null && IsExited(capture);
                if (captureExited)
                {
                    int captureExitCode = 0;
                    try { captureExitCode = capture!.ExitCode; }
                    catch (InvalidOperationException) { }
                    await StopCaptureAsync(capture, captureSessionName, captureCancellation, captureReadTask).ConfigureAwait(false);
                    while (captureErrors.TryDequeue(out string? finalCaptureError))
                        lastCaptureError = finalCaptureError;
                    string exitDetail = lastCaptureError ?? "sem erro informado pelo PresentMon";
                    LogDiagnostic($"PresentMon encerrou (código {captureExitCode}): {exitDetail}");
                    captureStatus = $"PresentMon encerrou (código {captureExitCode}): {exitDetail}";
                    capture = null;
                    captureSessionName = null;
                    captureCancellation = null;
                    captureReadTask = null;
                    retryCaptureAfter = DateTime.UtcNow.AddSeconds(
                        lastCaptureError?.Contains("1450", StringComparison.Ordinal) == true ? 30 : 5);
                }

                if (enabled && capture is null && DateTime.UtcNow >= retryCaptureAfter)
                {
                    try
                    {
                        captureStatus = "Iniciando a captura PresentMon…";
                        lastCaptureError = null;
                        while (captureErrors.TryDequeue(out _)) { }
                        captureCancellation = new CancellationTokenSource();
                        (capture, captureReadTask, captureSessionName) = StartCapture(
                            pendingFrames, captureErrors, captureCancellation.Token);
                        LogDiagnostic("Captura PresentMon iniciada para todos os processos; FPS será selecionado pelo processo em primeiro plano.");
                        captureStatus = foregroundProcessId == 0
                            ? "PresentMon ativo; aguardando um app em primeiro plano."
                            : $"PresentMon ativo; aguardando quadros do processo {foregroundProcessId}…";
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException
                        or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        Debug.WriteLine("Could not start PresentMon: " + ex.Message);
                        captureStatus = "Falha ao iniciar PresentMon: " + ex.Message;
                        LogDiagnostic(captureStatus);
                        captureCancellation?.Dispose();
                        captureCancellation = null;
                        retryCaptureAfter = DateTime.UtcNow.AddSeconds(5);
                    }
                }

                while (pendingFrames.TryDequeue(out FrameSample sample))
                {
                    string swapChainKey = sample.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + sample.SwapChain;
                    if (!swapChains.TryGetValue(swapChainKey, out Queue<FrameSample>? frames))
                    {
                        frames = new Queue<FrameSample>();
                        swapChains.Add(swapChainKey, frames);
                    }
                    frames.Enqueue(sample);
                }

                double? fps = enabled ? GetForegroundFps(swapChains, foregroundProcessId) : null;
                if (fps is double currentFps)
                    captureStatus = $"Capturando o processo {foregroundProcessId}: {currentFps:F0} FPS.";
                else if (lastCaptureError is not null)
                    captureStatus = "PresentMon: " + lastCaptureError;
                else if (enabled && capture is not null && foregroundProcessId != 0)
                    captureStatus = $"PresentMon ativo; aguardando quadros do processo {foregroundProcessId}…";
                string value = fps?.ToString("F2", CultureInfo.InvariantCulture) ?? "NA";
                await writer.WriteLineAsync(value + "|" + captureStatus).ConfigureAwait(false);

                Task delay = Task.Delay(200);
                await Task.WhenAny(commandTask, delay).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopCaptureAsync(capture, captureSessionName, captureCancellation, captureReadTask).ConfigureAwait(false);
        }
    }

    private static async Task CleanupStalePresentMonSessionsAsync()
    {
        string logPath = GetDiagnosticLogPath();
        if (!File.Exists(logPath)) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionNames = new List<string>();
        try
        {
            foreach (string line in File.ReadLines(logPath))
            {
                bool started = line.Contains("PresentMon iniciado", StringComparison.OrdinalIgnoreCase);
                bool stopped = line.Contains("Sessão PresentMon encerrada:", StringComparison.OrdinalIgnoreCase);
                if (!started && !stopped)
                    continue;

                foreach (Match match in PresentMonSessionNamePattern.Matches(line))
                {
                    string sessionName = match.Value;
                    if (stopped)
                    {
                        seen.Remove(sessionName);
                        sessionNames.RemoveAll(name => name.Equals(sessionName, StringComparison.OrdinalIgnoreCase));
                        continue;
                    }

                    int prefixEnd = sessionName.IndexOf('_', "JungleLeopardFps_".Length);
                    if (prefixEnd < 0
                        || !int.TryParse(sessionName.AsSpan("JungleLeopardFps_".Length,
                            prefixEnd - "JungleLeopardFps_".Length), NumberStyles.None,
                            CultureInfo.InvariantCulture, out int brokerProcessId)
                        || IsProcessRunning((uint)brokerProcessId))
                    {
                        continue;
                    }

                    if (seen.Add(sessionName))
                        sessionNames.Add(sessionName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogDiagnostic("Não consegui ler os nomes de sessões PresentMon anteriores: " + ex.Message);
            return;
        }

        // ETW allows only a limited number of active sessions; this also bounds startup cleanup work.
        foreach (string sessionName in sessionNames.TakeLast(64))
        {
            TraceSessionStopResult result = await StopPresentMonSessionAsync(sessionName).ConfigureAwait(false);
            if (result != TraceSessionStopResult.Failed)
                LogDiagnostic("Sessão PresentMon encerrada: " + sessionName + " (limpeza de inicialização).");
        }
    }

    private enum TraceSessionStopResult
    {
        Stopped,
        NotFound,
        Failed
    }

    private static async Task<TraceSessionStopResult> StopPresentMonSessionAsync(string sessionName)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "PresentMon", "PresentMon.exe");
        if (!File.Exists(executable)) return TraceSessionStopResult.Failed;

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("--session_name");
        startInfo.ArgumentList.Add(sessionName);
        startInfo.ArgumentList.Add("--terminate_existing_session");

        try
        {
            using Process stopper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Não foi possível iniciar o PresentMon para encerrar a sessão ETW.");
            Task<string> stdoutTask = stopper.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = stopper.StandardError.ReadToEndAsync();
            await stopper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            string standardError = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            if (stopper.ExitCode == 0) return TraceSessionStopResult.Stopped;

            if (standardError.Contains("no existing sessions found", StringComparison.OrdinalIgnoreCase))
                return TraceSessionStopResult.NotFound;
            LogDiagnostic($"PresentMon não conseguiu encerrar a sessão {sessionName}: {standardError.Trim()}");
            return TraceSessionStopResult.Failed;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or TimeoutException or ArgumentException)
        {
            LogDiagnostic($"Falha ao encerrar a sessão PresentMon {sessionName}: {ex.Message}");
            return TraceSessionStopResult.Failed;
        }
    }

    private static (Process Process, Task ReaderTask, string SessionName) StartCapture(
        ConcurrentQueue<FrameSample> pendingFrames,
        ConcurrentQueue<string> captureErrors,
        CancellationToken cancellationToken)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "PresentMon", "PresentMon.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("PresentMon.exe não foi incluído no pacote do aplicativo.", executable);
        string sessionName = $"JungleLeopardFps_{Environment.ProcessId}_{Guid.NewGuid():N}";

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("--session_name");
        startInfo.ArgumentList.Add(sessionName);
        // Use the present-to-present interval for the game FPS counter.  The v2
        // FrameTime field is app-start timing and can differ from submitted FPS.
        startInfo.ArgumentList.Add("--v1_metrics");
        startInfo.ArgumentList.Add("--output_stdout");
        startInfo.ArgumentList.Add("--no_console_stats");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("O PresentMon não pôde ser iniciado.");
        LogDiagnostic($"PresentMon iniciado para todos os processos, sessão {sessionName}.");
        Task stderrTask = ReadPresentMonErrorsAsync(process, captureErrors, cancellationToken);
        Task outputTask = ReadPresentMonOutputAsync(process, pendingFrames, cancellationToken);
        return (process, Task.WhenAll(stderrTask, outputTask), sessionName);
    }

    private static async Task ReadPresentMonErrorsAsync(
        Process process,
        ConcurrentQueue<string> captureErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                string message = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (message.Length == 0) continue;
                if (message.Length > 300) message = message[..300];
                captureErrors.Enqueue(message);
                LogDiagnostic("Erro PresentMon: " + message);
                while (captureErrors.Count > 16)
                    captureErrors.TryDequeue(out _);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    private static async Task ReadPresentMonOutputAsync(
        Process process,
        ConcurrentQueue<FrameSample> pendingFrames,
        CancellationToken cancellationToken)
    {
        int swapChainColumn = -1;
        int intervalColumn = -1;
        int processIdColumn = -1;
        bool headerLogged = false;
        bool firstFrameLogged = false;
        int preambleLines = 0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                string[] fields = SplitCsv(line);
                if (swapChainColumn < 0 || intervalColumn < 0 || processIdColumn < 0)
                {
                    swapChainColumn = Array.FindIndex(fields, field => field.Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase));
                    intervalColumn = Array.FindIndex(fields, field => field.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
                    if (intervalColumn < 0)
                        intervalColumn = Array.FindIndex(fields, field => field.Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
                    processIdColumn = Array.FindIndex(fields, field => field.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
                    if (swapChainColumn >= 0 && intervalColumn >= 0 && processIdColumn >= 0)
                    {
                        headerLogged = true;
                        LogDiagnostic($"Cabeçalho CSV PresentMon reconhecido (ProcessID={processIdColumn}, SwapChainAddress={swapChainColumn}, intervalo={fields[intervalColumn]}).");
                    }
                    else if (!headerLogged && preambleLines++ < 4)
                    {
                        string diagnosticLine = line.Length > 240 ? line[..240] : line;
                        LogDiagnostic("Saída PresentMon antes do cabeçalho CSV: " + diagnosticLine);
                    }
                    continue;
                }

                if (fields.Length <= Math.Max(processIdColumn, Math.Max(swapChainColumn, intervalColumn))
                    || !uint.TryParse(fields[processIdColumn], NumberStyles.None, CultureInfo.InvariantCulture, out uint processId)
                    || processId == 0
                    || !double.TryParse(fields[intervalColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double intervalMs)
                    || !double.IsFinite(intervalMs) || intervalMs <= 0 || intervalMs > 10000)
                    continue;

                string swapChain = fields[swapChainColumn].Trim();
                if (swapChain.Length == 0) continue;
                pendingFrames.Enqueue(new FrameSample(processId, swapChain, intervalMs, Stopwatch.GetTimestamp()));
                if (!firstFrameLogged)
                {
                    firstFrameLogged = true;
                    LogDiagnostic($"Primeiro quadro PresentMon recebido para PID {processId}: {intervalMs:F2} ms.");
                }
                while (pendingFrames.Count > 4096)
                    pendingFrames.TryDequeue(out _);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char current = line[i];
            if (current == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                    quoted = !quoted;
            }
            else if (current == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(current);
            }
        }
        fields.Add(value.ToString());
        return fields.ToArray();
    }

    private static double? GetForegroundFps(Dictionary<string, Queue<FrameSample>> swapChains, uint processId)
    {
        if (processId == 0) return null;
        long now = Stopwatch.GetTimestamp();
        long cutoff = now - 2 * Stopwatch.Frequency;
        double? best = null;
        foreach (Queue<FrameSample> frames in swapChains.Values)
        {
            while (frames.Count > 0 && frames.Peek().Timestamp < cutoff)
                frames.Dequeue();
            if (frames.Count < 2 || frames.Peek().ProcessId != processId
                || now - frames.Last().Timestamp > 2 * Stopwatch.Frequency)
                continue;

            double totalIntervalMs = frames.Sum(frame => frame.IntervalMs);
            if (totalIntervalMs <= 0) continue;
            double fps = frames.Count * 1000d / totalIntervalMs;
            if (double.IsFinite(fps) && fps > 0 && fps <= 2000 && (best is null || fps > best))
                best = fps;
        }
        return best;
    }

    private static async Task StopCaptureAsync(
        Process? process,
        string? sessionName,
        CancellationTokenSource? cancellation,
        Task? readerTask)
    {
        TraceSessionStopResult sessionStopResult = sessionName is null
            ? TraceSessionStopResult.NotFound
            : await StopPresentMonSessionAsync(sessionName).ConfigureAwait(false);
        bool killed = false;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or ObjectDisposedException or OperationCanceledException) { }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    killed = true;
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
            finally { process.Dispose(); }
        }

        // If forced termination was necessary, the second stop ensures ETW did not retain the session.
        if (killed && sessionName is not null)
            sessionStopResult = await StopPresentMonSessionAsync(sessionName).ConfigureAwait(false);
        if (sessionName is not null && sessionStopResult == TraceSessionStopResult.Failed)
            LogDiagnostic("A sessão ETW do PresentMon pode continuar ativa: " + sessionName + ".");
        else if (sessionName is not null)
            LogDiagnostic("Sessão PresentMon encerrada: " + sessionName + ".");

        cancellation?.Cancel();
        if (readerTask is not null)
        {
            try { await readerTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                or OperationCanceledException or TimeoutException) { }
        }
        cancellation?.Dispose();
    }

    private static bool IsExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static bool IsProcessRunning(uint processId)
    {
        if (processId == 0) return false;
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static uint GetForegroundProcessId()
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero) return 0;
        _ = GetWindowThreadProcessId(foregroundWindow, out uint processId);
        return processId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private readonly record struct FrameSample(uint ProcessId, string SwapChain, double IntervalMs, long Timestamp);
}
