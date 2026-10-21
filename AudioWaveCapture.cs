using System.Runtime.InteropServices;

namespace JungleLeopardPoc;

/// <summary>Reads the default Windows playback endpoint through WASAPI loopback.</summary>
internal static class AudioWaveCapture
{
    private static readonly object Gate = new();
    private static AudioLoopbackCapture? _capture;
    private static DateTime _retryAfterUtc;
    private static string? _lastError;

    public static string Status
    {
        get
        {
            lock (Gate) return _capture is not null ? "capturando" : _lastError ?? "aguardando";
        }
    }

    static AudioWaveCapture()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            AudioLoopbackCapture? capture;
            lock (Gate)
            {
                capture = _capture;
                _capture = null;
            }
            capture?.Dispose();
        };
    }

    public static float[] ReadLatest(int sampleCount)
    {
        sampleCount = Math.Clamp(sampleCount, 1, 2048);
        AudioLoopbackCapture? capture;
        lock (Gate)
        {
            if (_capture is null && DateTime.UtcNow >= _retryAfterUtc)
            {
                try
                {
                    _capture = new AudioLoopbackCapture();
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    // Themes must remain renderable on machines without an active playback endpoint.
                    _lastError = ex.Message;
                    _retryAfterUtc = DateTime.UtcNow.AddSeconds(10);
                }
            }
            capture = _capture;
        }
        return capture?.ReadLatest(sampleCount) ?? new float[sampleCount];
    }
}

internal sealed class AudioLoopbackCapture : IDisposable
{
    private const int RingLength = 8192;
    private const int AudioClientShareModeShared = 0;
    private const uint AudioClientStreamFlagsLoopback = 0x00020000;
    private const uint AudioClientBufferFlagSilent = 0x00000002;
    private const int AudioRender = 0;
    private const int AudioConsole = 0;
    private const int ClsctxAll = 23;

    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly float[] _ring = new float[RingLength];
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _initialized = new();
    private int _writeIndex;
    private int _count;
    private volatile bool _stopping;
    private int _channels;
    private int _bitsPerSample;
    private bool _floatingPoint;
    private int _blockAlign;
    private Exception? _initializationError;
    private IMMDevice? _device;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;

    public AudioLoopbackCapture()
    {
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "LCD theme audio capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_initialized.Wait(TimeSpan.FromSeconds(5)))
        {
            _stopping = true;
            _thread.Join(500);
            throw new TimeoutException("O Windows não abriu a captura de áudio em 5 segundos.");
        }
        if (_initializationError is not null)
        {
            _stopping = true;
            _thread.Join(500);
            throw new InvalidOperationException("Não foi possível capturar o áudio de saída do Windows.", _initializationError);
        }
    }

