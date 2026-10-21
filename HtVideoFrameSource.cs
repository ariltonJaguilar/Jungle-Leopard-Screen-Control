using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JungleLeopardPoc;

/// <summary>Decodes an embedded theme video on a dedicated WPF dispatcher and exposes its latest frame.</summary>
internal sealed class HtVideoFrameSource : IDisposable
{
    private static readonly TimeSpan FrameCaptureInterval = TimeSpan.FromMilliseconds(1000d / 30d);
    private readonly string _path;
    private readonly object _frameGate = new();
    private readonly object _lifecycleGate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _dispatcherReady = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private byte[]? _latestFrame;
    private string? _normalizedVideoPath;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timer;
    private MediaPlayer? _player;
    private Task? _fallbackTask;
    private volatile bool _disposed;
    private int _fallbackStarted;
    private int _frameWidth = 960;
    private int _frameHeight = 480;
    private volatile bool _hasNaturalVideoSize;

    public HtVideoFrameSource(string path)
    {
        _path = path;
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "Jungle Leopard .ht video decoder" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcherReady.Wait(TimeSpan.FromSeconds(1));
    }

    public byte[]? LatestFrame
    {
        get { lock (_frameGate) return _latestFrame; }
    }

    public System.Drawing.Size NaturalVideoSize
        => _hasNaturalVideoSize
            ? new System.Drawing.Size(Volatile.Read(ref _frameWidth), Volatile.Read(ref _frameHeight))
            : System.Drawing.Size.Empty;

    private void DecodeLoop()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcherReady.Set();
            _player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            _player.MediaOpened += (_, _) =>
            {
                Volatile.Write(ref _frameWidth, Math.Clamp(_player.NaturalVideoWidth, 64, 4096));
                Volatile.Write(ref _frameHeight, Math.Clamp(_player.NaturalVideoHeight, 64, 4096));
                _hasNaturalVideoSize = true;
                CaptureFrame();
            };
            _player.MediaEnded += (_, _) =>
            {
                _player.Position = TimeSpan.Zero;
                _player.Play();
            };
            _player.MediaFailed += (_, _) => BeginFfmpegFallback();
            _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = FrameCaptureInterval
            };
            _timer.Tick += (_, _) => CaptureFrame();
            _player.Open(new Uri(_path, UriKind.Absolute));
            _player.Play();
            _timer.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UriFormatException or ArgumentException)
        {
            _dispatcherReady.Set();
        }
        finally { _dispatcherReady.Set(); }

        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is not null)
            Dispatcher.Run();

        try { _player?.Close(); }
        catch (InvalidOperationException) { }
        _player = null;
    }

    private void CaptureFrame()
    {
        MediaPlayer? player = _player;
        if (_disposed || player is null || player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0)
            return;
        try
        {
            int width = _frameWidth;
            int height = _frameHeight;
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, width, height));
                context.DrawVideo(player, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            lock (_frameGate) _latestFrame = stream.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or OutOfMemoryException) { }
    }

    public void Dispose()
    {
        Task? fallbackTask;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCancellation.Cancel();
            fallbackTask = _fallbackTask;
        }
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            try
            {
                dispatcher.InvokeAsync(() =>
                {
                    _timer?.Stop();
                    _player?.Close();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }, DispatcherPriority.Send).Task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or AggregateException) { }
        }
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        try { fallbackTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        lock (_frameGate) _latestFrame = null;
        _disposeCancellation.Dispose();
        if (_normalizedVideoPath is string normalizedPath)
        {
            try { File.Delete(normalizedPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void BeginFfmpegFallback()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _fallbackStarted != 0 || HtVideoTranscoder.FindExecutable() is null)
                return;

            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
                return;

            _fallbackStarted = 1;
            CancellationToken cancellationToken = _disposeCancellation.Token;
            string normalizedPath = Path.Combine(Path.GetDirectoryName(_path) ?? Path.GetTempPath(),
                Guid.NewGuid().ToString("N") + ".normalized.mp4");
            _normalizedVideoPath = normalizedPath;
            _fallbackTask = Task.Run(async () =>
            {
                try
                {
                    VideoImportResult result = await HtVideoTranscoder.PrepareForThemeAsync(_path, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.WasNormalized)
                        return;
                    await File.WriteAllBytesAsync(normalizedPath, result.Bytes, cancellationToken).ConfigureAwait(false);
                    if (_disposed || cancellationToken.IsCancellationRequested || dispatcher.HasShutdownStarted)
                        return;

                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_disposed || _player is null || dispatcher.HasShutdownStarted)
                            return;
                        try
                        {
                            _player.Close();
                            _player.Open(new Uri(normalizedPath, UriKind.Absolute));
                            _player.Play();
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or UriFormatException) { }
                    }), DispatcherPriority.Send);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException or ArgumentException) { }
            });
        }
    }
}
