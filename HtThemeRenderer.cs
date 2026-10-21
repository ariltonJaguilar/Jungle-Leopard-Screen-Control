using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JungleLeopardPoc;

/// <summary>Renders the common live components in an imported Jungle Leopard .ht scene.</summary>
internal sealed class HtThemeRenderer : IDisposable
{
    private ThemePackage _package;
    private JsonElement _definition;
    private readonly bool _fitToScreen;
    private readonly bool _allowForegroundFps;
    private readonly Dictionary<string, CachedThemeImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Size> _mediaSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HtVideoFrameSource> _videos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GifTimeline> _gifTimelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<SensorPoint>> _sensorHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastSensorSample = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProgressTransition> _progressTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float[]> _audioWavePeaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Text, int Width, int FontSizeHalf, string FontFamily), int> _lyricWrapLineCache = new();
    private readonly HtSensorReader _sensors = new();
    private volatile SensorSnapshot _lastRenderedSensors = SensorSnapshot.Empty;
    private IDisposable? _mediaLyricsLease;
    private volatile bool _requestsForegroundFps;
    private string? _videoDirectory;
    private string? _mediaCoverKey;
    private Image? _mediaCoverImage;
    private Image? _mediaBlurredCoverImage;
    private Bitmap? _songLyricsMaskBuffer;
    private string? _lyricScrollTrackKey;
    private int _lyricScrollActiveIndex = int.MinValue;
    private float _lyricScrollFrom;
    private float _lyricScrollTarget;
    private long _lyricScrollStartedAt;
    private string? _songLyricHighlightTrackKey;
    private int _songLyricHighlightIndex = int.MinValue;
    private int _songLyricPreviousHighlightIndex = -1;
    private long _songLyricHighlightStartedAt;

    public HtThemeRenderer(ThemePackage package, bool fitToScreen = false, bool allowForegroundFps = true)
    {
        _package = package;
        _definition = package.Definition;
        _fitToScreen = fitToScreen;
        _allowForegroundFps = allowForegroundFps;
        UpdateLyricsLease(_definition);
        UpdateForegroundFpsLease(_definition);
    }

    public void UpdateDefinition(JsonObject definition)
    {
        using JsonDocument document = JsonDocument.Parse(definition.ToJsonString());
        _definition = document.RootElement.Clone();
        UpdateLyricsLease(_definition);
        UpdateForegroundFpsLease(_definition);
    }

    public void UpdatePackage(ThemePackage package)
    {
        _package = package;
        _definition = package.Definition;
        UpdateLyricsLease(_definition);
        UpdateForegroundFpsLease(_definition);
    }

    private void UpdateLyricsLease(JsonElement definition)
    {
        bool needsLyrics = ThemeRequestsLyrics(definition);
        if (needsLyrics && _mediaLyricsLease is null)
            _mediaLyricsLease = MediaSessionService.RequestLyrics();
        else if (!needsLyrics && _mediaLyricsLease is not null)
        {
            _mediaLyricsLease.Dispose();
            _mediaLyricsLease = null;
        }
    }

    private void UpdateForegroundFpsLease(JsonElement definition)
    {
        bool requestsFps = _allowForegroundFps && ThemeRequestsForegroundFps(definition);
        if (requestsFps == _requestsForegroundFps) return;
        _requestsForegroundFps = requestsFps;
        ForegroundFpsSensor.SetRequested(this, requestsFps);
    }

    public Bitmap Render(Size outputSize)
    {
        int width = _package.DeviceWidth;
        int height = _package.DeviceHeight;
        using var scene = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(scene))
        {
            Configure(graphics);
            DrawBackground(graphics, width, height);

            SensorSnapshot sensors = _sensors.Read();
            if (_requestsForegroundFps)
                sensors = sensors with { ForegroundFps = ForegroundFpsSensor.CurrentValue };
            _lastRenderedSensors = sensors;
            if (_definition.ValueKind == JsonValueKind.Object
                && _definition.TryGetProperty("children", out JsonElement children)
                && children.ValueKind == JsonValueKind.Array)
            {
                Dictionary<string, PointF> groupOffsets = ReadGroupOffsets(children);
                DrawOrderedSceneNodes(graphics, children, width, height, sensors, groupOffsets);
            }
        }

        return FitOrientation(scene, outputSize, _fitToScreen);
    }

    internal string? GetTextLayerValue(JsonObject node)
    {
        if (node["children"] is not JsonObject properties) return null;
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        JsonElement element = document.RootElement;
        string type = String(element, "type")?.ToLowerInvariant() ?? string.Empty;
        return ResolveTextValue(element.GetProperty("children"), type is "data" or "lyric", _lastRenderedSensors);
    }

    private void DrawOrderedSceneNodes(Graphics graphics, JsonElement nodes, int canvasWidth, int canvasHeight,
        SensorSnapshot sensors, IReadOnlyDictionary<string, PointF> groupOffsets)
    {
        var entries = new List<(JsonElement Node, int Position)>();
        int position = 0;
        foreach (JsonElement node in nodes.EnumerateArray())
            entries.Add((node, position++));

        var groupIds = entries
            .Where(entry => String(entry.Node, "type")?.Equals("group", StringComparison.OrdinalIgnoreCase) == true)
            .Select(entry => String(entry.Node, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var childrenByParent = entries
            .Where(entry => String(entry.Node, "parentId") is { Length: > 0 } parentId && groupIds.Contains(parentId))
            .GroupBy(entry => String(entry.Node, "parentId")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(entry => Number(entry.Node, "index")).ThenBy(entry => entry.Position).ToArray(),
                StringComparer.Ordinal);
        var visited = new HashSet<int>();

        void DrawEntry((JsonElement Node, int Position) entry, HashSet<string> ancestors, bool parentVisible)
        {
            if (!visited.Add(entry.Position)) return;
            bool visible = parentVisible && Bool(entry.Node, "isShow", true);
            if (visible)
                DrawNode(graphics, entry.Node, canvasWidth, canvasHeight, sensors, groupOffsets);

            if (String(entry.Node, "type")?.Equals("group", StringComparison.OrdinalIgnoreCase) != true
                || String(entry.Node, "id") is not { Length: > 0 } groupId
                || !childrenByParent.TryGetValue(groupId, out var groupedChildren)
                || !ancestors.Add(groupId))
                return;

            foreach (var child in groupedChildren
                .OrderByDescending(candidate => Number(candidate.Node, "index"))
                .ThenByDescending(candidate => candidate.Position))
                DrawEntry(child, ancestors, visible);
            ancestors.Remove(groupId);
        }

        var roots = entries
            .Where(entry => String(entry.Node, "parentId") is not { Length: > 0 } parentId || !groupIds.Contains(parentId))
            // In .ht, missing index means the default z-order (zero), not the
            // array position. Using the position here can let a media layer
            // with index=2 cover ordinary layers that have no index field.
            .OrderByDescending(entry => Number(entry.Node, "index"))
            .ThenByDescending(entry => entry.Position);
        foreach (var root in roots)
            DrawEntry(root, new HashSet<string>(StringComparer.Ordinal), parentVisible: true);

        // Keep orphaned children visible if a package references a missing group.
        foreach (var orphan in entries.Where(entry => !visited.Contains(entry.Position))
            .OrderByDescending(entry => Number(entry.Node, "index"))
            .ThenByDescending(entry => entry.Position))
            DrawEntry(orphan, new HashSet<string>(StringComparer.Ordinal), parentVisible: true);
    }

    private void DrawBackground(Graphics graphics, int width, int height)
    {
        Rectangle canvas = new(0, 0, width, height);
        if (_definition.ValueKind != JsonValueKind.Object)
        {
            graphics.Clear(Color.Black);
            return;
        }

        Color[] backgroundColors = ColorPair(_definition, "backgroundColor", Color.Black);
        using (Brush background = MakeBrush(canvas, backgroundColors,
            String(_definition, "backgroundGradient")?.Equals("circle", StringComparison.OrdinalIgnoreCase) == true))
            graphics.FillRectangle(background, canvas);

        string? mediaPath = null;
        JsonElement bgConfig = default;
        if (_definition.TryGetProperty("bgMedia", out JsonElement bgMedia))
            mediaPath = FirstPath(bgMedia);
        if (_definition.TryGetProperty("bgConfig", out JsonElement config))
        {
            bgConfig = config;
        }
        if (string.IsNullOrWhiteSpace(mediaPath))
            mediaPath = String(bgConfig, "src");

        if (mediaPath is null) return;
        Rectangle target = new(0, 0, width, height);
        string fit = (String(bgConfig, "fit") ?? String(_definition, "bgSetting"))?.ToLowerInvariant() ?? "cover";
        if (TryGetImage(mediaPath, out Image? image) && image is not null)
        {
            DrawAnimatedFrame(mediaPath, image);
            DrawBackgroundImage(graphics, image, target, bgConfig, fit);
        }
        else if (TryGetVideoFrame(mediaPath, out Bitmap? videoFrame) && videoFrame is not null)
        {
            using (videoFrame)
                DrawBackgroundImage(graphics, videoFrame, target, bgConfig, fit);
        }
        else if (TryGetImage(_package.PreviewPath, out Image? preview) && preview is not null)
        {
            DrawBackgroundImage(graphics, preview, target, default, fit);
        }
    }

    private static void DrawBackgroundImage(Graphics graphics, Image image, Rectangle target,
        JsonElement cropConfig, string fit)
    {
        if (TryGetBackgroundCrop(cropConfig, image.Size, out RectangleF source))
        {
            graphics.DrawImage(image, new RectangleF(target.X, target.Y, target.Width, target.Height),
                source, GraphicsUnit.Pixel);
            return;
        }

        if (fit is "fill" or "stretch")
            graphics.DrawImage(image, target);
        else
            DrawFittedImage(graphics, image, target, fit);
    }

    private static bool TryGetBackgroundCrop(JsonElement config, Size imageSize, out RectangleF source)
    {
        source = RectangleF.Empty;
        if (config.ValueKind != JsonValueKind.Object
            || imageSize.Width <= 0 || imageSize.Height <= 0
            || !config.TryGetProperty("width", out _)
            || !config.TryGetProperty("height", out _))
            return false;

        bool percent = String(config, "unit")?.Trim() == "%";
        double unitX = percent ? imageSize.Width / 100d : 1d;
        double unitY = percent ? imageSize.Height / 100d : 1d;
        double left = Math.Clamp(Number(config, "x") * unitX, 0, imageSize.Width);
        double top = Math.Clamp(Number(config, "y") * unitY, 0, imageSize.Height);
        double width = Math.Clamp(Number(config, "width") * unitX, 0, imageSize.Width - left);
        double height = Math.Clamp(Number(config, "height") * unitY, 0, imageSize.Height - top);
        if (width < 1 || height < 1)
            return false;

        source = new RectangleF((float)left, (float)top, (float)width, (float)height);
        return true;
    }

    private void DrawNode(Graphics graphics, JsonElement node, int canvasWidth, int canvasHeight, SensorSnapshot sensors,
        IReadOnlyDictionary<string, PointF> groupOffsets)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !Bool(node, "isShow", true)
            || !node.TryGetProperty("children", out JsonElement properties))
            return;

        if (properties.ValueKind == JsonValueKind.Array)
        {
            var children = properties.EnumerateArray()
                .Select((child, index) => (Node: child, Position: index))
                .OrderByDescending(entry => Number(entry.Node, "index"))
                .ThenByDescending(entry => entry.Position);
            foreach (var child in children)
                DrawNode(graphics, child.Node, canvasWidth, canvasHeight, sensors, groupOffsets);
            return;
        }
        if (properties.ValueKind != JsonValueKind.Object)
            return;

        double x = Number(properties, "x");
        double y = Number(properties, "y");
        string type = String(node, "type")?.ToLowerInvariant() ?? "";
        double textHeight = type is "text" or "data" or "lyric"
            ? Math.Clamp(Number(properties, "fontSize", 12), 5, 180)
            : 0;
        bool intrinsicText = type is "text" or "data" or "lyric";
        double w = Number(properties, "width", intrinsicText ? 0 : Number(node, "width"));
        double h = Number(properties, "height", intrinsicText ? textHeight : Number(node, "height"));
        if (type == "arc" && (w <= 0 || h <= 0))
        {
            double arcSize = w > 0 ? w : h > 0 ? h : 120;
            if (w <= 0) w = arcSize;
            if (h <= 0) h = arcSize;
        }
        if (type is ("media" or "image")
            && TryGetMediaDimensions(properties, out Size mediaSize))
        {
            // The original layer wrapper sets height:auto for image/media layers,
            // even when the .ht stores a square editor box. Chromium then derives
            // the rendered height from the source's intrinsic aspect ratio.
            if (w > 0)
                h = w * mediaSize.Height / mediaSize.Width;
            else
            {
                w = mediaSize.Width;
                h = mediaSize.Height;
            }
        }
        if (w <= 0 && h <= 0 && type is not ("text" or "data" or "lyric"))
            return;

        double sx = Scale(node, 0, 1);
        double sy = Scale(node, 1, sx);
        double tx = Number(node, "tx");
        double ty = Number(node, "ty");
        if (String(node, "parentId") is { } parentId && groupOffsets.TryGetValue(parentId, out PointF parentOffset))
        {
            tx += parentOffset.X;
            ty += parentOffset.Y;
        }
        float drawWidth = (float)(w * sx);
        float drawHeight = (float)(h * sy);
        float left = (float)(x + tx + (w - drawWidth) / 2d);
        float top = (float)(y + ty + (h - drawHeight) / 2d);
        float rotate = (float)Number(properties, "rotate");
        if (type is "text" or "data" or "lyric")
        {
            if (w <= 0) { left = (float)(x + tx); drawWidth = 0; }
            if (h <= 0) { top = (float)(y + ty); drawHeight = 0; }
        }
        GraphicsState state = graphics.Save();
        try
        {
            if (rotate != 0 && drawWidth > 0 && drawHeight > 0 && type is not ("text" or "data" or "lyric"))
            {
                graphics.TranslateTransform(left + drawWidth / 2f, top + drawHeight / 2f);
                graphics.RotateTransform(rotate);
                graphics.TranslateTransform(-(left + drawWidth / 2f), -(top + drawHeight / 2f));
            }

            RectangleF bounds = new(left, top, drawWidth, drawHeight);
            switch (type)
            {
                case "block":
                    DrawBlock(graphics, properties, bounds);
                    break;
                case "media":
                case "image":
                    DrawMedia(graphics, properties, bounds);
                    break;
                case "text":
                    DrawText(graphics, properties, node, bounds, sensors, data: false);
                    break;
                case "data":
                    DrawText(graphics, properties, node, bounds, sensors, data: true);
                    break;
                case "lyric":
                    DrawText(graphics, properties, node, bounds, sensors, data: true);
                    break;
                case "progress":
                    DrawProgress(graphics, properties, node, bounds, sensors);
                    break;
                case "arc":
                    DrawArc(graphics, properties, node, bounds, sensors, defaultGapDegree: 75);
                    break;
                case "linechart":
                    DrawLineChart(graphics, properties, node, bounds, sensors);
                    break;
                case "audiowave":
                case "audio":
                    DrawAudioWave(graphics, node, properties, bounds);
                    break;
                case "songdisplay":
                    DrawSongDisplay(graphics, properties, bounds, MediaSessionService.Current);
                    break;
            }
        }
        finally { graphics.Restore(state); }
    }

    private void DrawBlock(Graphics graphics, JsonElement p, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        float radius = (float)Math.Clamp(Number(p, "borderRadius"), 0, Math.Min(bounds.Width, bounds.Height) / 2d);
        using GraphicsPath path = RoundedRectangle(bounds, radius);
        Color[] colors = ColorPair(p, "backgroundColor", Color.FromArgb(180, 70, 70, 70));
        string? gradient = String(p, "linearGradient")?.ToLowerInvariant();
        using Brush fill = string.IsNullOrWhiteSpace(gradient)
            ? new SolidBrush(colors[0])
            : MakeBrush(bounds, colors,
                radial: gradient == "circle",
                linearMode: gradient == "to right" ? LinearGradientMode.Horizontal : LinearGradientMode.Vertical);
        graphics.FillPath(fill, path);
        float borderWidth = (float)Math.Clamp(Number(p, "borderWidth"), 0, 40);
        if (borderWidth > 0)
        {
            using Pen border = CreateBorderPen(p, borderWidth, Color.White);
            graphics.DrawPath(border, path);
        }
    }

    private void DrawMedia(Graphics graphics, JsonElement p, RectangleF bounds)
    {
        string? path = p.TryGetProperty("media", out JsonElement media) ? FirstPath(media) : null;
        if (path is null || bounds.Width <= 0)
            return;
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? mediaUri)
            && (mediaUri.Scheme == Uri.UriSchemeHttp || mediaUri.Scheme == Uri.UriSchemeHttps)
            && !string.Equals(String(p, "mediaLoadType"), "online", StringComparison.OrdinalIgnoreCase))
            return;

        Image? image = null;
        Bitmap? videoFrame = null;
        if (TryGetImage(path, out Image? imageAsset)) image = imageAsset;
        else if (TryGetVideoFrame(path, out videoFrame)) image = videoFrame;
        if (image is null) return;

        try
        {
            RectangleF target = bounds;
            if (target.Height <= 0)
                return;

            float radius = (float)Math.Clamp(Number(p, "borderRadius"), 0, Math.Min(target.Width, target.Height) / 2d);
            using GraphicsPath mediaPath = RoundedRectangle(target, radius);
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(mediaPath, CombineMode.Intersect);
                if (Bool(p, "flipX", false) || Bool(p, "flipY", false))
                {
                    graphics.TranslateTransform(target.Left + target.Width / 2f, target.Top + target.Height / 2f);
                    graphics.ScaleTransform(Bool(p, "flipX", false) ? -1 : 1, Bool(p, "flipY", false) ? -1 : 1);
                    graphics.TranslateTransform(-(target.Left + target.Width / 2f), -(target.Top + target.Height / 2f));
                }
                DrawAnimatedFrame(path, image);
                if (String(p, "fit")?.Equals("fill", StringComparison.OrdinalIgnoreCase) == true)
                    graphics.DrawImage(image, target);
                else
                    DrawCoverImage(graphics, image, target);
            }
            finally { graphics.Restore(state); }

            float borderWidth = (float)Math.Clamp(Number(p, "borderWidth"), 0, 40);
            if (borderWidth > 0)
            {
                using Pen border = CreateBorderPen(p, borderWidth, Color.White);
                graphics.DrawPath(border, mediaPath);
            }
        }
        finally { videoFrame?.Dispose(); }
    }

    internal bool TryGetMediaDimensions(JsonObject properties, out Size size)
    {
        using JsonDocument document = JsonDocument.Parse(properties.ToJsonString());
        return TryGetMediaDimensions(document.RootElement, out size);
    }

    private bool TryGetMediaDimensions(JsonElement properties, out Size size)
    {
        size = Size.Empty;
        string? path = properties.TryGetProperty("media", out JsonElement media) ? FirstPath(media) : null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (_mediaSizes.TryGetValue(path, out size)) return size.Width > 0 && size.Height > 0;

        if (TryGetImage(path, out Image? image) && image is not null)
            size = image.Size;
        else if (TryGetVideoFrame(path, out Bitmap? frame) && frame is not null)
        {
            using (frame) size = frame.Size;
        }
        else if (_videos.TryGetValue(path, out HtVideoFrameSource? videoSource))
            size = videoSource.NaturalVideoSize;

        if (size.Width <= 0 || size.Height <= 0) return false;
        _mediaSizes[path] = size;
        return true;
    }

    private static void DrawCoverImage(Graphics graphics, Image image, RectangleF target)
    {
        if (image.Width <= 0 || image.Height <= 0 || target.Width <= 0 || target.Height <= 0)
            return;

        double imageAspect = (double)image.Width / image.Height;
        double targetAspect = target.Width / target.Height;
        RectangleF source = new(0, 0, image.Width, image.Height);
        if (imageAspect > targetAspect)
        {
            float cropWidth = (float)(image.Height * targetAspect);
            source.X = (image.Width - cropWidth) / 2f;
            source.Width = cropWidth;
        }
        else if (imageAspect < targetAspect)
        {
            float cropHeight = (float)(image.Width / targetAspect);
            source.Y = (image.Height - cropHeight) / 2f;
            source.Height = cropHeight;
        }

        graphics.DrawImage(image, target, source, GraphicsUnit.Pixel);
    }

    private void DrawSongDisplay(Graphics graphics, JsonElement p, RectangleF bounds, MediaSessionSnapshot media)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        GraphicsState state = graphics.Save();
        try
        {
            graphics.SetClip(bounds);
            float padding = (float)Math.Clamp(Number(p, "padding"), 0, Math.Min(bounds.Width, bounds.Height) / 2d);
            const float contentPadding = 16;
            float radius = (float)Math.Clamp(Number(p, "backgroundBorderRadius"), 0,
                Math.Min(240, Math.Min(bounds.Width, bounds.Height) / 2d));
            using GraphicsPath card = RoundedRectangle(bounds, radius);
            string preset = String(p, "presetMode") ?? (bounds.Width >= bounds.Height ? "coverOnly" : "vFixed");
            string layout = String(p, "layout") ?? (preset switch
            {
                "hFixed" or "hCenter" => "horizontal",
                "vFixed" or "vCenter" => "vertical",
                "coverOnly" => "coverOnly",
                "lyricOnly" => "lyricOnly",
                _ => bounds.Width >= bounds.Height ? "horizontal" : "vertical"
            });
            bool horizontal = layout.Equals("horizontal", StringComparison.OrdinalIgnoreCase);
            bool vertical = layout.Equals("vertical", StringComparison.OrdinalIgnoreCase);
            bool coverOnly = layout.Equals("coverOnly", StringComparison.OrdinalIgnoreCase)
                || preset.Equals("coverOnly", StringComparison.OrdinalIgnoreCase);
            bool lyricOnly = layout.Equals("lyricOnly", StringComparison.OrdinalIgnoreCase)
                || preset.Equals("lyricOnly", StringComparison.OrdinalIgnoreCase);
            bool showCover = Bool(p, "showCover", true) && !lyricOnly;
            bool showTitle = Bool(p, "showTitle", true) && !coverOnly;
            bool showArtist = Bool(p, "showArtist", true) && !coverOnly;
            bool showLyrics = Bool(p, "showLyrics", true) && !coverOnly;
            Image? coverImage = media.HasMedia ? GetMediaCover(media) : null;
            DrawSongBackground(graphics, p, bounds, card, coverImage, preset);
            if (!media.HasMedia)
            {
                if (Bool(p, "showNoMediaHint", true))
                {
                    string hint = String(p, "noMediaHint") ?? "Nenhuma midia detectada";
                    DrawMediaLine(graphics, hint, String(p, "lyricColor"), Color.FromArgb(153, 255, 255, 255),
                        String(p, "lyricFontFamily") ?? String(p, "fontFamily") ?? "Arial",
                        (float)Math.Clamp(Number(p, "noMediaHintFontSize", 14), 8, 999),
                        RectangleF.Inflate(bounds, -padding, -padding), false, .5f);
                }
                return;
            }
            RectangleF contentBounds = RectangleF.Inflate(bounds, -padding, -padding);
            RectangleF coverBounds = RectangleF.Empty;
            bool reserveCover = media.HasMedia && showCover;

            if (coverOnly)
            {
                if (reserveCover)
                    coverBounds = contentBounds;
            }
            else if (horizontal && reserveCover)
            {
                bool coverRight = String(p, "coverPosition")?.Equals("right", StringComparison.OrdinalIgnoreCase) == true;
                float coverWidth;
                float coverHeight;
                float coverY;
                if (preset == "hFixed")
                {
                    float aspect = coverImage is { Height: > 0 } ? (float)coverImage.Width / coverImage.Height : 1f;
                    coverHeight = contentBounds.Height;
                    coverWidth = Math.Min(coverHeight * aspect, contentBounds.Width * .5f);
                    coverY = contentBounds.Top;
                }
                else
                {
                    float side = Math.Min(contentBounds.Height * .8f, contentBounds.Width * .5f);
                    string anchor = String(p, "coverAnchor") ?? "center";
                    coverWidth = coverHeight = side;
                    coverY = anchor.Equals("end", StringComparison.OrdinalIgnoreCase)
                        ? contentBounds.Bottom - side
                        : anchor.Equals("center", StringComparison.OrdinalIgnoreCase)
                            ? contentBounds.Top + (contentBounds.Height - side) / 2
                            : contentBounds.Top;
                }
                float coverX = coverRight ? contentBounds.Right - coverWidth : contentBounds.Left;
                coverBounds = new RectangleF(coverX, coverY, coverWidth, coverHeight);
                float gap = preset == "hFixed" ? 0 : (float)Math.Clamp(Number(p, "gap", 16), 0, 80);
                contentBounds = coverRight
                    ? RectangleF.FromLTRB(contentBounds.Left, contentBounds.Top, coverBounds.Left - gap, contentBounds.Bottom)
                    : RectangleF.FromLTRB(coverBounds.Right + gap, contentBounds.Top, contentBounds.Right, contentBounds.Bottom);
            }
            else if (vertical && reserveCover)
            {
                float side = Math.Min(contentBounds.Width * .8f, contentBounds.Height * .8f);
                bool coverBottom = String(p, "coverPosition")?.Equals("bottom", StringComparison.OrdinalIgnoreCase) == true;
                string anchor = String(p, "coverAnchor") ?? (preset == "vFixed" ? "start" : "center");
                float coverHeight;
                if (preset == "vFixed")
                {
                    float aspect = coverImage is { Width: > 0 } ? (float)coverImage.Width / coverImage.Height : 1f;
                    coverHeight = Math.Min(contentBounds.Width / Math.Max(.01f, aspect), contentBounds.Height * .5f);
                }
                else coverHeight = side;
                float coverY = coverBottom ? contentBounds.Bottom - coverHeight : contentBounds.Top;
                float coverX = anchor.Equals("start", StringComparison.OrdinalIgnoreCase)
                    ? contentBounds.Left
                    : anchor.Equals("end", StringComparison.OrdinalIgnoreCase)
                        ? contentBounds.Right - side
                        : contentBounds.Left + (contentBounds.Width - side) / 2;
                coverBounds = preset == "vFixed"
                    ? new RectangleF(contentBounds.Left, coverY, contentBounds.Width, coverHeight)
                    : new RectangleF(coverX, coverY, side, side);
                float gap = preset == "vFixed" ? 0 : (float)Math.Clamp(Number(p, "gap", 16), 0, 80);
                contentBounds = coverBottom
                    ? RectangleF.FromLTRB(contentBounds.Left, contentBounds.Top, contentBounds.Right, coverBounds.Top - gap)
                    : RectangleF.FromLTRB(contentBounds.Left, coverBounds.Bottom + gap, contentBounds.Right, contentBounds.Bottom);
            }

            if (!coverOnly)
            {
                contentBounds = RectangleF.Inflate(contentBounds, -contentPadding, -contentPadding);
                if (preset == "hCenter" && contentBounds.Width > bounds.Width * .5f)
                {
                    float centeredWidth = bounds.Width * .5f;
                    contentBounds = new RectangleF(contentBounds.Left + (contentBounds.Width - centeredWidth) / 2,
                        contentBounds.Top, centeredWidth, contentBounds.Height);
                }
            }

            if (coverImage is not null && coverBounds.Width > 1 && coverBounds.Height > 1)
                DrawAlbumCover(graphics, coverImage, coverBounds, p, preset, media.IsPlaying);
            

            if (media.HasMedia)
            {
                float contentTop = contentBounds.Top;
                if (showTitle)
                {
                    float size = (float)Math.Clamp(Number(p, "titleFontSize", 42), 8, 999);
                    float lineHeight = Math.Min(contentBounds.Height, Math.Max(size * 1.3f, 12));
                    DrawMediaLine(graphics, string.IsNullOrWhiteSpace(media.Title) ? "—" : media.Title, String(p, "titleColor"), Color.FromArgb(204, 255, 255, 255),
                        String(p, "titleFontFamily") ?? String(p, "fontFamily") ?? "Arial", size,
                        new RectangleF(contentBounds.Left, contentTop, contentBounds.Width, lineHeight), true);
                    contentTop += lineHeight;
                }
                if (showArtist)
                {
                    float size = (float)Math.Clamp(Number(p, "artistFontSize", 32), 8, 999);
                    float lineHeight = Math.Min(Math.Max(10, contentBounds.Bottom - contentTop), Math.Max(size * 1.35f, 10));
                    DrawMediaLine(graphics, string.IsNullOrWhiteSpace(media.Artist) ? "—" : media.Artist,
                        String(p, "artistColor"), Color.FromArgb(204, 255, 255, 255),
                        String(p, "artistFontFamily") ?? String(p, "fontFamily") ?? "Arial", size,
                        new RectangleF(contentBounds.Left, contentTop, contentBounds.Width, lineHeight), false);
                    contentTop += lineHeight + padding * .25f;
                }
                contentBounds = RectangleF.FromLTRB(contentBounds.Left, Math.Min(contentBounds.Bottom, contentTop),
                    contentBounds.Right, contentBounds.Bottom);
            }

            if (showLyrics && contentBounds.Width > 1 && contentBounds.Height > 1)
            {
                if (Bool(p, "lyricScroll", true) && media.Lyrics.Count > 0)
                    DrawSongLyricsWithMask(graphics, p, contentBounds, media);
                else
                    DrawSongLyrics(graphics, p, contentBounds, media);
            }
        }
        finally { graphics.Restore(state); }
    }

    private void DrawSongLyricsWithMask(Graphics target, JsonElement properties, RectangleF bounds,
        MediaSessionSnapshot media)
    {
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        if (_songLyricsMaskBuffer is null || _songLyricsMaskBuffer.Width != width || _songLyricsMaskBuffer.Height != height)
        {
            _songLyricsMaskBuffer?.Dispose();
            _songLyricsMaskBuffer = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        using (Graphics lyrics = Graphics.FromImage(_songLyricsMaskBuffer))
        {
            Configure(lyrics);
            lyrics.Clear(Color.Transparent);
            lyrics.TranslateTransform(-bounds.Left, -bounds.Top);
            DrawSongLyrics(lyrics, properties, bounds, media);
        }
        ApplySongLyricsEdgeMask(_songLyricsMaskBuffer);

        GraphicsState state = target.Save();
        try
        {
            target.SetClip(bounds, CombineMode.Intersect);
            target.DrawImage(_songLyricsMaskBuffer, bounds);
        }
        finally { target.Restore(state); }
    }

    private static void ApplySongLyricsEdgeMask(Bitmap bitmap)
    {
        Rectangle rectangle = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            float fadeHeight = Math.Max(1, bitmap.Height * .05f);
            for (int y = 0; y < bitmap.Height; y++)
            {
                float fade = Math.Min(y / fadeHeight, (bitmap.Height - 1 - y) / fadeHeight);
                fade = Math.Clamp(fade, 0, 1);
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int alphaIndex = row + x * 4 + 3;
                    pixels[alphaIndex] = (byte)Math.Round(pixels[alphaIndex] * fade);
                }
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private Image? GetMediaCover(MediaSessionSnapshot media)
    {
        if (media.CoverImageBytes is not { Length: > 0 } bytes) return null;
        string key = string.Join('\u001f', media.AppId, media.Title, media.Artist, media.Album);
        if (string.Equals(key, _mediaCoverKey, StringComparison.Ordinal)) return _mediaCoverImage;
        _mediaCoverImage?.Dispose();
        _mediaCoverImage = null;
        _mediaBlurredCoverImage?.Dispose();
        _mediaBlurredCoverImage = null;
        _mediaCoverKey = key;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using Image decoded = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            _mediaCoverImage = new Bitmap(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException or IOException)
        {
            _mediaCoverKey = null;
        }
        return _mediaCoverImage;
    }

    private Image? GetBlurredMediaCover(Image? source)
    {
        if (source is null) return null;
        if (_mediaBlurredCoverImage is not null) return _mediaBlurredCoverImage;
        int width = Math.Clamp(source.Width / 12, 24, 96);
        int height = Math.Clamp(source.Height / 12, 16, 64);
        var small = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        BlurSmallBitmap(small, Math.Clamp(Math.Min(width, height) / 7, 3, 8), passes: 3);
        _mediaBlurredCoverImage = small;
        return _mediaBlurredCoverImage;
    }

    private void DrawSongBackground(Graphics graphics, JsonElement p, RectangleF bounds, GraphicsPath clip,
        Image? coverImage, string preset)
    {
        bool showBackground = Bool(p, "showBackground", true)
            && !preset.Equals("lyricOnly", StringComparison.OrdinalIgnoreCase);
        string? configuredBackground = String(p, "backgroundColor");
        bool hasConfiguredBackground = !string.IsNullOrWhiteSpace(configuredBackground);
        if (!showBackground && !hasConfiguredBackground) return;

        float opacity = (float)Math.Clamp(Number(p, "backgroundOpacity", .8), 0, 1);
        double radius = Math.Clamp(Number(p, "backgroundBorderRadius"), 0,
            Math.Min(240, Math.Min(bounds.Width, bounds.Height) / 2d));
        using GraphicsPath backgroundPath = radius > 0 ? RoundedRectangle(bounds, (float)radius) : (GraphicsPath)clip.Clone();
        GraphicsState state = graphics.Save();
        try
        {
            graphics.SetClip(backgroundPath, CombineMode.Intersect);
            Color background = ParseColor(configuredBackground, Color.Transparent);
            if (background.A > 0)
            {
                using var backgroundBrush = new SolidBrush(Color.FromArgb(
                    (int)Math.Round(background.A * opacity), background.R, background.G, background.B));
                graphics.FillPath(backgroundBrush, backgroundPath);
            }

            if (showBackground && coverImage is not null)
            {
                Image blurred = GetBlurredMediaCover(coverImage) ?? coverImage;
                using var attributes = new ImageAttributes();
                const float saturation = 1.35f;
                const float inverseSaturation = 1 - saturation;
                const float luminanceRed = .3086f;
                const float luminanceGreen = .6094f;
                const float luminanceBlue = .0820f;
                var matrix = new ColorMatrix(new[]
                {
                    new[] { luminanceRed * inverseSaturation + saturation, luminanceRed * inverseSaturation, luminanceRed * inverseSaturation, 0f, 0f },
                    new[] { luminanceGreen * inverseSaturation, luminanceGreen * inverseSaturation + saturation, luminanceGreen * inverseSaturation, 0f, 0f },
                    new[] { luminanceBlue * inverseSaturation, luminanceBlue * inverseSaturation, luminanceBlue * inverseSaturation + saturation, 0f, 0f },
                    new[] { 0f, 0f, 0f, opacity, 0f },
                    new[] { 0f, 0f, 0f, 0f, 1f }
                });
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                float bleedX = bounds.Width * .1f;
                float bleedY = bounds.Height * .1f;
                Rectangle destination = Rectangle.Round(new RectangleF(bounds.Left - bleedX, bounds.Top - bleedY,
                    bounds.Width + bleedX * 2, bounds.Height + bleedY * 2));
                graphics.DrawImage(blurred, destination, 0, 0, blurred.Width, blurred.Height, GraphicsUnit.Pixel, attributes);

                using var overlay = new SolidBrush(Color.FromArgb((int)Math.Round(115 * opacity), 0, 0, 0));
                graphics.FillPath(overlay, backgroundPath);
            }
        }
        finally { graphics.Restore(state); }
    }

    private static void BlurSmallBitmap(Bitmap bitmap, int radius, int passes)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = bitmap.GetPixel(x, y).ToArgb();

        var intermediate = new int[pixels.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                intermediate[y * width + x] = AveragePixel(pixels, width, height, x, y, radius, horizontal: true);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = AveragePixel(intermediate, width, height, x, y, radius, horizontal: false);
        }

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(pixels[y * width + x]));
    }

    private static int AveragePixel(int[] pixels, int width, int height, int x, int y, int radius, bool horizontal)
    {
        int a = 0, r = 0, g = 0, b = 0, count = 0;
        for (int offset = -radius; offset <= radius; offset++)
        {
            int px = horizontal ? Math.Clamp(x + offset, 0, width - 1) : x;
            int py = horizontal ? y : Math.Clamp(y + offset, 0, height - 1);
            int color = pixels[py * width + px];
            a += (int)((uint)color >> 24);
            r += (color >> 16) & 0xff;
            g += (color >> 8) & 0xff;
            b += color & 0xff;
            count++;
        }
        return (a / count << 24) | (r / count << 16) | (g / count << 8) | (b / count);
    }

    private static void DrawAlbumCover(Graphics graphics, Image image, RectangleF target, JsonElement p, string preset, bool isPlaying)
    {
        bool usesRotatingCover = preset is "hCenter" or "vCenter" or "coverOnly";
        GraphicsState state = graphics.Save();
        try
        {
            using GraphicsPath clip = new();
            if (usesRotatingCover && Bool(p, "coverCircular", true)) clip.AddEllipse(target);
            else clip.AddRectangle(target);
            graphics.SetClip(clip, CombineMode.Intersect);

            float angle = 0;
            if (usesRotatingCover && p.TryGetProperty("coverRotate", out JsonElement rotation)
                && rotation.ValueKind == JsonValueKind.Number && rotation.TryGetSingle(out float fixedAngle))
            {
                angle = fixedAngle;
            }
            else if (usesRotatingCover && Bool(p, "coverRotate", true) && isPlaying)
            {
                double seconds = Math.Clamp(Number(p, "coverRotateDuration", 12), 1, 300);
                angle = (float)((Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency % seconds) * (360 / seconds));
            }

            graphics.TranslateTransform(target.Left + target.Width / 2, target.Top + target.Height / 2);
            if (Math.Abs(angle) > .01f) graphics.RotateTransform(angle);
            graphics.TranslateTransform(-(target.Left + target.Width / 2), -(target.Top + target.Height / 2));
            string fill = String(p, "coverFill") ?? (preset == "vFixed" ? "width" : "auto");
            string fit = fill.Equals("width", StringComparison.OrdinalIgnoreCase)
                || fill.Equals("contain", StringComparison.OrdinalIgnoreCase) ? "contain" : "cover";
            DrawFittedImage(graphics, image, Rectangle.Round(target), fit);
        }
        finally { graphics.Restore(state); }
    }

    private void DrawSongLyrics(Graphics graphics, JsonElement p, RectangleF bounds, MediaSessionSnapshot media)
    {
        if (media.Lyrics.Count == 0)
        {
            _lyricScrollTrackKey = null;
            if (!media.HasMedia && Bool(p, "showNoMediaHint", true))
            {
                string hint = String(p, "noMediaHint") ?? "Reproduza uma musica para ver as letras";
                DrawMediaLine(graphics, hint, String(p, "lyricColor"), Color.FromArgb(153, 255, 255, 255),
                    String(p, "lyricFontFamily") ?? String(p, "fontFamily") ?? "Arial", 14, bounds, false, .5f);
            }
            else if (media.HasMedia)
            {
                float size = (float)Math.Clamp(Number(p, "lyricActiveFontSize", 32), 8, 999);
                DrawMediaLine(graphics, "\u00b7\u00b7\u00b7", String(p, "lyricActiveColor"), Color.White,
                    String(p, "lyricFontFamily") ?? String(p, "fontFamily") ?? "Arial", size, bounds, false);
            }
            return;
        }

        var groups = new List<(long Time, List<string> Lines)>();
        foreach (SyncedLyricLine lyric in media.Lyrics)
        {
            if (groups.Count > 0 && Math.Abs(lyric.TimeMilliseconds - groups[^1].Time) < 50)
                groups[^1].Lines.Add(lyric.Text);
            else
                groups.Add((lyric.TimeMilliseconds, [lyric.Text]));
        }

        int activeIndex = -1;
        if (media.LyricsAreSynchronized)
        {
            long currentPosition = media.CurrentPositionMilliseconds;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Time > currentPosition) break;
                activeIndex = i;
            }
        }
        var highlight = UpdateSongLyricHighlight(media, activeIndex);

        int maximumLines = (int)Math.Clamp(Number(p, "lyricMaxLines", 5), 1, 99);
        float normalSize = (float)Math.Clamp(Number(p, "lyricFontSize", 24), 8, 999);
        float activeSize = (float)Math.Clamp(Number(p, "lyricActiveFontSize", 32), 8, 999);
        string lyricFontFamily = String(p, "lyricFontFamily") ?? String(p, "fontFamily") ?? "Arial";
        float lineSlotHeight = Math.Max(normalSize, activeSize) * 1.2f + 12;
        float lineInset = Math.Min(8f, bounds.Width / 2f);
        RectangleF lineBounds = RectangleF.FromLTRB(
            bounds.Left + lineInset, bounds.Top, bounds.Right - lineInset, bounds.Bottom);
        float[] heights = new float[groups.Count];
        float[] starts = new float[groups.Count];
        float trackHeight = 0;
        bool scroll = Bool(p, "lyricScroll", true);
        for (int index = 0; index < groups.Count; index++)
        {
            starts[index] = trackHeight;
            float activeMix = LyricHighlightMix(index, highlight);
            float fontSize = normalSize + (activeSize - normalSize) * activeMix;
            float contentHeight = 0;
            for (int textIndex = 0; textIndex < groups[index].Lines.Count; textIndex++)
            {
                float textSize = textIndex == 0 ? fontSize : fontSize * .85f;
                contentHeight += MeasureWrappedLines(graphics, groups[index].Lines[textIndex], textSize,
                    lineBounds.Width, lyricFontFamily) * textSize * 1.2f;
                if (textIndex > 0) contentHeight += fontSize * .2f;
            }
            heights[index] = scroll ? Math.Max(lineSlotHeight, contentHeight + 8) : contentHeight + 8;
            trackHeight += heights[index] + (!scroll && index > 0 ? 4 : 0);
        }

        if (scroll)
        {
            float averageSlotHeight = groups.Count == 0 ? lineSlotHeight : trackHeight / groups.Count;
            float viewportHeight = Math.Min(bounds.Height,
                Math.Max(lineSlotHeight, averageSlotHeight) * maximumLines);
            float viewportTop = bounds.Top + (bounds.Height - viewportHeight) / 2f;
            float targetOffset = activeIndex < 0
                ? 0
                : starts[activeIndex] + heights[activeIndex] / 2f - viewportHeight / 2f;
            targetOffset = Math.Clamp(targetOffset, 0, Math.Max(0, trackHeight - viewportHeight));
            string trackKey = string.Join('\u001f', media.AppId, media.Title, media.Artist, media.Album,
                groups.Count, groups[0].Time, groups[^1].Time);
            float offset = UpdateLyricScroll(trackKey, activeIndex, targetOffset);
            GraphicsState viewportState = graphics.Save();
            try
            {
                graphics.SetClip(new RectangleF(bounds.Left, viewportTop, bounds.Width, viewportHeight), CombineMode.Intersect);
                float y = viewportTop - offset;
                for (int index = 0; index < groups.Count; index++)
                {
                    float bottom = y + heights[index];
                    if (bottom >= viewportTop && y <= viewportTop + viewportHeight)
                        DrawLyricGroup(graphics, p, lineBounds, groups[index].Lines, y, heights[index],
                            LyricHighlightMix(index, highlight), normalSize, activeSize);
                    y = bottom;
                    if (y > viewportTop + viewportHeight) break;
                }
            }
            finally { graphics.Restore(viewportState); }
            return;
        }

        _lyricScrollTrackKey = null;
        int firstLine = activeIndex < 0 ? 0 : activeIndex - (maximumLines - 1) / 2;
        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, groups.Count - maximumLines));
        int lastLine = Math.Min(groups.Count, firstLine + maximumLines);
        float visibleHeight = heights.Skip(firstLine).Take(lastLine - firstLine).Sum()
            + Math.Max(0, lastLine - firstLine - 1) * 4;
        float top = bounds.Top + Math.Max(0, (bounds.Height - visibleHeight) / 2f);
        for (int index = firstLine; index < lastLine; index++)
        {
            DrawLyricGroup(graphics, p, lineBounds, groups[index].Lines, top, heights[index],
                LyricHighlightMix(index, highlight), normalSize, activeSize);
            top += heights[index] + 4;
        }
    }

    private (int PreviousIndex, int ActiveIndex, float Progress) UpdateSongLyricHighlight(
        MediaSessionSnapshot media, int activeIndex)
    {
        string trackKey = string.Join('\u001f', media.AppId, media.Title, media.Artist, media.Album);
        long now = Stopwatch.GetTimestamp();
        if (!string.Equals(trackKey, _songLyricHighlightTrackKey, StringComparison.Ordinal))
        {
            _songLyricHighlightTrackKey = trackKey;
            _songLyricHighlightIndex = activeIndex;
            _songLyricPreviousHighlightIndex = -1;
            _songLyricHighlightStartedAt = now;
            return (-1, activeIndex, 1);
        }

        if (activeIndex != _songLyricHighlightIndex)
        {
            _songLyricPreviousHighlightIndex = _songLyricHighlightIndex;
            _songLyricHighlightIndex = activeIndex;
            _songLyricHighlightStartedAt = now;
        }

        double elapsedMilliseconds = Stopwatch.GetElapsedTime(_songLyricHighlightStartedAt, now).TotalMilliseconds;
        float progress = CubicBezierEasing((float)Math.Clamp(elapsedMilliseconds / 200d, 0, 1), .25, .1, .25, 1);
        if (progress >= 1) _songLyricPreviousHighlightIndex = -1;
        return (_songLyricPreviousHighlightIndex, _songLyricHighlightIndex, progress);
    }

    private static float LyricHighlightMix(int groupIndex, (int PreviousIndex, int ActiveIndex, float Progress) highlight)
    {
        if (groupIndex == highlight.ActiveIndex) return highlight.Progress;
        if (groupIndex == highlight.PreviousIndex) return 1 - highlight.Progress;
        return 0;
    }

    private float UpdateLyricScroll(string trackKey, int activeIndex, float targetOffset)
    {
        long now = Stopwatch.GetTimestamp();
        if (!string.Equals(trackKey, _lyricScrollTrackKey, StringComparison.Ordinal))
        {
            _lyricScrollTrackKey = trackKey;
            _lyricScrollActiveIndex = activeIndex;
            _lyricScrollFrom = _lyricScrollTarget = targetOffset;
            _lyricScrollStartedAt = now;
            return targetOffset;
        }

        double elapsedMilliseconds = Stopwatch.GetElapsedTime(_lyricScrollStartedAt, now).TotalMilliseconds;
        float progress = (float)Math.Clamp(elapsedMilliseconds / 300d, 0, 1);
        float eased = CubicBezierEasing(progress, .33, 1, .68, 1);
        float currentOffset = _lyricScrollFrom + (_lyricScrollTarget - _lyricScrollFrom) * eased;
        if (activeIndex != _lyricScrollActiveIndex || Math.Abs(targetOffset - _lyricScrollTarget) > .5f)
        {
            _lyricScrollFrom = currentOffset;
            _lyricScrollTarget = targetOffset;
            _lyricScrollStartedAt = now;
            _lyricScrollActiveIndex = activeIndex;
            return currentOffset;
        }
        return currentOffset;
    }

    private static float CubicBezierEasing(float progress, double x1, double y1, double x2, double y2)
    {
        double targetX = Math.Clamp(progress, 0, 1);
        double lower = 0;
        double upper = 1;
        double parameter = targetX;
        for (int iteration = 0; iteration < 16; iteration++)
        {
            double inverse = 1 - parameter;
            double curveX = 3 * inverse * inverse * parameter * x1
                + 3 * inverse * parameter * parameter * x2
                + parameter * parameter * parameter;
            if (curveX < targetX) lower = parameter;
            else upper = parameter;
            parameter = (lower + upper) / 2;
        }
        double inverseParameter = 1 - parameter;
        return (float)(3 * inverseParameter * inverseParameter * parameter * y1
            + 3 * inverseParameter * parameter * parameter * y2
            + parameter * parameter * parameter);
    }

    private void DrawLyricGroup(Graphics graphics, JsonElement p, RectangleF bounds,
        IReadOnlyList<string> texts, float top, float slotHeight, float activeMix, float normalSize, float activeSize)
    {
        activeMix = Math.Clamp(activeMix, 0, 1);
        float size = normalSize + (activeSize - normalSize) * activeMix;
        Color normalColor = ParseColor(String(p, "lyricColor"), Color.White);
        Color activeColor = ParseColor(String(p, "lyricActiveColor"), Color.White);
        Color lineColor = BlendColors(normalColor, activeColor, activeMix);
        string fontFamily = String(p, "lyricFontFamily") ?? String(p, "fontFamily") ?? "Arial";
        float blockHeight = 0;
        for (int index = 0; index < texts.Count; index++)
        {
            float textSize = index == 0 ? size : size * .85f;
            blockHeight += MeasureWrappedLines(graphics, texts[index], textSize, bounds.Width, fontFamily) * textSize * 1.2f;
            if (index > 0) blockHeight += size * .2f;
        }
        float y = top + Math.Max(0, (slotHeight - blockHeight) / 2f);
        for (int index = 0; index < texts.Count; index++)
        {
            bool secondary = index > 0;
            float textSize = secondary ? size * .85f : size;
            float textHeight = MeasureWrappedLines(graphics, texts[index], textSize, bounds.Width, fontFamily) * textSize * 1.2f;
            float inactiveOpacity = secondary ? .323f : .38f;
            float activeOpacity = secondary ? .85f : 1f;
            float opacity = inactiveOpacity + (activeOpacity - inactiveOpacity) * activeMix;
            DrawMediaLine(graphics, texts[index], null, lineColor,
                fontFamily,
                textSize, new RectangleF(bounds.Left, y, bounds.Width, textHeight), false, opacity, wrap: true);
            y += textHeight + (secondary ? 0 : size * .2f);
        }
    }

    private static Color BlendColors(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private int MeasureWrappedLines(Graphics graphics, string text, float fontSize, float width, string fontFamily)
    {
        if (string.IsNullOrEmpty(text) || width <= 1) return 1;
        int pixelWidth = Math.Max(1, (int)Math.Round(width));
        int fontSizeHalf = Math.Max(1, (int)Math.Round(fontSize * 2));
        var key = (text, pixelWidth, fontSizeHalf, fontFamily);
        if (_lyricWrapLineCache.TryGetValue(key, out int cached)) return cached;

        using Font font = CreateFont(fontFamily, fontSizeHalf / 2f, FontStyle.Regular);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.LineLimit
        };
        float layoutHeight = Math.Max(1000, font.GetHeight(graphics) * 100);
        SizeF measured = graphics.MeasureString(text, font, new SizeF(pixelWidth, layoutHeight), format);
        int lines = Math.Clamp((int)Math.Round(measured.Height / Math.Max(1, font.Size * 1.2f)), 1, 100);
        if (_lyricWrapLineCache.Count >= 8192) _lyricWrapLineCache.Clear();
        _lyricWrapLineCache[key] = lines;
        return lines;
    }
    private void DrawAudioWave(Graphics graphics, JsonElement node, JsonElement properties, RectangleF bounds)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        int count = (int)Math.Clamp(Number(properties, "audioLineCount", 20), 20, 256);
        int sampleCount = 256;
        float[] samples = AudioWaveCapture.ReadLatest(sampleCount);
        var levels = new float[count];
        for (int bar = 0; bar < count; bar++)
        {
            int first = bar * samples.Length / count;
            int last = Math.Max(first + 1, (bar + 1) * samples.Length / count);
            float total = 0;
            int samplesInBar = 0;
            for (int i = first; i < Math.Min(last, samples.Length); i++)
            {
                total += Math.Abs(samples[i]);
                samplesInBar++;
            }
            levels[bar] = samplesInBar == 0 ? 0 : total / samplesInBar;
        }

        if (levels.Average() >= .001f)
        {
            float minimum = levels.Min();
            float range = levels.Max() - minimum;
            if (range > .0001f)
            {
                for (int bar = 0; bar < levels.Length; bar++)
                    levels[bar] = (levels[bar] - minimum) / range;
            }
            else
            {
                Array.Fill(levels, .5f);
            }
        }
        else Array.Clear(levels);

        if (Bool(properties, "arrangeArrayWithMaxInMiddle", false))
        {
            float[] ranked = levels.OrderDescending().ToArray();
            int middle = count / 2;
            for (int rank = 0; rank < ranked.Length; rank++)
            {
                int distance = (rank + 1) / 2;
                int destination = rank == 0 ? middle : rank % 2 == 1 ? middle - distance : middle + distance;
                if ((uint)destination < (uint)levels.Length) levels[destination] = ranked[rank];
            }
        }

        string id = String(node, "id") ?? String(node, "label") ?? "audio-wave";
        if (_audioWavePeaks.TryGetValue(id, out float[]? previous) && previous.Length == levels.Length)
        {
            float reduction = (float)Math.Clamp(Number(properties, "reduceSpeed", 10), 3, 18);
            float release = .012f + (reduction - 3f) * .004f;
            float decay = Math.Max(.002f, bounds.Height * release / Math.Max(bounds.Height, 1));
            for (int i = 0; i < levels.Length; i++)
                levels[i] = levels[i] >= previous[i] ? levels[i] : Math.Max(levels[i], previous[i] - decay);
        }
        _audioWavePeaks[id] = levels;

        Color[] colors = ColorPair(properties, "backgroundColor", Color.FromArgb(255, 90, 168, 255));
        string? gradient = String(properties, "linearGradient")?.ToLowerInvariant();
        using Brush barBrush = string.IsNullOrWhiteSpace(gradient)
            ? new SolidBrush(colors[0])
            : MakeBrush(bounds, colors,
                radial: gradient == "circle",
                linearMode: gradient == "to right" ? LinearGradientMode.Horizontal : LinearGradientMode.Vertical);
        float cellWidth = bounds.Width / count;
        float configuredMargin = (float)Math.Clamp(Number(properties, "margin", 1), 0, 5);
        float gap = Math.Min(configuredMargin, cellWidth * .45f);
        float barWidth = Math.Max(.25f, cellWidth - gap);
        for (int bar = 0; bar < count; bar++)
        {
            float barHeight = bounds.Height * levels[bar];
            if (barHeight < .5f) continue;
            float left = bounds.Left + bar * cellWidth + (cellWidth - barWidth) / 2f;
            graphics.FillRectangle(barBrush, left, bounds.Bottom - barHeight, barWidth, barHeight);
        }
    }

    private static void DrawMediaLine(Graphics graphics, string text, string? configuredColor, Color fallback,
        string fontFamily, float fontSize, RectangleF bounds, bool bold, float opacity = 1, bool wrap = false)
    {
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 1 || bounds.Height <= 1) return;
        using Font font = CreateFont(fontFamily, fontSize, bold ? FontStyle.Bold : FontStyle.Regular);
        Color parsedColor = ParseColor(configuredColor, fallback);
        using var brush = new SolidBrush(Color.FromArgb(
            (int)Math.Round(parsedColor.A * Math.Clamp(opacity, 0, 1)), parsedColor.R, parsedColor.G, parsedColor.B));
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = wrap ? StringTrimming.None : StringTrimming.EllipsisCharacter,
            FormatFlags = wrap ? StringFormatFlags.LineLimit : StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static void DrawText(Graphics graphics, JsonElement p, JsonElement node, RectangleF bounds,
        SensorSnapshot sensors, bool data)
    {
        string? value = ResolveTextValue(p, data, sensors);
        if (string.IsNullOrEmpty(value)) return;
        bool currentLyric = Bool(p, "isLyric", false)
            || String(p, "dataSource")?.Equals("lyric_current", StringComparison.OrdinalIgnoreCase) == true;

        string fontName = String(p, "fontFamily") ?? "Arial";
        float baseFontSize = (float)Math.Clamp(Number(p, "fontSize", 12), 5, 180);
        float scale = (float)Math.Clamp(Scale(node, 0, 1), .1, 8);
        float scaleY = (float)Math.Clamp(Scale(node, 1, scale), .1, 8);
        float fontSize = baseFontSize * scale;
        Color color = ParseColor(String(p, "color"), Color.White);
        FontStyle style = FontStyleFrom(p);
        using Font baseFont = CreateFont(fontName, baseFontSize, style);
        using Font font = CreateFont(fontName, fontSize, FontStyleFrom(p));
        using var brush = new SolidBrush(color);
        string alignment = String(p, "align")?.ToLowerInvariant() ?? "left";
        using var measureFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };
        SizeF baseMeasured = graphics.MeasureString(value, baseFont, PointF.Empty, measureFormat);
        SizeF measured = graphics.MeasureString(value, font, PointF.Empty, measureFormat);
        bool intrinsicBounds = bounds.Width <= 1;
        float layoutWidth = intrinsicBounds ? Math.Max(1, measured.Width) : bounds.Width;
        float layoutHeight = bounds.Height <= 1 ? Math.Max(1, measured.Height) : bounds.Height;
        float x = intrinsicBounds && alignment == "left"
            ? bounds.X + (baseMeasured.Width - measured.Width) / 2f
            : bounds.X - (alignment switch
        {
            "center" => layoutWidth / 2,
            "right" => layoutWidth,
            _ => 0
        });
        float y = bounds.Y - (data && !currentLyric ? .85f * scaleY : 0);
        RectangleF textBounds = new(x, y, layoutWidth, layoutHeight);
        GraphicsState textState = graphics.Save();
        try
        {
            float rotation = (float)Number(p, "rotate");
            if (rotation != 0)
            {
                graphics.TranslateTransform(textBounds.Left + textBounds.Width / 2f, textBounds.Top + textBounds.Height / 2f);
                graphics.RotateTransform(rotation);
                graphics.TranslateTransform(-(textBounds.Left + textBounds.Width / 2f), -(textBounds.Top + textBounds.Height / 2f));
            }

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = alignment switch
            {
                "center" => StringAlignment.Center,
                "right" => StringAlignment.Far,
                _ => StringAlignment.Near
            },
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };
        DrawTextShadow(graphics, value, font, textBounds, format, p, node);
        DrawTextStroke(graphics, value, font, textBounds, format, p, node);
        graphics.DrawString(value, font, brush, textBounds, format);
        }
        finally { graphics.Restore(textState); }
    }

    private static string? ResolveTextValue(JsonElement p, bool data, SensorSnapshot sensors)
    {
        bool currentLyric = Bool(p, "isLyric", false)
            || String(p, "dataSource")?.Equals("lyric_current", StringComparison.OrdinalIgnoreCase) == true;
        string? value;
        if (currentLyric)
        {
            value = CurrentLyricText(MediaSessionService.Current);
        }
        else if (data)
        {
            string source = String(p, "dataSource") ?? string.Empty;
            value = sensors.Format(source, (int)Math.Clamp(Number(p, "tofixCount"), 0, 3), Bool(p, "hideUnit", false));
        }
        else
        {
            value = String(p, "text") ?? string.Empty;
            if (value.Contains('{') && value.Contains('}'))
            {
                int decimals = (int)Math.Clamp(Number(p, "tofixCount"), 0, 3);
                bool hideUnit = Bool(p, "hideUnit", false);
                string? configuredSource = String(p, "dataSource");
                value = Regex.Replace(value, "\\{([^{}]+)\\}", match =>
                {
                    string token = match.Groups[1].Value.Trim();
                    string source = token.Equals("dataSource", StringComparison.OrdinalIgnoreCase)
                        ? configuredSource ?? string.Empty
                        : token;
                    if (string.IsNullOrWhiteSpace(source)) return string.Empty;
                    return IsClockDataSource(source)
                        ? FormatClock(sensors.Now, String(p, "format"), source.Equals("time_current_12h", StringComparison.OrdinalIgnoreCase))
                        : sensors.Format(source, decimals, hideUnit);
                });
            }
        }

        string? dataSource = String(p, "dataSource");
        if (data && dataSource is not null && IsClockDataSource(dataSource))
            value = FormatClock(sensors.Now, String(p, "format"), dataSource.Equals("time_current_12h", StringComparison.OrdinalIgnoreCase));
        return value;
    }

    private static string CurrentLyricText(MediaSessionSnapshot media)
    {
        if (media.Lyrics.Count == 0) return string.Empty;
        if (!media.LyricsAreSynchronized) return media.Lyrics[0].Text;
        long position = media.CurrentPositionMilliseconds;
        SyncedLyricLine? current = null;
        foreach (SyncedLyricLine line in media.Lyrics)
        {
            if (line.TimeMilliseconds > position) break;
            current = line;
        }
        return (current ?? media.Lyrics[0]).Text;
    }

    private static void DrawTextShadow(Graphics graphics, string text, Font font, RectangleF bounds,
        StringFormat format, JsonElement properties, JsonElement node)
    {
        JsonElement shadow = ChildObject(properties, "textShadow");
        string? colorText = String(shadow, "color");
        if (colorText is null) return;
        Color color = ParseColor(colorText, Color.Transparent);
        if (color.A == 0) return;

        float scaleX = (float)Math.Clamp(Scale(node, 0, 1), .1, 8);
        float scaleY = (float)Math.Clamp(Scale(node, 1, scaleX), .1, 8);
        float offsetX = (float)Number(shadow, "offsetX") * scaleX;
        float offsetY = (float)Number(shadow, "offsetY") * scaleY;
        float blur = (float)Math.Clamp(Number(shadow, "blurRadius") * Math.Max(scaleX, scaleY), 0, 24);
        if (blur <= .1f)
        {
            using var brush = new SolidBrush(color);
            graphics.DrawString(text, font, brush, Offset(bounds, offsetX, offsetY), format);
            return;
        }

        const int samples = 16;
        using var softBrush = new SolidBrush(Color.FromArgb(Math.Max(1, color.A / samples), color.R, color.G, color.B));
        for (int i = 0; i < samples; i++)
        {
            float angle = MathF.Tau * i / samples;
            float x = offsetX + MathF.Cos(angle) * blur;
            float y = offsetY + MathF.Sin(angle) * blur;
            graphics.DrawString(text, font, softBrush, Offset(bounds, x, y), format);
        }
    }

    private static void DrawTextStroke(Graphics graphics, string text, Font font, RectangleF bounds,
        StringFormat format, JsonElement properties, JsonElement node)
    {
        JsonElement stroke = ChildObject(properties, "textStroke");
        float scale = (float)Math.Clamp(Math.Sqrt(Scale(node, 0, 1) * Scale(node, 1, 1)), .1, 8);
        float width = (float)Math.Clamp(Number(stroke, "inputnumber") * scale, 0, 16);
        string? colorText = String(stroke, "color");
        if (width <= .1f || colorText is null) return;
        Color color = ParseColor(colorText, Color.Transparent);
        if (color.A == 0) return;

        const int samples = 16;
        float radius = width / 2f;
        using var brush = new SolidBrush(color);
        for (int i = 0; i < samples; i++)
        {
            float angle = MathF.Tau * i / samples;
            graphics.DrawString(text, font, brush,
                Offset(bounds, MathF.Cos(angle) * radius, MathF.Sin(angle) * radius), format);
        }
    }

    private static RectangleF Offset(RectangleF bounds, float x, float y)
        => new(bounds.X + x, bounds.Y + y, bounds.Width, bounds.Height);

    private void DrawProgress(Graphics graphics, JsonElement p, JsonElement node, RectangleF bounds, SensorSnapshot sensors)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        if (String(p, "progressType")?.Equals("circle", StringComparison.OrdinalIgnoreCase) == true)
        {
            DrawArc(graphics, p, node, bounds, sensors);
            return;
        }
        string source = String(p, "dataSource") ?? string.Empty;
        double min = NumberFromArray(p, "dataRange", 0, 0);
        double max = NumberFromArray(p, "dataRange", 1, 100);
        bool hasValue = sensors.TryGet(source, out double value) && max > min;
        float ratio = hasValue
            ? SmoothGaugeRatio(node, source, bounds, (float)Math.Clamp((value - min) / (max - min), 0, 1))
            : ClearGaugeTransition(node, source, bounds);
        float radius = (float)Math.Clamp(Number(p, "borderRadius", bounds.Height / 2), 0, bounds.Height / 2);
        using GraphicsPath trackPath = RoundedRectangle(bounds, radius);
        using Brush track = new SolidBrush(ParseColor(String(p, "trailColor"), Color.FromArgb(100, 255, 255, 255)));
        graphics.FillPath(track, trackPath);
        if (ratio > 0)
        {
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(trackPath);
                Color[] colors = ColorPair(p, "borderBgColor", ParseColor(String(p, "borderColor"), Color.DeepSkyBlue));
                string fillMode = FillMode(p);
                if (fillMode == "cut")
                {
                    const int segmentCount = 10;
                    float segmentWidth = bounds.Width / segmentCount;
                    float gap = Math.Min(3f, segmentWidth * .16f);
                    float progress = ratio * segmentCount;
                    for (int segment = 0; segment < segmentCount && progress > segment; segment++)
                    {
                        float fraction = Math.Min(1, progress - segment);
                        float width = Math.Max(0, (segmentWidth - gap) * fraction);
                        if (width <= 0) continue;
                        using var segmentBrush = new SolidBrush(CutSegmentColor(colors[0], colors.Length > 1 ? colors[1] : colors[0], segment));
                        graphics.FillRectangle(segmentBrush, bounds.Left + segment * segmentWidth, bounds.Top, width, bounds.Height);
                    }
                }
                else
                {
                    if (fillMode == "single") colors = [colors[0], colors[0]];
                    RectangleF fillBounds = new(bounds.X, bounds.Y, Math.Max(bounds.Height, bounds.Width * ratio), bounds.Height);
                    fillBounds.Width = Math.Min(bounds.Width, fillBounds.Width);
                    using GraphicsPath fillPath = RoundedRectangle(fillBounds, Math.Min(radius, fillBounds.Width / 2));
                    // The original keeps the gradient stretched across the full track and clips
                    // it to the current percentage; scaling it to fillBounds changes the color
                    // stops whenever the gauge is only partly filled.
                    using Brush fill = MakeBrush(bounds, colors, linearMode: LinearGradientMode.Horizontal);
                    graphics.FillPath(fill, fillPath);
                }
            }
            finally { graphics.Restore(state); }
        }
        float borderWidth = (float)Math.Clamp(Number(p, "borderWidth"), 0, 30);
        if (borderWidth > 0)
        {
            using Pen pen = CreateBorderPen(p, borderWidth, Color.White);
            graphics.DrawPath(pen, trackPath);
        }
        if (!hasValue)
            DrawUnavailableGauge(graphics, bounds);
    }

    private void DrawArc(Graphics graphics, JsonElement p, JsonElement node, RectangleF bounds, SensorSnapshot sensors,
        float defaultGapDegree = 0)
    {
        float size = Math.Min(bounds.Width, bounds.Height);
        if (size < 4) return;
        string source = String(p, "dataSource") ?? string.Empty;
        double min = NumberFromArray(p, "dataRange", 0, 0);
        double max = NumberFromArray(p, "dataRange", 1, 100);
        bool hasValue = sensors.TryGet(source, out double value) && max > min;
        float ratio = hasValue
            ? SmoothGaugeRatio(node, source, bounds, (float)Math.Clamp((value - min) / (max - min), 0, 1))
            : ClearGaugeTransition(node, source, bounds);
        float stroke = (float)Math.Clamp(Number(p, "strokeWidth", Number(p, "radiusWidth", Math.Max(5, size * .06))), 1, size / 2);
        float inset = stroke / 2;
        RectangleF ring = new(bounds.X + (bounds.Width - size) / 2 + inset,
            bounds.Y + (bounds.Height - size) / 2 + inset, size - stroke, size - stroke);
        float gap = (float)Math.Clamp(Number(p, "gapDegree", defaultGapDegree), 0, 295);
        float sweep = 360 - gap;
        // With no gap the original dashboard starts at 12 o'clock. A non-zero
        // bottom gap moves the start clockwise to the other edge of that gap.
        float start = gap <= 0 ? -90 : 90 + gap / 2;
        using var trail = new Pen(ParseColor(String(p, "trailColor"), Color.FromArgb(85, 255, 255, 255)), stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(trail, ring, start, sweep);
        if (hasValue && ratio > 0.001f)
        {
            Color[] colors = ColorPair(p, "borderBgColor", ParseColor(String(p, "borderColor"), Color.DeepSkyBlue));
            if (FillMode(p) == "cut")
            {
                const int segmentCount = 10;
                float segmentSweep = sweep / segmentCount;
                float gapDegrees = Math.Min(2.2f, segmentSweep * .1f);
                float progress = ratio * segmentCount;
                for (int segment = 0; segment < segmentCount && progress > segment; segment++)
                {
                    float fraction = Math.Min(1, progress - segment);
                    float visibleSweep = Math.Max(0, (segmentSweep - gapDegrees) * fraction);
                    if (visibleSweep <= 0) continue;
                    Color color = CutSegmentColor(colors[0], colors.Length > 1 ? colors[1] : colors[0], segment);
                    using var segmentedArc = new Pen(color, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                    graphics.DrawArc(segmentedArc, ring, start + segment * segmentSweep + gapDegrees / 2, visibleSweep);
                }
            }
            else
            {
                if (FillMode(p) == "single") colors = [colors[0], colors[0]];
                float visibleSweep = Math.Max(1, sweep * ratio);
                if (colors[0].ToArgb() == colors[^1].ToArgb())
                {
                    using var arc = new Pen(colors[0], stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    graphics.DrawArc(arc, ring, start, visibleSweep);
                }
                else
                {
                    DrawAngularGradientArc(graphics, ring, stroke, start, visibleSweep, colors[0], colors[^1]);
                }
            }
        }
        if (!hasValue)
            DrawUnavailableGauge(graphics, bounds);
    }

    private float SmoothGaugeRatio(JsonElement node, string source, RectangleF bounds, float target)
    {
        string key = GaugeTransitionKey(node, source, bounds);
        long now = Stopwatch.GetTimestamp();
        if (!_progressTransitions.TryGetValue(key, out ProgressTransition transition))
        {
            _progressTransitions[key] = new ProgressTransition(target, target, now);
            return target;
        }

        float current = ProgressTransitionValue(transition, now);
        if (Math.Abs(target - transition.Target) > .001f)
        {
            transition = new ProgressTransition(current, target, now);
            _progressTransitions[key] = transition;
        }
        return ProgressTransitionValue(transition, now);
    }

    private float ClearGaugeTransition(JsonElement node, string source, RectangleF bounds)
    {
        _progressTransitions.Remove(GaugeTransitionKey(node, source, bounds));
        return 0;
    }

    private static string GaugeTransitionKey(JsonElement node, string source, RectangleF bounds)
        => String(node, "id") ?? $"{source}:{bounds.X:R}:{bounds.Y:R}:{bounds.Width:R}:{bounds.Height:R}";

    private static float ProgressTransitionValue(ProgressTransition transition, long now)
    {
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(transition.StartedAt, now).TotalMilliseconds;
        float progress = (float)Math.Clamp(elapsedMilliseconds / 300d, 0, 1);
        float eased = CubicBezierEasing(progress, .25, .1, .25, 1);
        return transition.From + (transition.Target - transition.From) * eased;
    }

    private static void DrawAngularGradientArc(Graphics graphics, RectangleF ring, float stroke,
        float startAngle, float sweepAngle, Color startColor, Color endColor)
    {
        float centerX = ring.Left + ring.Width / 2f;
        float centerY = ring.Top + ring.Height / 2f;
        float centerRadius = ring.Width / 2f;
        float outerRadius = centerRadius + stroke / 2f;
        float innerRadius = Math.Max(0, centerRadius - stroke / 2f);
        int segments = Math.Clamp((int)Math.Ceiling(Math.Abs(sweepAngle) * Math.PI / 180d * centerRadius / 2d), 1, 1440);
        float segmentSweep = sweepAngle / segments;
        float overlap = Math.Min(.08f, Math.Abs(segmentSweep) * .08f);

        PointF At(float radius, float degrees)
        {
            float radians = degrees * (MathF.PI / 180f);
            return new PointF(centerX + MathF.Cos(radians) * radius, centerY + MathF.Sin(radians) * radius);
        }

        for (int index = 0; index < segments; index++)
        {
            float firstAngle = startAngle + index * segmentSweep - overlap;
            float lastAngle = startAngle + (index + 1) * segmentSweep + overlap;
            float middleAngle = startAngle + (index + .5f) * segmentSweep;
            Color color = BlendArcColor(startColor, endColor, (index + .5d) / segments);
            PointF[] sector =
            [
                At(outerRadius, firstAngle),
                At(outerRadius, middleAngle),
                At(outerRadius, lastAngle),
                At(innerRadius, lastAngle),
                At(innerRadius, middleAngle),
                At(innerRadius, firstAngle)
            ];
            using var brush = new SolidBrush(color);
            graphics.FillPolygon(brush, sector);
        }

        // The original SVG progress stroke has rounded ends.
        float capDiameter = stroke;
        using var firstCap = new SolidBrush(startColor);
        using var lastCap = new SolidBrush(BlendArcColor(startColor, endColor, 1));
        PointF firstCenter = At(centerRadius, startAngle);
        PointF lastCenter = At(centerRadius, startAngle + sweepAngle);
        graphics.FillEllipse(firstCap, firstCenter.X - capDiameter / 2f, firstCenter.Y - capDiameter / 2f, capDiameter, capDiameter);
        graphics.FillEllipse(lastCap, lastCenter.X - capDiameter / 2f, lastCenter.Y - capDiameter / 2f, capDiameter, capDiameter);
    }

    private static Color BlendArcColor(Color start, Color end, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static int Blend(byte first, byte second, double t) => (int)Math.Round(first + (second - first) * t);
        return Color.FromArgb(
            Blend(start.A, end.A, amount),
            Blend(start.R, end.R, amount),
            Blend(start.G, end.G, amount),
            Blend(start.B, end.B, amount));
    }

    private static void DrawUnavailableGauge(Graphics graphics, RectangleF bounds)
    {
        float size = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * .42f, 9, 22);
        using Font font = CreateFont("Segoe UI", size, FontStyle.Regular);
        using var brush = new SolidBrush(Color.FromArgb(210, Color.White));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("—", font, brush, bounds, format);
    }

    private void DrawLineChart(Graphics graphics, JsonElement p, JsonElement node, RectangleF bounds, SensorSnapshot sensors)
    {
        if (bounds.Width < 8 || bounds.Height < 8) return;
        float radius = (float)Math.Clamp(Number(p, "borderRadius"), 0, Math.Min(bounds.Width, bounds.Height) / 2d);
        using GraphicsPath card = RoundedRectangle(bounds, radius);
        Color background = ParseColor(String(p, "backgroundColor"), Color.Transparent);
        if (background.A > 0)
        {
            using var fill = new SolidBrush(background);
            graphics.FillPath(fill, card);
        }
        float frameWidth = (float)Math.Clamp(Number(p, "borderWidth"), 0, 20);
        if (frameWidth > 0)
        {
            using Pen border = CreateBorderPen(p, frameWidth, Color.FromArgb(100, 255, 255, 255));
            graphics.DrawPath(border, card);
        }

        string source = String(p, "dataSource") ?? "";
        string chartId = String(node, "id") ?? $"{source}:{bounds.X}:{bounds.Y}";
        bool hasCurrentValue = sensors.TryGet(source, out double value);
        if (hasCurrentValue)
            AddSensorSample(chartId, source, value, sensors.Now);
        else
        {
            _sensorHistory.Remove(chartId);
            _lastSensorSample.Remove(chartId);
        }
        bool showAxes = HasArrayString(p, "xyLineConfig", "showXY");
        bool showGrid = HasArrayString(p, "xyLineConfig", "splitLine");
        // ECharts uses grid={top:1,right:1,bottom:1,left:1}; the outer
        // chart border does not alter the plot area, even when axes are enabled.
        const float inset = 1;
        var plot = RectangleF.FromLTRB(bounds.Left + inset, bounds.Top + inset,
            bounds.Right - inset, bounds.Bottom - inset);
        if (plot.Width < 4 || plot.Height < 4) return;

        GraphicsState state = graphics.Save();
        try
        {
            graphics.SetClip(card);
            if (showGrid)
            {
                using var gridPen = new Pen(ColorTranslator.FromHtml("#E0E6F1"), 1);
                const int xDivisions = 4;
                const int yDivisions = 5;
                for (int i = 0; i <= yDivisions; i++)
                {
                    float y = plot.Top + plot.Height * i / yDivisions;
                    graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                }
                for (int i = 0; i <= xDivisions; i++)
                {
                    float x = plot.Left + plot.Width * i / xDivisions;
                    graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                }
            }

            if (showAxes)
                DrawLineChartAxes(graphics, p, plot, bounds, frameWidth);

            double minimum = NumberFromArray(p, "dataRange", 0, 0);
            double maximum = NumberFromArray(p, "dataRange", 1, 100);
            if (maximum <= minimum) maximum = minimum + 1;
            SensorPoint[] history = _sensorHistory.TryGetValue(chartId, out Queue<SensorPoint>? samples)
                ? samples.ToArray()
                : [];
            if (history.Length == 1)
                history = [history[0], new SensorPoint(sensors.Now.AddMilliseconds(1), history[0].Value)];

            if (history.Length == 0)
            {
                if (!hasCurrentValue)
                    DrawUnavailableGauge(graphics, plot);
                return;
            }

            var points = new PointF[history.Length];
            for (int i = 0; i < history.Length; i++)
            {
                double ratio = Math.Clamp((history[i].Value - minimum) / (maximum - minimum), 0, 1);
                // ECharts' value axis rounds the 0..18/19 sample domain to 0..20
                // with its default five splits; the last sample does not touch
                // the right edge of the plot.
                const float xAxisMaximum = 20;
                float x = plot.Left + plot.Width * i / xAxisMaximum;
                float y = plot.Bottom - plot.Height * (float)ratio;
                points[i] = new PointF(x, y);
            }

            Color lineColor = ParseColor(String(p, "areaStyle"), Color.Black);
            bool showArea = HasArrayString(p, "xyLineConfig", "showAreaStyle");
            using var curvePath = new GraphicsPath();
            if (points.Length > 2) curvePath.AddCurve(points, .5f);
            else if (points.Length > 1) curvePath.AddLines(points);
            if (showArea)
            {
                using var areaPath = new GraphicsPath();
                areaPath.AddLine(points[0].X, plot.Bottom, points[0].X, points[0].Y);
                if (curvePath.PointCount > 0) areaPath.AddPath(curvePath, connect: true);
                areaPath.AddLine(points[^1].X, points[^1].Y, points[^1].X, plot.Bottom);
                areaPath.CloseFigure();
                using var areaBrush = new SolidBrush(lineColor);
                graphics.FillPath(areaBrush, areaPath);
            }

            // Jungle Leopard's ECharts series uses its default two-pixel line width.
            using var line = new Pen(lineColor, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (curvePath.PointCount > 0) graphics.DrawPath(line, curvePath);
            else graphics.DrawLine(line, plot.Left, points[0].Y, plot.Right, points[0].Y);
        }
        finally { graphics.Restore(state); }
    }

    private static void DrawLineChartAxes(Graphics graphics, JsonElement p, RectangleF plot, RectangleF bounds, float frameWidth)
    {
        Color axisColor = ColorTranslator.FromHtml("#6E7079");
        using var axisPen = new Pen(axisColor, 1);
        graphics.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
        graphics.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        double minimum = NumberFromArray(p, "dataRange", 0, 0);
        double maximum = NumberFromArray(p, "dataRange", 1, 100);
        if (maximum <= minimum) maximum = minimum + 1;
        float fontSize = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * .055f, 6, 10);
        using Font font = CreateFont("Segoe UI", fontSize, FontStyle.Regular);
        using var textBrush = new SolidBrush(Color.FromArgb(175, 205, 210, 220));
        using var yFormat = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var xFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, FormatFlags = StringFormatFlags.NoWrap };
        const int yDivisions = 5;
        for (int i = 0; i <= yDivisions; i++)
        {
            double value = maximum - (maximum - minimum) * i / yDivisions;
            string label = value.ToString("0.#", CultureInfo.CurrentCulture);
            var yLabel = new RectangleF(bounds.Left + frameWidth, plot.Top + plot.Height * i / 4 - fontSize / 2,
                Math.Max(1, plot.Left - bounds.Left - frameWidth - 3), fontSize + 2);
            graphics.DrawString(label, font, textBrush, yLabel, yFormat);
        }
        for (int i = 0; i <= 4; i++)
        {
            string label = (i * 5).ToString(CultureInfo.CurrentCulture);
            var xLabel = new RectangleF(plot.Left + plot.Width * i / 4 - 12, plot.Bottom + 2, 24,
                Math.Max(1, bounds.Bottom - plot.Bottom - 2));
            graphics.DrawString(label, font, textBrush, xLabel, xFormat);
        }
    }

    private void AddSensorSample(string chartId, string source, double value, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(chartId) || string.IsNullOrWhiteSpace(source)) return;
        if (!_sensorHistory.TryGetValue(chartId, out Queue<SensorPoint>? samples))
        {
            samples = new Queue<SensorPoint>();
            // The original ECharts line starts with twenty zero samples, so the
            // graph is visible as soon as the theme loads. At the first 500 ms
            // update it appends the live sample, then keeps only the latest 19.
            for (int i = 19; i >= 0; i--)
                samples.Enqueue(new SensorPoint(timestamp.AddMilliseconds(-500 * i), 0));
            _sensorHistory[chartId] = samples;
            _lastSensorSample[chartId] = timestamp;
            return;
        }
        if (_lastSensorSample.TryGetValue(chartId, out DateTime previous) && (timestamp - previous).TotalMilliseconds < 500)
            return;
        _lastSensorSample[chartId] = timestamp;
        if (!double.IsFinite(value)) return;
        samples.Enqueue(new SensorPoint(timestamp, value));
        while (samples.Count > 19) samples.Dequeue();
    }

    private static bool HasArrayString(JsonElement element, string property, string expected)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private bool TryGetImage(string path, out Image? image)
    {
        if (IsVideoExtension(GetMediaExtension(path)))
        {
            image = null;
            return false;
        }
        if (_images.TryGetValue(path, out CachedThemeImage? cached))
        {
            image = cached.Image;
            return true;
        }
        Stream? stream = null;
        try
        {
            if (_package.TryGetAsset(path, out byte[] bytes))
                stream = new MemoryStream(bytes, writable: false);
            else if (ThemeRemoteAssetCache.TryGetCachedFile(path, out string remotePath))
            {
                if (IsVideoExtension(Path.GetExtension(remotePath)))
                {
                    image = null;
                    return false;
                }
                stream = new FileStream(remotePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            if (stream is null)
            {
                image = null;
                return false;
            }

            Image decoded = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            _images[path] = new CachedThemeImage(decoded, stream);
            image = decoded;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            stream?.Dispose();
            image = null;
            return false;
        }
    }

    private bool TryGetVideoFrame(string path, out Bitmap? frame)
    {
        frame = null;
        if (!_videos.TryGetValue(path, out HtVideoFrameSource? source))
        {
            try
            {
                string videoPath;
                if (_package.TryGetAsset(path, out byte[] bytes))
                {
                    _videoDirectory ??= Path.Combine(Path.GetTempPath(), "JungleLeopardStudio", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_videoDirectory);
                    videoPath = Path.Combine(_videoDirectory, Guid.NewGuid().ToString("N") + GetMediaExtension(path));
                    File.WriteAllBytes(videoPath, bytes);
                }
                else if (!ThemeRemoteAssetCache.TryGetCachedFile(path, out videoPath))
                {
                    return false;
                }
                if (!IsVideoExtension(Path.GetExtension(videoPath))) return false;
                source = new HtVideoFrameSource(videoPath);
                _videos[path] = source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        byte[]? frameBytes = source.LatestFrame;
        if (frameBytes is null) return false;
        try
        {
            using var stream = new MemoryStream(frameBytes, writable: false);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            frame = new Bitmap(image);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            frame?.Dispose();
            frame = null;
            return false;
        }
    }

    private static string GetMediaExtension(string path)
        => Uri.TryCreate(path, UriKind.Absolute, out Uri? uri)
            ? Path.GetExtension(uri.AbsolutePath)
            : Path.GetExtension(path);

    private static bool IsVideoExtension(string extension)
        => extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);

    private void DrawAnimatedFrame(string path, Image image)
    {
        try
        {
            if (!_gifTimelines.TryGetValue(path, out GifTimeline? timeline))
            {
                FrameDimension? dimension = FrameDimensionsList(image)
                    .FirstOrDefault(item => item.Guid == FrameDimension.Time.Guid)
                    ?? FrameDimensionsList(image).FirstOrDefault();
                if (dimension is null) return;
                int frameCount = image.GetFrameCount(dimension);
                if (frameCount <= 1) return;
                timeline = new GifTimeline(dimension, ReadGifFrameDurations(image, frameCount), Environment.TickCount64);
                _gifTimelines[path] = timeline;
            }

            image.SelectActiveFrame(timeline.Dimension, timeline.FrameAt(Environment.TickCount64));
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException) { }
    }

    private static int[] ReadGifFrameDurations(Image image, int frameCount)
    {
        var durations = Enumerable.Repeat(100, frameCount).ToArray();
        try
        {
            byte[]? values = image.GetPropertyItem(0x5100)?.Value;
            if (values is null) return durations;
            for (int i = 0; i < frameCount && (i + 1) * sizeof(int) <= values.Length; i++)
            {
                int centiseconds = BitConverter.ToInt32(values, i * sizeof(int));
                if (centiseconds > 0)
                    durations[i] = Math.Clamp(centiseconds * 10, 20, 10_000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException) { }
        return durations;
    }

    private static FrameDimension[] FrameDimensionsList(Image image)
        => image.FrameDimensionsList.Select(guid => new FrameDimension(guid)).ToArray();

    private sealed class GifTimeline(FrameDimension dimension, int[] frameDurations, long startTime)
    {
        private readonly int _totalDuration = Math.Max(1, frameDurations.Sum());

        public FrameDimension Dimension { get; } = dimension;

        public int FrameAt(long now)
        {
            int offset = (int)(Math.Max(0, now - startTime) % _totalDuration);
            for (int frame = 0; frame < frameDurations.Length; frame++)
            {
                if (offset < frameDurations[frame]) return frame;
                offset -= frameDurations[frame];
            }
            return frameDurations.Length - 1;
        }
    }

    private static void DrawFittedImage(Graphics graphics, Image image, Rectangle target, string fit)
    {
        if (image.Width <= 0 || image.Height <= 0 || target.Width <= 0 || target.Height <= 0) return;
        if (fit.Equals("fill", StringComparison.OrdinalIgnoreCase) || fit.Equals("stretch", StringComparison.OrdinalIgnoreCase))
        {
            graphics.DrawImage(image, target);
            return;
        }
        double sx = (double)target.Width / image.Width;
        double sy = (double)target.Height / image.Height;
        double scale = fit == "contain" ? Math.Min(sx, sy) : Math.Max(sx, sy);
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var destination = new Rectangle(target.Left + (target.Width - width) / 2, target.Top + (target.Height - height) / 2, width, height);
        GraphicsState state = graphics.Save();
        try
        {
            graphics.SetClip(target);
            graphics.DrawImage(image, destination);
        }
        finally { graphics.Restore(state); }
    }

    private static Bitmap FitOrientation(Image source, Size outputSize, bool fitToScreen)
    {
        bool sourcePortrait = source.Height > source.Width;
        bool targetPortrait = outputSize.Height > outputSize.Width;
        using var oriented = new Bitmap(source);
        if (sourcePortrait != targetPortrait)
            oriented.RotateFlip(RotateFlipType.Rotate90FlipNone);

        var result = new Bitmap(outputSize.Width, outputSize.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            Configure(graphics);
            graphics.Clear(Color.Black);
            if (fitToScreen)
            {
                graphics.DrawImage(oriented, new Rectangle(Point.Empty, outputSize));
            }
            else
            {
                double scale = Math.Min((double)outputSize.Width / oriented.Width, (double)outputSize.Height / oriented.Height);
                int width = Math.Max(1, (int)Math.Round(oriented.Width * scale));
                int height = Math.Max(1, (int)Math.Round(oriented.Height * scale));
                var destination = new Rectangle((outputSize.Width - width) / 2, (outputSize.Height - height) / 2, width, height);
                graphics.DrawImage(oriented, destination);
            }
        }
        return result;
    }

    private static void Configure(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceOver;
        switch (AppRenderSettings.Mode)
        {
            case "highQuality":
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                break;
            case "balanced":
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.Default;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                break;
            default:
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                break;
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        float d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Brush MakeBrush(RectangleF bounds, Color[] colors, bool radial = false, LinearGradientMode linearMode = LinearGradientMode.Vertical)
    {
        if (colors.Length < 2 || colors[0].ToArgb() == colors[1].ToArgb())
            return new SolidBrush(colors.FirstOrDefault(Color.White));
        if (radial)
        {
            using var ellipse = new GraphicsPath();
            ellipse.AddEllipse(bounds);
            var brush = new PathGradientBrush(ellipse) { CenterColor = colors[0] };
            brush.SurroundColors = Enumerable.Repeat(colors[1], ellipse.PointCount).ToArray();
            return brush;
        }
        return new LinearGradientBrush(bounds, colors[0], colors[1], linearMode);
    }

    private static string FillMode(JsonElement properties)
        => String(properties, "borderBgType")?.ToLowerInvariant() ?? "normal";

    private static Color CutSegmentColor(Color first, Color second, int index)
    {
        Color source = index < 5 ? first : second;
        float alpha = index < 5 ? 1f - index * .1f : index * .1f;
        return Color.FromArgb((int)Math.Round(alpha * 255), source.R, source.G, source.B);
    }

    private static Pen CreateBorderPen(JsonElement properties, float width, Color fallback)
    {
        var pen = new Pen(ParseColor(String(properties, "borderColor"), fallback), width);
        if (String(properties, "borderStyle")?.Equals("dashed", StringComparison.OrdinalIgnoreCase) == true)
            pen.DashStyle = DashStyle.Dash;
        return pen;
    }

    private static Color[] ColorPair(JsonElement p, string key, Color fallback)
    {
        if (!p.TryGetProperty(key, out JsonElement value)) return [fallback];
        if (value.ValueKind == JsonValueKind.Array)
        {
            Color[] colors = value.EnumerateArray()
                .Select(item => ParseColor(item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString(), fallback))
                .Take(2).ToArray();
            return colors.Length == 0 ? [fallback] : colors;
        }
        return [ParseColor(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(), fallback)];
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string color = value.Trim();
        if (color.StartsWith('#') && ColorTranslator.FromHtml(color) is Color html)
            return html;
        string[] parts = color.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 3
            && byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
        {
            double alpha = 1;
            if (parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                alpha = parsed > 1 ? parsed / 255d : parsed;
            return Color.FromArgb((int)Math.Clamp(Math.Round(alpha * 255), 0, 255), r, g, b);
        }
        try { return ColorTranslator.FromHtml(color); }
        catch (ArgumentException) { return fallback; }
    }

    private static Font CreateFont(string family, float size, FontStyle style)
    {
        family = family.Trim().Trim('"', '\'');
        try { return new Font(family, size, style, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSerif, size, style, GraphicsUnit.Pixel); }
    }

    private static FontStyle FontStyleFrom(JsonElement p)
    {
        FontStyle style = FontStyle.Regular;
        string fontStyle = String(p, "fontStyle") ?? "";
        string fontWeight = String(p, "fontWeight") ?? "";
        string decoration = String(p, "textDecoration") ?? "";
        if (Bool(p, "bold", false) || fontWeight.Contains("bold", StringComparison.OrdinalIgnoreCase)
            || Number(p, "fontWeight", -1) >= 600
            || (int.TryParse(fontWeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight) && weight >= 600)
            || HasArrayString(p, "fontStyle", "bold") || HasArrayString(p, "fontWeight", "bold"))
            style |= FontStyle.Bold;
        if (Bool(p, "italic", false) || fontStyle.Contains("italic", StringComparison.OrdinalIgnoreCase)
            || HasArrayString(p, "fontStyle", "italic")) style |= FontStyle.Italic;
        if (Bool(p, "underline", false) || decoration.Contains("underline", StringComparison.OrdinalIgnoreCase)
            || HasArrayString(p, "fontStyle", "underline")) style |= FontStyle.Underline;
        return style;
    }

    private static bool ThemeRequestsLyrics(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in node.EnumerateArray())
                if (ThemeRequestsLyrics(item)) return true;
            return false;
        }
        if (node.ValueKind != JsonValueKind.Object) return false;

        if (String(node, "type") is { } layerType
            && (layerType.Equals("data", StringComparison.OrdinalIgnoreCase)
                || layerType.Equals("text", StringComparison.OrdinalIgnoreCase)
                || layerType.Equals("lyric", StringComparison.OrdinalIgnoreCase))
            && node.TryGetProperty("children", out JsonElement dataProperties)
            && dataProperties.ValueKind == JsonValueKind.Object
            && (Bool(dataProperties, "isLyric", false)
                || String(dataProperties, "dataSource")?.Equals("lyric_current", StringComparison.OrdinalIgnoreCase) == true
                || layerType.Equals("lyric", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (String(node, "type")?.Equals("songdisplay", StringComparison.OrdinalIgnoreCase) == true
            && node.TryGetProperty("children", out JsonElement properties)
            && Bool(properties, "showLyrics", true))
            return true;

        return node.TryGetProperty("children", out JsonElement children)
            && children.ValueKind == JsonValueKind.Array
            && ThemeRequestsLyrics(children);
    }

    private static bool ThemeRequestsForegroundFps(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in node.EnumerateArray())
                if (ThemeRequestsForegroundFps(item)) return true;
            return false;
        }
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("children", out JsonElement children))
            return false;
        if (children.ValueKind == JsonValueKind.Object
            && String(children, "dataSource")?.Equals("fps_foreground", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return ThemeRequestsForegroundFps(children);
    }

    private static Dictionary<string, PointF> ReadGroupOffsets(JsonElement nodes)
    {
        var groups = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            if (String(node, "type")?.Equals("group", StringComparison.OrdinalIgnoreCase) == true
                && String(node, "id") is { Length: > 0 } id)
                groups[id] = node;
        }

        var offsets = new Dictionary<string, PointF>(StringComparer.Ordinal);
        foreach (string id in groups.Keys)
            Resolve(id, new HashSet<string>(StringComparer.Ordinal));
        return offsets;

        PointF Resolve(string id, HashSet<string> visiting)
        {
            if (offsets.TryGetValue(id, out PointF existing)) return existing;
            if (!groups.TryGetValue(id, out JsonElement group) || !visiting.Add(id)) return PointF.Empty;
            JsonElement properties = group.TryGetProperty("children", out JsonElement value) ? value : default;
            float x = (float)(Number(properties, "x") + Number(group, "tx"));
            float y = (float)(Number(properties, "y") + Number(group, "ty"));
            if (String(group, "parentId") is { Length: > 0 } parentId)
            {
                PointF parent = Resolve(parentId, visiting);
                x += parent.X;
                y += parent.Y;
            }
            visiting.Remove(id);
            var result = new PointF(x, y);
            offsets[id] = result;
            return result;
        }
    }

    private static string? FirstPath(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    return item.GetString();
        }
        return null;
    }

    private static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement ChildObject(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Object ? value : default;

    private static double Number(JsonElement element, string property, double fallback = 0)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && TryParseNumber(value, out double number) ? number : fallback;

    private static double Scale(JsonElement node, int index, double fallback)
    {
        if (!node.TryGetProperty("scale", out JsonElement value)) return fallback;
        if (value.ValueKind == JsonValueKind.String)
        {
            string[] axes = (value.GetString() ?? string.Empty)
                .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int axis = axes.Length == 1 ? 0 : index;
            if (axis >= 0 && axis < axes.Length && TryParseNumber(axes[axis], out double parsed))
                return Math.Clamp(parsed, .05, 12);
            return fallback;
        }
        if (TryParseNumber(value, out double uniform) && value.ValueKind != JsonValueKind.Array)
            return Math.Clamp(uniform, .05, 12);
        if (value.ValueKind != JsonValueKind.Array) return fallback;
        JsonElement.ArrayEnumerator items = value.EnumerateArray();
        int current = 0;
        while (items.MoveNext())
        {
            if (current++ == index && TryParseNumber(items.Current, out double number))
                return Math.Clamp(number, .05, 12);
        }
        return fallback;
    }

    private static bool Bool(JsonElement element, string property, bool fallback)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt32(out int number) => number != 0,
                JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
                _ => fallback
            } : fallback;

    private static double NumberFromArray(JsonElement element, string property, int index, double fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return fallback;
        JsonElement.ArrayEnumerator items = array.EnumerateArray();
        for (int i = 0; items.MoveNext(); i++)
            if (i == index) return TryParseNumber(items.Current, out double number) ? number : fallback;
        return fallback;
    }

    private static bool TryParseNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out number) && double.IsFinite(number);
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return double.IsFinite(number);
        number = 0;
        return false;
    }

    private static bool TryParseNumber(string? value, out double number)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);

    private static bool IsClockDataSource(string source)
        => source.Equals("time_current", StringComparison.OrdinalIgnoreCase)
            || source.Equals("time_current_12h", StringComparison.OrdinalIgnoreCase);

    private static string FormatClock(DateTime now, string? format, bool twelveHour = false)
    {
        string pattern = string.IsNullOrWhiteSpace(format)
            ? twelveHour ? "YYYY/MM/DD h:mm:ss A" : "HH:mm:ss"
            : format;
        pattern = pattern.Replace("YYYY", "yyyy", StringComparison.Ordinal)
            .Replace("YY", "yy", StringComparison.Ordinal)
            .Replace("DD", "dd", StringComparison.Ordinal)
            .Replace("D", "d", StringComparison.Ordinal)
            .Replace("SSS", "fff", StringComparison.Ordinal)
            .Replace("A", "tt", StringComparison.Ordinal);
        return now.ToString(pattern, CultureInfo.CurrentCulture);
    }

    public void Dispose()
    {
        if (_requestsForegroundFps)
        {
            _requestsForegroundFps = false;
            ForegroundFpsSensor.SetRequested(this, false);
        }
        _mediaLyricsLease?.Dispose();
        _mediaCoverImage?.Dispose();
        _mediaCoverImage = null;
        _mediaBlurredCoverImage?.Dispose();
        _mediaBlurredCoverImage = null;
        _songLyricsMaskBuffer?.Dispose();
        _songLyricsMaskBuffer = null;
        _lyricWrapLineCache.Clear();
        _mediaCoverKey = null;
        foreach (HtVideoFrameSource video in _videos.Values)
            video.Dispose();
        _videos.Clear();
        _gifTimelines.Clear();
        foreach (CachedThemeImage cached in _images.Values)
        {
            cached.Image.Dispose();
            cached.Stream.Dispose();
        }
        _images.Clear();
        _sensorHistory.Clear();
        _lastSensorSample.Clear();
        _progressTransitions.Clear();
        if (_videoDirectory is not null)
        {
            try { Directory.Delete(_videoDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _videoDirectory = null;
        }
    }

    private sealed record CachedThemeImage(Image Image, Stream Stream);
    private sealed record SensorPoint(DateTime Timestamp, double Value);
    private readonly record struct ProgressTransition(float From, float Target, long StartedAt);
}

internal sealed class HtSensorReader
{
    private static readonly object CpuLock = new();
    private static readonly object NetworkLock = new();
    private static readonly object HardwareMonitorLock = new();
    private static LibreHardwareMonitor.Hardware.Computer? _hardwareMonitor;
    private static DateTime _hardwareMonitorNextRetryUtc;
    private static DateTime _nextHardwareSensorRefreshUtc;
    private static IReadOnlyList<MonitorSensor> _lastHardwareMonitorSensors = [];
    private static IReadOnlyList<MonitorSensor> _lastAvailableMonitorSensors = [];
    private static ulong _lastIdle;
    private static ulong _lastKernel;
    private static ulong _lastUser;
    private static long _lastNetworkReceived;
    private static long _lastNetworkSent;
    private static DateTime _lastNetworkSampleUtc;
    private DateTime _nextHardwareRead;
    private DateTime _nextDiskSensorRead;
    private string? _lastSensorSelectionSignature;
    private double? _diskTemperature;
    private double? _diskUtilization;
    private double? _diskReadMbPerSecond;
    private double? _diskWriteMbPerSecond;
    private int _hardwareReadInFlight;
    private volatile SensorSnapshot _last = SensorSnapshot.Empty;

    static HtSensorReader()
        => AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseHardwareMonitor();

    public SensorSnapshot Read()
    {
        double cpu = ReadCpuUtilization(out bool cpuUtilizationAvailable);
        string sensorSelectionSignature = CurrentSensorSelectionSignature();
        if (_lastSensorSelectionSignature is not null
            && !string.Equals(_lastSensorSelectionSignature, sensorSelectionSignature, StringComparison.Ordinal))
        {
            _last = SensorSnapshot.Empty;
            _nextHardwareRead = DateTime.MinValue;
        }
        _lastSensorSelectionSignature = sensorSelectionSignature;
        bool cpuUtilizationSelected = AppSensorSettings.HasSensorSelection("cpuLayout_utilization");
        if (DateTime.UtcNow >= _nextHardwareRead && Interlocked.Exchange(ref _hardwareReadInFlight, 1) == 0)
        {
            _nextHardwareRead = DateTime.UtcNow.AddSeconds(1.5);
            _ = Task.Run(() =>
            {
                try
                {
                    SensorSnapshot next = ReadHardware(cpu, cpuUtilizationAvailable);
                    if (string.Equals(sensorSelectionSignature, CurrentSensorSelectionSignature(), StringComparison.Ordinal))
                        _last = next;
                }
                finally { Volatile.Write(ref _hardwareReadInFlight, 0); }
            });
        }
        double currentCpu = cpuUtilizationSelected
            ? _last.CpuUtilization
            : cpu;
        return _last with
        {
            CpuUtilization = currentCpu,
            CpuUtilizationReady = cpuUtilizationSelected ? _last.CpuUtilizationReady : cpuUtilizationAvailable,
            Now = DateTime.Now
        };
    }

    private static string CurrentSensorSelectionSignature()
        => string.Join("\n", AppSensorSettings.SensorSelections
            .OrderBy(selection => selection.Key, StringComparer.OrdinalIgnoreCase)
            .Select(selection => $"{selection.Key}\0{selection.Value}"));

    private static double ReadCpuUtilization(out bool available)
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            available = false;
            return 0;
        }
        available = true;
        ulong i = idle.Value, k = kernel.Value, u = user.Value;
        lock (CpuLock)
        {
            ulong idleDelta = i >= _lastIdle ? i - _lastIdle : 0;
            ulong totalDelta = (k >= _lastKernel ? k - _lastKernel : 0) + (u >= _lastUser ? u - _lastUser : 0);
            _lastIdle = i; _lastKernel = k; _lastUser = u;
            return totalDelta == 0 ? 0 : Math.Clamp((double)(totalDelta - Math.Min(totalDelta, idleDelta)) * 100 / totalDelta, 0, 100);
        }
    }

    private SensorSnapshot ReadHardware(double cpu, bool cpuUtilizationAvailable)
    {
        var snapshot = new SensorSnapshot { CpuUtilization = cpu, CpuUtilizationReady = cpuUtilizationAvailable, Now = DateTime.Now };
        IReadOnlyList<MonitorSensor> monitorSensors = ReadMonitorSensors()
            .Concat(ReadLibreHardwareMonitorSensors())
            .Concat(ReadOriginalSystemInfosSensors())
            .DistinctBy(sensor => $"{sensor.Type}\0{sensor.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        double? selectedCpuUtilization = FindExplicitMonitorValue(monitorSensors, "CPU", "Load", "cpuLayout_utilization");
        if (AppSensorSettings.HasSensorSelection("cpuLayout_utilization"))
        {
            snapshot.CpuUtilization = selectedCpuUtilization ?? 0;
            snapshot.CpuUtilizationReady = selectedCpuUtilization is not null;
        }
        Volatile.Write(ref _lastAvailableMonitorSensors, monitorSensors);
        var extraValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var extraLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                snapshot.MemoryTotalMb = memory.TotalPhysical / 1024d / 1024d;
                snapshot.MemoryUsedMb = (memory.TotalPhysical - memory.AvailablePhysical) / 1024d / 1024d;
                snapshot.MemoryFreeMb = memory.AvailablePhysical / 1024d / 1024d;
                snapshot.MemoryUtilization = memory.TotalPhysical == 0 ? 0 : snapshot.MemoryUsedMb * 100 / snapshot.MemoryTotalMb;
                snapshot.MemoryTelemetryReady = true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        double? selectedMemoryUsage = FindMonitorValue(monitorSensors, "Memory", "Load", "memLayout_utilization");
        if (AppSensorSettings.HasSensorSelection("memLayout_utilization"))
        {
            snapshot.MemoryTelemetryReady = selectedMemoryUsage is not null;
            if (selectedMemoryUsage is double selectedValue)
                snapshot.MemoryUtilization = selectedValue;
        }
        else if (selectedMemoryUsage is double selectedValue)
        {
            snapshot.MemoryUtilization = selectedValue;
            snapshot.MemoryTelemetryReady = true;
        }

        double? selectedCpuTemperature = FindExplicitMonitorValue(monitorSensors, "CPU", "Temperature", "cpuLayout_temperature");
        snapshot.CpuTemperature = AppSensorSettings.HasSensorSelection("cpuLayout_temperature")
            ? selectedCpuTemperature
            : FindMonitorValue(monitorSensors, "CPU", "Temperature") ?? ReadAcpiTemperature();
        double? selectedGpuTemperature = FindExplicitMonitorValue(monitorSensors, "GPU", "Temperature", "graphics_temperature");
        double? nvidiaGpuTemperature = ReadNvidiaStats(
            snapshot,
            out double? gpuUtilization,
            out double? gpuMemoryUsed,
            out double? gpuMemoryTotal,
            out double? gpuClock,
            out double? gpuPower,
            out string? gpuName);
        snapshot.GpuTemperature = AppSensorSettings.HasSensorSelection("graphics_temperature")
            ? selectedGpuTemperature
            : nvidiaGpuTemperature ?? FindMonitorValue(monitorSensors, "GPU", "Temperature");
        double? selectedGpuUtilization = FindExplicitMonitorValue(monitorSensors, "GPU", "Load", "graphics_utilization");
        snapshot.GpuUtilization = AppSensorSettings.HasSensorSelection("graphics_utilization")
            ? selectedGpuUtilization
            : gpuUtilization ?? ReadGpuUtilization() ?? FindMonitorValue(monitorSensors, "GPU", "Load");
        snapshot.GpuMemoryUsedMb = gpuMemoryUsed;
        snapshot.GpuMemoryTotalMb = gpuMemoryTotal;
        snapshot.CpuName = ReadWmiString("SELECT Name FROM Win32_Processor") ?? ReadCpuName();
        snapshot.GpuName = gpuName ?? ReadWmiString("SELECT Name FROM Win32_VideoController");
        snapshot.MemoryName = "Memória física";
        double? selectedCpuClock = FindExplicitMonitorValue(monitorSensors, "CPU", "Clock", "cpuLayout_currentRefreshRate");
        snapshot.CpuCurrentRefreshRate = AppSensorSettings.HasSensorSelection("cpuLayout_currentRefreshRate")
            ? selectedCpuClock
            : ReadWmiNumber("SELECT CurrentClockSpeed FROM Win32_Processor", "CurrentClockSpeed")
                ?? ReadNativeCpuClock() ?? FindMonitorValue(monitorSensors, "CPU", "Clock");
        double? selectedGpuClock = FindExplicitMonitorValue(monitorSensors, "GPU", "Clock", "graphics_currentRefreshRate");
        snapshot.GpuCurrentRefreshRate = AppSensorSettings.HasSensorSelection("graphics_currentRefreshRate")
            ? selectedGpuClock
            : gpuClock ?? FindMonitorValue(monitorSensors, "GPU", "Clock");
        snapshot.CpuPowerDraw = FindMonitorValue(monitorSensors, "CPU", "Power", "cpuLayout_powerDraw");
        double? selectedGpuPower = FindExplicitMonitorValue(monitorSensors, "GPU", "Power", "graphics_powerDraw");
        snapshot.GpuPowerDraw = AppSensorSettings.HasSensorSelection("graphics_powerDraw")
            ? selectedGpuPower
            : gpuPower ?? FindMonitorValue(monitorSensors, "GPU", "Power");
        snapshot.CpuVoltage = FindMonitorValue(monitorSensors, "CPU", "Voltage", "cpuLayout_vid");
        double? selectedGpuVoltage = FindExplicitMonitorValue(monitorSensors, "GPU", "Voltage", "graphics_vid");
        snapshot.GpuVoltage = AppSensorSettings.HasSensorSelection("graphics_vid")
            ? selectedGpuVoltage
            : FindMonitorValue(monitorSensors, "GPU", "Voltage");
        snapshot.DiskName = ReadWmiProperty("SELECT Model FROM Win32_DiskDrive", "Model");
        snapshot.DiskFreeGb = ReadSystemDriveSpace(out double? diskUsedGb, out double? diskTotalGb);
        if (snapshot.DiskFreeGb is double freeGb) extraValues["diskLayout_free"] = freeGb;
        if (diskUsedGb is double usedGb) extraValues["diskLayout_used"] = usedGb;
        if (diskTotalGb is double totalGb) extraValues["diskLayout_total"] = totalGb;
        if (!string.IsNullOrWhiteSpace(snapshot.DiskName)) extraLabels["diskLayout_name"] = snapshot.DiskName;

        AddMonitorValue(extraValues, "cpuLayout_fanSpeed", FindMonitorValue(monitorSensors, "CPU", "Fan", "cpuLayout_fanSpeed"));
        AddMonitorValue(extraValues, "graphics_fanSpeed", FindMonitorValue(monitorSensors, "GPU", "Fan", "graphics_fanSpeed"));
        AddMonitorValue(extraValues, "memLayout_temperature", AppTemperatureUnit.Convert(FindMonitorValue(monitorSensors, "Memory", "Temperature", "memLayout_temperature")));
        AddMonitorValue(extraValues, "memLayout_powerDraw", FindMonitorValue(monitorSensors, "Memory", "Power", "memLayout_powerDraw"));
        AddMonitorValue(extraValues, "memLayout_vid", FindMonitorValue(monitorSensors, "Memory", "Voltage", "memLayout_vid"));
        AddMonitorValue(extraValues, "memLayout_currentRefreshRate", FindMonitorValue(monitorSensors, "Memory", "Clock", "memLayout_currentRefreshRate"));
        AddMonitorValue(extraValues, "memLayout_fanSpeed", FindMonitorValue(monitorSensors, "Memory", "Fan", "memLayout_fanSpeed"));
        AddMonitorValue(extraValues, "diskLayout_powerDraw", FindMonitorValue(monitorSensors, "Disk", "Power", "diskLayout_powerDraw"));
        AddMonitorValue(extraValues, "diskLayout_vid", FindMonitorValue(monitorSensors, "Disk", "Voltage", "diskLayout_vid"));
        AddMonitorValue(extraValues, "diskLayout_currentRefreshRate", FindMonitorValue(monitorSensors, "Disk", "Clock", "diskLayout_currentRefreshRate"));
        AddMonitorValue(extraValues, "diskLayout_fanSpeed", FindMonitorValue(monitorSensors, "Disk", "Fan", "diskLayout_fanSpeed"));
        AddMonitorValue(extraValues, "mainBordLayout_fanSpeed", FindMonitorValue(monitorSensors, "Mainboard", "Fan", "mainBordLayout_fanSpeed"));
        AddMonitorValue(extraValues, "fanLayout_fanSpeed", FindMonitorValue(monitorSensors, "Any", "Fan", "fanLayout_fanSpeed"));
        AddMonitorValue(extraValues, "coolLayout_fanSpeed", FindMonitorValue(monitorSensors, "Any", "Fan", "coolLayout_fanSpeed"));
        string? mainboardName = ReadWmiProperty("SELECT Product FROM Win32_BaseBoard", "Product");
        if (!string.IsNullOrWhiteSpace(mainboardName)) extraLabels["mainBordLayout_name"] = mainboardName;

        (double? networkUp, double? networkDown) = ReadNetworkRates();
        AddMonitorValue(extraValues, "netInfo_up", networkUp);
        AddMonitorValue(extraValues, "netInfo_down", networkDown);

        if (AppSensorSettings.DisableDiskSensorScan)
        {
            _diskTemperature = null;
            _diskUtilization = null;
            _diskReadMbPerSecond = null;
            _diskWriteMbPerSecond = null;
            _nextDiskSensorRead = DateTime.MinValue;
        }
        else
        {
            if (DateTime.UtcNow >= _nextDiskSensorRead)
            {
                _diskTemperature = FindMonitorValue(monitorSensors, "Disk", "Temperature", "diskLayout_temperature");
                (double? diskUtilization, _diskReadMbPerSecond, _diskWriteMbPerSecond) = ReadDiskPerformance();
                double? selectedDiskUtilization = FindMonitorValue(monitorSensors, "Disk", "Load", "diskLayout_utilization");
                _diskUtilization = AppSensorSettings.HasSensorSelection("diskLayout_utilization")
                    ? selectedDiskUtilization
                    : selectedDiskUtilization ?? diskUtilization;
                _nextDiskSensorRead = DateTime.UtcNow.AddMilliseconds(AppSensorSettings.DiskSensorScanIntervalMs);
            }
            snapshot.DiskTemperature = _diskTemperature;
            snapshot.DiskUtilization = _diskUtilization;
        }
        AddMonitorValue(extraValues, "diskLayout_utilization", snapshot.DiskUtilization);
        AddMonitorValue(extraValues, "diskLayout_up", _diskWriteMbPerSecond);
        AddMonitorValue(extraValues, "diskLayout_down", _diskReadMbPerSecond);
        AddMonitorValue(extraValues, "disk_up", _diskWriteMbPerSecond);
        snapshot.ExtraValues = extraValues;
        snapshot.ExtraLabels = extraLabels;
        return snapshot;
    }

    private static double? ReadNvidiaStats(
        SensorSnapshot snapshot,
        out double? utilization,
        out double? memoryUsed,
        out double? memoryTotal,
        out double? clock,
        out double? power,
        out string? name)
    {
        utilization = null; memoryUsed = null; memoryTotal = null; clock = null; power = null; name = null;
        try
        {
            var start = new ProcessStartInfo("nvidia-smi")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            string? line = RunNvidiaQuery(start,
                "name,temperature.gpu,utilization.gpu,memory.used,memory.total,clocks.current.graphics,power.draw");
            bool extendedFormat = line is not null;
            line ??= RunNvidiaQuery(start, "temperature.gpu,utilization.gpu,memory.used,memory.total");
            if (line is null) return null;
            string[] values = line.Split(',', StringSplitOptions.TrimEntries);
            if (!extendedFormat && values.Length >= 4)
                values = ["", values[0], values[1], values[2], values[3]];
            if (values.Length < 5) return null;
            name = string.IsNullOrWhiteSpace(values[0]) ? null : values[0];
            double? temperatureValue = TryNumber(values[1], out double temperature)
                && IsUsableSensorValue("Temperature", temperature) ? temperature : null;
            if (temperatureValue is double validTemperature) snapshot.GpuTemperatureValue = validTemperature;
            if (TryNumber(values[2], out double gpuUsage) && IsUsableSensorValue("Load", gpuUsage))
                utilization = Math.Clamp(gpuUsage, 0, 100);
            if (TryNumber(values[3], out double used) && used >= 0) memoryUsed = used;
            if (TryNumber(values[4], out double total) && total > 0) memoryTotal = total;
            if (memoryUsed > memoryTotal) memoryUsed = null;
            if (values.Length > 5 && TryNumber(values[5], out double gpuClock) && gpuClock > 0) clock = gpuClock;
            if (values.Length > 6 && TryNumber(values[6], out double gpuPower) && gpuPower > 0) power = gpuPower;
            return temperatureValue;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
            or UnauthorizedAccessException or System.Security.SecurityException or FormatException)
        {
            return null;
        }
    }

    private static string? RunNvidiaQuery(ProcessStartInfo start, string fields)
    {
        start.ArgumentList.Clear();
        start.ArgumentList.Add($"--query-gpu={fields}");
        start.ArgumentList.Add("--format=csv,noheader,nounits");
        using Process? process = Process.Start(start);
        if (process is null) return null;
        if (!process.WaitForExit(700))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        if (process.ExitCode != 0) return null;
        string? line = process.StandardOutput.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static string? ReadCpuName()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            string? name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static double? ReadNativeCpuClock()
    {
        int processorCount = Math.Max(1, Environment.ProcessorCount);
        int itemSize = Marshal.SizeOf<ProcessorPowerInformation>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(checked(itemSize * processorCount));
            int status = CallNtPowerInformation(
                informationLevel: 11, // ProcessorInformation
                inputBuffer: IntPtr.Zero,
                inputBufferLength: 0,
                outputBuffer: buffer,
                outputBufferLength: itemSize * processorCount);
            if (status != 0) return null;

            double totalMhz = 0;
            int validProcessors = 0;
            for (int index = 0; index < processorCount; index++)
            {
                var info = Marshal.PtrToStructure<ProcessorPowerInformation>(IntPtr.Add(buffer, index * itemSize));
                if (info.CurrentMhz == 0) continue;
                totalMhz += info.CurrentMhz;
                validProcessors++;
            }
            return validProcessors == 0 ? null : totalMhz / validProcessors;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException or OverflowException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    internal static IReadOnlyList<MonitorSensor> GetAvailableSensors()
        => Volatile.Read(ref _lastAvailableMonitorSensors);

    private static IReadOnlyList<MonitorSensor> ReadOriginalSystemInfosSensors()
        => ReadOriginalSystemInfosSensors(
            Path.Combine(Path.GetTempPath(), "Jungle_Leopard_Display.bin"),
            DateTime.Now);

    internal static IReadOnlyList<MonitorSensor> ReadOriginalSystemInfosSensors(string path, DateTime now)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < sizeof(uint) + 1 || stream.Length > 1 << 20) return [];

            Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
            stream.ReadExactly(lengthBytes);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (payloadLength == 0 || payloadLength > 65_532 || payloadLength > stream.Length - sizeof(uint)) return [];

            byte[] payload = new byte[payloadLength];
            stream.ReadExactly(payload);
            using JsonDocument frame = JsonDocument.Parse(payload);
            JsonElement root = frame.RootElement;
            if (!root.TryGetProperty("systemTime", out JsonElement timeElement)
                || timeElement.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(timeElement.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out DateTime capturedAt))
                return [];

            TimeSpan age = now - capturedAt;
            if (age < TimeSpan.FromSeconds(-2) || age > TimeSpan.FromSeconds(5)) return [];

            var sensors = new List<MonitorSensor>();
            foreach (JsonProperty group in root.EnumerateObject())
            {
                string device = group.Name switch
                {
                    "cpuLayout" => "CPU",
                    "graphics" => "GPU",
                    "memLayout" => "Memory",
                    "diskLayout" => "Disk",
                    "mainBordLayout" => "Mainboard",
                    "unKnowLayout" or "fanLayout" or "coolLayout" => "Any",
                    _ => ""
                };
                if (device.Length == 0 || group.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (JsonProperty list in group.Value.EnumerateObject())
                {
                    if (!list.Name.EndsWith("_list", StringComparison.OrdinalIgnoreCase)
                        || list.Value.ValueKind != JsonValueKind.Array) continue;
                    string type = list.Name[..^5] switch
                    {
                        "temperature" => "Temperature",
                        "utilization" => "Load",
                        "currentRefreshRate" => "Clock",
                        "powerDraw" => "Power",
                        "vid" => "Voltage",
                        "fanSpeed" => "Fan",
                        _ => ""
                    };
                    if (type.Length == 0) continue;

                    foreach (JsonElement item in list.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !item.TryGetProperty("sensorname", out JsonElement nameElement)
                            || nameElement.ValueKind != JsonValueKind.String
                            || !item.TryGetProperty("value", out JsonElement valueElement)) continue;
                        string sensorName = nameElement.GetString()?.Trim() ?? "";
                        if (sensorName.Length == 0) continue;
                        double value;
                        if (valueElement.ValueKind == JsonValueKind.Number)
                        {
                            if (!valueElement.TryGetDouble(out value)) continue;
                        }
                        else if (valueElement.ValueKind == JsonValueKind.String
                            && double.TryParse(valueElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                            value = parsed;
                        else continue;
                        if (!double.IsFinite(value)) continue;

                        string name = MonitorNameMatches(sensorName, device) || device == "Any"
                            ? sensorName
                            : $"{device} {sensorName}";
                        sensors.Add(new MonitorSensor(name, type, value));
                    }
                }
            }
            return sensors;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException or OverflowException)
        {
            return [];
        }
    }

    private static IReadOnlyList<MonitorSensor> ReadMonitorSensors()
    {
        foreach (string nameSpace in new[] { "root\\LibreHardwareMonitor", "root\\OpenHardwareMonitor" })
        {
            object? locator = null;
            object? service = null;
            object? records = null;
            var result = new List<MonitorSensor>();
            try
            {
                locator = CreateWmiLocator();
                if (locator is null) continue;
                service = ((dynamic)locator).ConnectServer(".", nameSpace);
                records = ((dynamic)service).ExecQuery("SELECT Name, Value, SensorType FROM Sensor");
                foreach (dynamic record in (dynamic)records)
                {
                    string name = Convert.ToString(record.Name, CultureInfo.InvariantCulture) ?? "";
                    string type = Convert.ToString(record.SensorType, CultureInfo.InvariantCulture) ?? "";
                    if (double.TryParse(Convert.ToString(record.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        && double.IsFinite(value))
                        result.Add(new MonitorSensor(name, type, value));
                }
            }
            catch (Exception ex) when (IsWmiFailure(ex)) { }
            finally
            {
                ReleaseCom(records);
                ReleaseCom(service);
                ReleaseCom(locator);
            }
            if (result.Count > 0) return result;
        }
        return [];
    }

    private static IReadOnlyList<MonitorSensor> ReadLibreHardwareMonitorSensors()
    {
        lock (HardwareMonitorLock)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextHardwareSensorRefreshUtc) return _lastHardwareMonitorSensors;
            if (_hardwareMonitor is null)
            {
                if (now < _hardwareMonitorNextRetryUtc) return [];
                var computer = new LibreHardwareMonitor.Hardware.Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true
                };
                try
                {
                    computer.Open();
                    _hardwareMonitor = computer;
                }
                catch (Exception ex) when (IsOptionalHardwareMonitorFailure(ex))
                {
                    try { computer.Close(); } catch (Exception closeEx) when (IsOptionalHardwareMonitorFailure(closeEx)) { }
                    _hardwareMonitorNextRetryUtc = now.AddSeconds(30);
                    return [];
                }
            }

            var sensors = new List<MonitorSensor>();
            try
            {
                foreach (LibreHardwareMonitor.Hardware.IHardware hardware in _hardwareMonitor.Hardware)
                    ReadLibreHardwareMonitorHardware(hardware, sensors);
            }
            catch (Exception ex) when (IsOptionalHardwareMonitorFailure(ex))
            {
                try { _hardwareMonitor.Close(); } catch (Exception closeEx) when (IsOptionalHardwareMonitorFailure(closeEx)) { }
                _hardwareMonitor = null;
                _hardwareMonitorNextRetryUtc = now.AddSeconds(30);
                _lastHardwareMonitorSensors = [];
                _nextHardwareSensorRefreshUtc = _hardwareMonitorNextRetryUtc;
                return _lastHardwareMonitorSensors;
            }

            _lastHardwareMonitorSensors = sensors;
            _nextHardwareSensorRefreshUtc = now.AddSeconds(1);
            return _lastHardwareMonitorSensors;
        }
    }

    private static void ReadLibreHardwareMonitorHardware(
        LibreHardwareMonitor.Hardware.IHardware hardware,
        ICollection<MonitorSensor> sensors)
    {
        try { hardware.Update(); }
        catch (Exception ex) when (IsOptionalHardwareMonitorFailure(ex)) { }

        string device = hardware.HardwareType switch
        {
            LibreHardwareMonitor.Hardware.HardwareType.Cpu => "CPU",
            LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or
            LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or
            LibreHardwareMonitor.Hardware.HardwareType.GpuIntel => "GPU",
            LibreHardwareMonitor.Hardware.HardwareType.Memory => "Memory",
            LibreHardwareMonitor.Hardware.HardwareType.Motherboard => "Motherboard",
            LibreHardwareMonitor.Hardware.HardwareType.Storage => "Disk",
            _ => hardware.HardwareType.ToString()
        };

        try
        {
            foreach (LibreHardwareMonitor.Hardware.ISensor sensor in hardware.Sensors)
            {
                if (sensor.Value is float value && float.IsFinite(value))
                    sensors.Add(new MonitorSensor($"{device} {hardware.Name} {sensor.Name}", sensor.SensorType.ToString(), value));
            }
        }
        catch (Exception ex) when (IsOptionalHardwareMonitorFailure(ex)) { }

        try
        {
            foreach (LibreHardwareMonitor.Hardware.IHardware subHardware in hardware.SubHardware)
                ReadLibreHardwareMonitorHardware(subHardware, sensors);
        }
        catch (Exception ex) when (IsOptionalHardwareMonitorFailure(ex)) { }
    }

    private static bool IsOptionalHardwareMonitorFailure(Exception ex)
        => ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static void CloseHardwareMonitor()
    {
        lock (HardwareMonitorLock)
        {
            if (_hardwareMonitor is null) return;
            try { _hardwareMonitor.Close(); }
            catch (Exception ex) when (IsOptionalHardwareMonitorFailure(ex)) { }
            _hardwareMonitor = null;
        }
    }

    private static double? FindMonitorValue(
        IReadOnlyList<MonitorSensor> sensors,
        string device,
        string sensorType,
        string? selectionKey = null)
    {
        IEnumerable<MonitorSensor> candidates = sensors.Where(sensor => sensor.Type.Contains(sensorType, StringComparison.OrdinalIgnoreCase)
            && MonitorNameMatches(sensor.Name, device)
            && IsUsableSensorValue(sensor.Type, sensor.Value));
        string? selectedName = AppSensorSettings.GetSensorSelection(selectionKey);
        if (selectedName is not null)
        {
            MonitorSensor? selectedSensor = candidates.FirstOrDefault(sensor =>
                sensor.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            return selectedSensor is null
                ? null
                : NormalizeSensorValue(selectedSensor.Type, selectedSensor.Value);
        }
        MonitorSensor? selected = candidates.FirstOrDefault();
        // A zero temperature from some hardware providers means that the driver
        // could not read the sensor. Skip it so the next provider (such as ACPI)
        // can supply a real value. Zero remains valid for utilization/load.
        return selected is null ? null : NormalizeSensorValue(selected.Type, selected.Value);
    }

    internal static bool IsUsableSensorValue(string sensorType, double value)
    {
        if (!double.IsFinite(value)) return false;
        if (sensorType.Contains("Load", StringComparison.OrdinalIgnoreCase))
            return value is >= 0 and <= 100;
        if (sensorType.Contains("Fan", StringComparison.OrdinalIgnoreCase))
            return value >= 0;
        if (sensorType.Contains("Temperature", StringComparison.OrdinalIgnoreCase))
            return value is > 0 and < 200;
        return value > 0;
    }

    private static double NormalizeSensorValue(string sensorType, double value)
        => sensorType.Contains("Load", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(value, 0, 100)
            : value;

    private static double? FindExplicitMonitorValue(
        IReadOnlyList<MonitorSensor> sensors,
        string device,
        string sensorType,
        string selectionKey)
        => AppSensorSettings.HasSensorSelection(selectionKey)
            ? FindMonitorValue(sensors, device, sensorType, selectionKey)
            : null;

    internal static bool MonitorNameMatches(string name, string device)
    {
        bool has(string value) => name.Contains(value, StringComparison.OrdinalIgnoreCase);
        return device switch
        {
            "CPU" => has("CPU") || ((!has("GPU") && !has("Graphics")) && (has("Core") || has("Package"))),
            "GPU" => has("GPU") || has("Graphics"),
            "Memory" => (has("Memory") || has("RAM") || has("DIMM")) && !has("Graphics") && !has("GPU"),
            "Disk" => has("Disk") || has("Drive") || has("HDD") || has("SSD") || has("Storage"),
            "Mainboard" => has("Mainboard") || has("Motherboard") || has("Board"),
            "Any" => true,
            _ => false
        };
    }

    private static void AddMonitorValue(IDictionary<string, double> values, string key, double? value)
    {
        if (value is double number && double.IsFinite(number)) values[key] = number;
    }

    private static string? ReadWmiString(string query) => ReadWmiProperty(query, "Name");

    private static string? ReadWmiProperty(string query, string propertyName)
    {
        try
        {
            object? locator = CreateWmiLocator();
            if (locator is null) return null;
            dynamic service = ((dynamic)locator).ConnectServer(".", "root\\CIMV2");
            dynamic records = service.ExecQuery(query);
            string? result = null;
            foreach (dynamic record in records)
            {
                object? value = record.Properties_.Item(propertyName).Value;
                result = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(result)) break;
            }
            ReleaseCom(records); ReleaseCom(service); ReleaseCom(locator);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch (Exception ex) when (IsWmiFailure(ex)) { return null; }
    }

    private static double? ReadWmiNumber(string query, string propertyName)
    {
        try
        {
            object? locator = CreateWmiLocator();
            if (locator is null) return null;
            dynamic service = ((dynamic)locator).ConnectServer(".", "root\\CIMV2");
            dynamic records = service.ExecQuery(query);
            double? result = null;
            foreach (dynamic record in records)
            {
                object value = record.Properties_.Item(propertyName).Value;
                if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
                {
                    result = parsed;
                    break;
                }
            }
            ReleaseCom(records); ReleaseCom(service); ReleaseCom(locator);
            return result;
        }
        catch (Exception ex) when (IsWmiFailure(ex)) { return null; }
    }

    private static (double? Utilization, double? ReadMbPerSecond, double? WriteMbPerSecond) ReadDiskPerformance()
    {
        try
        {
            object? locator = CreateWmiLocator();
            if (locator is null) return (null, null, null);
            dynamic service = ((dynamic)locator).ConnectServer(".", "root\\CIMV2");
            dynamic records = service.ExecQuery("SELECT Name, PercentDiskTime, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
            double? utilization = null, readRate = null, writeRate = null;
            foreach (dynamic record in records)
            {
                if (double.TryParse(Convert.ToString(record.PercentDiskTime, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    utilization = Math.Clamp(parsed, 0, 100);
                if (double.TryParse(Convert.ToString(record.DiskReadBytesPersec, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double readBytes))
                    readRate = Math.Max(0, readBytes / 1024d / 1024d);
                if (double.TryParse(Convert.ToString(record.DiskWriteBytesPersec, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double writeBytes))
                    writeRate = Math.Max(0, writeBytes / 1024d / 1024d);
            }
            ReleaseCom(records); ReleaseCom(service); ReleaseCom(locator);
            return (utilization, readRate, writeRate);
        }
        catch (Exception ex) when (IsWmiFailure(ex)) { return (null, null, null); }
    }

    private static double? ReadSystemDriveSpace(out double? usedGb, out double? totalGb)
    {
        usedGb = null;
        totalGb = null;
        try
        {
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
            DriveInfo? drive = DriveInfo.GetDrives().FirstOrDefault(item => item.IsReady && string.Equals(item.Name, systemDrive, StringComparison.OrdinalIgnoreCase));
            if (drive is null) return null;
            const double gb = 1024d * 1024d * 1024d;
            totalGb = drive.TotalSize / gb;
            usedGb = (drive.TotalSize - drive.AvailableFreeSpace) / gb;
            return drive.AvailableFreeSpace / gb;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static (double? UpMbPerSecond, double? DownMbPerSecond) ReadNetworkRates()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
                received = checked(received + stats.BytesReceived);
                sent = checked(sent + stats.BytesSent);
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or OverflowException or InvalidOperationException)
        {
            return (null, null);
        }

        DateTime now = DateTime.UtcNow;
        lock (NetworkLock)
        {
            if (_lastNetworkSampleUtc == default)
            {
                _lastNetworkReceived = received;
                _lastNetworkSent = sent;
                _lastNetworkSampleUtc = now;
                return (null, null);
            }
            double seconds = (now - _lastNetworkSampleUtc).TotalSeconds;
            long receivedDelta = Math.Max(0, received - _lastNetworkReceived);
            long sentDelta = Math.Max(0, sent - _lastNetworkSent);
            _lastNetworkReceived = received;
            _lastNetworkSent = sent;
            _lastNetworkSampleUtc = now;
            return seconds <= 0 ? (null, null)
                : (sentDelta / seconds / 1024d / 1024d, receivedDelta / seconds / 1024d / 1024d);
        }
    }

    private static double? ReadAcpiTemperature()
    {
        try
        {
            object? locator = CreateWmiLocator();
            if (locator is null) return null;
            dynamic service = ((dynamic)locator).ConnectServer(".", "root\\WMI");
            dynamic records = service.ExecQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            var values = new List<double>();
            foreach (dynamic record in records)
            {
                double kelvinTenth = Convert.ToDouble(record.CurrentTemperature, CultureInfo.InvariantCulture);
                double celsius = kelvinTenth / 10d - 273.15;
                if (celsius is > 0 and < 120) values.Add(celsius);
            }
            ReleaseCom(records); ReleaseCom(service); ReleaseCom(locator);
            return values.Count == 0 ? null : values.Max();
        }
        catch (Exception ex) when (IsWmiFailure(ex)) { return null; }
    }

    private static double? ReadGpuUtilization()
    {
        try
        {
            object? locator = CreateWmiLocator();
            if (locator is null) return null;
            dynamic service = ((dynamic)locator).ConnectServer(".", "root\\CIMV2");
            dynamic records = service.ExecQuery("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D%'");
            var values = new List<double>();
            foreach (dynamic record in records)
            {
                if (double.TryParse(Convert.ToString(record.UtilizationPercentage, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                    values.Add(percent);
            }
            ReleaseCom(records); ReleaseCom(service); ReleaseCom(locator);
            return values.Count == 0 ? null : Math.Clamp(values.Max(), 0, 100);
        }
        catch (Exception ex) when (IsWmiFailure(ex)) { return null; }
    }

    private static object? CreateWmiLocator()
    {
        Type? type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        return type is null ? null : Activator.CreateInstance(type);
    }

    private static bool IsWmiFailure(Exception ex)
        => ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException;

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static bool TryNumber(string value, out double parsed)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);

    internal sealed record MonitorSensor(string Name, string Type, double Value);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, int inputBufferLength, IntPtr outputBuffer, int outputBufferLength);
}

internal sealed record SensorSnapshot
{
    public static SensorSnapshot Empty { get; } = new();
    public DateTime Now { get; set; } = DateTime.Now;
    public double CpuUtilization { get; set; }
    public bool CpuUtilizationReady { get; set; }
    public double MemoryUtilization { get; set; }
    public bool MemoryTelemetryReady { get; set; }
    public double MemoryUsedMb { get; set; }
    public double MemoryTotalMb { get; set; }
    public double MemoryFreeMb { get; set; }
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }
    public string? MemoryName { get; set; }
    public string? DiskName { get; set; }
    public double? CpuCurrentRefreshRate { get; set; }
    public double? GpuCurrentRefreshRate { get; set; }
    public double? CpuPowerDraw { get; set; }
    public double? GpuPowerDraw { get; set; }
    public double? CpuVoltage { get; set; }
    public double? GpuVoltage { get; set; }
    public double? DiskTemperature { get; set; }
    public double? DiskUtilization { get; set; }
    public double? DiskFreeGb { get; set; }
    public double? CpuTemperature { get; set; }
    public double? GpuTemperature { get; set; }
    public double GpuTemperatureValue { get; set; }
    public double? GpuUtilization { get; set; }
    public double? ForegroundFps { get; set; }
    public double? GpuMemoryUsedMb { get; set; }
    public double? GpuMemoryTotalMb { get; set; }
    public IReadOnlyDictionary<string, double> ExtraValues { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> ExtraLabels { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string? source, out double value)
    {
        string key = source?.ToLowerInvariant() ?? "";
        double? reading = key switch
        {
            "cpulayout_utilization" => CpuUtilizationReady ? CpuUtilization : null,
            "memlayout_utilization" or "mem_utilization" => MemoryTelemetryReady ? MemoryUtilization : null,
            "graphics_utilization" => GpuUtilization,
            "fps_foreground" => ForegroundFps,
            "cpulayout_temperature" => AppTemperatureUnit.Convert(CpuTemperature),
            "graphics_temperature" => AppTemperatureUnit.Convert(GpuTemperature),
            "disklayout_temperature" => AppTemperatureUnit.Convert(DiskTemperature),
            "disklayout_utilization" => DiskUtilization,
            "memlayout_used" or "memlayout_usedmemory" => MemoryTelemetryReady ? MemoryUsedMb / 1024d : null,
            "memlayout_free" => MemoryTelemetryReady ? MemoryFreeMb / 1024d : null,
            "memlayout_total" or "memlayout_totalmemory" => MemoryTelemetryReady ? MemoryTotalMb / 1024d : null,
            "mem_free" => MemoryTelemetryReady ? MemoryFreeMb : null,
            "cpulayout_currentrefreshrate" => CpuCurrentRefreshRate,
            "graphics_currentrefreshrate" => GpuCurrentRefreshRate,
            "cpulayout_powerdraw" => CpuPowerDraw,
            "graphics_powerdraw" => GpuPowerDraw,
            "cpulayout_vid" => CpuVoltage,
            "graphics_vid" => GpuVoltage,
            "graphics_memoryused" or "graphics_memory_used" => GpuMemoryUsedMb,
            "graphics_memorytotal" or "graphics_memory_total" => GpuMemoryTotalMb,
            "time_current" or "time_current_12h" => Now.Second,
            _ => ExtraValues.TryGetValue(key, out double extra) ? extra : null
        };
        if (reading is double actual && double.IsFinite(actual))
        {
            value = actual;
            return true;
        }
        value = 0;
        return false;
    }

    // Keep numeric fallbacks available for integrations that require the original getter behavior.
    // Live renderers use TryGet so an unavailable sensor cannot appear as a real zero.
    public double Get(string? source) => TryGet(source, out double value) ? value : 0;

    public string Format(string source, int decimals, bool hideUnit)
    {
        if (source.Equals("time_current", StringComparison.OrdinalIgnoreCase))
            return Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        if (source.Equals("time_current_12h", StringComparison.OrdinalIgnoreCase))
            return Now.ToString("yyyy/MM/dd h:mm:ss tt", CultureInfo.CurrentCulture);
        string key = source.ToLowerInvariant();
        string? label = key switch
        {
            "cpulayout_name" => CpuName,
            "graphics_name" => GpuName,
            "memlayout_name" => MemoryName,
            _ => ExtraLabels.TryGetValue(key, out string? extraLabel) ? extraLabel : null
        };
        if (label is not null) return label;
        if (!TryGet(source, out double value)) return "\u2014";
        string number = value.ToString("F" + decimals, CultureInfo.CurrentCulture);
        if (hideUnit) return number;
        string unit = source.ToLowerInvariant() switch
        {
            "cpulayout_temperature" or "graphics_temperature" or "memlayout_temperature" or "disklayout_temperature" => AppTemperatureUnit.UseFahrenheit ? "°F" : "°C",
            "cpulayout_utilization" or "graphics_utilization" or "memlayout_utilization" or "mem_utilization" or "disklayout_utilization" => "%",
            "cpulayout_currentrefreshrate" or "graphics_currentrefreshrate" => " MHz",
            "cpulayout_powerdraw" or "graphics_powerdraw" => " W",
            "cpulayout_vid" or "graphics_vid" => " V",
            "memlayout_used" or "memlayout_usedmemory" or "memlayout_free" or "memlayout_total" or "memlayout_totalmemory" => " GB",
            "mem_free" or "graphics_memoryused" or "graphics_memory_used" or "graphics_memorytotal" or "graphics_memory_total" => " MB",
            "cpulayout_fanspeed" or "graphics_fanspeed" or "memlayout_fanspeed" or "disklayout_fanspeed"
                or "mainbordlayout_fanspeed" or "fanlayout_fanspeed" or "coollayout_fanspeed" => " RPM",
            "memlayout_powerdraw" or "disklayout_powerdraw" => " W",
            "memlayout_vid" or "disklayout_vid" => " V",
            "disklayout_free" or "disklayout_used" or "disklayout_total" => " GB",
            "disklayout_up" or "disklayout_down" or "disk_up" or "netinfo_up" or "netinfo_down" => " MB/s",
            "disklayout_currentrefreshrate" => " MHz",
            "fps_foreground" => " FPS",
            _ => ""
        };
        return number + unit;
    }
}

internal static class AppTemperatureUnit
{
    public static bool UseFahrenheit { get; set; }

    public static double Convert(double? celsius)
    {
        if (celsius is not double value) return double.NaN;
        return UseFahrenheit ? value * 9d / 5d + 32d : value;
    }
}

internal static class AppRenderSettings
{
    public static string Mode { get; set; } = "highPerformance";
}

internal static class AppSensorSettings
{
    public static volatile bool DisableDiskSensorScan;
    public static volatile int DiskSensorScanIntervalMs = 2000;
    public static ConcurrentDictionary<string, string> SensorSelections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string? GetSensorSelection(string? key)
        => !string.IsNullOrWhiteSpace(key) && SensorSelections.TryGetValue(key, out string? sensor)
            ? sensor
            : null;

    public static bool HasSensorSelection(string key)
        => SensorSelections.ContainsKey(key);
}