    private void InitializeCapture()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(AudioRender, AudioConsole, out _device));
        }
        finally { Marshal.FinalReleaseComObject(enumerator); }

        Guid audioClientId = AudioClientId;
        Marshal.ThrowExceptionForHR(_device!.Activate(ref audioClientId, ClsctxAll, IntPtr.Zero, out _audioClient));
        Marshal.ThrowExceptionForHR(_audioClient!.GetMixFormat(out IntPtr formatPointer));
        try
        {
            WaveFormatEx format = Marshal.PtrToStructure<WaveFormatEx>(formatPointer);
            _channels = format.Channels;
            _bitsPerSample = format.BitsPerSample;
            _blockAlign = format.BlockAlign;
            _floatingPoint = format.FormatTag == 3;
            if (format.FormatTag == 0xFFFE && format.ExtraSize >= 22)
            {
                Guid subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(formatPointer, 24));
                _floatingPoint = subFormat == IeeeFloatSubFormat;
            }
            if (_channels is < 1 or > 32 || _blockAlign < _channels * Math.Max(1, _bitsPerSample / 8)
                || _bitsPerSample is not (8 or 16 or 24 or 32))
                throw new NotSupportedException("O formato de áudio padrão do Windows não é aceito pelo leitor de espectro.");

            Guid sessionId = Guid.Empty;
            Marshal.ThrowExceptionForHR(_audioClient.Initialize(AudioClientShareModeShared,
                AudioClientStreamFlagsLoopback, 1_000_000, 0, formatPointer, ref sessionId));
        }
        finally { Marshal.FreeCoTaskMem(formatPointer); }

        Guid captureClientId = AudioCaptureClientId;
        Marshal.ThrowExceptionForHR(_audioClient.GetService(ref captureClientId, out _captureClient));
        Marshal.ThrowExceptionForHR(_audioClient.Start());
    }

    public float[] ReadLatest(int sampleCount)
    {
        var result = new float[sampleCount];
        lock (_gate)
        {
            int available = Math.Min(sampleCount, _count);
            int start = (_writeIndex - available + RingLength) % RingLength;
            int padding = sampleCount - available;
            for (int i = 0; i < available; i++) result[padding + i] = _ring[(start + i) % RingLength];
        }
        return result;
    }

    private void CaptureLoop()
    {
        try
        {
            InitializeCapture();
            _initialized.Set();
            while (!_stopping)
            {
                try
                {
                    Marshal.ThrowExceptionForHR(_captureClient!.GetNextPacketSize(out uint packetFrames));
                    if (packetFrames == 0)
                    {
                        Thread.Sleep(4);
                        continue;
                    }

                    while (packetFrames > 0 && !_stopping)
                    {
                        int result = _captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags,
                            out _, out _);
                        Marshal.ThrowExceptionForHR(result);
                        try
                        {
                            if ((flags & AudioClientBufferFlagSilent) == 0 && data != IntPtr.Zero)
                                AppendSamples(data, frames);
                        }
                        finally { Marshal.ThrowExceptionForHR(_captureClient.ReleaseBuffer(frames)); }
                        Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out packetFrames));
                    }
                }
                catch (COMException)
                {
                    Thread.Sleep(100);
                }
                catch (ObjectDisposedException) { break; }
            }
        }
        catch (Exception ex) { _initializationError = ex; }
        finally
        {
            _initialized.Set();
            try { _audioClient?.Stop(); } catch (COMException) { }
            ReleaseComObject(_captureClient);
            ReleaseComObject(_audioClient);
            ReleaseComObject(_device);
        }
    }

    private void AppendSamples(IntPtr data, uint frames)
    {
        lock (_gate)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                int frameOffset = frame * _blockAlign;
                for (int channel = 0; channel < _channels; channel++)
                {
                    int offset = frameOffset + channel * (_bitsPerSample / 8);
                    sum += Math.Abs(ReadSample(IntPtr.Add(data, offset)));
                }
                _ring[_writeIndex] = (float)Math.Clamp(sum / _channels, -1d, 1d);
                _writeIndex = (_writeIndex + 1) % RingLength;
                _count = Math.Min(_count + 1, RingLength);
            }
        }
    }

    private float ReadSample(IntPtr pointer)
    {
        if (_floatingPoint && _bitsPerSample == 32)
            return Math.Clamp(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer)), -1f, 1f);
        return _bitsPerSample switch
        {
            8 => (Marshal.ReadByte(pointer) - 128) / 128f,
            16 => Marshal.ReadInt16(pointer) / 32768f,
            24 => ReadInt24(pointer) / 8388608f,
            32 => Marshal.ReadInt32(pointer) / 2147483648f,
            _ => 0
        };
    }

    private static int ReadInt24(IntPtr pointer)
    {
        int value = Marshal.ReadByte(pointer)
            | Marshal.ReadByte(IntPtr.Add(pointer, 1)) << 8
            | Marshal.ReadByte(IntPtr.Add(pointer, 2)) << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(500);
        _initialized.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            try { Marshal.FinalReleaseComObject(instance); }
            catch (InvalidComObjectException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, int classContext, IntPtr activationParameters,
            out IAudioClient audioClient);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
            IntPtr format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, out IAudioCaptureClient captureClient);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
    }
}
