using System.Collections.ObjectModel;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawingImage = System.Drawing.Image;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingSize = System.Drawing.Size;
using RotateFlipType = System.Drawing.RotateFlipType;

namespace JungleLeopardPoc;

/// <summary>Native XAML theme studio with an independent local library and the vendor's public catalog.</summary>
public partial class CoverStudioWindow : System.Windows.Controls.UserControl
{
    private static readonly HttpClient Http = CreateHttpClient();
    // The vendor's theme API is hosted on www.htssl.com. cloudapi.htssl.com is
    // used by separate APIs and does not serve the /api/ht/theme routes.
    private static readonly Uri CatalogBaseUrl = new("https://www.htssl.com/");
    private static readonly Uri CatalogEndpoint = new(CatalogBaseUrl, "api/ht/theme/getAllThemes");
    private const int CatalogPageSize = 20;
    private const long MaxCatalogPackageBytes = 100L * 1024 * 1024;
    private readonly Action<ThemePackage, bool> _applyTheme;
    private readonly Func<int> _rotation;
    private readonly Func<(int Rotation, int Brightness)> _getScreenOptions;
    private readonly Action<int, int> _setScreenOptions;
    internal event Action<ThemePackage, bool>? EditRequested;
    private readonly List<ThemeItem> _libraryItems = [];
    private readonly ObservableCollection<ThemeItem> _localThemes = [];
    private readonly ObservableCollection<ThemeItem> _catalogThemes = [];
    private readonly DispatcherTimer _livePreviewTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000d / 30d) };
    private readonly object _previewRendererGate = new();
    private ThemeItem? _selectedLocal;
    private ThemeItem? _selectedCatalog;
    private HtThemeRenderer? _previewRenderer;
    private WriteableBitmap? _livePreviewBitmap;
    private byte[]? _livePreviewPixels;
    private bool _isCatalogView;
    private bool _catalogLoaded;
    private bool _catalogSearchInProgress;
    private bool _catalogHasMore;
    private bool _catalogRefreshQueued;
    private int _catalogPage = 1;
    private int? _catalogTotal;
    private bool _previewRenderInProgress;
    private int _previewRequest;
    private int _previewGeneration;
    private bool _syncingScreenOptions;

    internal CoverStudioWindow(Action<ThemePackage, bool> applyTheme, Func<int> rotation,
        Func<(int Rotation, int Brightness)> getScreenOptions,
        Action<int, int> setScreenOptions)
    {
        InitializeComponent();
        PreviewTitle.Text = AppLanguage.Translate(PreviewTitle.Text);
        _applyTheme = applyTheme;
        _rotation = rotation;
        _getScreenOptions = getScreenOptions;
        _setScreenOptions = setScreenOptions;
        _livePreviewTimer.Tick += async (_, _) => await RenderLivePreviewAsync();
        Loaded += (_, _) =>
        {
            SyncScreenOptionsFromController();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshPanelOrientation));
            UpdateLivePreviewTimer();
        };
        IsVisibleChanged += (_, _) => UpdateLivePreviewTimer();
        Unloaded += (_, _) => StopLivePreview();
        LocalList.ItemsSource = _localThemes;
        CatalogList.ItemsSource = _catalogThemes;
        RefreshLibrary();
        SyncScreenOptionsFromController();
        UpdateOrientation();
        AppLanguage.Apply(this);
    }

    private void SyncScreenOptionsFromController()
    {
        var options = _getScreenOptions();
        _syncingScreenOptions = true;
        try
        {
            RotationSelector.SelectedIndex = Math.Clamp(options.Rotation, 0, 3);
            BrightnessSelector.Value = Math.Clamp(options.Brightness, 0, 100);
            BrightnessText.Text = $"{Math.Clamp(options.Brightness, 0, 100)}%";
        }
        finally { _syncingScreenOptions = false; }
    }

    private void ScreenOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingScreenOptions || RotationSelector.SelectedIndex < 0) return;
        int previousRotation = _rotation();
        int brightness = (int)Math.Round(BrightnessSelector.Value);
        _setScreenOptions(RotationSelector.SelectedIndex, brightness);
        BrightnessText.Text = $"{brightness}%";
        if (previousRotation != _rotation())
        {
            if (_isCatalogView)
            {
                UpdateOrientation();
                if (_selectedCatalog?.ThumbnailBytes is byte[] catalogPreview)
                    SetPreview(catalogPreview);
            }
            else
            {
                RefreshPanelOrientation();
            }
        }
    }

    private static string LibraryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JungleLeopardStudio", "Themes");
    private static string InstalledLibraryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jungle_Leopard_Display", "themes");

    internal void RefreshPanelOrientation()
    {
        UpdateOrientation();
        if (_isCatalogView)
        {
            if (_catalogSearchInProgress)
            {
                _catalogRefreshQueued = true;
                return;
            }
            _catalogLoaded = false;
            _ = SearchCatalogAsync(resetPage: true);
            return;
        }

        if (_selectedLocal?.Package is not ThemePackage package) return;
        if (_previewRenderer is null)
        {
            if (!StartLivePreview(package))
                SetPreview(package.PreviewBytes);
        }
        else
            _ = RenderLivePreviewAsync();
    }

    private void LibraryTabClicked(object sender, RoutedEventArgs e) => ShowLibrary();

    private void CatalogTabClicked(object sender, RoutedEventArgs e)
    {
        ShowCatalog();
        if (!_catalogLoaded && _catalogThemes.Count == 0)
            _ = SearchCatalogAsync();
    }

    private void ShowLibrary()
    {
        _isCatalogView = false;
        _catalogRefreshQueued = false;
        LibraryTab.IsChecked = true;
        CatalogTab.IsChecked = false;
        LocalSearch.Visibility = Visibility.Visible;
        CatalogSearch.Visibility = Visibility.Collapsed;
        CatalogPager.Visibility = Visibility.Collapsed;
        LocalList.Visibility = Visibility.Visible;
        CatalogList.Visibility = Visibility.Collapsed;
        CatalogDownloadArea.Visibility = Visibility.Collapsed;
        LocalEmpty.Visibility = _localThemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogEmpty.Visibility = Visibility.Collapsed;
        StatusText.Text = "Minha biblioteca • temas locais e pacotes importados.";
        SelectLocal(LocalList.SelectedItem as ThemeItem ?? _selectedLocal);
    }

    private void ShowCatalog()
    {
        _isCatalogView = true;
        LibraryTab.IsChecked = false;
        CatalogTab.IsChecked = true;
        LocalSearch.Visibility = Visibility.Collapsed;
        CatalogSearch.Visibility = Visibility.Visible;
        CatalogPager.Visibility = Visibility.Visible;
        LocalList.Visibility = Visibility.Collapsed;
        CatalogList.Visibility = Visibility.Visible;
        LocalEmpty.Visibility = Visibility.Collapsed;
        CatalogDownloadArea.Visibility = _selectedCatalog is null ? Visibility.Collapsed : Visibility.Visible;
        CatalogEmpty.Visibility = _catalogThemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = "Catálogo oficial • explore e instale temas na biblioteca local.";
        if (_selectedCatalog is not null)
            SelectCatalog(_selectedCatalog);
        else
            ClearSelection();
    }

    private void LocalSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => FilterLibrary();

    private void RefreshLibrary(string? selectPath = null)
    {
        Directory.CreateDirectory(LibraryDirectory);
        _libraryItems.Clear();
        _libraryItems.AddRange(LoadLibraryItems());
        FilterLibrary(selectPath);
    }

    private void FilterLibrary(string? selectPath = null)
    {
        string query = LocalSearch.Text.Trim();
        _localThemes.Clear();
        foreach (ThemeItem item in _libraryItems)
            if (query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                _localThemes.Add(item);
        LocalEmpty.Text = _localThemes.Count == 0
            ? "Sua biblioteca está vazia.\nImporte um .ht, crie um tema ou confira os temas instalados do Jungle Leopard Display."
            : "Nenhum tema local corresponde a essa busca.";
        LocalEmpty.Visibility = _localThemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectPath is not null)
            LocalList.SelectedItem = _localThemes.FirstOrDefault(item => item.Path == selectPath);
        if (LocalList.SelectedItem is null && _localThemes.Count > 0)
            LocalList.SelectedIndex = 0;
    }
    private static IEnumerable<ThemeItem> LoadLibraryItems()
    {
        Directory.CreateDirectory(LibraryDirectory);
        foreach (string path in Directory.EnumerateFiles(LibraryDirectory, "*.ht").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            ThemePackage package;
            try { package = ThemePackage.Read(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException) { continue; }
            yield return new ThemeItem(package.Name, Path.GetFileName(path), path, package,
                thumbnail: DecodeImage(package.PreviewBytes), thumbnailBytes: package.PreviewBytes);
        }

        if (!Directory.Exists(InstalledLibraryDirectory)) yield break;
        foreach (string path in Directory.EnumerateFiles(InstalledLibraryDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            (string Name, byte[] PreviewBytes)? installed = null;
            try { installed = ThemePackage.ReadInstalledThemePreview(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException) { }
            if (installed is not { } preview) continue;
            yield return new ThemeItem(preview.Name, "Instalado no Jungle Leopard Display", path, null,
                thumbnail: DecodeImage(preview.PreviewBytes), thumbnailBytes: preview.PreviewBytes, isInstalledTheme: true);
        }
    }
    private void LocalSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isCatalogView)
            SelectLocal(LocalList.SelectedItem as ThemeItem);
    }

    private void SelectLocal(ThemeItem? item)
    {
        StopLivePreview();
        _selectedLocal = item;
        _selectedCatalog = null;
        DownloadButton.Visibility = Visibility.Collapsed;
        string? loadError = null;
        if (item?.IsInstalledTheme == true && item.Package is null && item.Path is string installedJson)
        {
            try { item.Package = ThemePackage.ImportInstalledTheme(installedJson); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException)
            {
                loadError = ex.Message;
            }
        }

        ThemePackage? package = item?.Package;
        EditButton.IsEnabled = package?.Definition.ValueKind == JsonValueKind.Object
            && package.Definition.TryGetProperty("children", out _);
        ExportButton.IsEnabled = package is not null;
        DuplicateButton.IsEnabled = package is not null;
        DeleteButton.IsEnabled = item is not null && !item.IsInstalledTheme;
        ApplyButton.IsEnabled = package is not null;
        if (item is null)
        {
            if (PreviewImage.Source is null)
                PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        PreviewTitle.Text = item.Name;
        if (package is null)
        {
            if (item.ThumbnailBytes is not null) SetPreview(item.ThumbnailBytes);
            PreviewDetails.Text = "Tema instalado no Jungle Leopard Display; os arquivos originais são somente leitura.";
            StatusText.Text = "Não foi possível carregar os arquivos do tema: " + (loadError ?? "prévia indisponível");
            return;
        }

        SetPreview(package.PreviewBytes);
        bool livePreview = StartLivePreview(package);
        bool hasLiveData = ContainsLiveDataNode(package.Definition);
        string source = item.IsInstalledTheme ? "Tema instalado" : "Pacote .ht";
        PreviewDetails.Text = livePreview
            ? $"{source} • {Path.GetFileName(item.Path)} • {(hasLiveData ? "indicadores dinâmicos" : "cena ao vivo")}" 
                + (hasLiveData ? " • sensores sem leitura aparecem como —" : string.Empty)
            : $"{source} • {Path.GetFileName(item.Path)} • somente imagem estática; este arquivo não contém uma cena .ht.";
        StatusText.Text = livePreview
            ? "Prévia pronta. Temas instalados são abertos sem alterar os arquivos originais."
            : "Este tema contém apenas uma imagem de prévia; ele não traz camadas com mostradores para renderizar.";
    }
    private void SearchClicked(object sender, RoutedEventArgs e) => _ = SearchCatalogAsync(resetPage: true);

    private void FullScreenFilterClicked(object sender, RoutedEventArgs e) => _ = SearchCatalogAsync(resetPage: true);

    private void CatalogSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SearchCatalogAsync(resetPage: true);
        }
    }

    private async void CatalogPreviousClicked(object sender, RoutedEventArgs e)
    {
        if (_catalogSearchInProgress || _catalogPage <= 1) return;
        _catalogPage--;
        await SearchCatalogAsync();
    }

    private async void CatalogNextClicked(object sender, RoutedEventArgs e)
    {
        if (_catalogSearchInProgress || !_catalogHasMore) return;
        _catalogPage++;
        await SearchCatalogAsync();
    }

    private async Task SearchCatalogAsync(bool resetPage = false)
    {
        if (_catalogSearchInProgress) return;
        if (resetPage) _catalogPage = 1;
        _catalogSearchInProgress = true;
        _selectedCatalog = null;
        _selectedLocal = null;
        ApplyButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        CatalogDownloadArea.Visibility = Visibility.Collapsed;
        _catalogThemes.Clear();
        _catalogTotal = null;
        _catalogHasMore = false;
            CatalogEmpty.Text = "Buscando temas no catálogo oficial…";
        CatalogEmpty.Visibility = Visibility.Visible;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewTitle.Text = AppLanguage.Translate("Catálogo online");
        PreviewDetails.Text = "Pesquisando temas compatíveis com o Pro Flow 360.";
        SearchButtonState(false);
        StatusText.Text = "Conectando ao catálogo oficial…";
        string? responseBody = null;
        try
        {
            var values = new List<KeyValuePair<string, string>>
            {
                new("current", _catalogPage.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("pageSize", CatalogPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("fullScreen", (FullScreenFilter.IsChecked == true).ToString().ToLowerInvariant())
            };
            if (FullScreenFilter.IsChecked == true)
            {
                values.Add(new("deviceWidth", "960"));
                values.Add(new("deviceHeight", "480"));
                values.Add(new("rotate", (-Math.Clamp(_rotation(), 0, 3) * 90).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            values.AddRange([
                new("themeType", "@lcd"),
                new("visibleBrandIds[]", "2")
            ]);
            string search = CatalogSearchBox.Text.Trim();
            if (search.Length > 0)
                values.Add(new("name", search));
            string query = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            using var searchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using HttpResponseMessage response = await Http.GetAsync(
                new Uri(CatalogEndpoint + "?" + query), searchTimeout.Token);
            string json = await response.Content.ReadAsStringAsync(searchTimeout.Token);
            responseBody = json;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ": " + SummarizeResponse(json),
                    null, response.StatusCode);
            using JsonDocument document = JsonDocument.Parse(json);
            IReadOnlyList<CatalogTheme> results = ParseCatalog(document.RootElement);
            _catalogTotal = ReadCatalogTotal(document.RootElement);
            _catalogHasMore = _catalogTotal is int total
                ? _catalogPage * CatalogPageSize < total
                : results.Count >= CatalogPageSize;
            foreach (CatalogTheme result in results)
            {
                var item = new ThemeItem(result.Name, result.FileName ?? "Tema oficial", null, null, result.Id, result.PackageUrl, result.FileName, result.CoverUrl);
                _catalogThemes.Add(item);
                if (result.CoverUrl is not null)
                    _ = LoadCatalogThumbnailAsync(item);
            }
            _catalogLoaded = true;
            if (_catalogThemes.Count == 0)
            {
                CatalogEmpty.Text = GetCatalogEmptyMessage(document.RootElement);
                StatusText.Text = "A API oficial respondeu, mas não retornou pacotes para este filtro.";
                return;
            }
            CatalogEmpty.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Encontrados {_catalogThemes.Count} temas oficiais. Selecione um para visualizar e instalar.";
            CatalogList.SelectedIndex = 0;
        }
        catch (HttpRequestException ex)
        {
            CatalogEmpty.Text = "Não foi possível conectar ao catálogo. Verifique sua conexão e tente novamente.";
            StatusText.Text = $"Falha HTTP no catálogo: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            CatalogEmpty.Text = "O catálogo demorou para responder. Tente novamente.";
            StatusText.Text = "A consulta do catálogo expirou após 25 segundos.";
        }
        catch (JsonException ex)
        {
            CatalogEmpty.Text = "O catálogo respondeu em um formato que o Studio não reconheceu.";
            StatusText.Text = $"Resposta JSON inválida: {ex.Message} • {SummarizeResponse(responseBody ?? string.Empty)}";
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or NotSupportedException)
        {
            CatalogEmpty.Text = "O catálogo retornou um endereço ou formato que o Studio não reconheceu.";
            StatusText.Text = "Resposta incompatível: " + ex.Message;
        }
        finally
        {
            _catalogSearchInProgress = false;
            SearchButtonState(true);
            UpdateCatalogPagination();
            CatalogEmpty.Visibility = _catalogThemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_catalogRefreshQueued && _isCatalogView)
            {
                _catalogRefreshQueued = false;
                _catalogLoaded = false;
                _ = SearchCatalogAsync(resetPage: true);
            }
        }
    }

    private void SearchButtonState(bool enabled)
    {
        SearchButton.IsEnabled = enabled;
        CatalogSearchBox.IsEnabled = enabled;
        FullScreenFilter.IsEnabled = enabled;
        CatalogPreviousButton.IsEnabled = enabled && _catalogPage > 1;
        CatalogNextButton.IsEnabled = enabled && _catalogHasMore;
    }

    private void UpdateCatalogPagination()
    {
        int? lastPage = _catalogTotal is int total
            ? Math.Max(1, (int)Math.Ceiling(total / (double)CatalogPageSize))
            : null;
        CatalogPageLabel.Text = lastPage is int pages
            ? $"Página {_catalogPage} de {pages} · {_catalogTotal} temas"
            : $"Página {_catalogPage}";
        CatalogPreviousButton.IsEnabled = !_catalogSearchInProgress && _catalogPage > 1;
        CatalogNextButton.IsEnabled = !_catalogSearchInProgress && _catalogHasMore;
    }

    private async Task LoadCatalogThumbnailAsync(ThemeItem item)
    {
        try
        {
            using var thumbnailTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            byte[] bytes = await Http.GetByteArrayAsync(ResolveUrl(item.CoverUrl!), thumbnailTimeout.Token);
            BitmapImage? image = DecodeImage(bytes);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_catalogThemes.Contains(item)) return;
                item.Thumbnail = image;
                item.ThumbnailBytes = bytes;
                CatalogList.Items.Refresh();
                if (ReferenceEquals(_selectedCatalog, item))
                    SetPreview(bytes);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or ArgumentException or InvalidDataException) { }
    }

    private void CatalogSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isCatalogView)
            SelectCatalog(CatalogList.SelectedItem as ThemeItem);
    }

    private void SelectCatalog(ThemeItem? item)
    {
        StopLivePreview();
        _selectedCatalog = item;
        _selectedLocal = null;
        EditButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        DuplicateButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        CatalogDownloadArea.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadButton.IsEnabled = item?.PackageUrl is not null;
        CatalogDownloadProgress.Visibility = Visibility.Collapsed;
        CatalogDownloadStatus.Visibility = Visibility.Collapsed;
        if (item is null) return;
        PreviewTitle.Text = item.Name;
        PreviewDetails.Text = "Tema do catálogo oficial • baixe para instalar na biblioteca local.";
        if (item.ThumbnailBytes is not null)
        {
            SetPreview(item.ThumbnailBytes);
        }
        else
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
        StatusText.Text = item.PackageUrl is null
            ? "Este item não publicou um arquivo de pacote para download."
            : "O pacote será salvo na biblioteca independente do Studio.";
    }

    private async void DownloadClicked(object sender, RoutedEventArgs e)
    {
        ThemeItem? item = _selectedCatalog;
        if (item?.PackageUrl is null) return;
        DownloadButton.IsEnabled = false;
        CatalogList.IsEnabled = false;
        SearchButtonState(false);
        CatalogDownloadArea.Visibility = Visibility.Visible;
        CatalogDownloadProgress.Visibility = Visibility.Visible;
        CatalogDownloadProgress.Value = 0;
        CatalogDownloadProgress.IsIndeterminate = true;
        CatalogDownloadStatus.Visibility = Visibility.Visible;
        CatalogDownloadStatus.Text = "Conectando ao servidor de temas…";
        StatusText.Text = $"Baixando “{item.Name}”…";
        try
        {
            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using HttpResponseMessage response = await Http.GetAsync(
                ResolveUrl(item.PackageUrl), HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxCatalogPackageBytes)
                throw new InvalidDataException("O pacote excede o limite de 100 MB do Studio.");

            await using Stream input = await response.Content.ReadAsStreamAsync(downloadTimeout.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[64 * 1024];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), downloadTimeout.Token)) > 0)
            {
                received += count;
                if (received > MaxCatalogPackageBytes)
                    throw new InvalidDataException("O pacote excede o limite de 100 MB do Studio.");
                await output.WriteAsync(buffer.AsMemory(0, count), downloadTimeout.Token);
                if (contentLength is > 0)
                {
                    CatalogDownloadProgress.IsIndeterminate = false;
                    CatalogDownloadProgress.Value = Math.Clamp(received * 100d / contentLength.Value, 0d, 100d);
                    CatalogDownloadStatus.Text = $"Baixando pacote… {CatalogDownloadProgress.Value:0}%";
                }
                else
                {
                    CatalogDownloadStatus.Text = $"Baixando pacote… {received / 1024:N0} KB";
                }
            }
            byte[] bytes = output.ToArray();
            ThemePackage package = ThemePackage.Read(bytes, item.Name);
            Directory.CreateDirectory(LibraryDirectory);
            string path = Path.Combine(LibraryDirectory, MakeUniqueName(item.FileName ?? item.Name));
            await File.WriteAllBytesAsync(path, bytes, downloadTimeout.Token);
            RefreshLibrary(path);
            CatalogDownloadProgress.IsIndeterminate = false;
            CatalogDownloadProgress.Value = 100;
            CatalogDownloadStatus.Text = "Download concluído e pacote validado.";
            StatusText.Text = $"“{package.Name}” foi instalada na biblioteca local.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or JsonException or ArgumentException)
        {
            CatalogDownloadProgress.IsIndeterminate = false;
            CatalogDownloadStatus.Text = $"Falha no download ou na validação: {ex.Message}";
            StatusText.Text = $"Não foi possível instalar o tema: {ex.Message}";
        }
        finally
        {
            CatalogList.IsEnabled = true;
            SearchButtonState(true);
            DownloadButton.IsEnabled = _selectedCatalog?.PackageUrl is not null;
        }
    }

    private void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = AppLanguage.Translate("Importar tema Jungle Leopard"),
            Filter = AppLanguage.Translate("Temas Jungle Leopard (*.ht;*.zip;*.json)|*.ht;*.zip;*.json|Pacotes .ht (*.ht)|*.ht|Arquivos ZIP (*.zip)|*.zip|Temas instalados (*.json)|*.json"),
            CheckFileExists = true,
            Multiselect = false
        };
        StudioFileDialogDefaults.Configure(dialog);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            bool installedThemeJson = Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase);
            ThemePackage package = installedThemeJson
                ? ThemePackage.ImportInstalledTheme(dialog.FileName)
                : ThemePackage.Read(dialog.FileName);
            byte[] packageBytes = installedThemeJson ? package.ToBytes() : File.ReadAllBytes(dialog.FileName);
            Directory.CreateDirectory(LibraryDirectory);
            string target = Path.Combine(LibraryDirectory, MakeUniqueName(package.Name));
            File.WriteAllBytes(target, packageBytes);
            RefreshLibrary(target);
            ShowLibrary();
            StatusText.Text = installedThemeJson
                ? $"“{package.Name}” e seus arquivos de mídia foram empacotados em um .ht independente."
                : $"“{package.Name}” foi importada. O pacote original foi preservado.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException)
        {
            AppMessageBox.Show(Window.GetWindow(this), ex.Message,
                "Tema não reconhecido", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CreateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] emptyPackage = ThemePackage.CreateBlank("Novo tema .ht");
            ThemePackage package = ThemePackage.Read(emptyPackage, "Novo tema .ht");
            EditRequested?.Invoke(package, true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or ExternalException)
        {
            AppMessageBox.Show(Window.GetWindow(this), ex.Message,
                "Não foi possível criar o tema", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal void SaveEditedPackage(byte[] bytes, string name, bool isNew)
    {
        try
        {
            ThemePackage savedPackage = ThemePackage.Read(bytes, name);
            Directory.CreateDirectory(LibraryDirectory);
            string uniqueName = isNew ? name : name + " (editado)";
            string path = Path.Combine(LibraryDirectory, MakeUniqueName(uniqueName));
            File.WriteAllBytes(path, bytes);
            RefreshLibrary(path);
            ShowLibrary();
            StatusText.Text = isNew
                ? $"“{savedPackage.Name}” foi criada como pacote .ht editável."
                : "Tema salvo como um novo pacote .ht; o original permaneceu na biblioteca.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or ExternalException or JsonException)
        {
            AppMessageBox.Show(Window.GetWindow(this), ex.Message,
                "Não foi possível salvar o tema", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedLocal?.Package is not ThemePackage package) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = AppLanguage.Translate("Exportar pacote Jungle Leopard"),
            Filter = AppLanguage.Translate("Pacote Jungle Leopard (*.ht)|*.ht"),
            FileName = Path.GetFileNameWithoutExtension(_selectedLocal.Path ?? package.Name) + ".ht",
            AddExtension = true,
            DefaultExt = "ht"
        };
        StudioFileDialogDefaults.Configure(dialog, _selectedLocal.Path);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, package.ToBytes());
            StatusText.Text = "Pacote .ht exportado.";
        }
        catch (IOException ex) { StatusText.Text = $"Falha ao exportar: {ex.Message}"; }
    }

    private void DuplicateClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedLocal?.Package is not ThemePackage package) return;
        try
        {
            Directory.CreateDirectory(LibraryDirectory);
            string copyName = $"Cópia de {package.Name}";
            string path = Path.Combine(LibraryDirectory, MakeUniqueName(copyName));
            File.WriteAllBytes(path, package.CreateNamedCopy(copyName));
            RefreshLibrary(path);
            ShowLibrary();
            StatusText.Text = $"“{package.Name}” foi duplicado. A cópia mantém os elementos do tema .ht.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException)
        {
            StatusText.Text = $"Não foi possível duplicar o tema: {ex.Message}";
        }
    }

    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedLocal is not { Path: string path, IsInstalledTheme: false } item) return;
        MessageBoxResult answer = AppMessageBox.Show(Window.GetWindow(this),
            $"Excluir “{item.Name}” da biblioteca local? Essa ação remove o arquivo .ht.",
            "Excluir tema", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(path);
            _selectedLocal = null;
            RefreshLibrary();
            ClearSelection();
            StatusText.Text = $"“{item.Name}” foi removido da biblioteca local.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Não foi possível excluir o tema: {ex.Message}";
        }
    }

    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedLocal?.Package is not ThemePackage package) return;
        _applyTheme(package, FitThemeToScreenToggle.IsChecked == true);
        StatusText.Text = "Tema .ht aplicado. Os elementos compatíveis são atualizados em tempo real no painel.";
    }

    private void EditThemeClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedLocal?.Package is not ThemePackage package) return;

        EditRequested?.Invoke(package, false);
    }

    private bool StartLivePreview(ThemePackage package)
    {
        bool hasScene = package.Definition.ValueKind == JsonValueKind.Object
            && ((package.Definition.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0)
                || package.Definition.TryGetProperty("bgMedia", out _)
                || package.Definition.TryGetProperty("bgConfig", out _));
        if (!hasScene) return false;

        lock (_previewRendererGate)
        {
            _previewRenderer = new HtThemeRenderer(package, FitThemeToScreenToggle.IsChecked == true);
            _previewGeneration++;
        }
        UpdateLivePreviewTimer();
        _ = RenderLivePreviewAsync();
        return true;
    }

    private void FitThemeToScreenClicked(object sender, RoutedEventArgs e)
    {
        PreviewImage.Stretch = FitThemeToScreenToggle.IsChecked == true ? Stretch.Fill : Stretch.Uniform;
        if (_selectedLocal?.Package is ThemePackage package)
        {
            StopLivePreview();
            if (!StartLivePreview(package))
                SetPreview(package.PreviewBytes);
        }
        else if (_selectedLocal?.ThumbnailBytes is byte[] localPreview)
        {
            SetPreview(localPreview);
        }
        else if (_selectedCatalog?.ThumbnailBytes is byte[] catalogPreview)
        {
            SetPreview(catalogPreview);
        }
    }

    private static bool ContainsLiveDataNode(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                if (ContainsLiveDataNode(child)) return true;
            return false;
        }
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (element.TryGetProperty("type", out JsonElement type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString()?.ToLowerInvariant() is "data" or "progress" or "arc" or "linechart")
            return true;

        return element.TryGetProperty("children", out JsonElement children)
            && ContainsLiveDataNode(children);
    }

    private async Task RenderLivePreviewAsync()
    {
        HtThemeRenderer? renderer = _previewRenderer;
        if (!IsVisible || renderer is null || _previewRenderInProgress || _selectedLocal is null || _isCatalogView)
            return;

        _previewRenderInProgress = true;
        int generation = _previewGeneration;
        bool portrait = _rotation() is 1 or 3;
        var outputSize = portrait ? new DrawingSize(480, 960) : new DrawingSize(960, 480);
        try
        {
            byte[]? framePixels = await Task.Run(() =>
            {
                lock (_previewRendererGate)
                {
                    if (!ReferenceEquals(renderer, _previewRenderer)) return null;
                    using DrawingBitmap frame = renderer.Render(outputSize);
                    return CopyFramePixels(frame);
                }
            });
            if (framePixels is null || !IsVisible || generation != _previewGeneration || !ReferenceEquals(renderer, _previewRenderer))
                return;

            UpdateOrientation();
            int stride = GetBgr24Stride(outputSize.Width);
            if (_livePreviewBitmap is null
                || _livePreviewBitmap.PixelWidth != outputSize.Width
                || _livePreviewBitmap.PixelHeight != outputSize.Height)
                _livePreviewBitmap = new WriteableBitmap(outputSize.Width, outputSize.Height, 96, 96, PixelFormats.Bgr24, null);
            _livePreviewBitmap.WritePixels(new Int32Rect(0, 0, outputSize.Width, outputSize.Height), framePixels, stride, 0);
            PreviewImage.Source = _livePreviewBitmap;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException or IOException)
        {
            if (generation == _previewGeneration)
            {
                PreviewDetails.Text = $"Não foi possível renderizar os elementos do tema: {ex.Message}. A imagem de prévia continua disponível.";
            }
        }
        finally
        {
            _previewRenderInProgress = false;
        }
    }

    private byte[] CopyFramePixels(DrawingBitmap frame)
    {
        int stride = GetBgr24Stride(frame.Width);
        int byteCount = stride * frame.Height;
        if (_livePreviewPixels is null || _livePreviewPixels.Length != byteCount)
            _livePreviewPixels = new byte[byteCount];

        var bounds = new System.Drawing.Rectangle(0, 0, frame.Width, frame.Height);
        BitmapData data = frame.LockBits(bounds, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            int sourceStride = Math.Abs(data.Stride);
            if (sourceStride < stride)
                throw new InvalidOperationException("O quadro da prévia tem um stride de pixels inválido.");
            if (data.Stride == stride)
            {
                Marshal.Copy(data.Scan0, _livePreviewPixels, 0, byteCount);
                return _livePreviewPixels;
            }

            int destinationStride = stride;
            for (int row = 0; row < frame.Height; row++)
            {
                IntPtr source = IntPtr.Add(data.Scan0, row * data.Stride);
                int destinationRow = data.Stride < 0 ? frame.Height - 1 - row : row;
                Marshal.Copy(source, _livePreviewPixels, destinationRow * destinationStride, destinationStride);
            }
            return _livePreviewPixels;
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }

    private static int GetBgr24Stride(int width) => (width * 3 + 3) & ~3;

    private void UpdateLivePreviewTimer()
    {
        if (IsVisible && _previewRenderer is not null && _selectedLocal is not null && !_isCatalogView)
        {
            if (!_livePreviewTimer.IsEnabled)
            {
                _livePreviewTimer.Start();
                _ = RenderLivePreviewAsync();
            }
            return;
        }

        _livePreviewTimer.Stop();
    }

    private void StopLivePreview()
    {
        _livePreviewTimer.Stop();
        _previewGeneration++;
        lock (_previewRendererGate)
        {
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }
    }

    private void UpdateOrientation()
    {
        int angle = _rotation() switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        bool portrait = angle is 90 or 270;
        PreviewSurface.Width = portrait ? 480 : 960;
        PreviewSurface.Height = portrait ? 960 : 480;
        OrientationLabel.Text = portrait ? $"Painel 480 × 960  •  rotação {angle}°" : $"Painel 960 × 480  •  rotação {angle}°";
    }

    private void SetPreview(byte[] bytes, bool applyRotation = true)
    {
        int request = ++_previewRequest;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using DrawingImage original = DrawingImage.FromStream(stream);
            using var display = new DrawingBitmap(original);
            int angle = _rotation() switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
            if (applyRotation && angle != 0)
                display.RotateFlip(angle switch
                {
                    90 => RotateFlipType.Rotate90FlipNone,
                    180 => RotateFlipType.Rotate180FlipNone,
                    _ => RotateFlipType.Rotate270FlipNone
                });
            using var output = new MemoryStream();
            display.Save(output, ImageFormat.Png);
            if (request != _previewRequest) return;
            PreviewImage.Source = DecodeImage(output.ToArray());
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            UpdateOrientation();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewDetails.Text = $"A prévia não pôde ser decodificada: {ex.Message}";
        }
    }

    private void ClearSelection()
    {
        StopLivePreview();
        _selectedLocal = null;
        _selectedCatalog = null;
        EditButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        DuplicateButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewTitle.Text = AppLanguage.Translate(_isCatalogView ? "Catálogo online" : "Temas");
        PreviewDetails.Text = _isCatalogView
            ? "Pesquise, visualize e instale temas compatíveis."
            : "Importe um .ht, crie um tema ou explore o catálogo online.";
    }

    private static BitmapImage? DecodeImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException) { return null; }
    }

    private static IReadOnlyList<CatalogTheme> ParseCatalog(JsonElement root)
    {
        var found = new List<CatalogTheme>();
        Visit(root, found);
        return found.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(CatalogPageSize).ToArray();
    }

    private static int? ReadCatalogTotal(JsonElement root)
    {
        JsonElement data = GetProperty(root, "data", "result");
        JsonElement nestedData = GetProperty(data, "data", "result");
        foreach (JsonElement container in new[] { root, data, nestedData })
        {
            JsonElement value = GetProperty(container, "total", "totalCount", "totalElements", "recordsTotal");
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return Math.Max(0, number);
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out number))
                return Math.Max(0, number);
        }
        return null;
    }

    private static void Visit(JsonElement node, List<CatalogTheme> found)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            string? name = GetString(node, "name", "label", "themeName", "title");
            string? id = GetString(node, "themeId", "theme_id", "id");
            JsonElement package = GetProperty(node, "packageFile", "package", "file", "themeFile", "themePackage", "downloadFile");
            string? packageUrl = ReadUrl(package, "url", "downloadUrl", "download_url", "fileUrl", "file_url", "downloadLink", "download_link", "path", "filePath", "file_path")
                ?? GetString(node, "packageUrl", "package_url", "downloadUrl", "download_url", "fileUrl", "file_url", "downloadPath", "download_path", "filePath", "file_path");
            string? fileName = GetString(package, "fileName", "file_name", "filename", "name") ?? GetString(node, "fileName", "file_name", "filename");
            JsonElement cover = GetProperty(node, "coverPictureFile", "coverPicture", "cover", "previewFile", "preview", "thumbnail");
            string? coverUrl = ReadUrl(cover, "url", "src", "fileUrl", "file_url", "path")
                ?? GetString(node, "coverUrl", "cover_url", "previewUrl", "preview_url", "thumbnailUrl", "thumbnail_url");
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(packageUrl))
                found.Add(new CatalogTheme(id ?? packageUrl, name, packageUrl, fileName, coverUrl));
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (property.Name.Equals("packageFile", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("package", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("file", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("themeFile", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("themePackage", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("downloadFile", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("coverPictureFile", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("coverPicture", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("cover", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("previewFile", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("preview", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
                    continue;
                Visit(property.Value, found);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in node.EnumerateArray()) Visit(item, found);
        }
        else if (node.ValueKind == JsonValueKind.String && node.GetString() is string nestedJson
            && (nestedJson.TrimStart().StartsWith('{') || nestedJson.TrimStart().StartsWith('[')))
        {
            try
            {
                using JsonDocument nested = JsonDocument.Parse(nestedJson);
                Visit(nested.RootElement, found);
            }
            catch (JsonException) { }
        }
    }

    private static string? ReadUrl(JsonElement value, params string[] names)
    {
        string? candidate = value.ValueKind switch
        {
            JsonValueKind.Object => GetString(value, names),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();
    }

    private static JsonElement GetProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (JsonProperty property in element.EnumerateObject())
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        return default;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        JsonElement value = GetProperty(element, names);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string SummarizeResponse(string value)
    {
        string compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 280 ? compact : compact[..280] + "…";
    }

    private static string GetCatalogEmptyMessage(JsonElement root)
    {
        string? message = GetString(root, "message", "msg", "error");
        string? code = GetString(root, "code", "status", "statusCode");
        if (!string.IsNullOrWhiteSpace(message))
            return $"O catálogo respondeu: {message}" + (code is null ? string.Empty : $" (código {code}).");
        if (code is not null && code is not "0" and not "200")
            return $"O catálogo respondeu com código {code}, sem temas para exibir.";
        return "A consulta foi concluída, mas a API não retornou pacotes .ht compatíveis.";
    }

    private static Uri ResolveUrl(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
            return new Uri("https:" + value);
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute)) return absolute;
        return new Uri(CatalogBaseUrl, value.TrimStart('/'));
    }

    private static string MakeUniqueName(string value)
    {
        string name = Path.GetFileNameWithoutExtension(value);
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(name)) name = "Tema";
        string candidate = name + ".ht";
        int number = 2;
        while (File.Exists(Path.Combine(LibraryDirectory, candidate))) candidate = $"{name} ({number++}).ht";
        return candidate;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JungleLeopardScreenControl/1.0 (open-source theme client)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.htssl.com/");
        return client;
    }

    private sealed record CatalogTheme(string Id, string Name, string? PackageUrl, string? FileName, string? CoverUrl);

    private sealed class ThemeItem
    {
        public ThemeItem(string name, string subtitle, string? path, ThemePackage? package,
            string? id = null, string? packageUrl = null, string? fileName = null, string? coverUrl = null,
            BitmapImage? thumbnail = null, byte[]? thumbnailBytes = null, bool isInstalledTheme = false)
        {
            Name = name;
            Subtitle = subtitle;
            Path = path;
            Package = package;
            Id = id;
            PackageUrl = packageUrl;
            FileName = fileName;
            CoverUrl = coverUrl;
            Thumbnail = thumbnail;
            ThumbnailBytes = thumbnailBytes;
            IsInstalledTheme = isInstalledTheme;
        }

        public string Name { get; }
        public string Subtitle { get; }
        public string? Path { get; }
        public ThemePackage? Package { get; set; }
        public bool IsInstalledTheme { get; }
        public string? Id { get; }
        public string? PackageUrl { get; }
        public string? FileName { get; }
        public string? CoverUrl { get; }
        public BitmapImage? Thumbnail { get; set; }
        public byte[]? ThumbnailBytes { get; set; }
    }

}
