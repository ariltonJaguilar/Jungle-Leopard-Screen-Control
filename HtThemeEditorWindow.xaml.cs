using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingSize = System.Drawing.Size;
using PointF = System.Drawing.PointF;
using RotateFlipType = System.Drawing.RotateFlipType;

namespace JungleLeopardPoc;

/// <summary>Independent layer editor for compatible Jungle Leopard .ht scenes.</summary>
public partial class HtThemeEditorWindow : System.Windows.Controls.UserControl
{
    [Flags]
    private enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private static readonly (string Label, string[] Sources)[] DataSourceGroups =
    [
        ("CPU", ["cpuLayout_name", "cpuLayout_utilization", "cpuLayout_temperature", "cpuLayout_vid", "cpuLayout_powerDraw", "cpuLayout_fanSpeed", "cpuLayout_currentRefreshRate"]),
        ("Placa de vídeo", ["graphics_name", "graphics_utilization", "graphics_temperature", "graphics_vid", "graphics_powerDraw", "graphics_fanSpeed", "graphics_currentRefreshRate"]),
        ("Memória", ["memLayout_name", "memLayout_utilization", "memLayout_temperature", "memLayout_vid", "memLayout_powerDraw", "memLayout_fanSpeed", "memLayout_currentRefreshRate", "memLayout_used", "memLayout_free", "memLayout_total"]),
        ("Disco", ["diskLayout_name", "diskLayout_utilization", "diskLayout_temperature", "diskLayout_vid", "diskLayout_powerDraw", "diskLayout_fanSpeed", "diskLayout_currentRefreshRate", "diskLayout_used", "diskLayout_free", "diskLayout_total", "diskLayout_up", "diskLayout_down"]),
        ("Sistema", ["mainBordLayout_name", "mainBordLayout_fanSpeed", "netInfo_up", "netInfo_down", "time_current", "time_current_12h"]),
        ("Aplicativo em primeiro plano", ["fps_foreground"])
    ];
    private static readonly IReadOnlyDictionary<string, string> DataSourceLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["cpuLayout_name"] = "Modelo do processador",
        ["cpuLayout_utilization"] = "Uso da CPU (%)",
        ["cpuLayout_temperature"] = "Temperatura da CPU",
        ["cpuLayout_vid"] = "Tensão da CPU",
        ["cpuLayout_powerDraw"] = "Consumo de energia da CPU",
        ["cpuLayout_fanSpeed"] = "Velocidade da ventoinha da CPU",
        ["cpuLayout_currentRefreshRate"] = "Frequência da CPU",
        ["graphics_name"] = "Modelo da placa de vídeo",
        ["graphics_utilization"] = "Uso da placa de vídeo (%)",
        ["graphics_temperature"] = "Temperatura da placa de vídeo",
        ["graphics_vid"] = "Tensão da placa de vídeo",
        ["graphics_powerDraw"] = "Consumo de energia da placa de vídeo",
        ["graphics_fanSpeed"] = "Velocidade da ventoinha da placa de vídeo",
        ["graphics_currentRefreshRate"] = "Frequência da placa de vídeo",
        ["memLayout_name"] = "Tipo de memória",
        ["memLayout_utilization"] = "Uso da memória (%)",
        ["memLayout_temperature"] = "Temperatura da memória",
        ["memLayout_vid"] = "Tensão da memória",
        ["memLayout_powerDraw"] = "Consumo de energia da memória",
        ["memLayout_fanSpeed"] = "Velocidade da ventoinha da memória",
        ["memLayout_currentRefreshRate"] = "Frequência da memória",
        ["memLayout_used"] = "Memória usada (GB)",
        ["memLayout_free"] = "Memória disponível (GB)",
        ["memLayout_total"] = "Memória total (GB)",
        ["diskLayout_name"] = "Nome da unidade de armazenamento",
        ["diskLayout_utilization"] = "Uso do disco (%)",
        ["diskLayout_temperature"] = "Temperatura do disco",
        ["diskLayout_vid"] = "Tensão do disco",
        ["diskLayout_powerDraw"] = "Consumo de energia do disco",
        ["diskLayout_fanSpeed"] = "Velocidade da ventoinha do disco",
        ["diskLayout_currentRefreshRate"] = "Frequência do disco",
        ["diskLayout_used"] = "Espaço usado no disco (GB)",
        ["diskLayout_free"] = "Espaço livre no disco (GB)",
        ["diskLayout_total"] = "Espaço total no disco (GB)",
        ["diskLayout_up"] = "Gravação no disco (MB/s)",
        ["diskLayout_down"] = "Leitura do disco (MB/s)",
        ["mainBordLayout_name"] = "Modelo da placa-mãe",
        ["mainBordLayout_fanSpeed"] = "Velocidade da ventoinha da placa-mãe",
        ["netInfo_up"] = "Velocidade de envio pela rede",
        ["netInfo_down"] = "Velocidade de recebimento pela rede",
        ["time_current"] = "Data e hora (24 horas)",
        ["time_current_12h"] = "Data e hora (12 horas)",
        ["fps_foreground"] = "FPS do app em primeiro plano"
    };
    private static readonly SensorSnapshot LayerMeasurementSensors = CreateLayerMeasurementSensors();

    private readonly ThemePackage _sourcePackage;
    private readonly bool _steamLayoutMode;
    private readonly JsonObject _definition;
    private readonly Dictionary<string, byte[]> _addedAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DrawingSize> _mediaDimensionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mediaDimensionMisses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Text, string FontFamily, int FontSizeBits, int FontStyle), System.Drawing.SizeF> _textMeasureCache = [];
    private readonly ObservableCollection<LayerItem> _layers = [];
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30d) };
    private readonly object _renderGate = new();
    private HtThemeRenderer? _renderer;
    private bool _loadingProperties;
    private bool _rendering;
    private bool _dragging;
    private bool _resizing;
    private bool _rotating;
    private bool _previewDirty;
    private int _generation;
    private int _contentVersion;
    private System.Windows.Point _dragOrigin;
    private System.Windows.Point _rotationCenter;
    private double _rotationStartPointerAngle;
    private double _rotationStartDegrees;
    private ResizeEdges _resizeEdges;
    private (double X, double Y, double Width, double Height) _dragStartBounds;
    private double _dragStartX;
    private double _dragStartY;
    private double _dragStartWidth;
    private double _dragStartHeight;
    private readonly Dictionary<string, double> _dragStartFontSizes = new(StringComparer.Ordinal);
    private LayerItem? _dragItem;
    private LayerItem? _layerDragSource;
    private LayerGeometry? _displayedSelectionGeometry;
    private DateTime _nextSelectionGeometryCheckUtc;
    private System.Windows.Point _layerDragOrigin;

    internal HtThemeEditorWindow(ThemePackage package, bool steamLayoutMode = false)
    {
        InitializeComponent();
        _steamLayoutMode = steamLayoutMode;
        RotateLabel.Text = "Rotação (graus)";
        LoadFontFamilies();
        _sourcePackage = package;
        _definition = package.CreateEditableDefinition();
        PreviewSurface.Width = PreviewCanvasHost.Width = EditorOverlay.Width = package.DeviceWidth;
        PreviewSurface.Height = PreviewCanvasHost.Height = EditorOverlay.Height = package.DeviceHeight;
        ThemeNameBox.Text = package.Name;
        if (_steamLayoutMode)
        {
            EditorHeading.Text = "EDITOR DA INTEGRA\u00C7\u00C3O STEAM";
            EditorDescription.Text = "Posicione a capa Steam e adicione componentes para esta orienta\u00E7\u00E3o.";
            ThemeNameHeader.Visibility = ThemeNameBox.Visibility = Visibility.Collapsed;
            ThemeNameBox.IsEnabled = false;
            SaveEditorButton.Content = "Salvar layout Steam";
            BackgroundInspectorTab.IsEnabled = false;
            InspectorTabs.SelectedItem = LayerInspectorTab;
            StatusText.Text = "O fundo acompanha a arte do jogo Steam e n\u00E3o pode ser alterado.";
        }
        LoadBackgroundControls();
        LayerList.ItemsSource = _layers;
        _previewTimer.Tick += async (_, _) => await RenderPreviewAsync();
        Unloaded += (_, _) => StopRenderer();
        Loaded += (_, _) => RebuildRenderer();
        ReloadLayers();
        RebuildRenderer();
    }

    private void LoadFontFamilies()
    {
        foreach (string family in System.Windows.Media.Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            FontFamilyBox.Items.Add(family);
    }

    public byte[]? PackageBytes { get; private set; }
    public string ThemeName => ThemeNameBox.Text.Trim();
    internal event EventHandler? SaveRequested;
    internal event EventHandler? CancelRequested;

    private void ReloadLayers(JsonObject? select = null)
    {
        _layers.Clear();
        if (_definition["children"] is not JsonArray children)
        {
            children = [];
            _definition["children"] = children;
        }
        AddChildren(children, 0);
        if (select is not null)
            LayerList.SelectedItem = _layers.FirstOrDefault(item => ReferenceEquals(item.Node, select));
        if (LayerList.SelectedItem is null && _layers.Count > 0)
            LayerList.SelectedIndex = 0;
        if (_layers.Count == 0) ClearInspector();
    }

    private void AddChildren(JsonArray children, int depth)
    {
        JsonObject[] nodes = children.OfType<JsonObject>().ToArray();
        JsonObject[] orderedNodes = nodes
            .Select((node, position) => (Node: node, Position: position))
            .OrderBy(entry => GetNumber(entry.Node["index"], entry.Position))
            .ThenBy(entry => entry.Position)
            .Select(entry => entry.Node)
            .ToArray();
        var groups = nodes.Where(node => Text(node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase))
            .Select(node => Text(node["id"]))
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var added = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);

        void AddNode(JsonObject node, int nodeDepth)
        {
            if (!added.Add(node)) return;
            string type = Text(node["type"]);
            if (type.Length > 0)
            {
                string name = Text(node["label"]);
                string title = string.IsNullOrWhiteSpace(name) ? FriendlyType(type) : name;
                string subtitle = FriendlyType(type) + (nodeDepth > 0 ? " • nível " + (nodeDepth + 1) : string.Empty);
                _layers.Add(new LayerItem(node, children, nodeDepth, title, subtitle, _layers, _steamLayoutMode));
            }
            string id = Text(node["id"]);
            if (type.Equals("group", StringComparison.OrdinalIgnoreCase) && id.Length > 0)
            {
                foreach (JsonObject child in orderedNodes
                    .Where(candidate => Text(candidate["parentId"]).Equals(id, StringComparison.Ordinal)))
                    AddNode(child, nodeDepth + 1);
            }
            if (node["children"] is JsonArray nested)
                AddChildren(nested, nodeDepth + 1);
        }

        foreach (JsonObject node in orderedNodes)
        {
            string parentId = Text(node["parentId"]);
            if (parentId.Length > 0 && groups.Contains(parentId)) continue;
            AddNode(node, depth);
        }
    }

    private void LayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayerList.SelectedItem is not LayerItem item)
        {
            LayerInspectorTab.IsEnabled = false;
            RemoveLayerButton.IsEnabled = false;
            InspectorTabs.SelectedItem = _steamLayoutMode ? LayerInspectorTab : BackgroundInspectorTab;
            ClearInspector();
            return;
        }

        LayerInspectorTab.IsEnabled = true;
        InspectorTabs.SelectedItem = LayerInspectorTab;

        JsonObject? properties = item.Node["children"] as JsonObject;
        _loadingProperties = true;
        try
        {
            bool editable = properties is not null;
            string selectedType = Text(item.Node["type"]).ToLowerInvariant();
            bool group = selectedType == "group";
            LayerNameBox.IsEnabled = true;
            LayerNameBox.Text = Text(item.Node["label"]);
            LayerTypeText.Text = "Tipo: " + FriendlyType(selectedType);
            bool boxStyle = selectedType is "block" or "progress" or "linechart" or "media" or "image";
            bool arcStyle = selectedType is "arc" or "progress";
            bool lineChart = selectedType == "linechart";
            bool dataText = selectedType is "data" or "text";
            bool lyricLayer = selectedType == "lyric";
            bool styledText = dataText || lyricLayer;
            bool progress = selectedType == "progress";
            bool block = selectedType == "block";
            bool audioWave = selectedType is "audiowave" or "audio";
            bool mediaLayer = selectedType is "media" or "image";
            bool steamRequired = _steamLayoutMode && item.IsRequired;
            bool songDisplay = selectedType == "songdisplay";
            bool hasDataRange = selectedType is "progress" or "arc" or "linechart";
            ConfigureSongDisplayInspector(songDisplay);
            bool generalStyle = editable && !songDisplay
                && (boxStyle || arcStyle || lineChart || styledText || block || audioWave);
            bool primaryColor = styledText || progress || arcStyle || lineChart || block || audioWave || songDisplay;
            bool secondColor = progress || arcStyle || block || audioWave || songDisplay;
            bool outlineColor = boxStyle || arcStyle || songDisplay;
            bool trackColor = progress || arcStyle || lineChart || songDisplay;
            LayerTransformPanel.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
            RemoveLayerButton.IsEnabled = !steamRequired;
            ContentPanel.Visibility = dataText || songDisplay ? Visibility.Visible : Visibility.Collapsed;
            GeneralLayerStylePanel.Visibility = generalStyle ? Visibility.Visible : Visibility.Collapsed;
            BorderRadiusPanel.Visibility = BorderWidthPanel.Visibility = BorderStylePanel.Visibility
                = boxStyle || songDisplay ? Visibility.Visible : Visibility.Collapsed;
            StrokeWidthPanel.Visibility = GapDegreePanel.Visibility = arcStyle || songDisplay
                ? Visibility.Visible : Visibility.Collapsed;
            PrecisionPanel.Visibility = dataText ? Visibility.Visible : Visibility.Collapsed;
            ClockFormatPanel.Visibility = dataText && IsClockDataSource(Text(properties?["dataSource"]))
                ? Visibility.Visible : Visibility.Collapsed;
            ProgressTypePanel.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
            BorderBgTypePanel.Visibility = arcStyle ? Visibility.Visible : Visibility.Collapsed;
            BlockGradientPanel.Visibility = block || audioWave ? Visibility.Visible : Visibility.Collapsed;
            TextStrokeWidthPanel.Visibility = TextStrokeColorPanel.Visibility
                = TextShadowOffsetXPanel.Visibility = TextShadowOffsetYPanel.Visibility
                = TextShadowBlurPanel.Visibility = TextShadowColorPanel.Visibility
                = styledText ? Visibility.Visible : Visibility.Collapsed;
            KeepRatioCheck.Visibility = editable && !group && !mediaLayer && !songDisplay && !lyricLayer
                ? Visibility.Visible : Visibility.Collapsed;
            PrecisionPanel.Visibility = selectedType == "data" ? Visibility.Visible : Visibility.Collapsed;
            HideUnitCheck.Visibility = IsLyricCheck.Visibility = selectedType == "data"
                ? Visibility.Visible : Visibility.Collapsed;
            ShowAreaCheck.Visibility = ShowXYCheck.Visibility = ShowSplitLinesCheck.Visibility
                = lineChart ? Visibility.Visible : Visibility.Collapsed;
            ColorLabel.Visibility = PrimaryColorInputPanel.Visibility
                = primaryColor ? Visibility.Visible : Visibility.Collapsed;
            SecondColorLabel.Visibility = SecondColorInputPanel.Visibility
                = secondColor ? Visibility.Visible : Visibility.Collapsed;
            OutlineColorLabel.Visibility = OutlineColorInputPanel.Visibility
                = outlineColor ? Visibility.Visible : Visibility.Collapsed;
            TrackColorLabel.Visibility = TrackColorInputPanel.Visibility
                = trackColor ? Visibility.Visible : Visibility.Collapsed;
            FontFamilyLabel.Visibility = FontFamilyBox.Visibility
                = styledText || songDisplay ? Visibility.Visible : Visibility.Collapsed;
            BoldCheck.Visibility = ItalicCheck.Visibility = styledText ? Visibility.Visible : Visibility.Collapsed;
            UnderlineCheck.Visibility = dataText ? Visibility.Visible : Visibility.Collapsed;
            FontSizePanel.Visibility = styledText || songDisplay ? Visibility.Visible : Visibility.Collapsed;
            GeneralColorHelpText.Visibility = styledText || songDisplay ? Visibility.Visible : Visibility.Collapsed;
            AudioWaveSettingsPanel.Visibility = audioWave ? Visibility.Visible : Visibility.Collapsed;
            AudioLineCountBox.IsEnabled = AudioMarginBox.IsEnabled = AudioReduceSpeedBox.IsEnabled = editable && audioWave;
            ArrangeAudioPeakCheck.IsEnabled = editable && audioWave;
            MediaSettingsPanel.Visibility = mediaLayer ? Visibility.Visible : Visibility.Collapsed;
            MediaSourceModeBox.IsEnabled = editable && mediaLayer && !steamRequired;
            MediaUrlBox.IsEnabled = editable && mediaLayer && !steamRequired && SelectedTag(MediaSourceModeBox, "local") == "online";
            ConfigureDataSourceOptions(selectedType, Text(properties?["dataSource"]));
            bool canTransform = editable && !item.IsLocked;
            XBox.IsEnabled = YBox.IsEnabled = canTransform;
            WidthBox.IsEnabled = HeightBox.IsEnabled = RotateBox.IsEnabled = canTransform && !group;
            ContentBox.IsEnabled = editable && !group && (dataText || songDisplay);
            ColorBox.IsEnabled = SecondColorBox.IsEnabled = TrackColorBox.IsEnabled = OutlineColorBox.IsEnabled = editable && !group;
            FontFamilyBox.IsEnabled = editable && !group && (styledText || songDisplay);
            BoldCheck.IsEnabled = ItalicCheck.IsEnabled = editable && !group && styledText;
            FontSizeBox.IsEnabled = editable && !group && (styledText || songDisplay);
            AlignBox.IsEnabled = editable && !group && styledText;
            ContentLabel.Visibility = ContentBox.Visibility = dataText || songDisplay ? Visibility.Visible : Visibility.Collapsed;
            TextAlignmentPanel.Visibility = styledText ? Visibility.Visible : Visibility.Collapsed;
            BoldCheck.Visibility = ItalicCheck.Visibility = styledText ? Visibility.Visible : Visibility.Collapsed;
            UnderlineCheck.Visibility = dataText ? Visibility.Visible : Visibility.Collapsed;
            PrecisionBox.Visibility = selectedType == "data" ? Visibility.Visible : Visibility.Collapsed;
            HideUnitCheck.Visibility = IsLyricCheck.Visibility = selectedType == "data" ? Visibility.Visible : Visibility.Collapsed;
            DataSourceBox.IsEnabled = editable && HasDataSourceEditor(selectedType);
            DataRangePanel.Visibility = hasDataRange ? Visibility.Visible : Visibility.Collapsed;
            RangeMinBox.IsEnabled = RangeMaxBox.IsEnabled = editable && hasDataRange;
            BorderRadiusBox.IsEnabled = BorderWidthBox.IsEnabled = editable && (boxStyle || songDisplay);
            StrokeWidthBox.IsEnabled = GapDegreeBox.IsEnabled = editable && (arcStyle || songDisplay);
            BorderBgTypeBox.IsEnabled = editable && arcStyle;
            BorderStyleBox.IsEnabled = editable && boxStyle;
            ProgressTypeBox.IsEnabled = editable && progress;
            BlockGradientBox.IsEnabled = editable && (block || audioWave);
            KeepRatioCheck.IsEnabled = editable && !group;
            ShowAreaCheck.IsEnabled = ShowXYCheck.IsEnabled = ShowSplitLinesCheck.IsEnabled = editable && lineChart;
            PrecisionBox.IsEnabled = HideUnitCheck.IsEnabled = editable && dataText;
            IsLyricCheck.IsEnabled = editable && dataText;
            TextStrokeWidthBox.IsEnabled = TextStrokeColorBox.IsEnabled = TextShadowOffsetXBox.IsEnabled
                = TextShadowOffsetYBox.IsEnabled = TextShadowBlurBox.IsEnabled = TextShadowColorBox.IsEnabled = editable && styledText;
            ClockFormatBox.IsEnabled = editable && dataText && IsClockDataSource(Text(properties?["dataSource"]));
            UnderlineCheck.IsEnabled = editable && dataText;
            SongPresetModeBox.IsEnabled = SongCoverFillBox.IsEnabled = editable && songDisplay;
            SongShowCoverCheck.IsEnabled = SongShowTitleCheck.IsEnabled = SongShowArtistCheck.IsEnabled = SongShowLyricsCheck.IsEnabled = SongShowBackgroundCheck.IsEnabled = SongShowHintCheck.IsEnabled = SongCircularCoverCheck.IsEnabled = SongRotateCoverCheck.IsEnabled = SongLyricScrollCheck.IsEnabled = editable && songDisplay;
            SongActiveFontSizeBox.IsEnabled = SongMaxLinesBox.IsEnabled = SongTitleFontSizeBox.IsEnabled = SongArtistFontSizeBox.IsEnabled = SongBackgroundRadiusBox.IsEnabled = SongLineSpacingBox.IsEnabled = SongTitleColorBox.IsEnabled = SongBackgroundOpacityBox.IsEnabled = SongCoverRotateDurationBox.IsEnabled = SongGapBox.IsEnabled = editable && songDisplay;
            SecondColorBox.IsEnabled = editable && (selectedType is "progress" or "arc" or "block" or "audiowave" or "audio" || songDisplay);
            TrackColorBox.IsEnabled = editable && (selectedType is "progress" or "arc" or "linechart" || songDisplay);
            if (properties is null)
            {
                XBox.Text = YBox.Text = WidthBox.Text = HeightBox.Text = RotateBox.Text = string.Empty;
                ContentBox.Text = ColorBox.Text = SecondColorBox.Text = OutlineColorBox.Text = TrackColorBox.Text = FontFamilyBox.Text = FontSizeBox.Text = RangeMinBox.Text = RangeMaxBox.Text = string.Empty;
                BorderRadiusBox.Text = BorderWidthBox.Text = StrokeWidthBox.Text = GapDegreeBox.Text = PrecisionBox.Text = ClockFormatBox.Text = string.Empty;
                TextStrokeWidthBox.Text = TextStrokeColorBox.Text = TextShadowOffsetXBox.Text = TextShadowOffsetYBox.Text = TextShadowBlurBox.Text = TextShadowColorBox.Text = string.Empty;
                HideUnitCheck.IsChecked = IsLyricCheck.IsChecked = ShowAreaCheck.IsChecked = ShowXYCheck.IsChecked = ShowSplitLinesCheck.IsChecked = false;
                KeepRatioCheck.IsChecked = false;
                ProgressTypeBox.SelectedIndex = BorderBgTypeBox.SelectedIndex = BorderStyleBox.SelectedIndex = BlockGradientBox.SelectedIndex = -1;
                BoldCheck.IsChecked = ItalicCheck.IsChecked = false;
                UnderlineCheck.IsChecked = false;
                DataSourceBox.SelectedIndex = -1;
                AlignBox.SelectedIndex = -1;
                AudioLineCountBox.Text = AudioMarginBox.Text = AudioReduceSpeedBox.Text = string.Empty;
                ArrangeAudioPeakCheck.IsChecked = false;
                MediaSourceModeBox.SelectedIndex = 0;
                MediaUrlBox.Text = string.Empty;
            }
            else
            {
                XBox.Text = NumberText(properties["x"]);
                YBox.Text = NumberText(properties["y"]);
                WidthBox.Text = NumberText(properties["width"]);
                HeightBox.Text = NumberText(properties["height"]);
                RotateBox.Text = NumberText(properties["rotate"]);
                string songPreset = Text(properties["presetMode"]);
                if (songPreset.Length == 0)
                    songPreset = "vFixed";
                ContentBox.Text = songDisplay
                    ? Text(properties["noMediaHint"]).Length > 0 ? Text(properties["noMediaHint"]) : "Reproduza uma musica para ver as letras"
                    : Text(properties["text"]);
                JsonNode? colorValue = songDisplay ? properties["lyricColor"]
                    : properties["color"] ?? properties["borderBgColor"] ?? properties["backgroundColor"] ?? properties["areaStyle"];
                ColorBox.Text = songDisplay && FirstColor(colorValue).Length == 0 ? "255,255,255,0.6" : FirstColor(colorValue);
                SecondColorBox.Text = songDisplay
                    ? Text(properties["lyricActiveColor"]).Length == 0 ? "255,255,255,1" : Text(properties["lyricActiveColor"])
                    : SecondColor(colorValue);
                OutlineColorBox.Text = songDisplay
                    ? Text(properties["artistColor"]).Length == 0 ? "255,255,255,0.8" : Text(properties["artistColor"])
                    : Text(properties["borderColor"]);
                TrackColorBox.Text = selectedType switch
                {
                    "progress" or "arc" => Text(properties["trailColor"]),
                    "linechart" => Text(properties["backgroundColor"]),
                    "songdisplay" => Text(properties["backgroundColor"]).Length == 0 ? "0,0,0,0" : Text(properties["backgroundColor"]),
                    _ => string.Empty
                };
                FontFamilyBox.Text = songDisplay
                    ? Text(properties["lyricFontFamily"]).Length == 0 ? "Arial" : Text(properties["lyricFontFamily"])
                    : Text(properties["fontFamily"]);
                BoldCheck.IsChecked = IsBoldFontWeight(properties["fontWeight"])
                    || Text(properties["bold"]).Equals("true", StringComparison.OrdinalIgnoreCase)
                    || HasFontStyle(properties["fontStyle"], "bold");
                ItalicCheck.IsChecked = HasFontStyle(properties["fontStyle"], "italic")
                    || Text(properties["fontStyle"]).Contains("italic", StringComparison.OrdinalIgnoreCase);
                UnderlineCheck.IsChecked = HasFontStyle(properties["fontStyle"], "underline")
                    || Text(properties["textDecoration"]).Contains("underline", StringComparison.OrdinalIgnoreCase);
                FontSizeBox.Text = songDisplay && properties["lyricFontSize"] is null ? "24"
                    : NumberText(songDisplay ? properties["lyricFontSize"] : properties["fontSize"]);
                RangeMinBox.Text = NumberText(ArrayItem(properties["dataRange"], 0));
                RangeMaxBox.Text = NumberText(ArrayItem(properties["dataRange"], 1));
                BorderRadiusBox.Text = NumberText(properties["borderRadius"]);
                BorderWidthBox.Text = NumberText(properties["borderWidth"]);
                StrokeWidthBox.Text = NumberText(songDisplay ? properties["lyricActiveFontSize"] : properties["strokeWidth"] ?? properties["radiusWidth"]);
                GapDegreeBox.Text = NumberText(songDisplay ? properties["lyricMaxLines"] : properties["gapDegree"]);
                TextStrokeWidthBox.Text = NumberText(properties["textStroke"]?["inputnumber"]);
                TextStrokeColorBox.Text = Text(properties["textStroke"]?["color"]);
                TextShadowOffsetXBox.Text = NumberText(properties["textShadow"]?["offsetX"]);
                TextShadowOffsetYBox.Text = NumberText(properties["textShadow"]?["offsetY"]);
                TextShadowBlurBox.Text = NumberText(properties["textShadow"]?["blurRadius"]);
                TextShadowColorBox.Text = Text(properties["textShadow"]?["color"]);
                PrecisionBox.Text = NumberText(properties["tofixCount"]);
                ClockFormatBox.Text = Text(properties["format"]);
                HideUnitCheck.IsChecked = IsTruthy(properties["hideUnit"]);
                IsLyricCheck.IsChecked = IsTruthy(properties["isLyric"]);
                ShowAreaCheck.IsChecked = ArrayContainsString(properties["xyLineConfig"], "showAreaStyle");
                ShowXYCheck.IsChecked = ArrayContainsString(properties["xyLineConfig"], "showXY");
                ShowSplitLinesCheck.IsChecked = ArrayContainsString(properties["xyLineConfig"], "splitLine");
                KeepRatioCheck.IsChecked = properties["keepRatio"] is null ? !(songDisplay || lyricLayer) : IsTruthy(properties["keepRatio"]);
                SelectOptionByTag(ProgressTypeBox, Text(properties["progressType"]).Length == 0 ? "strip" : Text(properties["progressType"]));
                SelectOptionByTag(BorderBgTypeBox, Text(properties["borderBgType"]).Length == 0 ? "normal" : Text(properties["borderBgType"]));
                SelectOptionByTag(BorderStyleBox, Text(properties["borderStyle"]).Length == 0 ? "solid" : Text(properties["borderStyle"]));
                SelectOptionByTag(BlockGradientBox, Text(properties["linearGradient"]));
                string align = Text(properties["align"]);
                AlignBox.SelectedIndex = align switch { "center" => 1, "right" => 2, "left" => 0, _ => -1 };
                SelectOptionByTag(SongPresetModeBox, songPreset);
                SongShowCoverCheck.IsChecked = properties["showCover"] is null
                    ? songPreset != "lyricOnly" : IsTruthy(properties["showCover"]);
                SongShowTitleCheck.IsChecked = properties["showTitle"] is null
                    ? songPreset != "coverOnly" : IsTruthy(properties["showTitle"]);
                SongShowArtistCheck.IsChecked = properties["showArtist"] is null
                    ? songPreset != "coverOnly" : IsTruthy(properties["showArtist"]);
                SongShowLyricsCheck.IsChecked = properties["showLyrics"] is null
                    ? songPreset != "coverOnly" : IsTruthy(properties["showLyrics"]);
                SongShowBackgroundCheck.IsChecked = properties["showBackground"] is null
                    ? songPreset != "lyricOnly" : IsTruthy(properties["showBackground"]);
                SongShowHintCheck.IsChecked = properties["showNoMediaHint"] is null || IsTruthy(properties["showNoMediaHint"]);
                SongCircularCoverCheck.IsChecked = properties["coverCircular"] is null || IsTruthy(properties["coverCircular"]);
                SongRotateCoverCheck.IsChecked = properties["coverRotate"] is null || IsTruthy(properties["coverRotate"]);
                SongLyricScrollCheck.IsChecked = properties["lyricScroll"] is null || IsTruthy(properties["lyricScroll"]);
                SongTitleColorBox.Text = Text(properties["titleColor"]).Length == 0 ? "255,255,255,0.8" : Text(properties["titleColor"]);
                SongBackgroundOpacityBox.Text = properties["backgroundOpacity"] is null ? "0.8" : NumberText(properties["backgroundOpacity"]);
                SongCoverRotateDurationBox.Text = properties["coverRotateDuration"] is null ? "12" : NumberText(properties["coverRotateDuration"]);
                SongGapBox.Text = properties["gap"] is null ? "16" : NumberText(properties["gap"]);
                string songCoverFill = Text(properties["coverFill"]);
                if (songCoverFill.Length == 0)
                    songCoverFill = songPreset == "hFixed" ? "height" : songPreset == "vFixed" ? "width" : "auto";
                SelectOptionByTag(SongCoverFillBox, songCoverFill);
                SongActiveFontSizeBox.Text = properties["lyricActiveFontSize"] is null ? "32" : NumberText(properties["lyricActiveFontSize"]);
                SongMaxLinesBox.Text = properties["lyricMaxLines"] is null ? "5" : NumberText(properties["lyricMaxLines"]);
                SongTitleFontSizeBox.Text = properties["titleFontSize"] is null ? "42" : NumberText(properties["titleFontSize"]);
                SongArtistFontSizeBox.Text = properties["artistFontSize"] is null ? "32" : NumberText(properties["artistFontSize"]);
                SongBackgroundRadiusBox.Text = properties["backgroundBorderRadius"] is null ? "0" : NumberText(properties["backgroundBorderRadius"]);
                SongLineSpacingBox.Text = properties["lyricLineSpacing"] is null ? "8" : NumberText(properties["lyricLineSpacing"]);
                AudioLineCountBox.Text = NumberText(properties["audioLineCount"]);
                AudioMarginBox.Text = NumberText(properties["margin"]);
                AudioReduceSpeedBox.Text = NumberText(properties["reduceSpeed"]);
                ArrangeAudioPeakCheck.IsChecked = IsTruthy(properties["arrangeArrayWithMaxInMiddle"]);
                string mediaSource = FirstString(properties["media"]) ?? string.Empty;
                bool onlineMedia = Text(properties["mediaLoadType"]).Equals("online", StringComparison.OrdinalIgnoreCase)
                    || IsHttpMediaUrl(mediaSource);
                SelectOptionByTag(MediaSourceModeBox, onlineMedia ? "online" : "local");
                MediaUrlBox.Text = IsHttpMediaUrl(mediaSource) ? mediaSource : string.Empty;
            }
        }
        finally { _loadingProperties = false; }
        UpdateSelectionOverlay();
    }

    private void LayerListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.CheckBox
            || !IsLayerDragHandle(e.OriginalSource as DependencyObject))
        {
            _layerDragSource = null;
            return;
        }
        ListBoxItem? container = ItemsControl.ContainerFromElement(LayerList, e.OriginalSource as DependencyObject) as ListBoxItem;
        _layerDragSource = container?.DataContext as LayerItem;
        _layerDragOrigin = e.GetPosition(LayerList);
    }

    private static bool IsLayerDragHandle(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (current is FrameworkElement element
                && string.Equals(element.Tag as string, "LayerDragHandle", StringComparison.Ordinal))
                return true;
            if (current is ListBoxItem) break;
            current = current switch
            {
                FrameworkContentElement content => content.Parent,
                ContentElement content => ContentOperations.GetParent(content),
                _ => System.Windows.Media.VisualTreeHelper.GetParent(current)
            };
        }
        return false;
    }

    private void LayerListMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_layerDragSource is not LayerItem source || e.LeftButton != MouseButtonState.Pressed) return;
        System.Windows.Point current = e.GetPosition(LayerList);
        if (Math.Abs(current.X - _layerDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _layerDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _layerDragSource = null;
        System.Windows.DragDrop.DoDragDrop(LayerList, source, System.Windows.DragDropEffects.Move);
    }

    private void LayerListDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(LayerItem)) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void LayerListDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LayerItem)) is not LayerItem source
            || ItemsControl.ContainerFromElement(LayerList, e.OriginalSource as DependencyObject) is not ListBoxItem targetContainer
            || targetContainer.DataContext is not LayerItem target
            || ReferenceEquals(source, target)
            || !ReferenceEquals(source.Parent, target.Parent))
            return;

        JsonArray siblings = source.Parent;
        bool sourceIsGroup = Text(source.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase);
        bool targetIsGroup = Text(target.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase);
        bool nestIntoTarget = targetIsGroup && (!sourceIsGroup || IsDropIntoGroup(targetContainer, e));
        string? oldParentId = EmptyToNull(Text(source.Node["parentId"]));
        string? newParentId = nestIntoTarget
            ? Text(target.Node["id"])
            : EmptyToNull(Text(target.Node["parentId"]));

        if (sourceIsGroup && newParentId is not null
            && IsGroupDescendant(siblings, Text(source.Node["id"]), newParentId))
        {
            StatusText.Text = "Não é possível mover um grupo para dentro dele mesmo ou de um descendente.";
            return;
        }

        PointF oldOffset = FindGroupOffset(siblings, oldParentId);
        PointF newOffset = FindGroupOffset(siblings, newParentId);
        source.Node["tx"] = GetNumber(source.Node["tx"]) + oldOffset.X - newOffset.X;
        source.Node["ty"] = GetNumber(source.Node["ty"]) + oldOffset.Y - newOffset.Y;
        if (newParentId is null) source.Node.Remove("parentId");
        else source.Node["parentId"] = newParentId;

        if (nestIntoTarget)
            MoveLayerIntoGroup(siblings, source.Node, target.Node);
        else
            MoveLayerNearTarget(siblings, source.Node, target.Node, IsDropBefore(targetContainer, e));

        UpdateSiblingOrder(siblings);
        ReloadLayers(source.Node);
        LayerList.SelectedItems.Clear();
        LayerList.SelectedItem = _layers.FirstOrDefault(item => ReferenceEquals(item.Node, source.Node));
        StatusText.Text = nestIntoTarget
            ? $"Camada movida para o grupo '{Text(target.Node["label"])}'."
            : "Ordem das camadas atualizada por arraste.";
        RebuildRenderer();
        e.Handled = true;
    }

    private static bool IsDropBefore(ListBoxItem container, System.Windows.DragEventArgs e)
        => e.GetPosition(container).Y < container.ActualHeight / 2;

    private static bool IsDropIntoGroup(ListBoxItem container, System.Windows.DragEventArgs e)
    {
        double y = e.GetPosition(container).Y;
        return y >= container.ActualHeight * .25 && y <= container.ActualHeight * .75;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static PointF FindGroupOffset(JsonArray siblings, string? groupId)
        => FindGroupOffset(siblings, groupId, new HashSet<string>(StringComparer.Ordinal));

    private static PointF FindGroupOffset(JsonArray siblings, string? groupId, HashSet<string> visited)
    {
        if (groupId is null || !visited.Add(groupId)) return PointF.Empty;
        JsonObject? group = siblings.OfType<JsonObject>().FirstOrDefault(node =>
            Text(node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)
            && Text(node["id"]).Equals(groupId, StringComparison.Ordinal));
        if (group is null) return PointF.Empty;
        JsonObject? properties = group["children"] as JsonObject;
        PointF parent = FindGroupOffset(siblings, EmptyToNull(Text(group["parentId"])), visited);
        return new PointF(
            parent.X + (float)(GetNumber(properties?["x"]) + GetNumber(group["tx"])),
            parent.Y + (float)(GetNumber(properties?["y"]) + GetNumber(group["ty"])));
    }

    private static bool IsGroupDescendant(JsonArray siblings, string ancestorId, string candidateId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = candidateId;
        while (current is not null && visited.Add(current))
        {
            if (current.Equals(ancestorId, StringComparison.Ordinal)) return true;
            JsonObject? group = siblings.OfType<JsonObject>().FirstOrDefault(node =>
                Text(node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)
                && Text(node["id"]).Equals(current, StringComparison.Ordinal));
            current = group is null ? null : EmptyToNull(Text(group["parentId"]));
        }
        return false;
    }
    private static void MoveLayerNearTarget(JsonArray siblings, JsonObject source, JsonObject target, bool before)
    {
        int sourceIndex = siblings.IndexOf(source);
        int targetIndex = siblings.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return;
        siblings.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex) targetIndex--;
        if (!before) targetIndex++;
        siblings.Insert(Math.Clamp(targetIndex, 0, siblings.Count), source);
    }

    private static void MoveLayerIntoGroup(JsonArray siblings, JsonObject source, JsonObject group)
    {
        string groupId = Text(group["id"]);
        int sourceIndex = siblings.IndexOf(source);
        if (sourceIndex < 0 || groupId.Length == 0) return;
        siblings.RemoveAt(sourceIndex);
        int insertionIndex = siblings.IndexOf(group) + 1;
        for (int i = 0; i < siblings.Count; i++)
            if (siblings[i] is JsonObject candidate
                && Text(candidate["parentId"]).Equals(groupId, StringComparison.Ordinal))
                insertionIndex = i + 1;
        siblings.Insert(Math.Clamp(insertionIndex, 0, siblings.Count), source);
    }

    private void ClearInspector()
    {
        _loadingProperties = true;
        try
        {
            ConfigureSongDisplayInspector(false);
            ContentLabel.Visibility = ContentBox.Visibility = TextAlignmentPanel.Visibility = Visibility.Visible;
            BoldCheck.Visibility = ItalicCheck.Visibility = UnderlineCheck.Visibility = Visibility.Visible;
            AudioWaveSettingsPanel.Visibility = Visibility.Collapsed;
            DataSourceLabel.Visibility = DataSourceBox.Visibility = Visibility.Collapsed;
            DataRangePanel.Visibility = Visibility.Collapsed;
            DataSourceBox.SelectedIndex = -1;
            LayerNameBox.Text = XBox.Text = YBox.Text = WidthBox.Text = HeightBox.Text = RotateBox.Text = string.Empty;
            ContentBox.Text = ColorBox.Text = SecondColorBox.Text = OutlineColorBox.Text = TrackColorBox.Text = FontFamilyBox.Text = FontSizeBox.Text = RangeMinBox.Text = RangeMaxBox.Text = string.Empty;
            BorderRadiusBox.Text = BorderWidthBox.Text = StrokeWidthBox.Text = GapDegreeBox.Text = PrecisionBox.Text = ClockFormatBox.Text = string.Empty;
            TextStrokeWidthBox.Text = TextStrokeColorBox.Text = TextShadowOffsetXBox.Text = TextShadowOffsetYBox.Text = TextShadowBlurBox.Text = TextShadowColorBox.Text = string.Empty;
            BoldCheck.IsChecked = ItalicCheck.IsChecked = false;
            UnderlineCheck.IsChecked = false;
            HideUnitCheck.IsChecked = IsLyricCheck.IsChecked = ShowAreaCheck.IsChecked = ShowXYCheck.IsChecked = ShowSplitLinesCheck.IsChecked = false;
            KeepRatioCheck.IsChecked = false;
            ProgressTypeBox.SelectedIndex = BorderBgTypeBox.SelectedIndex = BorderStyleBox.SelectedIndex = BlockGradientBox.SelectedIndex = -1;
            AlignBox.SelectedIndex = -1;
            LayerTypeText.Text = "Selecione uma camada";
            SongShowTitleCheck.IsChecked = SongShowArtistCheck.IsChecked = SongShowLyricsCheck.IsChecked = SongShowBackgroundCheck.IsChecked = SongShowHintCheck.IsChecked = false;
            SongShowCoverCheck.IsChecked = SongCircularCoverCheck.IsChecked = SongRotateCoverCheck.IsChecked = SongLyricScrollCheck.IsChecked = false;
            SongActiveFontSizeBox.Text = SongMaxLinesBox.Text = SongTitleFontSizeBox.Text = SongArtistFontSizeBox.Text = SongBackgroundRadiusBox.Text = SongLineSpacingBox.Text = string.Empty;
            SongTitleColorBox.Text = SongBackgroundOpacityBox.Text = SongCoverRotateDurationBox.Text = SongGapBox.Text = string.Empty;
            SongPresetModeBox.SelectedIndex = SongCoverFillBox.SelectedIndex = -1;
            AudioLineCountBox.Text = AudioMarginBox.Text = AudioReduceSpeedBox.Text = string.Empty;
            ArrangeAudioPeakCheck.IsChecked = false;
            LayerNameBox.IsEnabled = XBox.IsEnabled = YBox.IsEnabled = WidthBox.IsEnabled = HeightBox.IsEnabled = false;
            RotateBox.IsEnabled = ContentBox.IsEnabled = DataSourceBox.IsEnabled = ColorBox.IsEnabled = SecondColorBox.IsEnabled = TrackColorBox.IsEnabled
                = OutlineColorBox.IsEnabled = FontFamilyBox.IsEnabled = BoldCheck.IsEnabled = ItalicCheck.IsEnabled = FontSizeBox.IsEnabled = AlignBox.IsEnabled = RangeMinBox.IsEnabled = RangeMaxBox.IsEnabled = false;
            BorderRadiusBox.IsEnabled = BorderWidthBox.IsEnabled = StrokeWidthBox.IsEnabled = GapDegreeBox.IsEnabled = PrecisionBox.IsEnabled = ClockFormatBox.IsEnabled = HideUnitCheck.IsEnabled = IsLyricCheck.IsEnabled = ShowAreaCheck.IsEnabled = ShowXYCheck.IsEnabled = ShowSplitLinesCheck.IsEnabled = false;
            TextStrokeWidthBox.IsEnabled = TextStrokeColorBox.IsEnabled = TextShadowOffsetXBox.IsEnabled = TextShadowOffsetYBox.IsEnabled = TextShadowBlurBox.IsEnabled = TextShadowColorBox.IsEnabled = false;
            ProgressTypeBox.IsEnabled = BorderBgTypeBox.IsEnabled = BorderStyleBox.IsEnabled = BlockGradientBox.IsEnabled = KeepRatioCheck.IsEnabled = UnderlineCheck.IsEnabled = false;
            AudioLineCountBox.IsEnabled = AudioMarginBox.IsEnabled = AudioReduceSpeedBox.IsEnabled = ArrangeAudioPeakCheck.IsEnabled = false;
        }
        finally { _loadingProperties = false; }
        UpdateSelectionOverlay();
    }

    private void LayerPropertyChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingProperties) return;
        if (ReferenceEquals(sender, ThemeNameBox))
        {
            string name = ThemeNameBox.Text.Trim();
            if (name.Length > 0)
            {
                _definition["label"] = name;
                _definition["name"] = name;
            }
            return;
        }
        if (LayerList.SelectedItem is not LayerItem item) return;
        if (ReferenceEquals(sender, LayerNameBox))
        {
            item.Node["label"] = LayerNameBox.Text;
            item.Title = LayerNameBox.Text;
            LayerList.Items.Refresh();
        }
        else if (item.Node["children"] is JsonObject p)
        {
            string type = Text(item.Node["type"]).ToLowerInvariant();
            if (!item.IsLocked)
            {
                SetNumberIfValid(p, "x", XBox.Text);
                SetNumberIfValid(p, "y", YBox.Text);
                if (type != "group")
                {
                    SetNumberIfValid(p, "width", WidthBox.Text);
                    SetNumberIfValid(p, "height", HeightBox.Text);
                    SetNumberIfValid(p, "rotate", RotateBox.Text);
                }
            }
            if (type != "group")
            {
                if (type == "songdisplay")
                {
                    if (ReferenceEquals(sender, SongPresetModeBox))
                    {
                        string preset = SelectedTag(SongPresetModeBox, "");
                        if (preset.Length > 0) ApplySongPresetDefaults(p, preset);
                    }
                    p["noMediaHint"] = ContentBox.Text;
                    SetOptionalNumberIfValid(p, "lyricFontSize", FontSizeBox.Text);
                    SetOptionalNumberIfValid(p, "lyricActiveFontSize", SongActiveFontSizeBox.Text);
                    SetOptionalNumberIfValid(p, "lyricMaxLines", SongMaxLinesBox.Text);
                    SetOptionalNumberIfValid(p, "titleFontSize", SongTitleFontSizeBox.Text);
                    SetOptionalNumberIfValid(p, "artistFontSize", SongArtistFontSizeBox.Text);
                    SetOptionalNumberIfValid(p, "backgroundBorderRadius", SongBackgroundRadiusBox.Text);
                    SetOptionalNumberIfValid(p, "lyricLineSpacing", SongLineSpacingBox.Text);
                    SetOptionalNumberIfValid(p, "backgroundOpacity", SongBackgroundOpacityBox.Text);
                    SetOptionalNumberIfValid(p, "coverRotateDuration", SongCoverRotateDurationBox.Text);
                    SetOptionalNumberIfValid(p, "gap", SongGapBox.Text);
                    SetOptionalString(p, "lyricFontFamily", FontFamilyBox.Text);
                    if (ColorBox.Text.Trim().Length > 0) p["lyricColor"] = ColorBox.Text.Trim();
                    if (SecondColorBox.Text.Trim().Length > 0) p["lyricActiveColor"] = SecondColorBox.Text.Trim();
                    if (OutlineColorBox.Text.Trim().Length > 0) p["artistColor"] = OutlineColorBox.Text.Trim();
                    if (TrackColorBox.Text.Trim().Length > 0) p["backgroundColor"] = TrackColorBox.Text.Trim();
                    if (SongTitleColorBox.Text.Trim().Length > 0) p["titleColor"] = SongTitleColorBox.Text.Trim();
                    p["presetMode"] = SelectedTag(SongPresetModeBox, "coverOnly");
                    p["coverFill"] = SelectedTag(SongCoverFillBox, "auto");
                    p["coverCircular"] = SongCircularCoverCheck.IsChecked == true;
                    p["coverRotate"] = SongRotateCoverCheck.IsChecked == true;
                    p["lyricScroll"] = SongLyricScrollCheck.IsChecked == true;
                    p["showCover"] = SongShowCoverCheck.IsChecked == true;
                    p["showTitle"] = SongShowTitleCheck.IsChecked == true;
                    p["showArtist"] = SongShowArtistCheck.IsChecked == true;
                    p["showLyrics"] = SongShowLyricsCheck.IsChecked == true;
                    p["showBackground"] = SongShowBackgroundCheck.IsChecked == true;
                    p["showNoMediaHint"] = SongShowHintCheck.IsChecked == true;
                    RebuildRenderer();
                    UpdateSelectionOverlay();
                    return;
                }
                if (type is "audiowave" or "audio")
                {
                    SetBoundedNumberIfValid(p, "audioLineCount", AudioLineCountBox.Text, 20, 256, integer: true);
                    SetBoundedNumberIfValid(p, "margin", AudioMarginBox.Text, 0, 5, step: .5);
                    SetBoundedNumberIfValid(p, "reduceSpeed", AudioReduceSpeedBox.Text, 3, 18, integer: true);
                    p["arrangeArrayWithMaxInMiddle"] = ArrangeAudioPeakCheck.IsChecked == true;
                }
                if (type is "media" or "image")
                {
                    bool onlineMedia = SelectedTag(MediaSourceModeBox, "local") == "online";
                    p["mediaLoadType"] = onlineMedia ? "online" : "local";
                    if (onlineMedia)
                    {
                        string url = MediaUrlBox.Text.Trim();
                        if (url.Length == 0)
                            p.Remove("media");
                        else if (IsHttpMediaUrl(url))
                            p["media"] = new JsonArray(url);
                        else
                            StatusText.Text = "Informe uma URL HTTP ou HTTPS válida para a mídia online.";
                    }
                    else if (FirstString(p["media"]) is string currentMedia && IsHttpMediaUrl(currentMedia))
                    {
                        p.Remove("media");
                    }
                }
                p["keepRatio"] = KeepRatioCheck.IsChecked == true;
                if (type is "data" or "text")
                    p["text"] = ContentBox.Text;
                SetNumberIfValid(p, "fontSize", FontSizeBox.Text);
                SetArrayNumberIfValid(p, "dataRange", 0, RangeMinBox.Text);
                SetArrayNumberIfValid(p, "dataRange", 1, RangeMaxBox.Text);
                if (type is "block" or "progress" or "linechart")
                {
                    SetOptionalNumberIfValid(p, "borderRadius", BorderRadiusBox.Text);
                    SetOptionalNumberIfValid(p, "borderWidth", BorderWidthBox.Text);
                }
                if (type is "media" or "image")
                {
                    SetOptionalNumberIfValid(p, "borderRadius", BorderRadiusBox.Text);
                    SetOptionalNumberIfValid(p, "borderWidth", BorderWidthBox.Text);
                }
                if (type is "block" or "progress" or "linechart" or "media" or "image")
                    SetOptionalString(p, "borderStyle", SelectedTag(BorderStyleBox, "solid"));
                if (type is "block" or "audiowave" or "audio")
                    SetOptionalString(p, "linearGradient", SelectedTag(BlockGradientBox, ""));
                if (type == "arc")
                {
                    if (string.IsNullOrWhiteSpace(StrokeWidthBox.Text))
                    {
                        p.Remove("strokeWidth");
                        p.Remove("radiusWidth");
                    }
                    else if (TryNumber(StrokeWidthBox.Text, out double strokeWidth))
                    {
                        string strokeProperty = p.ContainsKey("radiusWidth") && !p.ContainsKey("strokeWidth")
                            ? "radiusWidth"
                            : "strokeWidth";
                        p[strokeProperty] = strokeWidth;
                        if (strokeProperty == "strokeWidth") p.Remove("radiusWidth");
                    }
                    SetOptionalNumberIfValid(p, "gapDegree", GapDegreeBox.Text);
                }
                if (type == "progress")
                {
                    p["progressType"] = SelectedTag(ProgressTypeBox, "strip");
                    if (string.IsNullOrWhiteSpace(StrokeWidthBox.Text))
                    {
                        p.Remove("strokeWidth");
                        p.Remove("radiusWidth");
                    }
                    else if (TryNumber(StrokeWidthBox.Text, out double strokeWidth))
                    {
                        string strokeProperty = p.ContainsKey("radiusWidth") && !p.ContainsKey("strokeWidth") ? "radiusWidth" : "strokeWidth";
                        p[strokeProperty] = strokeWidth;
                        if (strokeProperty == "strokeWidth") p.Remove("radiusWidth");
                    }
                    SetOptionalNumberIfValid(p, "gapDegree", GapDegreeBox.Text);
                }
                if (type is "arc" or "progress")
                    p["borderBgType"] = SelectedTag(BorderBgTypeBox, "normal");
                if (type == "linechart")
                {
                    SetArrayStringOption(p, "xyLineConfig", "showAreaStyle", ShowAreaCheck.IsChecked == true);
                    SetArrayStringOption(p, "xyLineConfig", "showXY", ShowXYCheck.IsChecked == true);
                    SetArrayStringOption(p, "xyLineConfig", "splitLine", ShowSplitLinesCheck.IsChecked == true);
                }
                if (type is "data" or "text")
                {
                    SetOptionalNumberIfValid(p, "tofixCount", PrecisionBox.Text);
                    SetOptionalString(p, "format", ClockFormatBox.Text);
                    p["hideUnit"] = HideUnitCheck.IsChecked == true;
                    if (IsLyricCheck.IsChecked == true) p["isLyric"] = true;
                    else p.Remove("isLyric");
                    UpdateTextStroke(p);
                    UpdateTextShadow(p);
                }
                else if (type == "lyric")
                {
                    p["dataSource"] = "lyric_current";
                    p["isLyric"] = true;
                    UpdateTextStroke(p);
                    UpdateTextShadow(p);
                }
                if (FontFamilyBox.Text.Trim().Length > 0) p["fontFamily"] = FontFamilyBox.Text.Trim();
                if (ColorBox.Text.Trim().Length > 0)
                {
                    string colorProperty = type switch
                    {
                        "progress" or "arc" => "borderBgColor",
                        "linechart" => "areaStyle",
                        "block" or "audiowave" or "audio" => "backgroundColor",
                        _ => "color"
                    };
                    if (type is "progress" or "arc" or "block" or "audiowave" or "audio")
                        SetColorPair(p, colorProperty, ColorBox.Text.Trim(), SecondColorBox.Text.Trim());
                    else
                        p[colorProperty] = ColorBox.Text.Trim();
                }
                if (OutlineColorBox.Text.Trim().Length > 0)
                    p["borderColor"] = OutlineColorBox.Text.Trim();
                if (TrackColorBox.Text.Trim().Length > 0)
                {
                    if (type is "progress" or "arc") p["trailColor"] = TrackColorBox.Text.Trim();
                    else if (type == "linechart") p["backgroundColor"] = TrackColorBox.Text.Trim();
                }
                string dataSource = SelectedDataSource();
                if (dataSource.Length > 0)
                    p["dataSource"] = dataSource;
            }
        }
        RebuildRenderer();
        UpdateSelectionOverlay();
    }

    private void AdvancedPropertyChanged(object sender, RoutedEventArgs e)
        => LayerPropertyChanged(sender, null!);

    private void ApplySongPresetDefaults(JsonObject properties, string preset)
    {
        bool previousLoading = _loadingProperties;
        _loadingProperties = true;
        try
        {
            string layout = preset switch
            {
                "hFixed" or "hCenter" => "horizontal",
                "vFixed" or "vCenter" => "vertical",
                "coverOnly" => "coverOnly",
                "lyricOnly" => "lyricOnly",
                _ => "vFixed"
            };
            properties["presetMode"] = preset;
            properties["layout"] = layout;
            properties["coverPosition"] = preset switch
            {
                "vFixed" or "vCenter" => "top",
                "hCenter" or "hFixed" => "left",
                _ => "center"
            };
            properties["coverAnchor"] = preset switch
            {
                "hFixed" or "vFixed" => "start",
                _ => "center"
            };
            properties["contentAlign"] = "center";
            properties["coverFill"] = preset switch
            {
                "hFixed" => "height",
                "vFixed" => "width",
                _ => "auto"
            };

            bool coverOnly = preset == "coverOnly";
            bool lyricOnly = preset == "lyricOnly";
            SongShowCoverCheck.IsChecked = !lyricOnly;
            SongShowTitleCheck.IsChecked = !coverOnly;
            SongShowArtistCheck.IsChecked = !coverOnly;
            SongShowLyricsCheck.IsChecked = !coverOnly;
            if (lyricOnly)
            {
                SongShowBackgroundCheck.IsChecked = false;
                properties["backgroundColor"] = "0,0,0,0";
                TrackColorBox.Text = "0,0,0,0";
            }
            SelectOptionByTag(SongCoverFillBox, preset switch
            {
                "hFixed" => "height",
                "vFixed" => "width",
                _ => "auto"
            });
        }
        finally { _loadingProperties = previousLoading; }
    }

    private void SongDisplaySettingsChanged(object sender, RoutedEventArgs e)
        => LayerPropertyChanged(sender, null!);

    private void ConfigureSongDisplayInspector(bool enabled)
    {
        SongDisplaySettingsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        GeneralLayerStylePanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DataSourceLabel.Visibility = DataSourceBox.Visibility = DataRangePanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        BoldCheck.Visibility = ItalicCheck.Visibility = UnderlineCheck.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        TextAlignmentPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        FontSizeLabel.Text = enabled ? "Tamanho da letra (8-999 px)" : "Fonte (px)";
        ContentLabel.Text = enabled ? "Mensagem sem mídia" : "Texto";
        DataSourceLabel.Text = "Fonte de dados";
        ColorLabel.Text = enabled ? "Cor das letras" : "Cor (ex.: #FFFFFF)";
        SecondColorLabel.Text = enabled ? "Cor da linha ativa" : "Segunda cor do gradiente";
        OutlineColorLabel.Text = enabled ? "Cor do artista" : "Cor do contorno";
        TrackColorLabel.Text = enabled ? "Cor de fundo do cartão" : "Cor do trilho / fundo do gráfico";
    }

    private void SongDisplayNumberLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loadingProperties || SongDisplaySettingsPanel.Visibility != Visibility.Visible
            || sender is not System.Windows.Controls.TextBox textBox || !TryNumber(textBox.Text, out double value))
            return;

        (double Minimum, double Maximum, bool Integer) range = textBox.Name switch
        {
            nameof(FontSizeBox) => (8, 999, false),
            nameof(SongActiveFontSizeBox) => (8, 999, false),
            nameof(SongTitleFontSizeBox) => (8, 999, false),
            nameof(SongArtistFontSizeBox) => (8, 999, false),
            nameof(SongMaxLinesBox) => (1, 99, true),
            nameof(SongBackgroundRadiusBox) => (0, 240, false),
            nameof(SongBackgroundOpacityBox) => (0, 1, false),
            _ => (double.NaN, double.NaN, false)
        };
        if (!double.IsFinite(range.Minimum)) return;

        double normalized = Math.Clamp(value, range.Minimum, range.Maximum);
        if (range.Integer) normalized = Math.Round(normalized, MidpointRounding.AwayFromZero);
        textBox.Text = normalized.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void LayerRotateLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loadingProperties || !TryNumber(RotateBox.Text, out double value)) return;
        double normalized = Math.Clamp(value, 0, 360);
        RotateBox.Text = normalized.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void LayerNumericLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loadingProperties || sender is not System.Windows.Controls.TextBox textBox
            || !TryNumber(textBox.Text, out double value))
            return;
        (double Minimum, double Maximum) range = textBox.Name switch
        {
            nameof(PrecisionBox) => (0, 3),
            nameof(GapDegreeBox) => (0, 295),
            _ => (double.NaN, double.NaN)
        };
        if (!double.IsFinite(range.Minimum)) return;
        double normalized = Math.Round(Math.Clamp(value, range.Minimum, range.Maximum), MidpointRounding.AwayFromZero);
        textBox.Text = normalized.ToString("0", CultureInfo.CurrentCulture);
    }

    private void ConfigureDataSourceOptions(string layerType, string selectedSource)
    {
        bool visible = HasDataSourceEditor(layerType);
        DataSourceLabel.Visibility = DataSourceBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DataSourceBox.Items.Clear();
        if (!visible) return;

        foreach ((string label, string[] sources) in DataSourceGroups)
        {
            string[] allowed = sources.Where(source => IsDataSourceAllowed(layerType, source)).ToArray();
            if (allowed.Length == 0) continue;
            DataSourceBox.Items.Add(new ComboBoxItem
            {
                Content = label,
                IsEnabled = false,
                IsHitTestVisible = false,
                FontWeight = System.Windows.FontWeights.SemiBold
            });
            foreach (string source in allowed)
                DataSourceBox.Items.Add(new ComboBoxItem
                {
                    Content = DataSourceLabels.TryGetValue(source, out string? sourceLabel) ? sourceLabel : source,
                    Tag = source
                });
        }

        if (!string.IsNullOrWhiteSpace(selectedSource)
            && !DataSourceBox.Items.OfType<ComboBoxItem>().Any(option => string.Equals(option.Tag?.ToString(), selectedSource, StringComparison.OrdinalIgnoreCase)))
        {
            DataSourceBox.Items.Add(new ComboBoxItem
            {
                Content = "Outra fonte de dados (mantida do tema)",
                IsEnabled = false,
                IsHitTestVisible = false,
                FontWeight = System.Windows.FontWeights.SemiBold
            });
            DataSourceBox.Items.Add(new ComboBoxItem
            {
                Content = $"Fonte personalizada: {selectedSource}",
                Tag = selectedSource
            });
        }
        DataSourceBox.SelectedItem = string.IsNullOrWhiteSpace(selectedSource)
            ? null
            : DataSourceBox.Items.OfType<ComboBoxItem>().FirstOrDefault(option =>
                string.Equals(option.Tag?.ToString(), selectedSource, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedDataSource()
        => (DataSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? (DataSourceBox.SelectedItem as string)
            ?? string.Empty;

    private static bool HasDataSourceEditor(string layerType)
        => layerType is "data" or "progress" or "arc" or "linechart";

    private static bool IsDataSourceAllowed(string layerType, string source)
    {
        if (source.Equals("time_current", StringComparison.OrdinalIgnoreCase)
            || source.Equals("time_current_12h", StringComparison.OrdinalIgnoreCase))
            return layerType == "data";

        if (layerType == "data")
            return source is not "diskLayout_up" and not "diskLayout_down";

        if (layerType is "progress" or "arc")
        {
            string[] unsupported = [
                "cpuLayout_vid", "cpuLayout_fanSpeed", "graphics_vid", "graphics_fanSpeed",
                "memLayout_vid", "diskLayout_up", "diskLayout_down", "diskLayout_vid"
            ];
            return !unsupported.Contains(source, StringComparer.OrdinalIgnoreCase);
        }

        if (layerType == "linechart")
        {
            string[] unsupported = [
                "cpuLayout_vid", "graphics_vid", "graphics_fanSpeed", "memLayout_vid",
                "memLayout_free", "memLayout_used", "memLayout_total", "diskLayout_vid",
                "diskLayout_up", "diskLayout_down", "diskLayout_total", "diskLayout_free",
                "diskLayout_used", "diskLayout_currentRefreshRate"
            ];
            return !unsupported.Contains(source, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private void AdvancedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProperties) LayerPropertyChanged(sender, null!);
    }

    private void PickColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: System.Windows.Controls.TextBox colorBox }
            || !colorBox.IsEnabled)
            return;
        var picker = new ThemeColorPickerWindow(colorBox.Text.Trim());
        picker.Owner = Window.GetWindow(this);
        if (picker.ShowDialog() == true)
            colorBox.Text = picker.SelectedColorText;
    }

    private void MediaSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProperties) return;
        bool isMediaLayer = LayerList.SelectedItem is LayerItem item
            && (Text(item.Node["type"]).Equals("media", StringComparison.OrdinalIgnoreCase)
                || Text(item.Node["type"]).Equals("image", StringComparison.OrdinalIgnoreCase));
        MediaUrlBox.IsEnabled = isMediaLayer && SelectedTag(MediaSourceModeBox, "local") == "online";
        LayerPropertyChanged(sender, null!);
    }

    private static bool IsHttpMediaUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo);

    private void ClockFormatBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo
            || combo.Template.FindName("PART_EditableTextBox", combo) is not System.Windows.Controls.TextBox textBox)
            return;
        textBox.TextChanged -= LayerPropertyChanged;
        textBox.TextChanged += LayerPropertyChanged;
    }

    private void ClockFormatLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => LayerPropertyChanged(sender, null!);

    private void DataSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateClockFormatAvailability();
        if (!_loadingProperties && SelectedDataSource().Length > 0)
            LayerPropertyChanged(DataSourceBox, null!);
    }

    private void DataSourceLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateClockFormatAvailability();
        if (!_loadingProperties && SelectedDataSource().Length > 0)
            LayerPropertyChanged(DataSourceBox, null!);
    }

    private void UpdateClockFormatAvailability()
    {
        if (LayerList.SelectedItem is LayerItem item)
        {
            string type = Text(item.Node["type"]).ToLowerInvariant();
            ClockFormatBox.IsEnabled = type is "data" or "text"
                && IsClockDataSource(SelectedDataSource());
        }
    }

    private void LayerVisibilityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: LayerItem item }) return;
        if (_steamLayoutMode && item.IsRequired) return;
        item.IsVisible = !item.IsVisible;
        StatusText.Text = item.IsVisible
            ? $"Camada \u201c{item.Title}\u201d visível na prévia e no próximo .ht salvo."
            : $"Camada \u201c{item.Title}\u201d oculta na prévia e no próximo .ht salvo.";
        RebuildRenderer();
        e.Handled = true;
    }

    private void LayerLockClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: LayerItem item }) return;
        if (item.IsLockedByAncestor)
        {
            StatusText.Text = $"Desbloqueie o grupo que contém “{item.Title}” para mover esta camada.";
            e.Handled = true;
            return;
        }
        item.IsLocked = !item.IsLocked;
        StatusText.Text = item.IsLocked
            ? $"Camada \u201c{item.Title}\u201d bloqueada para mover e redimensionar."
            : $"Camada \u201c{item.Title}\u201d desbloqueada.";

        if (LayerList.SelectedItem is LayerItem selected)
        {
            bool editable = selected.Node["children"] is JsonObject;
            bool canTransform = editable && !selected.IsLocked;
            bool group = Text(selected.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase);
            XBox.IsEnabled = YBox.IsEnabled = canTransform;
            WidthBox.IsEnabled = HeightBox.IsEnabled = RotateBox.IsEnabled = canTransform && !group;
        }
        UpdateSelectionOverlay();
        e.Handled = true;
    }

    private static bool IsClockDataSource(string source)
        => source.Equals("time_current", StringComparison.OrdinalIgnoreCase)
            || source.Equals("time_current_12h", StringComparison.OrdinalIgnoreCase);

    private void LoadBackgroundControls()
    {
        _loadingProperties = true;
        try
        {
            JsonNode? colors = _definition["backgroundColor"];
            BackgroundColorBox.Text = FirstColor(colors);
            BackgroundSecondaryColorBox.Text = SecondColor(colors);
            string gradient = Text(_definition["backgroundGradient"]);
            BackgroundGradientBox.SelectedIndex = gradient.Equals("circle", StringComparison.OrdinalIgnoreCase) ? 1
                : colors is JsonArray ? 0 : 2;
            string fit = Text((_definition["bgConfig"] as JsonObject)?["fit"]);
            BackgroundFitBox.SelectedIndex = fit.ToLowerInvariant() switch { "contain" => 1, "stretch" or "fill" => 2, _ => 0 };
            JsonObject? config = _definition["bgConfig"] as JsonObject;
            BackgroundXBox.Text = ReadBackgroundNumber(config, "x", "0");
            BackgroundYBox.Text = ReadBackgroundNumber(config, "y", "0");
            BackgroundWidthBox.Text = ReadBackgroundNumber(config, "width", "100");
            BackgroundHeightBox.Text = ReadBackgroundNumber(config, "height", "100");
            string? mediaPath = FirstString(_definition["bgMedia"])
                ?? Text((_definition["bgConfig"] as JsonObject)?["src"]);
            ChooseBackgroundButton.Content = string.IsNullOrWhiteSpace(mediaPath)
                ? "Escolher mídia de fundo…"
                : "Substituir: " + Path.GetFileName(mediaPath);
            ClearBackgroundButton.IsEnabled = !string.IsNullOrWhiteSpace(mediaPath);
            BackgroundXBox.IsEnabled = BackgroundYBox.IsEnabled = BackgroundWidthBox.IsEnabled = BackgroundHeightBox.IsEnabled
                = !string.IsNullOrWhiteSpace(mediaPath);
        }
        finally { _loadingProperties = false; }
    }

    private void BackgroundColorChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loadingProperties) return;
        string first = BackgroundColorBox.Text.Trim();
        string second = BackgroundSecondaryColorBox.Text.Trim();
        string gradient = (BackgroundGradientBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "solid";
        if (first.Length == 0)
            _definition.Remove("backgroundColor");
        else if (!gradient.Equals("solid", StringComparison.OrdinalIgnoreCase) && second.Length > 0)
            _definition["backgroundColor"] = new JsonArray(first, second);
        else
            _definition["backgroundColor"] = first;
        RebuildRenderer();
    }

    private void BackgroundFitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProperties) return;
        string fit = (BackgroundFitBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cover";
        EnsureBackgroundConfig()["fit"] = fit;
        RebuildRenderer();
    }

    private void BackgroundLayoutChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loadingProperties) return;
        JsonObject config = EnsureBackgroundConfig();
        config["unit"] = "%";
        SetNumberIfValid(config, "x", BackgroundXBox.Text);
        SetNumberIfValid(config, "y", BackgroundYBox.Text);
        SetNumberIfValid(config, "width", BackgroundWidthBox.Text);
        SetNumberIfValid(config, "height", BackgroundHeightBox.Text);
        config["fit"] = (BackgroundFitBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cover";
        RebuildRenderer();
    }

    private void BackgroundGradientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProperties) return;
        string gradient = (BackgroundGradientBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "solid";
        if (gradient.Equals("solid", StringComparison.OrdinalIgnoreCase))
            _definition.Remove("backgroundGradient");
        else
            _definition["backgroundGradient"] = gradient;
        BackgroundColorChanged(BackgroundColorBox, null!);
    }

    private async void ChooseBackgroundClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolher mídia de fundo",
            Filter = "Mídia compatível|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|Todos os arquivos|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        StudioFileDialogDefaults.Configure(dialog);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            bool video = extension is ".mp4" or ".m4v" or ".wmv" or ".avi" or ".mov" or ".mkv" or ".webm";
            if (!video && extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
                throw new InvalidDataException("Escolha uma imagem ou vídeo compatível.");
            VideoImportResult? preparedVideo = null;
            byte[] bytes;
            if (video)
            {
                if (new FileInfo(dialog.FileName).Length > 90 * 1024 * 1024)
                {
                    StatusText.Text = "A mídia excede 90 MB; reduza o arquivo para manter o pacote .ht em um tamanho compatível.";
                    return;
                }
                StatusText.Text = "Preparando vídeo para o formato de temas .ht…";
                preparedVideo = await PrepareVideoImportAsync(dialog.FileName);
                bytes = preparedVideo.Bytes;
                extension = preparedVideo.Extension;
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(dialog.FileName);
            }
            if (bytes.Length > 90 * 1024 * 1024)
            {
                StatusText.Text = "A mídia excede 90 MB; reduza o arquivo para manter o pacote .ht em um tamanho compatível.";
                return;
            }
            string assetPath = (video ? "media/video/" : "media/image/") + Guid.NewGuid().ToString("N") + extension;
            _addedAssets[assetPath] = bytes;
            _definition["bgMedia"] = new JsonArray(assetPath);
            JsonObject config = EnsureBackgroundConfig();
            config["src"] = assetPath;
            config["unit"] = "%";
            config["x"] = 0;
            config["y"] = 0;
            config["width"] = 100;
            config["height"] = 100;
            config["fit"] = (BackgroundFitBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cover";
            LoadBackgroundControls();
            StatusText.Text = (preparedVideo?.Status ?? "Mídia de fundo adicionada.") + " O arquivo será embutido no pacote .ht ao salvar.";
            RebuildRenderer(refreshAssets: true);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            StatusText.Text = "Não foi possível usar a mídia de fundo: " + ex.Message;
        }
    }

    private void ClearBackgroundClicked(object sender, RoutedEventArgs e)
    {
        _definition.Remove("bgMedia");
        if (_definition["bgConfig"] is JsonObject config)
            config.Remove("src");
        LoadBackgroundControls();
        StatusText.Text = "Mídia de fundo removida. A cor e o gradiente continuam aplicados.";
        RebuildRenderer();
    }

    private static async Task<VideoImportResult> PrepareVideoImportAsync(string sourcePath)
    {
        try
        {
            return await HtVideoTranscoder.PrepareForThemeAsync(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            byte[] source = await File.ReadAllBytesAsync(sourcePath);
            return new VideoImportResult(source, Path.GetExtension(sourcePath), false,
                "A conversão FFmpeg falhou; o arquivo original foi mantido (" + ex.Message + ").");
        }
    }

    private JsonObject EnsureBackgroundConfig()
    {
        if (_definition["bgConfig"] is JsonObject existing) return existing;
        var config = new JsonObject { ["unit"] = "%", ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
        _definition["bgConfig"] = config;
        return config;
    }

    private static string? FirstString(JsonNode? value)
    {
        if (value is JsonArray array)
            return array.OfType<JsonValue>().Select(item =>
            {
                try { return item.GetValue<string>(); }
                catch (InvalidOperationException) { return null; }
            }).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return value is JsonValue jsonValue ? Text(jsonValue) : null;
    }

    private static string ReadBackgroundNumber(JsonObject? config, string key, string fallback)
    {
        string value = Text(config?[key]);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private void LayerFormattingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingProperties || LayerList.SelectedItem is not LayerItem item || item.Node["children"] is not JsonObject p)
            return;
        SetFontStyleFlag(p, "bold", BoldCheck.IsChecked == true);
        SetFontStyleFlag(p, "italic", ItalicCheck.IsChecked == true);
        SetFontStyleFlag(p, "underline", UnderlineCheck.IsChecked == true);
        string fontWeight = Text(p["fontWeight"]);
        if (BoldCheck.IsChecked == true)
            p["fontWeight"] = "bold";
        else if (fontWeight.Contains("bold", StringComparison.OrdinalIgnoreCase)
            || TryNumber(fontWeight, out double numericWeight) && numericWeight >= 600)
            p["fontWeight"] = "normal";
        if (Text(p["textDecoration"]).Contains("underline", StringComparison.OrdinalIgnoreCase))
        {
            if (UnderlineCheck.IsChecked != true) p.Remove("textDecoration");
        }
        RebuildRenderer();
    }

    private static void SetColorPair(JsonObject properties, string property, string first, string second)
    {
        var colors = new JsonArray();
        if (properties[property] is JsonArray existing)
        {
            foreach (JsonNode? value in existing)
                colors.Add(value?.DeepClone());
        }
        else
        {
            string current = Text(properties[property]);
            colors.Add(current.Length > 0 ? current : first);
            colors.Add(current.Length > 0 ? current : second.Length > 0 ? second : first);
        }
        while (colors.Count < 2) colors.Add(first);
        colors[0] = first;
        if (second.Length > 0) colors[1] = second;
        properties[property] = colors;
    }

    private static void SetFontStyleFlag(JsonObject properties, string style, bool enabled)
    {
        var styles = new JsonArray();
        JsonNode? current = properties["fontStyle"];
        IEnumerable<string> existing = current is JsonArray array
            ? array.Select(Text)
            : Text(current).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (string value in existing)
            if (!value.Equals(style, StringComparison.OrdinalIgnoreCase))
                styles.Add(value);
        if (enabled) styles.Add(style);
        if (styles.Count == 0) properties.Remove("fontStyle");
        else properties["fontStyle"] = styles;
    }

    private static bool HasFontStyle(JsonNode? value, string style)
    {
        IEnumerable<string> styles = value is JsonArray array
            ? array.Select(Text)
            : Text(value).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        return styles.Any(item => item.Equals(style, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstColor(JsonNode? value)
        => value is JsonArray array && array.Count > 0 ? Text(array[0]) : Text(value);

    private static string SecondColor(JsonNode? value)
        => value is JsonArray array && array.Count > 1 ? Text(array[1]) : string.Empty;

    private void AlignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProperties || LayerList.SelectedItem is not LayerItem item || item.Node["children"] is not JsonObject p)
            return;
        if (AlignBox.SelectedItem is ComboBoxItem align && align.Tag is string value)
            p["align"] = value;
        RebuildRenderer();
    }

    private void EditorOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        System.Windows.Point point = e.GetPosition(EditorOverlay);
        if (LayerList.SelectedItem is LayerItem selected && !selected.IsLocked
            && selected.Node["children"] is JsonObject rotationProperties
            && IsRotationHandleHit(point, selected))
        {
            LayerGeometry geometry = GetLayerGeometry(selected);
            _dragItem = selected;
            _rotationCenter = new System.Windows.Point(geometry.PivotX, geometry.PivotY);
            _rotationStartPointerAngle = Math.Atan2(point.Y - _rotationCenter.Y, point.X - _rotationCenter.X);
            _rotationStartDegrees = GetNumber(rotationProperties["rotate"]);
            _rotating = true;
            _resizing = false;
            _dragging = true;
            EditorOverlay.CaptureMouse();
            e.Handled = true;
            return;
        }
        LayerItem? hit = null;
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            LayerItem candidate = _layers[i];
            if (!IsLayerVisibleOnCanvas(candidate)) continue;
            if (candidate.Node["children"] is not JsonObject p) continue;
            var bounds = GetLayerBounds(candidate);
            if (bounds.Width > 0 && bounds.Height > 0 && ContainsLayerPoint(point, candidate, bounds))
            {
                hit = candidate;
                break;
            }
        }
        LayerList.SelectedItem = hit;
        if (hit is null) return;
        if (hit.IsLocked)
        {
            e.Handled = true;
            return;
        }
        if (hit.Node["children"] is not JsonObject selectedProperties) return;
        _dragItem = hit;
        _dragOrigin = point;
        _dragStartX = GetNumber(selectedProperties["x"], GetNumber(hit.Node["x"]));
        _dragStartY = GetNumber(selectedProperties["y"], GetNumber(hit.Node["y"]));
        var selectedBounds = GetLayerBounds(hit);
        _dragStartBounds = selectedBounds;
        _dragStartWidth = GetNumber(selectedProperties["width"], GetNumber(hit.Node["width"]));
        _dragStartHeight = GetNumber(selectedProperties["height"], GetNumber(hit.Node["height"]));
        if (_dragStartWidth <= 0) _dragStartWidth = selectedBounds.Width / LayerScale(hit.Node, 0);
        if (_dragStartHeight <= 0) _dragStartHeight = selectedBounds.Height / LayerScale(hit.Node, 1);
        _dragStartFontSizes.Clear();
        string selectedType = Text(hit.Node["type"]).ToLowerInvariant();
        if (selectedType is "text" or "data" or "lyric")
            _dragStartFontSizes["fontSize"] = Math.Clamp(GetNumber(selectedProperties["fontSize"], 12), 5, 180);
        else if (selectedType == "songdisplay")
        {
            _dragStartFontSizes["titleFontSize"] = Math.Clamp(GetNumber(selectedProperties["titleFontSize"], 42), 8, 999);
            _dragStartFontSizes["artistFontSize"] = Math.Clamp(GetNumber(selectedProperties["artistFontSize"], 32), 8, 999);
            _dragStartFontSizes["lyricFontSize"] = Math.Clamp(GetNumber(selectedProperties["lyricFontSize"], 24), 8, 999);
            _dragStartFontSizes["lyricActiveFontSize"] = Math.Clamp(GetNumber(selectedProperties["lyricActiveFontSize"], 32), 8, 999);
            _dragStartFontSizes["noMediaHintFontSize"] = Math.Clamp(GetNumber(selectedProperties["noMediaHintFontSize"], 14), 8, 999);
        }
        System.Windows.Point localPoint = ToLayerLocalPoint(point, hit, selectedBounds);
        _resizeEdges = GetResizeEdges(localPoint, selectedBounds.Width, selectedBounds.Height);
        _resizing = _resizeEdges != ResizeEdges.None;
        _dragging = true;
        EditorOverlay.CaptureMouse();
        e.Handled = true;
    }

    private bool IsLayerVisibleOnCanvas(LayerItem item)
    {
        JsonArray? root = _definition["children"] as JsonArray;
        bool Find(JsonArray? nodes, JsonObject target, bool parentVisible)
        {
            if (nodes is null) return false;
            foreach (JsonObject node in nodes.OfType<JsonObject>())
            {
                bool visible = parentVisible && (node["isShow"] is null || ReadBool(node["isShow"]));
                if (ReferenceEquals(node, target)) return visible;
                if (node["children"] is JsonArray nested && Find(nested, target, visible)) return true;
            }
            return false;
        }

        if (!Find(root, item.Node, parentVisible: true)) return false;
        var visitedGroups = new HashSet<string>(StringComparer.Ordinal);
        string parentId = Text(item.Node["parentId"]);
        while (parentId.Length > 0 && visitedGroups.Add(parentId))
        {
            LayerItem? parent = _layers.FirstOrDefault(candidate =>
                Text(candidate.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)
                && Text(candidate.Node["id"]).Equals(parentId, StringComparison.Ordinal));
            if (parent is null) break;
            if (!Find(root, parent.Node, parentVisible: true)) return false;
            parentId = Text(parent.Node["parentId"]);
        }
        return true;
    }

    private void EditorOverlayMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || _dragItem?.Node["children"] is not JsonObject p)
        {
            UpdateResizeCursor(e.GetPosition(EditorOverlay));
            return;
        }
        System.Windows.Point point = e.GetPosition(EditorOverlay);
        if (_rotating)
        {
            double pointerAngle = Math.Atan2(point.Y - _rotationCenter.Y, point.X - _rotationCenter.X);
            double delta = Math.Atan2(Math.Sin(pointerAngle - _rotationStartPointerAngle),
                Math.Cos(pointerAngle - _rotationStartPointerAngle));
            double degrees = ((_rotationStartDegrees + delta * 180d / Math.PI) % 360d + 360d) % 360d;
            p["rotate"] = degrees;
            _loadingProperties = true;
            try { RotateBox.Text = degrees.ToString("0.#", CultureInfo.CurrentCulture); }
            finally { _loadingProperties = false; }
            UpdateSelectionOverlay();
            RebuildRenderer();
            e.Handled = true;
            return;
        }
        double screenX = point.X - _dragOrigin.X;
        double screenY = point.Y - _dragOrigin.Y;
        double angle = LayerRotation(_dragItem);
        double localX = screenX * Math.Cos(angle) + screenY * Math.Sin(angle);
        double localY = -screenX * Math.Sin(angle) + screenY * Math.Cos(angle);
        double x = _dragStartX;
        double y = _dragStartY;
        double width = _dragStartWidth;
        double height = _dragStartHeight;
        if (_resizing)
        {
            double scaleX = LayerScale(_dragItem.Node, 0);
            double scaleY = LayerScale(_dragItem.Node, 1);
            double startVisualWidth = _dragStartBounds.Width;
            double startVisualHeight = _dragStartBounds.Height;
            double visualLeft = _dragStartBounds.X;
            double visualTop = _dragStartBounds.Y;
            double visualWidth = startVisualWidth;
            double visualHeight = startVisualHeight;
            bool resizeLeft = _resizeEdges.HasFlag(ResizeEdges.Left);
            bool resizeRight = _resizeEdges.HasFlag(ResizeEdges.Right);
            bool resizeTop = _resizeEdges.HasFlag(ResizeEdges.Top);
            bool resizeBottom = _resizeEdges.HasFlag(ResizeEdges.Bottom);
            if (resizeLeft) { visualLeft += localX; visualWidth -= localX; }
            if (resizeRight) visualWidth += localX;
            if (resizeTop) { visualTop += localY; visualHeight -= localY; }
            if (resizeBottom) visualHeight += localY;

            visualWidth = Math.Max(8, visualWidth);
            visualHeight = Math.Max(8, visualHeight);
            string layerType = Text(_dragItem.Node["type"]).ToLowerInvariant();
            bool intrinsicMedia = layerType is "image" or "media";
            bool keepRatio = ReadBool(p["keepRatio"]) || intrinsicMedia;
            if (keepRatio && startVisualWidth > 0 && startVisualHeight > 0)
            {
                double aspect = startVisualWidth / startVisualHeight;
                bool horizontal = resizeLeft || resizeRight;
                bool vertical = resizeTop || resizeBottom;
                if (horizontal && vertical)
                {
                    double horizontalChange = Math.Abs(visualWidth - startVisualWidth) / startVisualWidth;
                    double verticalChange = Math.Abs(visualHeight - startVisualHeight) / startVisualHeight;
                    if (horizontalChange >= verticalChange) visualHeight = visualWidth / aspect;
                    else visualWidth = visualHeight * aspect;
                }
                else if (horizontal) visualHeight = visualWidth / aspect;
                else if (vertical) visualWidth = visualHeight * aspect;
            }

            visualLeft = resizeLeft ? _dragStartBounds.X + startVisualWidth - visualWidth
                : resizeRight ? _dragStartBounds.X : _dragStartBounds.X + (startVisualWidth - visualWidth) / 2;
            visualTop = resizeTop ? _dragStartBounds.Y + startVisualHeight - visualHeight
                : resizeBottom ? _dragStartBounds.Y : _dragStartBounds.Y + (startVisualHeight - visualHeight) / 2;
            width = Math.Max(1, visualWidth / scaleX);
            height = Math.Max(1, visualHeight / scaleY);
            double widthRatio = startVisualWidth > 0 ? visualWidth / startVisualWidth : 1;
            double heightRatio = startVisualHeight > 0 ? visualHeight / startVisualHeight : 1;
            double fontScaleFactor = Math.Sqrt(Math.Max(.0001, widthRatio * heightRatio));
            foreach ((string property, double startingSize) in _dragStartFontSizes)
            {
                double minimum = property == "fontSize" ? 5 : 8;
                double maximum = property == "fontSize" ? 180 : 999;
                double scaledSize = Math.Clamp(startingSize * fontScaleFactor, minimum, maximum);
                p[property] = scaledSize;
            }

            System.Windows.Point groupOffset = GetGroupOffset(_dragItem.Node, []);
            double tx = GetNumber(_dragItem.Node["tx"]) + groupOffset.X;
            double ty = GetNumber(_dragItem.Node["ty"]) + groupOffset.Y;
            if (layerType is "text" or "data" or "lyric")
            {
                double fontSize = Math.Clamp(GetNumber(p["fontSize"], 12), 5, 180);
                string value = LayerMeasurementText(_dragItem, p, layerType);
                double renderedScaleX = Math.Clamp(scaleX, .1, 8);
                double renderedScaleY = Math.Clamp(scaleY, .1, 8);
                System.Drawing.SizeF measured = MeasureLayerText(value, p, fontSize * renderedScaleX);
                string alignment = Text(p["align"]).ToLowerInvariant();
                double glyphAnchor = alignment switch { "center" => measured.Width / 2, "right" => measured.Width, _ => 0 };
                bool currentLyric = ReadBool(p["isLyric"])
                    || Text(p["dataSource"]).Equals("lyric_current", StringComparison.OrdinalIgnoreCase);
                double dataOffset = (layerType is "data" or "lyric") && !currentLyric ? .85 * renderedScaleY : 0;
                x = visualLeft + glyphAnchor - tx - (width - width * scaleX) / 2;
                y = visualTop + dataOffset - ty - (height - height * scaleY) / 2;
            }
            else
            {
                x = visualLeft - tx - (width - width * scaleX) / 2;
                y = visualTop - ty - (height - height * scaleY) / 2;
            }
            p["x"] = x;
            p["y"] = y;
            p["width"] = width;
            p["height"] = height;
        }
        else
        {
            x += localX;
            y += localY;
            p["x"] = x;
            p["y"] = y;
        }
        _loadingProperties = true;
        try
        {
            XBox.Text = x.ToString("0.##", CultureInfo.CurrentCulture);
            YBox.Text = y.ToString("0.##", CultureInfo.CurrentCulture);
            if (_resizing)
            {
                WidthBox.Text = width.ToString("0.##", CultureInfo.CurrentCulture);
                HeightBox.Text = height.ToString("0.##", CultureInfo.CurrentCulture);
                if (_dragItem?.Node["children"] is JsonObject resizedProperties)
                {
                    string resizedType = Text(_dragItem.Node["type"]).ToLowerInvariant();
                    if (resizedType is "text" or "data" or "lyric")
                        FontSizeBox.Text = NumberText(resizedProperties["fontSize"]);
                    else if (resizedType == "songdisplay")
                    {
                        SongTitleFontSizeBox.Text = NumberText(resizedProperties["titleFontSize"]);
                        SongArtistFontSizeBox.Text = NumberText(resizedProperties["artistFontSize"]);
                        FontSizeBox.Text = NumberText(resizedProperties["lyricFontSize"]);
                        SongActiveFontSizeBox.Text = NumberText(resizedProperties["lyricActiveFontSize"]);
                    }
                }
            }
        }
        finally { _loadingProperties = false; }
        UpdateSelectionOverlay();
        RebuildRenderer();
        e.Handled = true;
    }

    private void EditorOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _resizing = false;
        _rotating = false;
        _resizeEdges = ResizeEdges.None;
        _dragItem = null;
        _dragStartFontSizes.Clear();
        EditorOverlay.ReleaseMouseCapture();
        EditorOverlay.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void UpdateResizeCursor(System.Windows.Point point)
    {
        if (LayerList.SelectedItem is not LayerItem selected || selected.IsLocked)
        {
            EditorOverlay.Cursor = Cursors.Arrow;
            return;
        }
        if (IsRotationHandleHit(point, selected))
        {
            EditorOverlay.Cursor = Cursors.Hand;
            return;
        }
        var bounds = GetLayerBounds(selected);
        if (bounds.Width <= 0 || bounds.Height <= 0 || !ContainsLayerPoint(point, selected, bounds))
        {
            EditorOverlay.Cursor = Cursors.Arrow;
            return;
        }
        ResizeEdges edges = GetResizeEdges(ToLayerLocalPoint(point, selected, bounds), bounds.Width, bounds.Height);
        if (edges is ResizeEdges.Left or ResizeEdges.Right)
            EditorOverlay.Cursor = Cursors.SizeWE;
        else if (edges is ResizeEdges.Top or ResizeEdges.Bottom)
            EditorOverlay.Cursor = Cursors.SizeNS;
        else if (edges == (ResizeEdges.Left | ResizeEdges.Top) || edges == (ResizeEdges.Right | ResizeEdges.Bottom))
            EditorOverlay.Cursor = Cursors.SizeNWSE;
        else if (edges == (ResizeEdges.Right | ResizeEdges.Top) || edges == (ResizeEdges.Left | ResizeEdges.Bottom))
            EditorOverlay.Cursor = Cursors.SizeNESW;
        else
            EditorOverlay.Cursor = edges == ResizeEdges.None ? Cursors.Arrow : Cursors.SizeAll;
    }

    private bool IsRotationHandleHit(System.Windows.Point point, LayerItem item)
    {
        LayerGeometry geometry = GetLayerGeometry(item);
        if (geometry.Width <= 0 || geometry.Height <= 0) return false;
        System.Windows.Point handle = GetRotationHandlePoint(geometry, LayerRotation(item));
        double dx = point.X - handle.X;
        double dy = point.Y - handle.Y;
        return dx * dx + dy * dy <= 14 * 14;
    }

    private static System.Windows.Point GetRotationHandlePoint(LayerGeometry geometry, double angle)
    {
        double localX = geometry.X + geometry.Width / 2;
        double localY = geometry.Y - 28;
        double dx = localX - geometry.PivotX;
        double dy = localY - geometry.PivotY;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        return new System.Windows.Point(geometry.PivotX + dx * cos - dy * sin,
            geometry.PivotY + dx * sin + dy * cos);
    }

    private void UpdateSelectionOverlay()
    {
        if (EditorOverlay is null) return;
        EditorOverlay.Children.Clear();
        _displayedSelectionGeometry = null;
        if (LayerList.SelectedItem is not LayerItem selected || selected.Node["children"] is not JsonObject p)
            return;
        LayerGeometry geometry = GetLayerGeometry(selected);
        _displayedSelectionGeometry = geometry;
        (double x, double y, double width, double height) = (geometry.X, geometry.Y, geometry.Width, geometry.Height);
        if (width <= 0 || height <= 0) return;
        var selection = new Grid
        {
            Width = width,
            Height = height,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point((geometry.PivotX - x) / width, (geometry.PivotY - y) / height),
            RenderTransform = new System.Windows.Media.RotateTransform(LayerRotation(selected) * 180d / Math.PI)
        };
        selection.Children.Add(new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 154, 247)),
            BorderThickness = new Thickness(2),
            Background = System.Windows.Media.Brushes.Transparent
        });
        if (!selected.IsLocked)
        {
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Top, new Thickness(-4)));
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Top, new Thickness(0, -4, 0, 0)));
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Top, new Thickness(0, -4, -4, 0)));
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Center, new Thickness(-4, 0, 0, 0)));
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Center, new Thickness(0, 0, -4, 0)));
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Bottom, new Thickness(-4, 0, 0, -4)));
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Bottom, new Thickness(0, 0, 0, -4)));
            selection.Children.Add(CreateResizeHandle(System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Bottom, new Thickness(0, 0, -4, -4)));
            var rotationAdornment = new System.Windows.Controls.Canvas
            {
                Width = width,
                Height = height,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            var handleBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(185, 154, 247));
            rotationAdornment.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = width / 2,
                Y1 = 0,
                X2 = width / 2,
                Y2 = -20,
                Stroke = handleBrush,
                StrokeThickness = 2
            });
            var rotationHandle = new System.Windows.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = handleBrush,
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 1.5
            };
            System.Windows.Controls.Canvas.SetLeft(rotationHandle, width / 2 - 7);
            System.Windows.Controls.Canvas.SetTop(rotationHandle, -35);
            rotationAdornment.Children.Add(rotationHandle);
            selection.Children.Add(rotationAdornment);
        }
        System.Windows.Controls.Canvas.SetLeft(selection, x);
        System.Windows.Controls.Canvas.SetTop(selection, y);
        EditorOverlay.Children.Add(selection);
    }

    private static Border CreateResizeHandle(System.Windows.HorizontalAlignment horizontal,
        System.Windows.VerticalAlignment vertical, Thickness margin)
        => new()
        {
            Width = 9,
            Height = 9,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = margin,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 154, 247)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2)
        };

    private static ResizeEdges GetResizeEdges(System.Windows.Point localPoint, double width, double height)
    {
        const double edgeDistance = 12;
        if (width <= 0 || height <= 0 || localPoint.X < 0 || localPoint.X > width || localPoint.Y < 0 || localPoint.Y > height)
            return ResizeEdges.None;
        bool nearLeft = localPoint.X <= edgeDistance;
        bool nearRight = width - localPoint.X <= edgeDistance;
        bool nearTop = localPoint.Y <= edgeDistance;
        bool nearBottom = height - localPoint.Y <= edgeDistance;
        if (nearLeft && nearRight)
        {
            nearLeft = localPoint.X <= width / 2;
            nearRight = !nearLeft;
        }
        if (nearTop && nearBottom)
        {
            nearTop = localPoint.Y <= height / 2;
            nearBottom = !nearTop;
        }
        ResizeEdges edges = ResizeEdges.None;
        if (nearLeft) edges |= ResizeEdges.Left;
        if (nearRight) edges |= ResizeEdges.Right;
        if (nearTop) edges |= ResizeEdges.Top;
        if (nearBottom) edges |= ResizeEdges.Bottom;
        return edges;
    }

    private (double X, double Y, double Width, double Height) GetLayerBounds(LayerItem item)
    {
        LayerGeometry geometry = GetLayerGeometry(item);
        return (geometry.X, geometry.Y, geometry.Width, geometry.Height);
    }

    private LayerGeometry GetLayerGeometry(LayerItem item)
    {
        if (item.Node["children"] is not JsonObject properties) return default;
        double x = GetNumber(properties["x"]);
        double y = GetNumber(properties["y"]);
        string type = Text(item.Node["type"]).ToLowerInvariant();
        double nodeScaleX = LayerScale(item.Node, 0);
        double nodeScaleY = LayerScale(item.Node, 1);
        System.Windows.Point groupOffset = GetGroupOffset(item.Node, []);
        double tx = GetNumber(item.Node["tx"]) + groupOffset.X;
        double ty = GetNumber(item.Node["ty"]) + groupOffset.Y;

        if (type is "text" or "data" or "lyric")
        {
            double fontSize = Math.Clamp(GetNumber(properties["fontSize"], 12), 5, 180);
            string text = LayerMeasurementText(item, properties, type);
            if (text.Length == 0) return default;
            double fontScaleX = Math.Clamp(nodeScaleX, .1, 8);
            double fontScaleY = Math.Clamp(nodeScaleY, .1, 8);
            System.Drawing.SizeF baseTextSize = MeasureLayerText(text, properties, fontSize);
            System.Drawing.SizeF measuredText = MeasureLayerText(text, properties, fontSize * fontScaleX);

            double textWidth = GetNumber(properties["width"], GetNumber(item.Node["width"]));
            double textHeight = GetNumber(properties["height"], fontSize);
            double drawWidth = textWidth * nodeScaleX;
            double drawHeight = textHeight * nodeScaleY;
            double drawLeft = textWidth <= 0 ? x + tx : x + tx + (textWidth - drawWidth) / 2;
            double drawTop = textHeight <= 0 ? y + ty : y + ty + (textHeight - drawHeight) / 2;
            bool intrinsicWidth = drawWidth <= 1;
            double layoutWidth = intrinsicWidth ? Math.Max(1, measuredText.Width) : drawWidth;
            double layoutHeight = drawHeight <= 1 ? Math.Max(1, measuredText.Height) : drawHeight;
            string alignment = Text(properties["align"]).ToLowerInvariant();
            double layoutLeft = intrinsicWidth && alignment == "left"
                ? drawLeft + (baseTextSize.Width - measuredText.Width) / 2
                : drawLeft - (alignment switch { "center" => layoutWidth / 2, "right" => layoutWidth, _ => 0 });
            bool dataText = type is "data" or "lyric";
            bool currentLyric = ReadBool(properties["isLyric"])
                || Text(properties["dataSource"]).Equals("lyric_current", StringComparison.OrdinalIgnoreCase);
            double layoutTop = drawTop - (dataText && !currentLyric ? .85 * fontScaleY : 0);
            double glyphLeft = layoutLeft + (alignment switch
            {
                "center" => (layoutWidth - measuredText.Width) / 2,
                "right" => layoutWidth - measuredText.Width,
                _ => 0
            });
            double glyphTop = layoutTop;
            double pivotX = layoutLeft + layoutWidth / 2;
            double pivotY = layoutTop + layoutHeight / 2;
            return new LayerGeometry(glyphLeft, glyphTop, measuredText.Width, measuredText.Height, pivotX, pivotY);
        }

        double width = GetNumber(properties["width"], GetNumber(item.Node["width"]));
        double height = GetNumber(properties["height"], GetNumber(item.Node["height"]));
        if (type == "arc" && (width <= 0 || height <= 0))
        {
            double arcSize = width > 0 ? width : height > 0 ? height : 120;
            if (width <= 0) width = arcSize;
            if (height <= 0) height = arcSize;
        }
        if ((type is "media" or "image") && TryGetEditorMediaDimensions(properties, out DrawingSize mediaSize))
        {
            if (width > 0)
                height = width * mediaSize.Height / mediaSize.Width;
            else
            {
                width = mediaSize.Width;
                height = mediaSize.Height;
            }
        }
        if (width <= 0 && height <= 0) return default;
        double left = x + tx + (width - width * nodeScaleX) / 2;
        double top = y + ty + (height - height * nodeScaleY) / 2;
        double drawnWidth = width * nodeScaleX;
        double drawnHeight = height * nodeScaleY;
        return new LayerGeometry(left, top, drawnWidth, drawnHeight,
            left + drawnWidth / 2, top + drawnHeight / 2);
    }

    private string LayerMeasurementText(LayerItem item, JsonObject properties, string type)
    {
        lock (_renderGate)
        {
            string? renderedText = _renderer?.GetTextLayerValue(item.Node);
            if (renderedText is not null) return renderedText;
        }
        return LayerMeasurementText(properties, type);
    }

    private void RefreshSelectionGeometry()
    {
        if (DateTime.UtcNow < _nextSelectionGeometryCheckUtc
            || LayerList.SelectedItem is not LayerItem item
            || item.Node["children"] is not JsonObject)
            return;
        _nextSelectionGeometryCheckUtc = DateTime.UtcNow.AddMilliseconds(250);
        LayerGeometry current = GetLayerGeometry(item);
        if (_displayedSelectionGeometry is not LayerGeometry displayed || displayed != current)
            UpdateSelectionOverlay();
    }

    private static string LayerMeasurementText(JsonObject properties, string type)
    {
        string source = Text(properties["dataSource"]);
        if (source.Equals("lyric_current", StringComparison.OrdinalIgnoreCase) || ReadBool(properties["isLyric"]))
            return "Reproduza uma musica para ver as letras";
        if (type is "data" or "lyric" || source.Length > 0)
            return LayerMeasurementSensors.Format(source,
                (int)Math.Clamp(GetNumber(properties["tofixCount"]), 0, 3), ReadBool(properties["hideUnit"]));

        string text = Text(properties["text"]);
        return text.Contains('{') && text.Contains('}')
            ? Regex.Replace(text, "\\{([^{}]+)\\}", match => LayerMeasurementSensors.Format(match.Groups[1].Value, 0, false))
            : text;
    }

    private static SensorSnapshot CreateLayerMeasurementSensors() => new()
    {
        Now = DateTime.Now,
        CpuUtilization = 100,
        MemoryUtilization = 100,
        MemoryTelemetryReady = true,
        MemoryUsedMb = 16384,
        MemoryTotalMb = 32768,
        MemoryFreeMb = 16384,
        CpuName = "AMD Ryzen 9 5900X",
        GpuName = "NVIDIA GeForce RTX 5080",
        MemoryName = "System memory",
        CpuCurrentRefreshRate = 5000,
        GpuCurrentRefreshRate = 5000,
        CpuPowerDraw = 100,
        GpuPowerDraw = 100,
        CpuVoltage = 1.5,
        GpuVoltage = 1.5,
        DiskTemperature = 100,
        DiskUtilization = 100,
        DiskFreeGb = 16384,
        CpuTemperature = 100,
        GpuTemperature = 100,
        GpuTemperatureValue = 100,
        GpuUtilization = 100,
        GpuMemoryUsedMb = 16384,
        GpuMemoryTotalMb = 32768,
        ExtraValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["memlayout_temperature"] = 100, ["memlayout_vid"] = 1.5, ["memlayout_powerdraw"] = 100,
            ["memlayout_fanspeed"] = 3000, ["disklayout_temperature"] = 100, ["disklayout_utilization"] = 100,
            ["disklayout_currentrefreshrate"] = 5000, ["disklayout_powerdraw"] = 100, ["disklayout_vid"] = 1.5,
            ["disklayout_fanspeed"] = 3000, ["disklayout_free"] = 16384, ["disklayout_used"] = 16384,
            ["disklayout_total"] = 32768, ["disklayout_up"] = 100, ["disklayout_down"] = 100,
            ["disk_up"] = 100, ["netinfo_up"] = 100, ["netinfo_down"] = 100,
            ["mainbordlayout_fanspeed"] = 3000, ["fanlayout_fanspeed"] = 3000, ["coollayout_fanspeed"] = 3000
        },
        ExtraLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["disklayout_name"] = "System drive", ["mainbordlayout_name"] = "Motherboard"
        }
    };

    private System.Drawing.SizeF MeasureLayerText(string text, JsonObject properties, double size)
    {
        if (string.IsNullOrEmpty(text)) return System.Drawing.SizeF.Empty;
        string family = properties["fontFamily"] is null
            ? "Arial"
            : Text(properties["fontFamily"]).Trim().Trim('"', '\'');
        System.Drawing.FontStyle style = System.Drawing.FontStyle.Regular;
        string fontStyle = Text(properties["fontStyle"]);
        if (IsTruthy(properties["bold"]) || IsBoldFontWeight(properties["fontWeight"])
            || HasFontStyle(properties["fontStyle"], "bold"))
            style |= System.Drawing.FontStyle.Bold;
        if (IsTruthy(properties["italic"]) || fontStyle.Contains("italic", StringComparison.OrdinalIgnoreCase)
            || HasFontStyle(properties["fontStyle"], "italic"))
            style |= System.Drawing.FontStyle.Italic;
        if (IsTruthy(properties["underline"]) || Text(properties["textDecoration"]).Contains("underline", StringComparison.OrdinalIgnoreCase)
            || HasFontStyle(properties["fontStyle"], "underline"))
            style |= System.Drawing.FontStyle.Underline;

        float fontSize = (float)Math.Clamp(size, .5, 1440);
        var key = (text, family, BitConverter.SingleToInt32Bits(fontSize), (int)style);
        if (_textMeasureCache.TryGetValue(key, out System.Drawing.SizeF cached)) return cached;
        if (_textMeasureCache.Count > 256) _textMeasureCache.Clear();

        using var bitmap = new DrawingBitmap(1, 1);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = AppRenderSettings.Mode is "highQuality" or "balanced"
            ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
            : System.Drawing.Text.TextRenderingHint.AntiAlias;
        System.Drawing.Font font;
        try { font = new System.Drawing.Font(family, fontSize, style, System.Drawing.GraphicsUnit.Pixel); }
        catch (ArgumentException) { font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSerif, fontSize, style, System.Drawing.GraphicsUnit.Pixel); }
        using (font)
        {
            using var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic)
            {
                FormatFlags = System.Drawing.StringFormatFlags.NoWrap | System.Drawing.StringFormatFlags.NoClip
            };
            System.Drawing.SizeF measured = graphics.MeasureString(text, font, PointF.Empty, format);
            _textMeasureCache[key] = measured;
            return measured;
        }
    }

    private bool ContainsLayerPoint(System.Windows.Point point, LayerItem item,
        (double X, double Y, double Width, double Height) bounds)
    {
        System.Windows.Point local = ToLayerLocalPoint(point, item, bounds);
        return local.X >= 0 && local.X <= bounds.Width && local.Y >= 0 && local.Y <= bounds.Height;
    }

    private System.Windows.Point ToLayerLocalPoint(System.Windows.Point point, LayerItem item,
        (double X, double Y, double Width, double Height) bounds)
    {
        double angle = LayerRotation(item);
        LayerGeometry geometry = GetLayerGeometry(item);
        double dx = point.X - geometry.PivotX;
        double dy = point.Y - geometry.PivotY;
        double localX = dx * Math.Cos(angle) + dy * Math.Sin(angle) + geometry.PivotX - bounds.X;
        double localY = -dx * Math.Sin(angle) + dy * Math.Cos(angle) + geometry.PivotY - bounds.Y;
        return new System.Windows.Point(localX, localY);
    }

    private static double LayerRotation(LayerItem item)
        => GetNumber((item.Node["children"] as JsonObject)?["rotate"]) * Math.PI / 180d;

    private bool TryGetEditorMediaDimensions(JsonObject properties, out DrawingSize size)
    {
        size = DrawingSize.Empty;
        lock (_renderGate)
        {
            if (_renderer?.TryGetMediaDimensions(properties, out size) == true)
                return true;
        }
        string? path = FirstString(properties["media"]);
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (_mediaDimensionCache.TryGetValue(path, out size)) return true;
        if (_mediaDimensionMisses.Contains(path)) return false;

        byte[]? bytes = _addedAssets.TryGetValue(path, out byte[]? added) ? added : null;
        if (bytes is null && _sourcePackage.TryGetAsset(path, out byte[] packaged)) bytes = packaged;
        if (bytes is not null)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using System.Drawing.Image image = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                size = image.Size;
                if (size.Width > 0 && size.Height > 0)
                {
                    _mediaDimensionCache[path] = size;
                    return true;
                }
            }
            catch (ArgumentException) { }
            catch (ExternalException) { }
        }
        _mediaDimensionMisses.Add(path);
        return false;
    }

    private static bool ReadBool(JsonNode? value)
    {
        if (value is null) return false;
        try { return value.GetValue<bool>(); }
        catch (InvalidOperationException) { return value.ToJsonString().Equals("true", StringComparison.OrdinalIgnoreCase); }
    }

    private System.Windows.Point GetGroupOffset(JsonObject node, HashSet<string> visited)
    {
        string parentId = Text(node["parentId"]);
        if (string.IsNullOrEmpty(parentId) || !visited.Add(parentId)
            || _definition["children"] is not JsonArray children)
            return new System.Windows.Point(0, 0);
        JsonObject? group = children.OfType<JsonObject>().FirstOrDefault(candidate =>
            Text(candidate["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)
            && Text(candidate["id"]).Equals(parentId, StringComparison.Ordinal));
        if (group is null) return new System.Windows.Point(0, 0);
        JsonObject? properties = group["children"] as JsonObject;
        System.Windows.Point inherited = GetGroupOffset(group, visited);
        return new System.Windows.Point(
            inherited.X + GetNumber(properties?["x"]) + GetNumber(group["tx"]),
            inherited.Y + GetNumber(properties?["y"]) + GetNumber(group["ty"]));
    }

    private static double LayerScale(JsonObject node, int index)
    {
        double fallback = index == 0 ? 1 : LayerScale(node, 0);
        JsonNode? value = node["scale"];
        if (value is null) return fallback;
        if (value is JsonArray array)
        {
            if (index < 0 || index >= array.Count) return fallback;
            value = array[index];
        }
        else if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? serialized))
        {
            string[] axes = (serialized ?? string.Empty)
                .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int axis = axes.Length == 1 ? 0 : index;
            if (axis < 0 || axis >= axes.Length)
                return fallback;
            return TryNumber(axes[axis], out double parsed) ? Math.Clamp(parsed, .05, 12) : fallback;
        }
        double scale = GetNumber(value, double.NaN);
        return double.IsFinite(scale) ? Math.Clamp(scale, .05, 12) : fallback;
    }

    private static double GetNumber(JsonNode? value)
    {
        return GetNumber(value, 0);
    }

    private static double GetNumber(JsonNode? value, double fallback)
    {
        if (value is null) return fallback;
        try { return value.GetValue<double>(); }
        catch (InvalidOperationException)
        {
            return TryNumber(Text(value), out double number) ? number : fallback;
        }
    }

    private void AddLayerClicked(object sender, RoutedEventArgs e)
    {
        JsonArray root = _definition["children"] as JsonArray ?? new JsonArray();
        _definition["children"] = root;
        string type = (NewLayerType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "text";
        if (type.Equals("addMedia", StringComparison.OrdinalIgnoreCase))
        {
            AddImageClicked(sender, e);
            return;
        }
        JsonObject node = CreateNode(type, CreateProperties(type));
        if (LayerList.SelectedItem is LayerItem { } selected
            && Text(selected.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)
            && ReferenceEquals(selected.Parent, root))
        {
            node["parentId"] = Text(selected.Node["id"]);
            node["index"] = root.Count;
        }
        root.Add(node);
        UpdateSiblingOrder(root);
        ReloadLayers(node);
        StatusText.Text = "Camada adicionada. Ajuste posição e conteúdo no painel de propriedades.";
        RebuildRenderer();
    }

    private void CheckAllLayersClicked(object sender, RoutedEventArgs e)
    {
        foreach (LayerItem item in _layers)
            item.IsChecked = true;
        LayerList.Items.Refresh();
        StatusText.Text = $"Marcadas {_layers.Count} camadas para exclusão em lote.";
    }

    private void UncheckAllLayersClicked(object sender, RoutedEventArgs e)
    {
        foreach (LayerItem item in _layers)
            item.IsChecked = false;
        LayerList.Items.Refresh();
        StatusText.Text = "Desmarcadas todas as camadas.";
    }

    private void InvertLayerChecksClicked(object sender, RoutedEventArgs e)
    {
        foreach (LayerItem item in _layers)
            item.IsChecked = !item.IsChecked;
        LayerList.Items.Refresh();
        StatusText.Text = "Marcação das camadas invertida.";
    }

    private void GroupSelectedLayersClicked(object sender, RoutedEventArgs e)
    {
        JsonArray root = _definition["children"] as JsonArray ?? new JsonArray();
        _definition["children"] = root;
        LayerItem[] selected = LayerList.SelectedItems.OfType<LayerItem>().ToArray();
        if (_steamLayoutMode && selected.Any(item => item.IsRequired))
        {
            StatusText.Text = "A capa Steam obrigat\u00F3ria n\u00E3o pode ser agrupada.";
            return;
        }
        string? parentId = selected.Length == 0 ? null : EmptyToNull(Text(selected[0].Node["parentId"]));
        if (selected.Length < 2 || selected.Any(item => !ReferenceEquals(item.Parent, root)
            || !string.Equals(EmptyToNull(Text(item.Node["parentId"])), parentId, StringComparison.Ordinal)))
        {
            StatusText.Text = "Selecione pelo menos duas camadas do mesmo grupo para agrupá-las.";
            return;
        }

        string groupId = NewLayerId();
        int insertAt = selected.Min(item => root.IndexOf(item.Node));
        var group = new JsonObject
        {
            ["id"] = groupId,
            ["label"] = "Novo grupo",
            ["type"] = "group",
            ["_checked"] = false,
            ["_chosed"] = false,
            ["children"] = new JsonObject { ["x"] = 0, ["y"] = 0 }
        };
        if (parentId is not null) group["parentId"] = parentId;
        foreach (LayerItem item in selected)
            item.Node["parentId"] = groupId;
        root.Insert(Math.Clamp(insertAt, 0, root.Count), group);
        UpdateSiblingOrder(root);
        ReloadLayers(group);
        LayerList.SelectedItems.Clear();
        LayerList.SelectedItem = _layers.FirstOrDefault(item => ReferenceEquals(item.Node, group));
        StatusText.Text = $"Grupo criado com {selected.Length} camadas. Selecione o grupo para ajustar seu deslocamento.";
        RebuildRenderer();
    }
    private void UngroupSelectedLayerClicked(object sender, RoutedEventArgs e)
    {
        if (LayerList.SelectedItem is not LayerItem group
            || !Text(group.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Selecione um grupo para desagrupá-lo.";
            return;
        }

        string groupId = Text(group.Node["id"]);
        string? targetParentId = EmptyToNull(Text(group.Node["parentId"]));
        PointF groupOffset = FindGroupOffset(group.Parent, groupId);
        PointF parentOffset = FindGroupOffset(group.Parent, targetParentId);
        float deltaX = groupOffset.X - parentOffset.X;
        float deltaY = groupOffset.Y - parentOffset.Y;
        foreach (JsonObject child in group.Parent.OfType<JsonObject>()
            .Where(node => Text(node["parentId"]).Equals(groupId, StringComparison.Ordinal)).ToArray())
        {
            if (child["children"] is JsonObject properties)
            {
                properties["x"] = GetNumber(properties["x"]) + deltaX;
                properties["y"] = GetNumber(properties["y"]) + deltaY;
            }
            if (targetParentId is null) child.Remove("parentId");
            else child["parentId"] = targetParentId;
        }
        group.Parent.Remove(group.Node);
        UpdateSiblingOrder(group.Parent);
        ReloadLayers();
        StatusText.Text = "Grupo removido; as camadas mantiveram sua posição visual.";
        RebuildRenderer();
    }
    private void RemoveCheckedLayersClicked(object sender, RoutedEventArgs e)
    {
        JsonArray? root = _definition["children"] as JsonArray;
        if (root is null)
        {
            StatusText.Text = "A cena não contém camadas marcadas.";
            return;
        }
        var selectedNodes = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (LayerItem item in _layers.Where(item => item.IsChecked))
            if (!_steamLayoutMode || !item.IsRequired)
                selectedNodes.Add(item.Node);
        if (selectedNodes.Count == 0)
        {
            StatusText.Text = "Marque as caixas ao lado das camadas que deseja excluir.";
            return;
        }
        HashSet<string> selectedGroups = CollectNestedGroupIds(root, selectedNodes
            .Where(node => Text(node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase))
            .Select(node => Text(node["id"])));
        int removed = 0;
        for (int i = root.Count - 1; i >= 0; i--)
        {
            if (root[i] is not JsonObject node) continue;
            if (!selectedNodes.Contains(node)
                && !selectedGroups.Contains(Text(node["id"]))
                && !selectedGroups.Contains(Text(node["parentId"]))) continue;
            root.RemoveAt(i);
            removed++;
        }
        UpdateSiblingOrder(root);
        ReloadLayers();
        StatusText.Text = $"Excluídas {removed} camadas marcadas e o conteúdo dos grupos selecionados.";
        RebuildRenderer();
    }

    private static HashSet<string> CollectNestedGroupIds(JsonArray nodes, IEnumerable<string> initialIds)
    {
        var groupIds = new HashSet<string>(initialIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (JsonObject node in nodes.OfType<JsonObject>())
            {
                if (!Text(node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)) continue;
                string id = Text(node["id"]);
                if (id.Length > 0 && groupIds.Contains(Text(node["parentId"]))) changed |= groupIds.Add(id);
            }
        } while (changed);
        return groupIds;
    }
    private static string NewLayerId()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N")[..5];

    private static void UpdateSiblingOrder(JsonArray siblings)
    {
        for (int i = 0; i < siblings.Count; i++)
            if (siblings[i] is JsonObject node)
                node["index"] = i;
    }

    private async void AddImageClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar imagem ao tema",
            Filter = "Mídia compatível|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Vídeos|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm|Todos os arquivos|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        StudioFileDialogDefaults.Configure(dialog);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            bool video = extension is ".mp4" or ".m4v" or ".wmv" or ".avi" or ".mov" or ".mkv" or ".webm";
            if (!video && extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")) extension = ".png";
            if (new FileInfo(dialog.FileName).Length > 90 * 1024 * 1024)
            {
                StatusText.Text = "A mídia excede 90 MB; reduza o arquivo para manter o pacote .ht em um tamanho compatível.";
                return;
            }
            VideoImportResult? preparedVideo = null;
            byte[] bytes;
            if (video)
            {
                StatusText.Text = "Preparando vídeo para o formato de temas .ht…";
                preparedVideo = await PrepareVideoImportAsync(dialog.FileName);
                bytes = preparedVideo.Bytes;
                extension = preparedVideo.Extension;
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(dialog.FileName);
            }
            if (bytes.Length > 90 * 1024 * 1024)
            {
                StatusText.Text = "A mídia excede 90 MB; reduza o arquivo para manter o pacote .ht em um tamanho compatível.";
                return;
            }
            using var stream = new MemoryStream(bytes, writable: false);
            string path = (video ? "media/video/" : "media/image/") + Guid.NewGuid().ToString("N") + extension;
            _addedAssets[path] = bytes;
            string type = video ? "media" : "image";
            LayerItem? selectedLayer = LayerList.SelectedItem as LayerItem;
            if (selectedLayer is not null
                && (!_steamLayoutMode || !selectedLayer.IsRequired)
                && Text(selectedLayer.Node["type"]).ToLowerInvariant() is "media" or "image"
                && selectedLayer.Node["children"] is JsonObject existingProperties)
            {
                selectedLayer.Node["type"] = type;
                selectedLayer.Node["label"] = Path.GetFileNameWithoutExtension(dialog.FileName);
                existingProperties["media"] = new JsonArray(path);
                existingProperties["mediaLoadType"] = "local";
                if (existingProperties["width"] is null || existingProperties["height"] is null)
                {
                    if (video)
                    {
                        existingProperties["width"] = _sourcePackage.DeviceWidth;
                        existingProperties["height"] = _sourcePackage.DeviceHeight;
                    }
                    else
                    {
                        using System.Drawing.Image image = System.Drawing.Image.FromStream(stream);
                        existingProperties["width"] = Math.Min(image.Width, _sourcePackage.DeviceWidth);
                        existingProperties["height"] = Math.Min(image.Height, _sourcePackage.DeviceHeight);
                    }
                }
                selectedLayer.Title = Text(selectedLayer.Node["label"]);
                ReloadLayers(selectedLayer.Node);
                StatusText.Text = (video ? (preparedVideo?.Status ?? "Video substituido.") : "Imagem substituida.") + " A nova midia sera embutida no .ht ao salvar.";
                RebuildRenderer();
                return;
            }

            JsonObject properties = CreateProperties(type);
            properties["media"] = new JsonArray(path);
            properties["keepRatio"] = true;
            if (video)
            {
                properties["width"] = _sourcePackage.DeviceWidth;
                properties["height"] = _sourcePackage.DeviceHeight;
                properties["x"] = 0;
                properties["y"] = 0;
            }
            else
            {
                using System.Drawing.Image image = System.Drawing.Image.FromStream(stream);
                properties["width"] = Math.Min(image.Width, _sourcePackage.DeviceWidth);
                properties["height"] = Math.Min(image.Height, _sourcePackage.DeviceHeight);
                properties["x"] = (_sourcePackage.DeviceWidth - GetNumber(properties["width"])) / 2d;
                properties["y"] = (_sourcePackage.DeviceHeight - GetNumber(properties["height"])) / 2d;
            }
            JsonObject node = CreateNode(type, properties, Path.GetFileNameWithoutExtension(dialog.FileName));
            JsonArray root = _definition["children"] as JsonArray ?? new JsonArray();
            _definition["children"] = root;
            if (selectedLayer is not null
                && Text(selectedLayer.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(selectedLayer.Parent, root))
                node["parentId"] = Text(selectedLayer.Node["id"]);
            root.Add(node);
            UpdateSiblingOrder(root);
            ReloadLayers(node);
            StatusText.Text = (video ? (preparedVideo?.Status ?? "Video adicionado.") : "Imagem adicionada.") + " A midia sera embutida no .ht ao salvar.";
            RebuildRenderer(refreshAssets: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "Não foi possível adicionar a imagem", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveLayerClicked(object sender, RoutedEventArgs e)
    {
        if (LayerList.SelectedItem is not LayerItem item) return;
        if (_steamLayoutMode && item.IsRequired)
        {
            StatusText.Text = "A capa Steam faz parte obrigat\u00F3ria do layout e n\u00E3o pode ser removida.";
            return;
        }
        if (Text(item.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            string groupId = Text(item.Node["id"]);
            HashSet<string> groupIds = CollectNestedGroupIds(item.Parent, [groupId]);
            for (int i = item.Parent.Count - 1; i >= 0; i--)
            {
                if (item.Parent[i] is not JsonObject node) continue;
                if (!groupIds.Contains(Text(node["id"])) && !groupIds.Contains(Text(node["parentId"]))) continue;
                item.Parent.RemoveAt(i);
            }
        }
        else
        {
            item.Parent.Remove(item.Node);
        }
        UpdateSiblingOrder(item.Parent);
        ReloadLayers();
        StatusText.Text = "Camada removida da cena.";
        RebuildRenderer();
    }
    private void DuplicateLayerClicked(object sender, RoutedEventArgs e)
    {
        if (LayerList.SelectedItem is not LayerItem item) return;
        if (Text(item.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            string oldGroupId = Text(item.Node["id"]);
            HashSet<string> groupIds = CollectNestedGroupIds(item.Parent, [oldGroupId]);
            JsonObject[] descendants = item.Parent.OfType<JsonObject>()
                .Where(node => groupIds.Contains(Text(node["parentId"])))
                .ToArray();
            JsonObject[] nestedGroups = descendants
                .Where(node => Text(node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var idMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [oldGroupId] = NewLayerId()
            };
            foreach (JsonObject nestedGroup in nestedGroups)
                idMap[Text(nestedGroup["id"])] = NewLayerId();

            JsonObject groupCopy = (JsonObject)item.Node.DeepClone();
            groupCopy["id"] = idMap[oldGroupId];
            groupCopy["label"] = Text(item.Node["label"]) + " (cópia)";
            groupCopy["_checked"] = false;
            groupCopy["_chosed"] = false;
            groupCopy["_editorLocked"] = false;
            var copies = new List<JsonObject> { groupCopy };
            foreach (JsonObject child in descendants)
            {
                JsonObject childCopy = (JsonObject)child.DeepClone();
                string childId = Text(child["id"]);
                childCopy["id"] = idMap.TryGetValue(childId, out string? mappedId) ? mappedId : NewLayerId();
                string parentId = Text(child["parentId"]);
                if (idMap.TryGetValue(parentId, out string? mappedParentId)) childCopy["parentId"] = mappedParentId;
                childCopy["_checked"] = false;
                childCopy["_chosed"] = false;
                childCopy["_editorLocked"] = false;
                copies.Add(childCopy);
            }

            int lastDescendantIndex = descendants.Select(item.Parent.IndexOf)
                .DefaultIfEmpty(item.Parent.IndexOf(item.Node)).Max();
            int insertionIndex = Math.Clamp(lastDescendantIndex + 1, 0, item.Parent.Count);
            foreach (JsonObject copy in copies)
                item.Parent.Insert(insertionIndex++, copy);
            UpdateSiblingOrder(item.Parent);
            ReloadLayers(groupCopy);
            StatusText.Text = $"Grupo duplicado com {descendants.Length} camadas e subgrupos.";
            RebuildRenderer();
            return;
        }

        JsonObject duplicate = (JsonObject)item.Node.DeepClone();
        duplicate["id"] = NewLayerId();
        duplicate["_checked"] = false;
        duplicate["_chosed"] = false;
        duplicate["_editorLocked"] = false;
        duplicate["_steamRequired"] = false;
        string name = Text(duplicate["label"]);
        duplicate["label"] = string.IsNullOrWhiteSpace(name) ? "Cópia" : name + " (cópia)";
        int insertionIndexForLayer = item.Parent.IndexOf(item.Node);
        item.Parent.Insert(insertionIndexForLayer < 0 ? item.Parent.Count : insertionIndexForLayer + 1, duplicate);
        UpdateSiblingOrder(item.Parent);
        ReloadLayers(duplicate);
        StatusText.Text = "Camada duplicada. A cópia mantém sua mídia e fonte de dados.";
        RebuildRenderer();
    }
    private void SaveClicked(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private void CancelClicked(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private async Task SaveAsync()
    {
        if (!_steamLayoutMode && string.IsNullOrWhiteSpace(ThemeNameBox.Text))
        {
            StatusText.Text = "Informe um nome para o tema antes de salvar.";
            ThemeNameBox.Focus();
            return;
        }
        try
        {
            ApplyEditorValues();
            StopRenderer();
            using var renderer = new HtThemeRenderer(_sourcePackage.WithDefinition(_definition, _addedAssets));
            DrawingSize size = new(_sourcePackage.DeviceWidth, _sourcePackage.DeviceHeight);
            using DrawingBitmap frame = await Task.Run(() => renderer.Render(size));
            using var preview = new MemoryStream();
            frame.Save(preview, ImageFormat.Jpeg);
            PackageBytes = _sourcePackage.CreateEditedCopy(_definition, _addedAssets, preview.ToArray());
            SaveRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or ExternalException or InvalidOperationException)
        {
            StatusText.Text = "Falha ao salvar pacote: " + ex.Message;
            RebuildRenderer();
        }
    }

    private void ApplyEditorValues()
    {
        if (LayerList.SelectedItem is not LayerItem item || item.Node["children"] is not JsonObject properties) return;
        string dataSource = SelectedDataSource();
        if (dataSource.Length > 0) properties["dataSource"] = dataSource;
        if (AlignBox.SelectedItem is ComboBoxItem align && align.Tag is string value) properties["align"] = value;
    }

    private void RebuildRenderer(bool refreshAssets = false)
    {
        ThemePackage? variant = null;
        if (refreshAssets || _renderer is null)
        {
            try { variant = _sourcePackage.WithDefinition(_definition, _addedAssets); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or System.Text.Json.JsonException)
            {
                StatusText.Text = "Cena inválida: " + ex.Message;
                return;
            }
        }
        lock (_renderGate)
        {
            if (_renderer is null)
            {
                _renderer = new HtThemeRenderer(variant!);
                _generation++;
            }
            else if (variant is not null)
                _renderer.UpdatePackage(variant);
            else
                _renderer.UpdateDefinition(_definition);
            _contentVersion++;
            _previewDirty = true;
        }
        _previewTimer.Start();
        _ = RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        HtThemeRenderer? renderer;
        int generation;
        int contentVersion;
        lock (_renderGate)
        {
            renderer = _renderer;
            generation = _generation;
            contentVersion = _contentVersion;
        }
        if (renderer is null || _rendering) return;
        _rendering = true;
        _previewDirty = false;
        try
        {
            DrawingSize size = new(_sourcePackage.DeviceWidth, _sourcePackage.DeviceHeight);
            byte[]? bytes = await Task.Run(() =>
            {
                lock (_renderGate)
                {
                    if (!ReferenceEquals(renderer, _renderer)) return null;
                    using DrawingBitmap bitmap = renderer.Render(size);
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            });
            if (bytes is null || generation != _generation || !ReferenceEquals(renderer, _renderer)) return;
            if (contentVersion != _contentVersion)
            {
                _previewDirty = true;
                return;
            }
            using var imageStream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = imageStream;
            image.EndInit();
            image.Freeze();
            PreviewImage.Source = image;
            RefreshSelectionGeometry();
            if (StatusText.Text.StartsWith("Prévia indisponível:", StringComparison.Ordinal))
                StatusText.Text = "Prévia ao vivo usando os sensores disponíveis neste PC.";
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException or IOException)
        {
            if (generation == _generation && contentVersion == _contentVersion) StatusText.Text = "Prévia indisponível: " + ex.Message;
        }
        finally
        {
            _rendering = false;
            if (_previewDirty && _renderer is not null)
                _ = RenderPreviewAsync();
        }
    }

    private void StopRenderer()
    {
        _previewTimer.Stop();
        lock (_renderGate)
        {
            _generation++;
            _contentVersion++;
            _previewDirty = false;
            _renderer?.Dispose();
            _renderer = null;
        }
    }

    private JsonObject CreateProperties(string type)
    {
        double canvasWidth = _sourcePackage.DeviceWidth;
        double canvasHeight = _sourcePackage.DeviceHeight;
        double halfShortSide = Math.Min(canvasWidth, canvasHeight) / 2d;
        var p = new JsonObject
        {
            ["x"] = canvasWidth * .05, ["y"] = canvasHeight * .1,
            ["width"] = canvasWidth * .28, ["height"] = canvasHeight * .12, ["rotate"] = 0, ["keepRatio"] = true
        };
        switch (type.ToLowerInvariant())
        {
            case "text":
                p["text"] = "Novo texto"; p["fontFamily"] = "Arial"; p["fontSize"] = 24; p["color"] = "#FFFFFF"; p["align"] = "left"; break;
            case "data":
                p["dataSource"] = "cpuLayout_temperature"; p["fontFamily"] = "Arial"; p["fontSize"] = 24; p["color"] = "#FFFFFF"; p["align"] = "left"; p["tofixCount"] = 0; break;
            case "lyric":
                p["x"] = (canvasWidth - 200) / 2d; p["y"] = canvasHeight * .7;
                p["width"] = 200; p["height"] = 20; p["keepRatio"] = false;
                p["dataSource"] = "lyric_current"; p["isLyric"] = true;
                p["fontFamily"] = "Arial"; p["fontSize"] = 24; p["color"] = "255,255,255,0.6"; p["align"] = "left"; break;
            case "block":
                p["width"] = halfShortSide; p["height"] = halfShortSide; p["backgroundColor"] = new JsonArray("105,99,99,1", "0,0,0,1"); p["borderRadius"] = 0; p["borderWidth"] = 0; p["borderColor"] = "255,255,255,1"; break;
            case "progress":
                p["width"] = canvasWidth / 2d; p["height"] = canvasWidth / 20d; p["dataSource"] = "cpuLayout_temperature"; p["dataRange"] = new JsonArray(0, 100); p["trailColor"] = "255,255,255,0.2"; p["borderBgColor"] = new JsonArray("105,99,99,1", "0,0,0,1"); p["borderBgType"] = "normal"; p["borderRadius"] = 16; p["borderWidth"] = 0; break;
            case "arc":
                p["width"] = halfShortSide; p["height"] = halfShortSide; p["dataSource"] = "cpuLayout_temperature"; p["dataRange"] = new JsonArray(0, 100); p["trailColor"] = "255,255,255,0.2"; p["borderBgColor"] = new JsonArray("105,99,99,1", "0,0,0,1"); p["borderBgType"] = "normal"; p["strokeWidth"] = 10; p["gapDegree"] = 0; break;
            case "linechart":
                p["width"] = halfShortSide; p["height"] = halfShortSide; p["dataSource"] = "cpuLayout_temperature"; p["dataRange"] = new JsonArray(0, 100); p["areaStyle"] = "84,112,198,1"; p["backgroundColor"] = "0,0,0,0"; p["xyLineConfig"] = new JsonArray("showAreaStyle"); break;
            case "audiowave":
            case "audio":
                p["x"] = canvasWidth * .05; p["y"] = canvasHeight * .2; p["width"] = canvasWidth * .9; p["height"] = canvasHeight * .6;
                p["audioLineCount"] = 20; p["margin"] = 1; p["reduceSpeed"] = 10;
                p["arrangeArrayWithMaxInMiddle"] = false; p["backgroundColor"] = new JsonArray("#438CF5", "#B65CF3"); break;
            case "songdisplay":
            {
                p["width"] = 480; p["height"] = 480;
                p["x"] = (canvasWidth - 480) / 2d; p["y"] = (canvasHeight - 480) / 2d;
                p["keepRatio"] = false;
                const string defaultSongPreset = "vFixed";
                p["presetMode"] = defaultSongPreset;
                p["layout"] = defaultSongPreset == "coverOnly" ? "coverOnly" : "vertical";
                p["coverPosition"] = defaultSongPreset == "coverOnly" ? "center" : "top";
                p["coverAnchor"] = defaultSongPreset == "coverOnly" ? "center" : "start";
                p["contentAlign"] = "center"; p["coverFill"] = defaultSongPreset == "coverOnly" ? "auto" : "width";
                p["showCover"] = true; p["showTitle"] = defaultSongPreset != "coverOnly";
                p["showArtist"] = defaultSongPreset != "coverOnly"; p["showLyrics"] = defaultSongPreset != "coverOnly";
                p["coverCircular"] = true; p["coverRotate"] = true;
                p["coverRotateDuration"] = 12; p["gap"] = 16; p["lyricScroll"] = true;
                p["lyricColor"] = "255,255,255,0.6"; p["lyricFontSize"] = 24;
                p["lyricActiveFontSize"] = 32; p["lyricActiveColor"] = "255,255,255,1"; p["lyricMaxLines"] = 5;
                p["lyricLineSpacing"] = 8; p["showBackground"] = true; p["backgroundColor"] = "0,0,0,0";
                p["backgroundBorderRadius"] = 0; p["backgroundOpacity"] = .8; p["showNoMediaHint"] = true;
                p["noMediaHint"] = "Reproduza uma música para ver as letras"; p["noLyricsHint"] = "Letras não encontradas";
                p["lyricFontFamily"] = "Arial"; p["titleColor"] = "255,255,255,0.8";
                p["titleFontSize"] = 42; p["artistColor"] = "255,255,255,0.8";
                p["artistFontSize"] = 32; break;
            }
            case "image":
                p["width"] = canvasWidth * .25; p["height"] = canvasHeight * .3; p["media"] = new JsonArray(); p["keepRatio"] = true; break;
        }
        return p;
    }

    private static JsonObject CreateNode(string type, JsonObject properties, string? label = null)
    {
        string defaultLabel = type.ToLowerInvariant() switch
        {
            "text" => "Texto", "data" => "Indicador", "lyric" => "Letra atual", "block" => "Bloco", "progress" => "Barra de progresso",
            "arc" => "Medidor circular", "linechart" => "Gráfico de linha", "audiowave" or "audio" => "Espectro de áudio", "songdisplay" => "Mídia e letras", "image" => "Imagem", _ => type
        };
        return new JsonObject
        {
            ["id"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..5],
            ["_chosed"] = false, ["_checked"] = false, ["icon"] = type, ["label"] = label ?? defaultLabel,
            ["type"] = type, ["_group"] = "Componentes gerais", ["children"] = properties,
            ["tx"] = 0, ["ty"] = 0, ["scale"] = new JsonArray(1, 1)
        };
    }

    private static void SetNumberIfValid(JsonObject target, string property, string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            if (double.IsFinite(number)) target[property] = number;
    }

    private static void SelectOptionByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        combo.SelectedIndex = -1;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static string SelectedTag(System.Windows.Controls.ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SetOptionalNumberIfValid(JsonObject target, string property, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            target.Remove(property);
            return;
        }
        SetNumberIfValid(target, property, value);
    }

    private static void SetBoundedNumberIfValid(JsonObject target, string property, string value,
        double minimum, double maximum, bool integer = false, double step = 0)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            target.Remove(property);
            return;
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return;
        if (!double.IsFinite(number)) return;
        number = Math.Clamp(number, minimum, maximum);
        if (integer) number = Math.Round(number, MidpointRounding.AwayFromZero);
        else if (step > 0) number = minimum + Math.Round((number - minimum) / step, MidpointRounding.AwayFromZero) * step;
        target[property] = number;
    }

    private static void SetOptionalString(JsonObject target, string property, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) target.Remove(property);
        else target[property] = value.Trim();
    }

    private void UpdateTextStroke(JsonObject properties)
    {
        JsonObject stroke = properties["textStroke"] as JsonObject ?? new JsonObject();
        SetOptionalNumberIfValid(stroke, "inputnumber", TextStrokeWidthBox.Text);
        SetOptionalString(stroke, "color", TextStrokeColorBox.Text);
        if (stroke.Count == 0) properties.Remove("textStroke");
        else properties["textStroke"] = stroke;
    }

    private void UpdateTextShadow(JsonObject properties)
    {
        JsonObject shadow = properties["textShadow"] as JsonObject ?? new JsonObject();
        SetOptionalNumberIfValid(shadow, "offsetX", TextShadowOffsetXBox.Text);
        SetOptionalNumberIfValid(shadow, "offsetY", TextShadowOffsetYBox.Text);
        SetOptionalNumberIfValid(shadow, "blurRadius", TextShadowBlurBox.Text);
        SetOptionalString(shadow, "color", TextShadowColorBox.Text);
        if (shadow.Count == 0) properties.Remove("textShadow");
        else properties["textShadow"] = shadow;
    }

    private static void SetArrayStringOption(JsonObject target, string property, string value, bool enabled)
    {
        if (target[property] is not JsonArray values)
        {
            if (enabled) target[property] = new JsonArray(value);
            return;
        }

        for (int i = values.Count - 1; i >= 0; i--)
            if (Text(values[i]).Equals(value, StringComparison.Ordinal)) values.RemoveAt(i);
        if (enabled) values.Add(value);
        if (values.Count == 0) target.Remove(property);
    }

    private static bool ArrayContainsString(JsonNode? node, string value)
        => node is JsonArray array && array.Any(item => Text(item).Equals(value, StringComparison.Ordinal));

    private static bool IsTruthy(JsonNode? value)
    {
        string text = Text(value);
        return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";
    }

    private static bool IsBoldFontWeight(JsonNode? value)
    {
        string text = Text(value);
        return text.Contains("bold", StringComparison.OrdinalIgnoreCase)
            || TryNumber(text, out double weight) && weight >= 600;
    }

    private static void SetArrayNumberIfValid(JsonObject target, string property, int index, string value)
    {
        if (!TryNumber(value, out double number)) return;
        if (target[property] is not JsonArray range || range.Count < 2)
        {
            range = new JsonArray(GetRangeValue(target, 0, 0), GetRangeValue(target, 1, 100));
            target[property] = range;
        }
        range[index] = number;
    }

    private static double GetRangeValue(JsonObject properties, int index, double fallback)
    {
        JsonNode? value = ArrayItem(properties["dataRange"], index);
        return value is null ? fallback : GetNumber(value);
    }

    private static JsonNode? ArrayItem(JsonNode? value, int index)
        => value is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;

    private static bool TryNumber(string value, out double number)
        => (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            && double.IsFinite(number);

    private static string NumberText(JsonNode? value)
    {
        if (value is null) return string.Empty;
        try { return value.GetValue<double>().ToString("0.##", CultureInfo.CurrentCulture); }
        catch (InvalidOperationException) { return Text(value); }
    }

    private static string Text(JsonNode? value)
    {
        if (value is null) return string.Empty;
        try { return value.GetValue<string>() ?? string.Empty; }
        catch (InvalidOperationException) { return value.ToJsonString(); }
    }

    private static string FriendlyType(string type) => type.ToLowerInvariant() switch
    {
        "text" => "Texto", "data" => "Indicador de dados", "lyric" => "Letra atual", "block" => "Bloco", "progress" => "Barra de progresso",
        "arc" => "Medidor circular", "linechart" => "Gráfico de linha", "audiowave" or "audio" => "Espectro de áudio", "songdisplay" => "Mídia e letras", "image" or "media" => "Imagem",
        "group" => "Grupo", _ => string.IsNullOrWhiteSpace(type) ? "Componente" : type
    };

    private readonly record struct LayerGeometry(double X, double Y, double Width, double Height, double PivotX, double PivotY);

    private sealed class LayerItem(JsonObject node, JsonArray parent, int depth, string title, string subtitle,
        ObservableCollection<LayerItem> allLayers, bool steamLayoutMode) : INotifyPropertyChanged
    {
        public JsonObject Node { get; } = node;
        public JsonArray Parent { get; } = parent;
        public int Depth { get; } = depth;
        public Thickness Indent { get; } = new(depth * 14, 0, 0, 0);
        public string Title { get; set; } = title;
        public string Subtitle { get; } = subtitle;
        public bool IsRequired => steamLayoutMode && ReadBool(Node["_steamRequired"]);
        public bool CanDelete => !IsRequired;
        public bool CanToggleVisibility => !IsRequired;
        public bool IsChecked
        {
            get => ReadBool(Node["_checked"]);
            set
            {
                if (IsChecked == value) return;
                Node["_checked"] = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
        public bool IsVisible
        {
            get => Node["isShow"] is null || ReadBool(Node["isShow"]);
            set
            {
                if (IsVisible == value) return;
                Node["isShow"] = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibilityTooltip)));
            }
        }
        public string VisibilityTooltip => IsVisible ? "Ocultar camada" : "Mostrar camada";
        public bool IsLocked
        {
            get => ReadBool(Node["_editorLocked"]) || IsLockedByAncestor;
            set
            {
                bool isOwnLockSet = ReadBool(Node["_editorLocked"]);
                if (isOwnLockSet == value) return;
                Node["_editorLocked"] = value;
                foreach (LayerItem layer in allLayers)
                    layer.NotifyLockStateChanged();
            }
        }
        public bool IsLockedByAncestor
        {
            get
            {
                string parentId = Text(Node["parentId"]);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (parentId.Length > 0 && visited.Add(parentId))
                {
                    LayerItem? group = allLayers.FirstOrDefault(candidate =>
                        Text(candidate.Node["type"]).Equals("group", StringComparison.OrdinalIgnoreCase)
                        && Text(candidate.Node["id"]).Equals(parentId, StringComparison.Ordinal));
                    if (group is null) break;
                    if (ReadBool(group.Node["_editorLocked"])) return true;
                    parentId = Text(group.Node["parentId"]);
                }
                return false;
            }
        }
        public string LockTooltip => IsLockedByAncestor
            ? "Bloqueada pelo grupo; desbloqueie o grupo para mover"
            : ReadBool(Node["_editorLocked"]) ? "Desbloquear camada para mover" : "Bloquear camada para impedir movimento";

        private void NotifyLockStateChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLocked)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLockedByAncestor)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockTooltip)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
