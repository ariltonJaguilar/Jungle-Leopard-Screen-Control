using System.Diagnostics;
using System.IO;

namespace JungleLeopardPoc;

/// <summary>Optional FFmpeg-backed normalization for videos embedded in .ht themes.</summary>
internal static class HtVideoTranscoder
{
    private const int MaxEmbeddedVideoBytes = 90 * 1024 * 1024;
    private static string? _configuredPath;

    public static string? ConfiguredPath
    {
        get => Volatile.Read(ref _configuredPath);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Volatile.Write(ref _configuredPath, null);
                return;
            }
            try { Volatile.Write(ref _configuredPath, Path.GetFullPath(value)); }
            catch (ArgumentException) { Volatile.Write(ref _configuredPath, null); }
            catch (PathTooLongException) { Volatile.Write(ref _configuredPath, null); }
        }
    }

    public static string? FindExecutable()
    {
        string? configured = ConfiguredPath;
        if (IsExecutable(configured)) return configured;

        string? environmentPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (IsExecutable(environmentPath)) return environmentPath;

        string[] appLocalCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe")
        ];
        string? appLocal = appLocalCandidates.FirstOrDefault(File.Exists);
        if (appLocal is not null) return appLocal;

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
        }

        return null;
    }

    public static async Task<VideoImportResult> PrepareForThemeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        FileInfo source = new(sourcePath);
        if (!source.Exists) throw new FileNotFoundException("O vídeo selecionado não existe.", sourcePath);
        if (source.Length <= 0 || source.Length > MaxEmbeddedVideoBytes)
            throw new InvalidDataException("O vídeo deve ter entre 1 byte e 90 MB para caber no pacote .ht.");

        string? ffmpeg = FindExecutable();
        if (ffmpeg is null)
        {
            byte[] originalBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            return new VideoImportResult(originalBytes, source.Extension, false,
                "FFmpeg não configurado; o vídeo original foi mantido e sua reprodução depende dos codecs do Windows.");
        }

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "JungleLeopardStudio", "video-import");
        Directory.CreateDirectory(temporaryDirectory);
        string outputPath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                         "-i", sourcePath,
                         "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,fps=30",
                         "-an", "-c:v", "libx264", "-crf", "18", "-preset", "ultrafast",
                         "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-f", "mp4", outputPath
                     })
                start.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = start };
            if (!process.Start())
                throw new InvalidOperationException("O FFmpeg não pôde ser iniciado.");

            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                if (!cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("A normalização do vídeo excedeu cinco minutos.");
                throw;
            }

            string error = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidDataException("FFmpeg não conseguiu converter o vídeo: " + Tail(error, 1200));

            var converted = new FileInfo(outputPath);
            if (converted.Length <= 0 || converted.Length > MaxEmbeddedVideoBytes)
                throw new InvalidDataException("O vídeo convertido excede o limite de 90 MB do pacote .ht.");

            byte[] bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            return new VideoImportResult(bytes, ".mp4", true,
                "Vídeo convertido para MP4/H.264, 4:2:0, 30 quadros/s e sem áudio para compatibilidade com temas .ht.");
        }
        finally
        {
            try { File.Delete(outputPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static string Tail(string value, int maxLength)
        => value.Length <= maxLength ? value.Trim() : value[^maxLength..].Trim();
}

internal sealed record VideoImportResult(byte[] Bytes, string Extension, bool WasNormalized, string Status);
