using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

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

    public static void SetRequested(object owner, bool requested)
    {
        ArgumentNullException.ThrowIfNull(owner);
        bool startBroker = false;
        lock (Gate)
        {
            if (requested)
            {
                if (!Requesters.Add(owner)) return;
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
        }

        if (startBroker)
            _ = Task.Run(StartBrokerAsync);
    }

    private static async Task StartBrokerAsync()
    {
        string pipeName = "JungleLeopardFps_" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        lock (Gate) _pipe = pipe;

        try
        {
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

            using Process? broker = Process.Start(startInfo);
            if (broker is null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
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
                lock (Gate)
                {
                    _lastUpdateUtc = DateTime.UtcNow;
                    _currentValue = double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
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
            Debug.WriteLine("FPS capture is unavailable: " + ex.Message);
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_pipe, pipe)) _pipe = null;
                _currentValue = null;
                _lastUpdateUtc = DateTime.MinValue;
            }
            pipe.Dispose();
        }
    }

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
            or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine("FPS broker stopped: " + ex.Message);
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
        string sessionName = $"JungleLeopardFps_{parentProcessId}_{Guid.NewGuid():N}";
        var pendingFrames = new ConcurrentQueue<FrameSample>();
        var swapChains = new Dictionary<string, Queue<FrameSample>>(StringComparer.OrdinalIgnoreCase);
        Process? capture = null;
        CancellationTokenSource? captureCancellation = null;
        Task? captureReadTask = null;
        uint capturedProcessId = 0;
        DateTime retryCaptureAfter = DateTime.MinValue;
        bool enabled = false;
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
                        await StopCaptureAsync(capture, captureCancellation, captureReadTask).ConfigureAwait(false);
                        capture = null;
                        captureCancellation = null;
                        captureReadTask = null;
                        capturedProcessId = 0;
                        swapChains.Clear();
                        while (pendingFrames.TryDequeue(out _)) { }
                    }
                }

                if (enabled && Stopwatch.GetTimestamp() >= nextForegroundCheck)
                {
                    nextForegroundCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
                    uint foregroundProcessId = GetForegroundProcessId();
                    if (foregroundProcessId == 0 || foregroundProcessId == (uint)parentProcessId
                        || foregroundProcessId == Environment.ProcessId)
                        foregroundProcessId = IsProcessRunning(capturedProcessId) ? capturedProcessId : 0;

                    bool processChanged = foregroundProcessId != capturedProcessId;
                    bool captureExited = capture is not null && IsExited(capture);
                    bool retryCapture = foregroundProcessId != 0 && capture is null && DateTime.UtcNow >= retryCaptureAfter;
                    if (processChanged || captureExited || retryCapture)
                    {
                        await StopCaptureAsync(capture, captureCancellation, captureReadTask).ConfigureAwait(false);
                        capture = null;
                        captureCancellation = null;
                        captureReadTask = null;
                        if (captureExited) retryCaptureAfter = DateTime.UtcNow.AddSeconds(5);
                        if (processChanged) retryCaptureAfter = DateTime.MinValue;
                        capturedProcessId = foregroundProcessId;
                        swapChains.Clear();
                        while (pendingFrames.TryDequeue(out _)) { }

                        if (foregroundProcessId != 0 && DateTime.UtcNow >= retryCaptureAfter)
                        {
                            try
                            {
                                captureCancellation = new CancellationTokenSource();
                                (capture, captureReadTask) = StartCapture(
                                    foregroundProcessId, sessionName, pendingFrames, captureCancellation.Token);
                            }
                            catch (Exception ex) when (ex is IOException or InvalidOperationException
                                or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                            {
                                Debug.WriteLine("Could not start PresentMon: " + ex.Message);
                                captureCancellation?.Dispose();
                                captureCancellation = null;
                                retryCaptureAfter = DateTime.UtcNow.AddSeconds(5);
                            }
                        }
                    }
                }

                while (pendingFrames.TryDequeue(out FrameSample sample))
                {
                    if (!swapChains.TryGetValue(sample.SwapChain, out Queue<FrameSample>? frames))
                    {
                        frames = new Queue<FrameSample>();
                        swapChains.Add(sample.SwapChain, frames);
                    }
                    frames.Enqueue(sample);
                }

                double? fps = enabled ? GetForegroundFps(swapChains) : null;
                await writer.WriteLineAsync(fps?.ToString("F2", CultureInfo.InvariantCulture) ?? "NA").ConfigureAwait(false);

                Task delay = Task.Delay(200);
                await Task.WhenAny(commandTask, delay).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopCaptureAsync(capture, captureCancellation, captureReadTask).ConfigureAwait(false);
        }
    }

    private static (Process Process, Task ReaderTask) StartCapture(
        uint processId,
        string sessionName,
        ConcurrentQueue<FrameSample> pendingFrames,
        CancellationToken cancellationToken)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "PresentMon", "PresentMon.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("PresentMon.exe não foi incluído no pacote do aplicativo.", executable);

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
        startInfo.ArgumentList.Add("--process_id");
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--session_name");
        startInfo.ArgumentList.Add(sessionName);
        startInfo.ArgumentList.Add("--stop_existing_session");
        startInfo.ArgumentList.Add("--v1_metrics");
        startInfo.ArgumentList.Add("--output_stdout");
        startInfo.ArgumentList.Add("--no_console_stats");
        startInfo.ArgumentList.Add("--no_track_gpu");
        startInfo.ArgumentList.Add("--no_track_display");
        startInfo.ArgumentList.Add("--no_track_input");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("O PresentMon não pôde ser iniciado.");
        Task stderrTask = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }, CancellationToken.None);
        Task outputTask = ReadPresentMonOutputAsync(process, pendingFrames, cancellationToken);
        return (process, Task.WhenAll(stderrTask, outputTask));
    }

    private static async Task ReadPresentMonOutputAsync(
        Process process,
        ConcurrentQueue<FrameSample> pendingFrames,
        CancellationToken cancellationToken)
    {
        int swapChainColumn = -1;
        int intervalColumn = -1;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                string[] fields = SplitCsv(line);
                if (swapChainColumn < 0 || intervalColumn < 0)
                {
                    swapChainColumn = Array.FindIndex(fields, field => field.Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase));
                    intervalColumn = Array.FindIndex(fields, field => field.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                if (fields.Length <= Math.Max(swapChainColumn, intervalColumn)
                    || !double.TryParse(fields[intervalColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double intervalMs)
                    || !double.IsFinite(intervalMs) || intervalMs <= 0 || intervalMs > 10000)
                    continue;

                string swapChain = fields[swapChainColumn].Trim();
                if (swapChain.Length == 0) continue;
                pendingFrames.Enqueue(new FrameSample(swapChain, intervalMs, Stopwatch.GetTimestamp()));
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

    private static double? GetForegroundFps(Dictionary<string, Queue<FrameSample>> swapChains)
    {
        long now = Stopwatch.GetTimestamp();
        long cutoff = now - 2 * Stopwatch.Frequency;
        double? best = null;
        foreach (Queue<FrameSample> frames in swapChains.Values)
        {
            while (frames.Count > 0 && frames.Peek().Timestamp < cutoff)
                frames.Dequeue();
            if (frames.Count < 2 || now - frames.Last().Timestamp > 2 * Stopwatch.Frequency)
                continue;

            double totalIntervalMs = frames.Sum(frame => frame.IntervalMs);
            if (totalIntervalMs <= 0) continue;
            double fps = frames.Count * 1000d / totalIntervalMs;
            if (double.IsFinite(fps) && fps > 0 && fps <= 2000 && (best is null || fps > best))
                best = fps;
        }
        return best;
    }

    private static async Task StopCaptureAsync(Process? process, CancellationTokenSource? cancellation, Task? readerTask)
    {
        if (cancellation is null) return;
        cancellation.Cancel();
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
            finally { process.Dispose(); }
        }
        if (readerTask is not null)
        {
            try { await readerTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                or OperationCanceledException or TimeoutException) { }
        }
        cancellation.Dispose();
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

    private readonly record struct FrameSample(string SwapChain, double IntervalMs, long Timestamp);
}
