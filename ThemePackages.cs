using System.IO;
using System.IO.Compression;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JungleLeopardPoc;

/// <summary>Reads and writes Jungle Leopard .ht theme packages without loading the vendor application.</summary>
internal sealed class ThemePackage
{
    private const int MaxPackageBytes = 100 * 1024 * 1024;
    internal const string SteamBackgroundAssetPath = "media/image/steam-dynamic-background.jpg";
    internal const string SteamCoverAssetPath = "media/image/steam-required-cover.jpg";
    private readonly byte[] _packageBytes;
    private readonly string? _themeJsonPath;

    private ThemePackage(string name, string previewPath, byte[] previewBytes, byte[] packageBytes,
        JsonElement definition, Dictionary<string, byte[]> assets, string? themeJsonPath)
    {
        Name = name;
        PreviewPath = previewPath;
        PreviewBytes = previewBytes;
        _packageBytes = packageBytes;
        Definition = definition;
        _assets = assets;
        _themeJsonPath = themeJsonPath;
    }

    private readonly Dictionary<string, byte[]> _assets;
    public string Name { get; }
    public string PreviewPath { get; }
    public byte[] PreviewBytes { get; }
    public JsonElement Definition { get; }
    public int DeviceWidth => ReadDimension("deviceWidth", 960);
    public int DeviceHeight => ReadDimension("deviceHeight", 480);

    public bool TryGetAsset(string path, out byte[] bytes)
    {
        string normalized = Normalize(path);
        if (_assets.TryGetValue(normalized, out byte[]? asset)
            || _assets.TryGetValue(Path.GetFileName(normalized), out asset))
        {
            bytes = asset;
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    public static ThemePackage Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public static (string Name, byte[] PreviewBytes) ReadInstalledThemePreview(string jsonPath)
    {
        string fullJsonPath = Path.GetFullPath(jsonPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(fullJsonPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("children", out _))
            throw new InvalidDataException("O JSON selecionado não parece ser um tema instalado do Jungle Leopard.");

        string name = ReadString(root, "label") ?? ReadString(root, "name") ?? Path.GetFileNameWithoutExtension(fullJsonPath);
        string previewPath = Normalize(ReadString(root, "preView") ?? string.Empty);
        if (!IsSafeRelativeAssetPath(previewPath))
            throw new InvalidDataException("O tema instalado não declara um caminho de prévia válido.");

        string themeDirectory = Path.GetDirectoryName(fullJsonPath)!;
        var roots = new List<string> { themeDirectory };
        if (Directory.GetParent(themeDirectory) is DirectoryInfo appDataRoot)
            roots.Insert(0, appDataRoot.FullName);

        byte[]? previewBytes = null;
        foreach (string candidateRoot in roots)
        {
            string rootPath = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(candidateRoot, previewPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) continue;
            previewBytes = File.ReadAllBytes(candidate);
            break;
        }
        if (previewBytes is null)
            throw new FileNotFoundException("Não encontrei a imagem de prévia na pasta de mídia da instalação original.", previewPath);
        ValidateImage(previewBytes);
        return (name, previewBytes);
    }

    public static ThemePackage ImportInstalledTheme(string jsonPath)
    {
        string fullJsonPath = Path.GetFullPath(jsonPath);
        byte[] jsonBytes = File.ReadAllBytes(fullJsonPath);
        using JsonDocument document = JsonDocument.Parse(jsonBytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("children", out _))
            throw new InvalidDataException("O JSON selecionado não parece ser um tema instalado do Jungle Leopard.");

        string name = ReadString(root, "label") ?? ReadString(root, "name") ?? Path.GetFileNameWithoutExtension(fullJsonPath);
        string previewPath = Normalize(ReadString(root, "preView") ?? string.Empty);
        if (string.IsNullOrWhiteSpace(previewPath))
            throw new InvalidDataException("O tema instalado não declara um arquivo preView.");

        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { previewPath };
        CollectMediaReferences(root, assetPaths);
        string themeDirectory = Path.GetDirectoryName(fullJsonPath)!;
        var roots = new List<string> { themeDirectory };
        if (Directory.GetParent(themeDirectory) is DirectoryInfo appDataRoot)
            roots.Insert(0, appDataRoot.FullName);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string assetPath in assetPaths)
        {
            if (!IsSafeRelativeAssetPath(assetPath)) continue;
            foreach (string candidateRoot in roots)
            {
                string candidate = Path.GetFullPath(Path.Combine(candidateRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
                string prefix = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) continue;
                files[assetPath] = File.ReadAllBytes(candidate);
                break;
            }
            if (!files.ContainsKey(assetPath))
                throw new FileNotFoundException("Não encontrei um arquivo de mídia referenciado pelo tema instalado.", assetPath);
        }

        if (!files.TryGetValue(previewPath, out byte[]? previewBytes))
            throw new FileNotFoundException("Não encontrei a imagem de prévia do tema na pasta media da instalação original.", previewPath);
        ValidateImage(previewBytes);

        string definitionPath = SanitizeFileName(Path.GetFileNameWithoutExtension(fullJsonPath)) + ".json";
        files[definitionPath] = jsonBytes;
        using var innerStream = new MemoryStream();
        using (var archive = new ZipArchive(innerStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using Stream output = entry.Open();
                output.Write(bytes);
            }
        }
        byte[] package = WrapZipCrypto(innerStream.ToArray(), "temp.zip", "ht");
        return Read(package, name);
    }

    private static void CollectMediaReferences(JsonElement element, HashSet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals("media", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("bgMedia", StringComparison.OrdinalIgnoreCase))
                    CollectMediaPaths(property.Value, paths);
                else if (property.Name.Equals("src", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is string mediaPath)
                    AddMediaPath(mediaPath, paths);
                CollectMediaReferences(property.Value, paths);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                CollectMediaReferences(child, paths);
        }
    }

    private static void CollectMediaPaths(JsonElement element, HashSet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is string value)
            AddMediaPath(value, paths);
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) CollectMediaPaths(child, paths);
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in element.EnumerateObject()) CollectMediaPaths(property.Value, paths);
    }

