using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JungleLeopardPoc;

/// <summary>Fetches explicitly referenced online .ht media without blocking theme rendering.</summary>
internal static class ThemeRemoteAssetCache
{
    private const int MaximumAssetBytes = 90 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Downloads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> CachedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "JungleLeopardStudio", "RemoteThemeMedia");

    public static bool TryGetCachedFile(string source, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || source.Length > 4096)
            return false;

        string key = uri.AbsoluteUri;
        if (CachedPaths.TryGetValue(key, out string? cached) && cached is not null && File.Exists(cached))
        {
            path = cached;
            return true;
        }
        if (RetryAfter.TryGetValue(key, out DateTimeOffset retryAt) && retryAt > DateTimeOffset.UtcNow)
            return false;

        Lazy<Task<string?>> download = Downloads.GetOrAdd(key,
            _ => new Lazy<Task<string?>>(() => DownloadAsync(uri), LazyThreadSafetyMode.ExecutionAndPublication));
        Task<string?> pending = download.Value;
        if (pending.IsCompletedSuccessfully && pending.Result is { } result && File.Exists(result))
        {
            CachedPaths[key] = result;
            RetryAfter.TryRemove(key, out _);
            path = result;
            return true;
        }
        if (pending.IsCompleted)
        {
            Downloads.TryRemove(key, out _);
            RetryAfter[key] = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        return false;
    }

    private static async Task<string?> DownloadAsync(Uri uri)
    {
        string temporaryPath = string.Empty;
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumAssetBytes)
                return null;

            string extension = NormalizeExtension(Path.GetExtension(uri.AbsolutePath));
            if (extension.Length == 0)
                extension = ExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);
            if (extension.Length == 0)
                return null;

            Directory.CreateDirectory(CacheDirectory);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
            string destinationPath = Path.Combine(CacheDirectory, key + extension);
            if (File.Exists(destinationPath))
                return destinationPath;
            temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".download";

            await using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[64 * 1024];
            int total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > MaximumAssetBytes)
                    return null;
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
            if (total == 0)
                return null;
            await output.FlushAsync().ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            temporaryPath = string.Empty;
            return destinationPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidOperationException or TaskCanceledException or OverflowException)
        {
            return null;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string NormalizeExtension(string extension)
    {
        extension = extension.ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".jfif" or ".bmp" or ".gif"
            or ".mp4" or ".m4v" or ".wmv" or ".avi" or ".mov" or ".mkv" or ".webm"
            ? extension
            : string.Empty;
    }

    private static string ExtensionFromContentType(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/x-png" => ".png",
        "image/jpeg" => ".jpg",
        "image/pjpeg" => ".jpg",
        "image/bmp" => ".bmp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        "video/x-m4v" => ".m4v",
        "video/webm" => ".webm",
        "video/x-msvideo" => ".avi",
        "video/quicktime" => ".mov",
        "video/x-ms-wmv" => ".wmv",
        "video/x-matroska" => ".mkv",
        _ => string.Empty
    };
}
