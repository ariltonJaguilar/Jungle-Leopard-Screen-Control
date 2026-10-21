using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JungleLeopardPoc;

public partial class LcdControlPage : System.Windows.Controls.UserControl
{
    private readonly LcdStudioBackend _controller;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private byte[]? _lastPreviewBytes;
    private int _lastPreviewWidth = -1;
    private int _lastPreviewHeight = -1;
    private bool _syncing;

    internal LcdControlPage(LcdStudioBackend controller)
    {
        InitializeComponent();
        _controller = controller;
        _refreshTimer.Tick += (_, _) => RefreshFromController();
        Loaded += (_, _) =>
        {
            SyncOptionsFromController();
            RefreshFromController();
            _refreshTimer.Start();
        };
        Unloaded += (_, _) => _refreshTimer.Stop();
        AppLanguage.Apply(this);
    }

    private void SyncOptionsFromController()
    {
        _syncing = true;
        try
        {
            RotationSelector.SelectedIndex = _controller.RotationIndex;
            BrightnessSelector.Value = _controller.Brightness;
            AutomaticSteamCover.IsChecked = _controller.AutomaticSteamCover;
        }
        finally { _syncing = false; }
    }

    private void RefreshFromController()
    {
        BrightnessText.Text = $"{_controller.Brightness}%";
        PanelDetection.Text = _controller.PanelDetectionText;
        PanelDetection.Foreground = (System.Windows.Media.Brush)FindResource(
            _controller.PanelConnectionHasError ? "ErrorBrush" : "TextBrush");
        int previewWidth = _controller.PreviewWidth;
        int previewHeight = _controller.PreviewHeight;
        if (previewWidth != _lastPreviewWidth || previewHeight != _lastPreviewHeight)
        {
            _lastPreviewWidth = previewWidth;
            _lastPreviewHeight = previewHeight;
            PreviewPanelSurface.Width = previewWidth;
            PreviewPanelSurface.Height = previewHeight;
        }
        PreviewSizeLabel.Text = $"{previewWidth} × {previewHeight} px";

        byte[]? current = _controller.CurrentPreviewBytes;
        if (ReferenceEquals(current, _lastPreviewBytes)) return;
        _lastPreviewBytes = current;
        if (current is null || current.Length == 0)
        {
            PanelPreview.Source = null;
            PanelPreview.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            using var stream = new MemoryStream(current, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            PanelPreview.Source = image;
            PanelPreview.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (NotSupportedException) { }
        catch (IOException) { }
    }

    private void ScreenOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || RotationSelector.SelectedIndex < 0) return;
        _controller.SetScreenOptions(
            RotationSelector.SelectedIndex,
            (int)Math.Round(BrightnessSelector.Value),
            AutomaticSteamCover.IsChecked == true);
        BrightnessText.Text = $"{(int)Math.Round(BrightnessSelector.Value)}%";
    }

}