    private static void AddMediaPath(string value, HashSet<string> paths)
    {
        string normalized = Normalize(value);
        if (IsSafeRelativeAssetPath(normalized)) paths.Add(normalized);
    }

    private static bool IsSafeRelativeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        string[] segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1
            && segments[0].Equals("media", StringComparison.OrdinalIgnoreCase)
            && segments.All(segment => segment is not "." and not ".." && !segment.Contains(':'));
    }

    public static ThemePackage Read(byte[] packageBytes, string fallbackName = "Tema importado")
    {
        if (packageBytes.Length == 0 || packageBytes.Length > MaxPackageBytes)
            throw new InvalidDataException("O pacote está vazio ou excede o limite de 100 MB.");

        byte[] innerZip = ExtractInnerArchive(packageBytes);
        using var stream = new MemoryStream(innerZip, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            if (entry.Length > MaxPackageBytes)
                throw new InvalidDataException("O pacote contém um arquivo grande demais.");
            using Stream item = entry.Open();
            using var output = new MemoryStream();
            item.CopyTo(output);
            files[Normalize(entry.FullName)] = output.ToArray();
        }

        JsonDocument? theme = null;
        string? themeJsonPath = null;
        try
        {
            foreach ((string name, byte[] bytes) in files)
            {
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    theme = JsonDocument.Parse(bytes);
                    if (theme.RootElement.ValueKind == JsonValueKind.Object
                        && (theme.RootElement.TryGetProperty("children", out _) || theme.RootElement.TryGetProperty("preView", out _)))
                    {
                        themeJsonPath = name;
                        break;
                    }
                    theme.Dispose();
                    theme = null;
                }
                catch (JsonException) { }
            }

            string nameFromTheme = ReadString(theme?.RootElement, "label")
                ?? ReadString(theme?.RootElement, "name")
                ?? fallbackName;
            string previewPath = Normalize(ReadString(theme?.RootElement, "preView") ?? "");
            byte[]? preview = null;
            if (!string.IsNullOrEmpty(previewPath))
                files.TryGetValue(previewPath, out preview);
            preview ??= files.FirstOrDefault(pair => IsImage(pair.Key)).Value;
            if (preview is null)
                throw new InvalidDataException("O .ht foi aberto, mas não contém uma imagem de prévia reconhecida.");
            if (string.IsNullOrEmpty(previewPath))
                previewPath = files.First(pair => ReferenceEquals(pair.Value, preview)).Key;
            ValidateImage(preview);
            return new ThemePackage(nameFromTheme, previewPath, preview, packageBytes, theme?.RootElement.Clone() ?? default, files, themeJsonPath);
        }
        finally { theme?.Dispose(); }
    }

    public byte[] ToBytes() => (byte[])_packageBytes.Clone();

    public JsonObject CreateEditableDefinition()
    {
        if (Definition.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Este pacote não contém uma cena .ht editável.");
        return JsonNode.Parse(Definition.GetRawText()) as JsonObject
            ?? throw new InvalidDataException("A definição do tema não pôde ser editada.");
    }

    public ThemePackage WithDefinition(JsonObject definition, IReadOnlyDictionary<string, byte[]>? additionalAssets = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var assets = new Dictionary<string, byte[]>(_assets, StringComparer.OrdinalIgnoreCase);
        if (additionalAssets is not null)
            foreach ((string path, byte[] bytes) in additionalAssets)
                assets[Normalize(path)] = (byte[])bytes.Clone();

        using JsonDocument json = JsonDocument.Parse(definition.ToJsonString());
        string name = ReadString(json.RootElement, "label") ?? ReadString(json.RootElement, "name") ?? Name;
        string previewPath = Normalize(ReadString(json.RootElement, "preView") ?? PreviewPath);
        assets.TryGetValue(previewPath, out byte[]? preview);
        preview ??= PreviewBytes;
        return new ThemePackage(name, previewPath, preview, _packageBytes,
            json.RootElement.Clone(), assets, _themeJsonPath);
    }

    public byte[] CreateEditedCopy(JsonObject definition, IReadOnlyDictionary<string, byte[]>? additionalAssets,
        byte[] previewBytes)
    {
        if (_themeJsonPath is null)
            throw new InvalidDataException("O .ht não contém um arquivo JSON de tema que possa ser salvo.");
        ValidateImage(previewBytes);

        JsonObject edited = (JsonObject)definition.DeepClone();
        string name = edited["label"]?.GetValue<string>() ?? edited["name"]?.GetValue<string>() ?? Name;
        edited["id"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N")[..4];
        string previewPath = $"media/preview/{Guid.NewGuid():N}.jpg";
        edited["preView"] = previewPath;
        if (string.IsNullOrWhiteSpace(edited["label"]?.GetValue<string>()))
            edited["label"] = name;

        var assets = new Dictionary<string, byte[]>(_assets, StringComparer.OrdinalIgnoreCase);
        if (additionalAssets is not null)
            foreach ((string path, byte[] bytes) in additionalAssets)
                assets[Normalize(path)] = bytes;
        assets[previewPath] = previewBytes;

        using var innerStream = new MemoryStream();
        using (var archive = new ZipArchive(innerStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in assets)
            {
                if (string.Equals(path, _themeJsonPath, StringComparison.OrdinalIgnoreCase)) continue;
                ZipArchiveEntry asset = archive.CreateEntry(path, CompressionLevel.Optimal);
                using Stream output = asset.Open();
                output.Write(bytes);
            }

            ZipArchiveEntry theme = archive.CreateEntry(_themeJsonPath, CompressionLevel.Optimal);
            using Stream jsonOutput = theme.Open();
            JsonSerializer.Serialize(jsonOutput, edited, new JsonSerializerOptions { WriteIndented = true });
        }
        if (innerStream.Length > MaxPackageBytes)
            throw new InvalidDataException("O tema editado excede o limite de 100 MB do formato .ht.");
        byte[] package = WrapZipCrypto(innerStream.ToArray(), "temp.zip", "ht");
        if (package.Length > MaxPackageBytes)
            throw new InvalidDataException("O pacote .ht criptografado excede o limite de 100 MB.");
        return package;
    }

    public byte[] CreateNamedCopy(string newName)
    {
        if (_themeJsonPath is null || Definition.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Este pacote não contém uma cena editável para copiar.");
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Informe um nome para a cópia.", nameof(newName));

        JsonObject definition = JsonNode.Parse(Definition.GetRawText()) as JsonObject
            ?? throw new InvalidDataException("A definição do tema não pôde ser copiada.");
        definition["id"] = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..4]}";
        definition["label"] = newName.Trim();
        if (definition.ContainsKey("name")) definition["name"] = newName.Trim();

        using var innerStream = new MemoryStream();
        using (var archive = new ZipArchive(innerStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in _assets)
            {
                if (string.Equals(path, _themeJsonPath, StringComparison.OrdinalIgnoreCase)) continue;
                ZipArchiveEntry asset = archive.CreateEntry(path, CompressionLevel.Optimal);
                using Stream output = asset.Open();
                output.Write(bytes);
            }

            string jsonPath = $"{SanitizeFileName(newName)}.json";
            ZipArchiveEntry theme = archive.CreateEntry(jsonPath, CompressionLevel.Optimal);
            using Stream jsonOutput = theme.Open();
            JsonSerializer.Serialize(jsonOutput, definition, new JsonSerializerOptions { WriteIndented = true });
        }

        return WrapZipCrypto(innerStream.ToArray(), "temp.zip", "ht");
    }

    public static byte[] CreateStatic(string name, byte[] renderedJpeg)
    {
        ValidateImage(renderedJpeg);
        string asset = $"media/image/{Guid.NewGuid():N}.jpg";
        string id = Guid.NewGuid().ToString("N");
        var theme = new JsonObject
        {
            ["id"] = id,
            ["label"] = name,
            ["name"] = name,
            ["deviceWidth"] = 960,
            ["deviceHeight"] = 480,
            ["preView"] = asset,
            ["children"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["_chosed"] = true,
                    ["_checked"] = true,
                    ["icon"] = "media",
                    ["label"] = "Multimídia",
                    ["type"] = "image",
                    ["_group"] = "Componentes gerais",
                    ["children"] = new JsonObject
                    {
                        ["keepRatio"] = false,
                        ["x"] = 480,
                        ["y"] = 240,
                        ["rotate"] = 0,
                        ["width"] = 960,
                        ["height"] = 480,
                        ["mediaLoadType"] = "local",
                        ["media"] = new JsonArray(asset)
                    },
                    ["tx"] = 0,
                    ["ty"] = 0,
                    ["scale"] = new JsonArray(1, 1)
                }
            }
        };

        using var innerStream = new MemoryStream();
        using (var zip = new ZipArchive(innerStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry jsonEntry = zip.CreateEntry($"{SanitizeFileName(name)}.json", CompressionLevel.Optimal);
            using (var output = jsonEntry.Open())
                JsonSerializer.Serialize(output, theme, new JsonSerializerOptions { WriteIndented = true });
            ZipArchiveEntry imageEntry = zip.CreateEntry(asset, CompressionLevel.Optimal);
            using var imageOutput = imageEntry.Open();
            imageOutput.Write(renderedJpeg);
        }
        return WrapZipCrypto(innerStream.ToArray(), "temp.zip", "ht");
    }

    public static byte[] CreateBlank(string name, int width = 960, int height = 480)
    {
        using var image = new Bitmap(Math.Clamp(width, 64, 4096), Math.Clamp(height, 64, 4096));
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.FromArgb(32, 32, 36));
            using var pen = new Pen(Color.FromArgb(95, 185, 154, 247), Math.Max(2, image.Width / 240f));
            float inset = Math.Max(16, image.Width * .04f);
            graphics.DrawRectangle(pen, inset, inset, image.Width - inset * 2, image.Height - inset * 2);
        }
        using var preview = new MemoryStream();
        image.Save(preview, ImageFormat.Jpeg);
        byte[] packageBytes = CreateStatic(name, preview.ToArray());
        ThemePackage package = Read(packageBytes, name);
        JsonObject definition = package.CreateEditableDefinition();
        definition["children"] = new JsonArray();
        return package.CreateEditedCopy(definition, null, preview.ToArray());
    }

    public static byte[] CreateSteamLayout(string name, int width, int height)
    {
        width = Math.Clamp(width, 64, 4096);
        height = Math.Clamp(height, 64, 4096);
        bool portrait = height > width;
        double coverAspect = portrait ? 2d / 3d : 920d / 430d;
        double coverWidth = Math.Min(width, height * coverAspect);
        double coverHeight = coverWidth / coverAspect;
        double x = (width - coverWidth) / 2d;
        double y = (height - coverHeight) / 2d;

        byte[] blank = CreateBlank(name, width, height);
        ThemePackage package = Read(blank, name);
        JsonObject definition = package.CreateEditableDefinition();
        definition["deviceWidth"] = width;
        definition["deviceHeight"] = height;
        definition["bgMedia"] = new JsonArray(SteamBackgroundAssetPath);
        definition["bgSetting"] = "cover";
        definition["bgConfig"] = new JsonObject { ["src"] = SteamBackgroundAssetPath, ["fit"] = "cover" };
        definition["children"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["_chosed"] = true,
                ["_checked"] = false,
                ["_steamRequired"] = true,
                ["label"] = "Capa Steam",
                ["type"] = "image",
                ["icon"] = "media",
                ["children"] = new JsonObject
                {
                    ["keepRatio"] = true,
                    ["x"] = x,
                    ["y"] = y,
                    ["rotate"] = 0,
                    ["width"] = coverWidth,
                    ["height"] = coverHeight,
                    ["mediaLoadType"] = "local",
                    ["media"] = new JsonArray(SteamCoverAssetPath)
                },
                ["tx"] = 0,
                ["ty"] = 0,
                ["scale"] = new JsonArray(1, 1)
            }
        };

        byte[] backgroundBytes = CreateSteamBackgroundAsset(width, height);
        int coverPixelWidth = portrait ? 600 : 920;
        int coverPixelHeight = portrait ? 900 : 430;
        byte[] coverBytes = CreateSteamPlaceholder(coverPixelWidth, coverPixelHeight, portrait, cover: true);
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [SteamBackgroundAssetPath] = backgroundBytes,
            [SteamCoverAssetPath] = coverBytes
        };

        using var renderer = new HtThemeRenderer(package.WithDefinition(definition, assets), fitToScreen: true, allowForegroundFps: false);
        using Bitmap preview = renderer.Render(new System.Drawing.Size(width, height));
        using var previewStream = new MemoryStream();
        preview.Save(previewStream, ImageFormat.Jpeg);
        return package.CreateEditedCopy(definition, assets, previewStream.ToArray());
    }

    internal static byte[] CreateSteamBackgroundAsset(int width, int height)
    {
        using var bitmap = new Bitmap(Math.Clamp(width, 1, 4096), Math.Clamp(height, 1, 4096), PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.Clear(Color.Black);

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Jpeg);
        return output.ToArray();
    }

    private static byte[] CreateSteamPlaceholder(int width, int height, bool portrait, bool cover)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(cover ? Color.FromArgb(34, 41, 56) : Color.Black);
            if (cover)
            {
                using var gradient = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, width, height),
                    Color.FromArgb(70, 105, 180), Color.FromArgb(20, 25, 38), 45f);
                graphics.FillRectangle(gradient, 0, 0, width, height);
                using var font = new Font(FontFamily.GenericSansSerif, Math.Max(14, height * .075f), FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.FromArgb(225, 240, 244, 255));
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                graphics.DrawString(portrait ? "STEAM\nCAPA RETRATO" : "STEAM\nCAPA PAISAGEM", font, brush,
                    new RectangleF(0, height * .38f, width, height * .24f), format);
            }
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Jpeg);
        return output.ToArray();
    }

    private static byte[] ExtractInnerArchive(byte[] outer)
    {
        int eocd = FindSignature(outer, 0x06054B50, Math.Max(0, outer.Length - 65_557));
        if (eocd < 0)
            throw new InvalidDataException("O arquivo não parece ser um ZIP ou .ht válido.");
        int entries = ReadUInt16(outer, eocd + 10);
        int cursor = (int)ReadUInt32(outer, eocd + 16);
        for (int i = 0; i < entries; i++)
        {
            if (cursor + 46 > outer.Length || ReadUInt32(outer, cursor) != 0x02014B50)
                break;
            int flags = ReadUInt16(outer, cursor + 8);
            int method = ReadUInt16(outer, cursor + 10);
            uint crc = ReadUInt32(outer, cursor + 16);
            int packedSize = checked((int)ReadUInt32(outer, cursor + 20));
            int nameLength = ReadUInt16(outer, cursor + 28);
            int extraLength = ReadUInt16(outer, cursor + 30);
            int commentLength = ReadUInt16(outer, cursor + 32);
            int localOffset = checked((int)ReadUInt32(outer, cursor + 42));
            string entryName = Encoding.UTF8.GetString(outer, cursor + 46, nameLength);
            int localNameLength = ReadUInt16(outer, localOffset + 26);
            int localExtraLength = ReadUInt16(outer, localOffset + 28);
            int payloadOffset = localOffset + 30 + localNameLength + localExtraLength;
            if (packedSize < 0 || payloadOffset < 0 || payloadOffset + packedSize > outer.Length)
                throw new InvalidDataException("O pacote .ht está truncado.");

            byte[] packed = outer.AsSpan(payloadOffset, packedSize).ToArray();
            if ((flags & 1) != 0)
            {
                if (packed.Length < 12)
                    throw new InvalidDataException("O pacote .ht tem um cabeçalho de criptografia inválido.");
                packed = ZipCrypto.Decrypt(packed, "ht");
            }

            byte[] content = method switch
            {
                0 => packed,
                8 => Inflate(packed),
                _ => throw new InvalidDataException($"O pacote usa o método ZIP não suportado ({method}).")
            };
            if (Crc32.Compute(content) != crc)
                throw new InvalidDataException("A verificação de integridade do pacote .ht falhou.");

            if (entryName.Equals("temp.zip", StringComparison.OrdinalIgnoreCase) || IsZip(content))
                return content;

            cursor += 46 + nameLength + extraLength + commentLength;
        }

        // Support regular ZIP files containing theme JSON directly.
        if (IsZip(outer))
            return outer;
        throw new InvalidDataException("Não encontrei o arquivo temp.zip dentro do pacote .ht.");
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflater.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] WrapZipCrypto(byte[] content, string entryName, string password)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        uint crc = Crc32.Compute(content);
        var random = new Random();
        byte[] plainHeader = new byte[12];
        random.NextBytes(plainHeader);
        plainHeader[11] = (byte)(crc >> 24);
        var crypto = new ZipCrypto(password);
        byte[] encrypted = new byte[content.Length + 12];
        for (int i = 0; i < plainHeader.Length; i++) encrypted[i] = crypto.Encrypt(plainHeader[i]);
        for (int i = 0; i < content.Length; i++) encrypted[i + 12] = crypto.Encrypt(content[i]);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        uint dosTime = 0x00210000; // 2020-01-01 00:00
        writer.Write(0x04034B50u); writer.Write((ushort)20); writer.Write((ushort)1); writer.Write((ushort)0);
        writer.Write((ushort)(dosTime & 0xFFFF)); writer.Write((ushort)(dosTime >> 16));
        writer.Write(crc); writer.Write((uint)encrypted.Length); writer.Write((uint)content.Length);
        writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write(name); writer.Write(encrypted);
        uint centralOffset = (uint)output.Position;
        writer.Write(0x02014B50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)1); writer.Write((ushort)0);
        writer.Write((ushort)(dosTime & 0xFFFF)); writer.Write((ushort)(dosTime >> 16));
        writer.Write(crc); writer.Write((uint)encrypted.Length); writer.Write((uint)content.Length);
        writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(0u); writer.Write(name);
        uint centralSize = (uint)output.Position - centralOffset;
        writer.Write(0x06054B50u); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(centralSize); writer.Write(centralOffset); writer.Write((ushort)0);
        return output.ToArray();
    }

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && ReadUInt32(bytes, 0) == 0x04034B50;
    private static int FindSignature(byte[] bytes, uint signature, int start)
    {
        for (int i = bytes.Length - 22; i >= start; i--)
            if (ReadUInt32(bytes, i) == signature) return i;
        return -1;
    }
    private static ushort ReadUInt16(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    private static uint ReadUInt32(byte[] bytes, int offset) => (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');
    private static bool IsImage(string path) => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
    private static void ValidateImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using Image image = Image.FromStream(stream);
        if (image.Width <= 0 || image.Height <= 0) throw new InvalidDataException("A imagem de prévia é inválida.");
    }
    private static string? ReadString(JsonElement? element, string property)
        => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out JsonElement result) && result.ValueKind == JsonValueKind.String
            ? result.GetString() : null;
    private int ReadDimension(string property, int fallback)
        => Definition.ValueKind == JsonValueKind.Object
            && Definition.TryGetProperty(property, out JsonElement value)
            && value.TryGetInt32(out int dimension) && dimension is > 0 and <= 4096
                ? dimension : fallback;
    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        value = value.Trim().Trim('.');
        return string.IsNullOrEmpty(value) ? "Minha capa" : value;
    }

    private sealed class ZipCrypto
    {
        private uint _key0 = 0x12345678, _key1 = 0x23456789, _key2 = 0x34567890;
        public ZipCrypto(string password) { foreach (byte b in Encoding.UTF8.GetBytes(password)) Update(b); }
        public byte Encrypt(byte plain)
        {
            uint temp = _key2 | 2;
            byte cipher = (byte)(plain ^ (byte)((temp * (temp ^ 1)) >> 8));
            Update(plain);
            return cipher;
        }
        public static byte[] Decrypt(byte[] cipher, string password)
        {
            var state = new ZipCrypto(password);
            var plain = new byte[cipher.Length - 12];
            for (int i = 0; i < cipher.Length; i++)
            {
                uint temp = state._key2 | 2;
                byte value = (byte)(cipher[i] ^ (byte)((temp * (temp ^ 1)) >> 8));
                state.Update(value);
                if (i >= 12) plain[i - 12] = value;
            }
            return plain;
        }
        private void Update(byte value)
        {
            _key0 = Crc32.Update(_key0, value);
            _key1 = unchecked((_key1 + (_key0 & 0xFF)) * 134775813 + 1);
            _key2 = Crc32.Update(_key2, (byte)(_key1 >> 24));
        }
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }
        public static uint Update(uint crc, byte value) => Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        public static uint Compute(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte value in bytes) crc = Update(crc, value);
            return ~crc;
        }
    }
}
