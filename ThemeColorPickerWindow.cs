using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace JungleLeopardPoc;

/// <summary>Small Windows 11 styled color picker for theme colors, including alpha.</summary>
internal sealed class ThemeColorPickerWindow : Window
{
    private static readonly string[] Palette =
    [
        "#FFFFFF", "#D9E2F2", "#A8B7D0", "#68758A", "#303A4B", "#111827",
        "#FF6B6B", "#FF9F43", "#F6C453", "#B8D96C", "#39C6A5", "#3FA9F5",
        "#5875F5", "#8F6BFA", "#D66BCE", "#F28AA5", "#8D5A44", "#000000"
    ];

    private readonly TextBox _hexBox;
    private readonly Slider _hueSlider;
    private readonly Slider _saturationSlider;
    private readonly Slider _brightnessSlider;
    private readonly Slider _alphaSlider;
    private readonly TextBlock _alphaValue;
    private readonly Border _preview;
    private readonly Button _applyButton;
    private Color _selectedColor;
    private bool _updating;

    public ThemeColorPickerWindow(string initialValue)
    {
        _selectedColor = ParseColor(initialValue, Colors.White);
        SelectedColorText = initialValue;

        Title = "Escolher cor";
        SourceInitialized += (_, _) => Win11WindowChrome.Apply(new WindowInteropHelper(this).Handle);
        Width = 392;
        Height = Math.Min(620, Math.Max(470, SystemParameters.WorkArea.Height - 64));
        MinWidth = Width;
        MinHeight = Height;
        MaxWidth = Width;
        MaxHeight = Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush("#201F23");
        FontFamily = new FontFamily("Segoe UI");
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/JungleLeopardScreenControl;component/Win11Theme.xaml", UriKind.Relative)
        });

        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = layout;

        var root = new StackPanel();
        var scrollViewer = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false
        };
        layout.Children.Add(scrollViewer);

        _preview = new Border
        {
            Height = 48,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Brush("#414047"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new TextBlock
            {
                Text = "Prévia",
                Foreground = Brush("#F3F1F5"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };
        root.Children.Add(_preview);

        root.Children.Add(Label("Cor RGB"));
        _hexBox = new TextBox
        {
            Text = Hex(_selectedColor),
            Height = 38,
            Padding = new Thickness(10, 4, 10, 4),
            Background = Brush("#252428"),
            Foreground = Brush("#F3F1F5"),
            BorderBrush = Brush("#414047"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 14)
        };
        _hexBox.TextChanged += (_, _) => UpdateFromHex();
        root.Children.Add(_hexBox);

        (double hue, double saturation, double brightness) = ToHsv(_selectedColor);
        _hueSlider = AddLabeledSlider(root, "Matiz", 360, hue, 30);
        _saturationSlider = AddLabeledSlider(root, "Saturação", 100, saturation * 100, 10);
        _brightnessSlider = AddLabeledSlider(root, "Luminosidade", 100, brightness * 100, 10);
        _hueSlider.ValueChanged += (_, _) => UpdateFromHsv();
        _saturationSlider.ValueChanged += (_, _) => UpdateFromHsv();
        _brightnessSlider.ValueChanged += (_, _) => UpdateFromHsv();

        var alphaHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        alphaHeader.Children.Add(Label("Opacidade"));
        _alphaValue = new TextBlock
        {
            Text = AlphaPercent(_selectedColor.A),
            Foreground = Brush("#B8B5BE"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(_alphaValue, Dock.Right);
        alphaHeader.Children.Add(_alphaValue);
        root.Children.Add(alphaHeader);

        _alphaSlider = new Slider
        {
            Minimum = 0,
            Maximum = 255,
            Value = _selectedColor.A,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 0, 0, 14)
        };
        _alphaSlider.ValueChanged += (_, _) => UpdateAlpha();
        root.Children.Add(_alphaSlider);

        root.Children.Add(Label("Cores sugeridas"));
        var palettePanel = new WrapPanel { Margin = new Thickness(-3, 4, -3, 14) };
        foreach (string color in Palette)
        {
            var swatch = new Button
            {
                Width = 30,
                Height = 26,
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                BorderBrush = Brush("#414047"),
                BorderThickness = new Thickness(1),
                Background = Brush(color),
                ToolTip = color,
                Tag = color
            };
            swatch.Click += (_, _) => ChoosePaletteColor((string)swatch.Tag);
            palettePanel.Children.Add(swatch);
        }
        root.Children.Add(palettePanel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancelButton = MakeButton("Cancelar", "Win11Button");
        cancelButton.Margin = new Thickness(0, 0, 8, 0);
        cancelButton.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(cancelButton);
        _applyButton = MakeButton("Aplicar", "Win11PrimaryButton");
        _applyButton.IsDefault = true;
        _applyButton.Click += (_, _) => ApplyColor();
        buttons.Children.Add(_applyButton);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);

        UpdatePreview();
        AppLanguage.Apply(this);
    }

    public string SelectedColorText { get; private set; }

    private void UpdateFromHex()
    {
        if (_updating) return;
        if (!TryParseHex(_hexBox.Text, out Color rgb))
        {
            _applyButton.IsEnabled = false;
            return;
        }
        _applyButton.IsEnabled = true;
        _selectedColor = Color.FromArgb((byte)Math.Round(_alphaSlider.Value), rgb.R, rgb.G, rgb.B);
        UpdateHsvSliders(_selectedColor);
        UpdatePreview();
    }

    private void UpdateAlpha()
    {
        _alphaValue.Text = AlphaPercent((byte)Math.Round(_alphaSlider.Value));
        if (TryParseHex(_hexBox.Text, out Color rgb))
            _selectedColor = Color.FromArgb((byte)Math.Round(_alphaSlider.Value), rgb.R, rgb.G, rgb.B);
        UpdatePreview();
    }

    private void ChoosePaletteColor(string value)
    {
        if (!TryParseHex(value, out Color rgb)) return;
        _updating = true;
        _hexBox.Text = Hex(rgb);
        _updating = false;
        _selectedColor = Color.FromArgb((byte)Math.Round(_alphaSlider.Value), rgb.R, rgb.G, rgb.B);
        UpdateHsvSliders(_selectedColor);
        _applyButton.IsEnabled = true;
        UpdatePreview();
    }

    private void UpdateFromHsv()
    {
        if (_updating) return;
        _selectedColor = FromHsv(_hueSlider.Value, _saturationSlider.Value / 100d,
            _brightnessSlider.Value / 100d, (byte)Math.Round(_alphaSlider.Value));
        _updating = true;
        _hexBox.Text = Hex(_selectedColor);
        _updating = false;
        _applyButton.IsEnabled = true;
        UpdatePreview();
    }

    private void UpdateHsvSliders(Color color)
    {
        (double hue, double saturation, double brightness) = ToHsv(color);
        _updating = true;
        _hueSlider.Value = hue;
        _saturationSlider.Value = saturation * 100;
        _brightnessSlider.Value = brightness * 100;
        _updating = false;
    }

    private void UpdatePreview()
    {
        _preview.Background = new SolidColorBrush(_selectedColor);
        double alpha = _selectedColor.A / 255d;
        double red = _selectedColor.R * alpha + 32 * (1 - alpha);
        double green = _selectedColor.G * alpha + 31 * (1 - alpha);
        double blue = _selectedColor.B * alpha + 35 * (1 - alpha);
        byte luminance = (byte)Math.Round(0.2126 * red + 0.7152 * green + 0.0722 * blue);
        if (_preview.Child is TextBlock label)
            label.Foreground = new SolidColorBrush(luminance >= 145
                ? Color.FromRgb(32, 31, 35)
                : Color.FromRgb(243, 241, 245));
    }

    private void ApplyColor()
    {
        if (!TryParseHex(_hexBox.Text, out Color rgb)) return;
        _selectedColor = Color.FromArgb((byte)Math.Round(_alphaSlider.Value), rgb.R, rgb.G, rgb.B);
        SelectedColorText = _selectedColor.A == 255
            ? Hex(_selectedColor)
            : string.Join(',', _selectedColor.R, _selectedColor.G, _selectedColor.B,
                (_selectedColor.A / 255d).ToString("0.###", CultureInfo.InvariantCulture));
        DialogResult = true;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Brush("#B8B5BE"),
        FontSize = 12
    };

    private static Slider AddLabeledSlider(Panel parent, string label, double maximum, double value, double tickFrequency)
    {
        parent.Children.Add(Label(label));
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = maximum,
            Value = Math.Clamp(value, 0, maximum),
            TickFrequency = tickFrequency,
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 0, 0, 7)
        };
        parent.Children.Add(slider);
        return slider;
    }

    private Button MakeButton(string text, string styleKey) => new()
    {
        Content = text,
        Style = (Style)FindResource(styleKey),
        MinWidth = 96,
        MinHeight = 36,
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string AlphaPercent(byte alpha) => $"{Math.Round(alpha * 100d / 255d)}%";

    private static (double Hue, double Saturation, double Brightness) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double maximum = Math.Max(r, Math.Max(g, b));
        double minimum = Math.Min(r, Math.Min(g, b));
        double delta = maximum - minimum;
        double hue = delta == 0 ? 0
            : maximum == r ? 60 * (((g - b) / delta) % 6)
            : maximum == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        double saturation = maximum == 0 ? 0 : delta / maximum;
        return (hue, saturation, maximum);
    }

    private static Color FromHsv(double hue, double saturation, double brightness, byte alpha)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        brightness = Math.Clamp(brightness, 0, 1);
        double chroma = brightness * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        double match = brightness - chroma;
        (double r, double g, double b) channels = hue switch
        {
            < 60 => (chroma, x, 0),
            < 120 => (x, chroma, 0),
            < 180 => (0, chroma, x),
            < 240 => (0, x, chroma),
            < 300 => (x, 0, chroma),
            _ => (chroma, 0, x)
        };
        return Color.FromArgb(alpha, ToByte(channels.r + match), ToByte(channels.g + match), ToByte(channels.b + match));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static bool TryParseHex(string value, out Color color)
    {
        color = Colors.White;
        string hex = value.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6
            || !byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string color = value.Trim();
        if (color.StartsWith('#'))
        {
            try { if (ColorConverter.ConvertFromString(color) is Color html) return html; }
            catch (FormatException) { }
            catch (NotSupportedException) { }
        }
        string[] parts = color.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 3
            && byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
        {
            double alpha = 1;
            if (parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                alpha = parsed > 1 ? parsed / 255d : parsed;
            return Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), r, g, b);
        }
        try { return ColorConverter.ConvertFromString(color) is Color named ? named : fallback; }
        catch (FormatException) { return fallback; }
        catch (NotSupportedException) { return fallback; }
    }
}
