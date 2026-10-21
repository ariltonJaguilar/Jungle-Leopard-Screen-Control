using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JungleLeopardPoc;

internal sealed record SyncedLyricLine(long TimeMilliseconds, string Text);

internal sealed record MediaSessionSnapshot(
    string AppId,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    long PositionAtCaptureMilliseconds,
    long DurationMilliseconds,
    long CapturedAtTimestamp,
    byte[]? CoverImageBytes,
    IReadOnlyList<SyncedLyricLine> Lyrics,
    bool LyricsAreSynchronized)
{
    public static MediaSessionSnapshot Empty { get; } = new("", "", "", "", false, 0, 0,
        Stopwatch.GetTimestamp(), null, [], false);

    public bool HasMedia => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist)
        || CoverImageBytes is { Length: > 0 };

    public long CurrentPositionMilliseconds
    {
        get
        {
            long elapsed = IsPlaying
                ? (long)Stopwatch.GetElapsedTime(CapturedAtTimestamp).TotalMilliseconds
                : 0;
            long position = Math.Max(0, PositionAtCaptureMilliseconds + elapsed);
            return DurationMilliseconds > 0 ? Math.Min(position, DurationMilliseconds) : position;
        }
    }
}

/// <summary>Reads the active Windows media session and fetches matching lyrics once per track.</summary>
internal static partial class MediaSessionService
{
    private static readonly HttpClient LyricsClient = CreateLyricsClient();
    private static readonly byte[] NativeSnapshotBuffer = new byte[4 * 1024 * 1024];
    private static readonly System.Threading.Timer PollTimer = new(Poll, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(750));
    private static readonly object LyricsGate = new();
    private static MediaSessionSnapshot _current = MediaSessionSnapshot.Empty;
    private static string _lastTrackKey = "";
    private static CancellationTokenSource? _lyricsRequest;
    private static int _pollInProgress;
    private static int _lyricsConsumerCount;

    public static MediaSessionSnapshot Current => Volatile.Read(ref _current);

    public static IDisposable RequestLyrics()
    {
        lock (LyricsGate)
        {
            _lyricsConsumerCount++;
            MediaSessionSnapshot current = Current;
            string trackKey = Volatile.Read(ref _lastTrackKey);
            if (_lyricsConsumerCount == 1 && current.HasMedia && current.Lyrics.Count == 0 && trackKey.Length > 0)
                StartLyricsFetch(trackKey, current);
        }
        return new LyricsLease();
    }

