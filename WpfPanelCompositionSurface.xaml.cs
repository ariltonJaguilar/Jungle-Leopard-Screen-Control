using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;

namespace JungleLeopardPoc;

public partial class WpfPanelCompositionSurface : System.Windows.Controls.UserControl
{
    private PanelCanvasItem[] _items = [];
    private string? _selectedPort;
    private bool _freeLayout;
    private bool _dragging;
    private Point _dragOrigin;
    private int _dragStartX;
    private int _dragStartY;
    private double _scale = 1;

    internal event Action<string>? PanelSelected;
    internal event Action<string, int, int>? PanelMoved;

    public WpfPanelCompositionSurface()
    {
        InitializeComponent();
        AppLanguage.Apply(this);
    }

    internal void SetPanels(IEnumerable<PanelCanvasItem> panels, string? selectedPort, bool freeLayout)
    {
        _items = panels.ToArray();
        _selectedPort = selectedPort;
        _freeLayout = freeLayout;
        HintText.Text = _freeLayout
            ? "Arraste os painéis para posicioná-los"
            : "Selecione ‘Compor livremente’ para mover os painéis";
        RenderPanels();
    }

    private void PanelCanvasSizeChanged(object sender, SizeChangedEventArgs e) => RenderPanels();

    private void RenderPanels()
    {
        if (PanelCanvas is null) return;
        PanelCanvas.Children.Clear();
        double width = Math.Max(1, PanelCanvas.ActualWidth);
        double height = Math.Max(1, PanelCanvas.ActualHeight);
        DrawGrid(width, height);
        if (_items.Length == 0)
        {
            var empty = new TextBlock
            {
                Text = "Nenhum painel detectado",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 14,
                Width = width,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            Canvas.SetTop(empty, Math.Max(0, height / 2 - 12));
            PanelCanvas.Children.Add(empty);
            return;
        }

        int minX = _items.Min(item => item.X);
        int minY = _items.Min(item => item.Y);
        int maxX = _items.Max(item => item.X + item.Width);
        int maxY = _items.Max(item => item.Y + item.Height);
        double areaWidth = Math.Max(1, maxX - minX);
        double areaHeight = Math.Max(1, maxY - minY);
        const double margin = 28;
        _scale = Math.Clamp(Math.Min((width - margin * 2) / areaWidth, (height - margin * 2) / areaHeight), .02, 4);
        double drawnWidth = areaWidth * _scale;
        double drawnHeight = areaHeight * _scale;
        double originX = (width - drawnWidth) / 2 - minX * _scale;
        double originY = (height - drawnHeight) / 2 - minY * _scale;

        foreach (PanelCanvasItem item in _items)
        {
            bool selected = item.Port.Equals(_selectedPort, StringComparison.OrdinalIgnoreCase);
            double panelWidth = Math.Max(40, item.Width * _scale);
            double panelHeight = Math.Max(44, item.Height * _scale);
            var border = new Border
            {
                Width = panelWidth,
                Height = panelHeight,
                Background = (Brush)FindResource(selected ? "SelectionBrush" : "PanelBrush"),
                BorderBrush = (Brush)FindResource(selected ? "AccentBrush" : "PanelBorderBrush"),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(6),
                Tag = item.Port,
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = item.Port, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 3, 4, 4) },
                        new TextBlock { Text = $"{item.Width} × {item.Height} · {item.RotationDegrees}°", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 10, TextAlignment = TextAlignment.Center, Margin = new Thickness(3, 0, 3, 3) }
                    }
                }
            };
            Canvas.SetLeft(border, originX + item.X * _scale);
            Canvas.SetTop(border, originY + item.Y * _scale);
            PanelCanvas.Children.Add(border);
        }
    }

    private void DrawGrid(double width, double height)
    {
        Brush brush = (Brush)FindResource("PanelBorderBrush");
        for (double x = 20; x < width; x += 32)
            PanelCanvas.Children.Add(new Border { Width = 1, Height = height, Background = brush, Opacity = .18, IsHitTestVisible = false, RenderTransform = new TranslateTransform(x, 0) });
        for (double y = 20; y < height; y += 32)
            PanelCanvas.Children.Add(new Border { Width = width, Height = 1, Background = brush, Opacity = .18, IsHitTestVisible = false, RenderTransform = new TranslateTransform(0, y) });
    }

    private void PanelCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        DependencyObject? current = source;
        while (current is not null && current is not Border { Tag: string })
            current = VisualTreeHelper.GetParent(current);
        if (current is not Border { Tag: string port } border) return;
        PanelCanvasItem? item = _items.FirstOrDefault(panel => panel.Port.Equals(port, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        _selectedPort = port;
        PanelSelected?.Invoke(port);
        if (_freeLayout)
        {
            _dragging = true;
            _dragOrigin = e.GetPosition(PanelCanvas);
            _dragStartX = item.X;
            _dragStartY = item.Y;
            Mouse.Capture(PanelCanvas);
        }
        RenderPanels();
        e.Handled = true;
    }

    private void PanelCanvasMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || !_freeLayout || _selectedPort is null) return;
        Point point = e.GetPosition(PanelCanvas);
        int x = _dragStartX + (int)Math.Round((point.X - _dragOrigin.X) / _scale);
        int y = _dragStartY + (int)Math.Round((point.Y - _dragOrigin.Y) / _scale);
        _items = _items.Select(item => item.Port.Equals(_selectedPort, StringComparison.OrdinalIgnoreCase)
            ? item with { X = Math.Clamp(x, -1920, 1920), Y = Math.Clamp(y, -960, 960) }
            : item).ToArray();
        RenderPanels();
    }

    private void PanelCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _selectedPort is null) return;
        _dragging = false;
        Mouse.Capture(null);
        PanelCanvasItem? item = _items.FirstOrDefault(panel => panel.Port.Equals(_selectedPort, StringComparison.OrdinalIgnoreCase));
        if (item is not null) PanelMoved?.Invoke(item.Port, item.X, item.Y);
    }
}
