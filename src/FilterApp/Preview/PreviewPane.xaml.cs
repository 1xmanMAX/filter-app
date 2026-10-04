using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FilterApp.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FilterApp.Preview;

/// Shows the selected file as it really looks, as fast and light as possible:
/// PDF with PDFium (only the pages on screen), Word/Excel/PowerPoint/Outlook with Windows' preview handlers on a
/// background thread (a Word document shows its text at once while the real layout loads), video and audio with
/// Windows' player, images decoded at the size of the pane, text natively. The Edge engine is only a last resort.
public partial class PreviewPane : UserControl
{
    static readonly string WebDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilterApp", "WebView2");

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer _mediaClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly PreviewHandlerHost _handler = new();
    string? _requested;
    string? _path;
    int _version;
    WebView2? _web;
    Task<bool>? _webReady;
    bool _warming;
    bool _textDragReady, _docDragReady;
    bool _webShowing, _handlerShowing, _pdfShowing, _mediaShowing, _playing, _seeking;

    public PreviewPane()
    {
        InitializeComponent();
        HandlerSlot.Content = _handler;
        // Arrowing through a list fires a selection per step: only the one the user stops on is loaded.
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(_requested); };
        _mediaClock.Tick += (_, _) => UpdateMediaTime();
        // Hold the middle button (or the left one on images) and drag to move around.
        DragScroll.Attach(ImageScroll, leftButtonToo: true);
        DocView.IsVisibleChanged += (_, _) =>
        {
            if (_docDragReady || !DocView.IsVisible) return;
            DocView.ApplyTemplate();
            if (DocView.Template?.FindName("PART_ContentHost", DocView) is not ScrollViewer scroll) return;
            DragScroll.Attach(scroll, leftButtonToo: false);   // left button selects text
            _docDragReady = true;
        };
        TextView.IsVisibleChanged += (_, _) =>
        {
            if (_textDragReady || !TextView.IsVisible) return;
            TextView.ApplyTemplate();
            if (TextView.Template?.FindName("PART_ContentHost", TextView) is not ScrollViewer scroll) return;
            DragScroll.Attach(scroll, leftButtonToo: false);   // left button selects text
            _textDragReady = true;
        };
    }

    /// The file being shown, if any.
    public string? CurrentPath => _path;

    public void Show(string? path)
    {
        _requested = path;
        _debounce.Stop();
        _debounce.Start();
    }

    /// Lets go of the file being shown: a preview may keep it open, which would stop it from being moved.
    public async Task ReleaseAsync()
    {
        _debounce.Stop();
        _path = null;
        _version++;
        await HideAllAsync();
        ShowMessage("Selecciona un archivo para verlo aquí");
    }

    async void Load(string? path)
    {
        if (path is not null && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
        _path = path;
        int version = ++_version;
        bool Current() => version == _version;
        var clock = Stopwatch.StartNew();
        await HideAllAsync();
        if (!Current()) return;

        if (path is null)
        {
            ShowMessage("Selecciona un archivo para verlo aquí");
            return;
        }
        if (!File.Exists(path))
        {
            ShowMessage("El archivo ya no existe.");
            return;
        }
        ShowHeader(path);
        var kind = PreviewText.Classify(path);

        try
        {
            switch (kind)
            {
                case PreviewKind.Image:
                    int width = (int)Math.Clamp(Stage.ActualWidth * Dpi, 320, 3000);
                    var picture = await Task.Run(() => LoadImage(path, width));
                    if (!Current()) return;
                    ShowImage(picture);
                    Trace(kind, clock, path);
                    return;
                case PreviewKind.Text:
                    var text = await Task.Run(() => PreviewText.ReadText(path));
                    if (Current()) ShowText(text);
                    Trace(kind, clock, path);
                    return;
                case PreviewKind.Pdf:
                    PdfView.Visibility = Visibility.Visible;
                    _pdfShowing = true;
                    await PdfView.OpenAsync(path, Current);
                    Trace(kind, clock, path);
                    return;
                case PreviewKind.Media:
                    ShowMedia(path);
                    return;
                case PreviewKind.Docx:
                    // The app's own reading appears at once; Word's exact previewer replaces it when (and if) it can.
                    await ShowDocumentAsync(path, Current);
                    if (!Current()) return;
                    Trace(kind, clock, path, "documento");
                    if (await ShowHandlerAsync(path, Current)) Trace(kind, clock, path, "formato");
                    return;
                case PreviewKind.Web:
                    if (await ShowWebAsync(path, Current) || !Current()) return;
                    break;
                default:
                    if (await ShowHandlerAsync(path, Current))
                    {
                        Trace(kind, clock, path, "formato");
                        return;
                    }
                    if (!Current()) return;
                    break;
            }
        }
        catch (Exception e)
        {
            // Unreadable, unsupported codec, damaged file: the thumbnail below is always possible.
            System.Diagnostics.Trace.WriteLine($"preview error {Path.GetFileName(path)}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            if (!Current()) return;
            await HideAllAsync();
        }
        if (Current()) await ShowFallbackAsync(path, Current);
    }

    static void Trace(PreviewKind kind, Stopwatch clock, string path, string what = "") =>
        System.Diagnostics.Trace.WriteLine($"preview {kind} {what} {clock.ElapsedMilliseconds} ms {Path.GetFileName(path)}");

    double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void ShowHeader(string path)
    {
        Title.Text = Path.GetFileName(path);
        Title.ToolTip = path;
        try
        {
            var info = new FileInfo(path);
            var type = info.Extension.TrimStart('.').ToUpperInvariant();
            Details.Text = $"{PreviewText.Size(info.Length)} · {(type.Length > 0 ? type : "archivo")} · {info.LastWriteTime:d MMM yyyy, HH:mm}";
        }
        catch (IOException) { Details.Text = ""; }
        OpenButton.IsEnabled = true;
    }

    void ShowMessage(string text)
    {
        Title.Text = "VISTA PREVIA";
        Title.ToolTip = null;
        Details.Text = "";
        OpenButton.IsEnabled = false;
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    void ShowText(string text)
    {
        TextView.Text = text;
        TextView.ScrollToHome();
        TextView.Visibility = Visibility.Visible;
    }

    /// A Word document as the app reads it: with headings, styles, lists, tables and pictures. If even that
    /// fails (damaged file), its plain text.
    async Task ShowDocumentAsync(string path, Func<bool> current)
    {
        try
        {
            var blocks = await Task.Run(() => DocxDocument.Read(path));
            if (!current()) return;
            DocView.Document = DocxDocument.Build(blocks);
            DocView.Visibility = Visibility.Visible;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            var text = await Task.Run(() => PreviewText.ReadDocx(path));
            if (current()) ShowText(text);
        }
    }

    async Task ShowFallbackAsync(string path, Func<bool> current)
    {
        Fallback.Visibility = Visibility.Visible;
        var thumb = await ShellThumbnail.GetAsync(path, 256);
        if (current()) Thumb.Source = thumb;
    }

    /// Hides everything and lets go of every file. Awaiting it guarantees no preview keeps a file open.
    async Task HideAllAsync()
    {
        Message.Visibility = Visibility.Collapsed;
        Loading.Visibility = Visibility.Collapsed;
        ImageView.Source = null;
        ImageScroll.Visibility = Visibility.Collapsed;
        TextView.Text = "";
        TextView.Visibility = Visibility.Collapsed;
        DocView.Document = null;
        DocView.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Collapsed;
        Thumb.Source = null;
        if (_mediaShowing) StopMedia();
        if (_webShowing && _web?.CoreWebView2 is { } core)
        {
            core.Navigate("about:blank");   // stops playback and closes the file
            _webShowing = false;
        }
        WebSlot.Visibility = Visibility.Collapsed;
        if (_pdfShowing)
        {
            _pdfShowing = false;
            PdfView.Visibility = Visibility.Collapsed;
            await PdfView.CloseAsync();
        }
        if (_handlerShowing)
        {
            _handlerShowing = false;
            HandlerSlot.Visibility = Visibility.Hidden;
            await _handler.UnloadAsync();
        }
    }

    // ---- Images ----

    /// A decoded image and the full size of the original, upright, in pixels.
    sealed record Picture(BitmapSource Image, int Width, int Height);

    Picture? _picture;
    /// 1 = fitted to the pane; more = zoomed in.
    double _imageZoom = 1;
    bool _imageUpgrading;

    void ShowImage(Picture picture)
    {
        _picture = picture;
        _imageZoom = 1;
        _imageUpgrading = false;
        ImageView.Source = picture.Image;
        ImageScroll.Visibility = Visibility.Visible;
        LayoutImage();
    }

    /// Scale that fits the whole image in the pane; never above 1 (small images are not blown up).
    double FitScale
    {
        get
        {
            if (_picture is null) return 1;
            double dpi = Dpi;
            return Math.Min(1, Math.Min((ImageScroll.ActualWidth - 16) * dpi / _picture.Width,
                                        (ImageScroll.ActualHeight - 16) * dpi / _picture.Height));
        }
    }

    void LayoutImage()
    {
        if (_picture is null || ImageView.Source is null) return;
        double scale = Math.Max(0.01, FitScale) * _imageZoom / Dpi;
        ImageView.Width = _picture.Width * scale;
        ImageView.Height = _picture.Height * scale;
        UpgradeImageIfBlurry();
    }

    /// Zooms so the point under the mouse stays under the mouse.
    void ZoomImageAt(double zoom, MouseEventArgs e)
    {
        if (_picture is null) return;
        var onImage = e.GetPosition(ImageView);
        double relX = onImage.X / Math.Max(1, ImageView.Width), relY = onImage.Y / Math.Max(1, ImageView.Height);
        var mouse = e.GetPosition(ImageScroll);
        _imageZoom = Math.Clamp(zoom, 1, Math.Max(1, 8 / Math.Max(0.01, FitScale)));
        LayoutImage();
        ImageScroll.UpdateLayout();
        ImageScroll.ScrollToHorizontalOffset(relX * ImageView.Width - mouse.X);
        ImageScroll.ScrollToVerticalOffset(relY * ImageView.Height - mouse.Y);
    }

    void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ZoomImageAt(_imageZoom * (e.Delta > 0 ? 1.25 : 1 / 1.25), e);
    }

    void OnImageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        // Fitted → real size (or twice, for a small image); zoomed → fitted again.
        ZoomImageAt(_imageZoom > 1.001 ? 1 : FitScale < 0.999 ? 1 / FitScale : 2, e);
    }

    void OnImageAreaResized(object sender, SizeChangedEventArgs e) => LayoutImage();

    /// The image was decoded at pane size to save memory: zooming past that decodes it again, sharper.
    async void UpgradeImageIfBlurry()
    {
        if (_picture is not { } picture || _imageUpgrading || _path is not { } path) return;
        int needed = (int)(ImageView.Width * Dpi);
        if (needed <= picture.Image.PixelWidth * 1.1 || picture.Image.PixelWidth >= picture.Width) return;
        _imageUpgrading = true;
        int version = _version;
        try
        {
            var sharper = await Task.Run(() => LoadImage(path, Math.Min(picture.Width, 8000)));
            if (version != _version || _picture != picture) return;
            _picture = sharper;
            ImageView.Source = sharper.Image;
        }
        catch (Exception) { /* keep the softer one */ }
    }

    /// Decoded off the UI thread at the size it is shown (a 48-megapixel photo does not need 190 MB),
    /// and turned upright like Explorer does for phone photos.
    static Picture LoadImage(string path, int maxWidth)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        double angle = Orientation(frame.Metadata as BitmapMetadata);
        // Turned a quarter, the stored height is what ends up across the pane.
        int across = angle is 90 or 270 ? frame.PixelHeight : frame.PixelWidth;
        int down = angle is 90 or 270 ? frame.PixelWidth : frame.PixelHeight;
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (across > maxWidth)
        {
            if (angle is 90 or 270) bitmap.DecodePixelHeight = maxWidth;
            else bitmap.DecodePixelWidth = maxWidth;
        }
        bitmap.EndInit();
        BitmapSource result = angle == 0 ? bitmap : new TransformedBitmap(bitmap, new RotateTransform(angle));
        result.Freeze();
        return new Picture(result, across, down);
    }

    static double Orientation(BitmapMetadata? metadata)
    {
        try
        {
            return metadata?.GetQuery("System.Photo.Orientation") switch
            {
                (ushort)3 => 180,
                (ushort)6 => 90,
                (ushort)8 => 270,
                _ => 0,
            };
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return 0;
        }
    }

    // ---- Video and audio ----

    static readonly HashSet<string> AudioExtensions = new([".mp3", ".m4a", ".wav", ".wma", ".aac", ".flac"], StringComparer.OrdinalIgnoreCase);
    static readonly Brush AudioBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x33, 0x3B));

    void ShowMedia(string path)
    {
        _mediaShowing = true;
        _playing = false;
        PlayButton.Content = "";
        // Windows only says a file has no picture once it plays: the extension tells it sooner.
        bool audio = AudioExtensions.Contains(Path.GetExtension(path));
        AudioIcon.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        MediaView.Background = audio ? AudioBackground : Brushes.Black;
        TimeText.Text = "0:00";
        Seek.Value = 0;
        MediaView.Visibility = Visibility.Visible;
        Player.Source = new Uri(path);
        Player.Pause();   // opens it and shows the first frame, without sound until the user presses play
    }

    void StopMedia()
    {
        _mediaShowing = false;
        _playing = false;
        _mediaClock.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;
        MediaView.Visibility = Visibility.Collapsed;
    }

    void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        AudioIcon.Visibility = Player.HasVideo ? Visibility.Collapsed : Visibility.Visible;
        Seek.Maximum = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        UpdateMediaTime();
        if (_path is not null) System.Diagnostics.Trace.WriteLine($"preview Media opened {Path.GetFileName(_path)}");
    }

    void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        _mediaClock.Stop();
        Player.Pause();
        Player.Position = TimeSpan.Zero;
        PlayButton.Content = "";
        UpdateMediaTime();
    }

    async void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // A codec Windows lacks: the Edge engine may still play it; if not, the thumbnail.
        if (_path is not { } path) return;
        int version = _version;
        bool Current() => version == _version;
        StopMedia();
        if (!await ShowWebAsync(path, Current) && Current()) await ShowFallbackAsync(path, Current);
    }

    void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (!_mediaShowing) return;
        _playing = !_playing;
        if (_playing) { Player.Play(); _mediaClock.Start(); }
        else { Player.Pause(); _mediaClock.Stop(); }
        PlayButton.Content = _playing ? "" : "";
    }

    void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking || !_mediaShowing) return;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
        UpdateMediaTime();
    }

    void UpdateMediaTime()
    {
        if (!_mediaShowing) return;
        _seeking = true;
        Seek.Value = Player.Position.TotalSeconds;
        _seeking = false;
        var total = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{Player.Position:m\\:ss} / {total:m\\:ss}";
    }

    // ---- Windows preview handlers: Word, Excel, PowerPoint, Outlook… ----

    async Task<bool> ShowHandlerAsync(string path, Func<bool> current)
    {
        if (PreviewHandlerHost.HandlerFor(path) is null) return false;
        if (!TextView.IsVisible && !DocView.IsVisible)
        {
            Message.Text = "Cargando vista previa…";
            Message.Visibility = Visibility.Visible;
        }
        Loading.Visibility = Visibility.Visible;
        bool ok = await _handler.OpenAsync(path);
        if (!current())
        {
            if (ok) await _handler.UnloadAsync();
            return true;   // the user moved on: nothing else to show for this file
        }
        Loading.Visibility = Visibility.Collapsed;
        if (!ok) return false;
        _handlerShowing = true;
        Message.Visibility = Visibility.Collapsed;
        TextView.Visibility = Visibility.Collapsed;
        DocView.Visibility = Visibility.Collapsed;
        HandlerSlot.Visibility = Visibility.Visible;
        return true;
    }

    // ---- Edge engine: web pages and what nothing else shows (SVG, WebM, Ogg) ----

    async Task<bool> ShowWebAsync(string path, Func<bool> current)
    {
        var clock = Stopwatch.StartNew();
        StartWeb();
        WebSlot.Visibility = Visibility.Visible;
        if (!await _webReady!)
        {
            _webReady = null;   // tried again next time
            WebSlot.Visibility = Visibility.Collapsed;
            return false;
        }
        if (!current()) return true;
        var core = _web!.CoreWebView2;
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Done;
            System.Diagnostics.Trace.WriteLine($"preview Web {clock.ElapsedMilliseconds} ms {Path.GetFileName(path)}");
        }
        core.NavigationCompleted += Done;
        core.Navigate(new Uri(path).AbsoluteUri);
        _webShowing = true;
        return true;
    }

    /// Starts the Edge engine ahead (about a second, once), so the first web page shows at once. Called when a
    /// web page arrives in Pendientes; nothing is started while there are none.
    public void WarmUpWeb()
    {
        if (_webReady is not null || _warming) return;
        _warming = true;
        // Once the app is idle: never slows the window down, and the engine cannot start before the app runs.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _warming = false;
            if (_webReady is not null) return;
            StartWeb();
            if (WebSlot.Visibility == Visibility.Collapsed) WebSlot.Visibility = Visibility.Hidden;   // it only starts once in the window
        });
    }

    void StartWeb()
    {
        if (_web is null)
        {
            _web = new WebView2 { AllowExternalDrop = false, DefaultBackgroundColor = System.Drawing.Color.White };
            WebSlot.Content = _web;
        }
        _webReady ??= InitWebAsync(_web);
    }

    static async Task<bool> InitWebAsync(WebView2 web)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebDataDir);
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // A preview never runs a page's code nor goes to the internet (fast, private): pages are drawn
            // with their own local styles and pictures.
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => e.Response = env.CreateWebResourceResponse(null, 404, "Sin internet", "");
            // Only local files are shown; nothing opens pages in here or in new windows.
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            return true;
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"preview web engine: {e.GetType().Name} {e.Message}");
            return false;   // no WebView2 runtime: the thumbnail is shown instead
        }
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_path is not null) OpenExternal(_path);
    }

    /// Opens a file with the program Windows has for it.
    public static void OpenExternal(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show($"No se pudo abrir «{Path.GetFileName(path)}»: {e.Message}", "Filter App",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