    private static HttpClient CreateLyricsClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("JungleLeopardLCDStudio", "1.0"));
        client.DefaultRequestHeaders.Add("Lrclib-App-Id", "JungleLeopardLCDStudio");
        return client;
    }

    private static void Poll(object? state)
    {
        if (Interlocked.Exchange(ref _pollInProgress, 1) != 0) return;
        try
        {
            MediaSessionSnapshot raw = ReadNativeSnapshot();
            string trackKey = raw.HasMedia
                ? string.Join('\u001f', raw.AppId, raw.Title, raw.Artist, raw.Album)
                : "";
            string previousKey = Volatile.Read(ref _lastTrackKey);
            MediaSessionSnapshot current = raw with
            {
                CoverImageBytes = raw.CoverImageBytes ?? (trackKey.Length > 0 && trackKey == previousKey ? Current.CoverImageBytes : null),
                Lyrics = trackKey.Length > 0 && trackKey == previousKey ? Current.Lyrics : [],
                LyricsAreSynchronized = trackKey.Length > 0 && trackKey == previousKey && Current.LyricsAreSynchronized
            };
            Interlocked.Exchange(ref _current, current);

            if (!string.Equals(trackKey, previousKey, StringComparison.Ordinal))
            {
                Volatile.Write(ref _lastTrackKey, trackKey);
                lock (LyricsGate)
                {
                    CancelLyricsFetch();
                    if (_lyricsConsumerCount > 0 && current.HasMedia)
                        StartLyricsFetch(trackKey, current);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or SEHException or ExternalException or JsonException or ArgumentException)
        {
            Interlocked.Exchange(ref _current, MediaSessionSnapshot.Empty);
        }
        finally { Volatile.Write(ref _pollInProgress, 0); }
    }

    private static void StartLyricsFetch(string trackKey, MediaSessionSnapshot track)
    {
        var request = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _lyricsRequest = request;
        _ = FetchLyricsAsync(trackKey, track, request.Token);
    }

    private static void CancelLyricsFetch()
    {
        CancellationTokenSource? request = _lyricsRequest;
        _lyricsRequest = null;
        if (request is null) return;
        request.Cancel();
        request.Dispose();
    }

    private sealed class LyricsLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (LyricsGate)
            {
                _lyricsConsumerCount = Math.Max(0, _lyricsConsumerCount - 1);
                if (_lyricsConsumerCount == 0) CancelLyricsFetch();
            }
        }
    }

    private static MediaSessionSnapshot ReadNativeSnapshot()
    {
        int length = GetMediaSessionSnapshot(NativeSnapshotBuffer, NativeSnapshotBuffer.Length);
        if (length <= 0) return MediaSessionSnapshot.Empty;
        using JsonDocument document = JsonDocument.Parse(NativeSnapshotBuffer.AsMemory(0, length));
        JsonElement root = document.RootElement;
        return new MediaSessionSnapshot(
            ReadString(root, "appId"),
            ReadString(root, "title"),
            ReadString(root, "artist"),
            ReadString(root, "album"),
            root.TryGetProperty("playing", out JsonElement playing) && playing.ValueKind == JsonValueKind.True,
            ReadInt64(root, "positionMs"),
            ReadInt64(root, "durationMs"),
            Stopwatch.GetTimestamp(), ReadThumbnail(root), [], false);
    }

    private static byte[]? ReadThumbnail(JsonElement root)
    {
        if (!root.TryGetProperty("coverBase64", out JsonElement image)
            || image.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(image.GetString()))
            return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(image.GetString()!);
            return bytes.Length is > 0 and <= 2 * 1024 * 1024 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    private static async Task FetchLyricsAsync(string trackKey, MediaSessionSnapshot track, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return;
            // Let a newly focused song settle before querying; this avoids a request on every track skip.
            await Task.Delay(450, cancellationToken).ConfigureAwait(false);
            var query = new List<string>
            {
                "track_name=" + Uri.EscapeDataString(track.Title),
                "artist_name=" + Uri.EscapeDataString(track.Artist)
            };
            if (!string.IsNullOrWhiteSpace(track.Album))
                query.Add("album_name=" + Uri.EscapeDataString(track.Album));
            if (track.DurationMilliseconds is > 0 and <= 3_600_000)
                query.Add("duration=" + Math.Max(1, (int)Math.Round(track.DurationMilliseconds / 1000d)).ToString(CultureInfo.InvariantCulture));

            string synced;
            string plain;
            using (HttpResponseMessage response = await LyricsClient.GetAsync(
                "https://lrclib.net/api/get?" + string.Join('&', query), cancellationToken).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                    synced = ReadString(document.RootElement, "syncedLyrics");
                    plain = ReadString(document.RootElement, "plainLyrics");
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // A local catalog search is a useful fallback when the session reports a rounded duration.
                    await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                    string search = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(track.Title)
                        + "&artist_name=" + Uri.EscapeDataString(track.Artist);
                    using HttpResponseMessage searchResponse = await LyricsClient.GetAsync(search, cancellationToken).ConfigureAwait(false);
                    if (!searchResponse.IsSuccessStatusCode) return;
                    await using Stream body = await searchResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                    string expectedTitle = NormalizeTrackValue(track.Title);
                    string expectedArtist = NormalizeTrackValue(track.Artist);
                    JsonElement match = document.RootElement.ValueKind == JsonValueKind.Array
                        ? document.RootElement.EnumerateArray().FirstOrDefault(candidate =>
                            NormalizeTrackValue(ReadString(candidate, "trackName")) == expectedTitle
                            && NormalizeTrackValue(ReadString(candidate, "artistName")) == expectedArtist)
                        : default;
                    if (match.ValueKind != JsonValueKind.Object) return;
                    synced = ReadString(match, "syncedLyrics");
                    plain = ReadString(match, "plainLyrics");
                }
                else return;
            }
            IReadOnlyList<SyncedLyricLine> lyrics = ParseLyrics(synced, plain, out bool synchronized);
            if (lyrics.Count == 0) return;

            while (true)
            {
                MediaSessionSnapshot current = Current;
                if (!string.Equals(trackKey, Volatile.Read(ref _lastTrackKey), StringComparison.Ordinal)) return;
                var updated = current with { Lyrics = lyrics, LyricsAreSynchronized = synchronized };
                if (ReferenceEquals(Interlocked.CompareExchange(ref _current, updated, current), current)) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or ArgumentException)
        {
            // Keep title and artist visible when lyrics are unavailable or the network is offline.
        }
    }

    private static IReadOnlyList<SyncedLyricLine> ParseLyrics(string synced, string plain, out bool synchronized)
    {
        var lines = new List<SyncedLyricLine>();
        foreach (string rawLine in synced.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            MatchCollection timestamps = LyricTimestampRegex().Matches(rawLine);
            if (timestamps.Count == 0) continue;
            string text = LyricTimestampRegex().Replace(rawLine, "").Trim();
            if (text.Length == 0) continue;
            foreach (Match timestamp in timestamps)
            {
                long minutes = long.Parse(timestamp.Groups["minutes"].Value, CultureInfo.InvariantCulture);
                long seconds = long.Parse(timestamp.Groups["seconds"].Value, CultureInfo.InvariantCulture);
                string fraction = timestamp.Groups["fraction"].Value;
                int fractionalMilliseconds = fraction.Length switch
                {
                    1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
                    3 => int.Parse(fraction, CultureInfo.InvariantCulture),
                    _ => 0
                };
                lines.Add(new SyncedLyricLine((minutes * 60 + seconds) * 1000 + fractionalMilliseconds, text));
            }
        }

        if (lines.Count > 0)
        {
            synchronized = true;
            return lines.OrderBy(line => line.TimeMilliseconds).ToArray();
        }

        synchronized = false;
        return plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new SyncedLyricLine(0, line.Trim()))
            .Where(line => line.Text.Length > 0)
            .ToArray();
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long ReadInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long number) ? number : 0;

    private static string NormalizeTrackValue(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    [GeneratedRegex(@"\[(?<minutes>\d{1,3}):(?<seconds>\d{2})(?:[.:](?<fraction>\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex LyricTimestampRegex();

    [DllImport("MediaSessionBridge.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int GetMediaSessionSnapshot([Out] byte[] buffer, int capacity);
}
