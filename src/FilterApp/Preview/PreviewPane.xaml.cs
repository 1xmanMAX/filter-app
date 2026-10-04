using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FilterApp.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FilterApp.Preview;

/// Shows the selected file: images and text natively, PDF and media with the Edge engine, Office and
/// other registered types with Windows' preview handlers, and the file's thumbnail for the rest.
public partial class PreviewPane : UserControl
{
    static readonly string WebDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilterApp", "WebView2");

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(60) };
    string? _requested;
    string? _path;
    int _version;
    WebView2? _web;
    Task<bool>? _webReady;
    bool _webShowing;
    PreviewHandlerHost? _handler;

    public PreviewPane()
    {
        InitializeComponent();
        // Arrowing through a list fires a selection per step: only the one the user stops on is loaded.
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(_requested); };
        Unloaded += (_, _) => _handler?.Unload();
    }

    /// The file being shown, if any.
    public string? CurrentPath => _path;

    public void Show(string? path)
    {
        _requested = path;
        _debounce.Stop();
        _debounce.Start();
    }

    /// Lets go of the file being shown (a preview handler or the browser may keep it open, which
    /// would stop it from being moved).
    public async Task ReleaseAsync()
    {
        _debounce.Stop();
        bool hadWeb = _webShowing;
        _path = null;
        _version++;
        HideAll();
        ShowMessage("Selecciona un archivo para verlo aquí");
        if (hadWeb) await Task.Delay(80);   // the browser closes the file asynchronously
    }

    async void Load(string? path)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && path is not null) return;
        _path = path;
        int version = ++_version;
        HideAll();

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

        try
        {
            switch (PreviewText.Classify(path))
            {
                case PreviewKind.Image:
                    var image = await Task.Run(() => LoadImage(path));
                    if (version != _version) return;
                    ImageView.Source = image;
                    ImageView.Visibility = Visibility.Visible;
                    return;
                case PreviewKind.Text:
                    ShowText(await Task.Run(() => PreviewText.ReadText(path)), version);
                    return;
                case PreviewKind.Docx:
                    if (ShowHandler(path)) return;
                    ShowText(await Task.Run(() => PreviewText.ReadDocx(path)), version);
                    return;
                case PreviewKind.Web:
                    if (await ShowWebAsync(path, version) || version != _version) return;
                    break;
                default:
                    if (ShowHandler(path)) return;
                    break;
            }
        }
        catch (Exception)
        {
            // Unreadable, unsupported codec, damaged file: the thumbnail below is always possible.
            if (version != _version) return;
            HideAll();
        }
        ShowFallback(path);
    }

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

    void ShowText(string text, int version)
    {
        if (version != _version) return;
        TextView.Text = text;
        TextView.ScrollToHome();
        TextView.Visibility = Visibility.Visible;
    }

    void ShowFallback(string path)
    {
        Thumb.Source = ShellThumbnail.Get(path, 256);
        Fallback.Visibility = Visibility.Visible;
    }

    void HideAll()
    {
        Message.Visibility = Visibility.Collapsed;
        ImageView.Source = null;
        ImageView.Visibility = Visibility.Collapsed;
        TextView.Text = "";
        TextView.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Collapsed;
        Thumb.Source = null;
        _handler?.Unload();
        HandlerSlot.Visibility = Visibility.Collapsed;
        if (_webShowing && _web?.CoreWebView2 is { } core)
        {
            core.Navigate("about:blank");   // stops audio/video and closes the file
            _webShowing = false;
        }
        WebSlot.Visibility = Visibility.Collapsed;
    }

    // ---- Images ----

    /// Decoded off the UI thread, scaled down to screen size, and turned upright like Explorer does
    /// for phone photos.
    static BitmapSource LoadImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int width = frame.PixelWidth;
        double angle = Orientation(frame.Metadata as BitmapMetadata);
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (width > 2400) bitmap.DecodePixelWidth = 2400;
        bitmap.EndInit();
        BitmapSource result = angle == 0 ? bitmap : new TransformedBitmap(bitmap, new RotateTransform(angle));
        result.Freeze();
        return result;
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

    // ---- Edge engine: PDF, SVG, audio, video ----

    async Task<bool> ShowWebAsync(string path, int version)
    {
        if (_web is null)
        {
            _web = new WebView2 { AllowExternalDrop = false, DefaultBackgroundColor = System.Drawing.Color.White };
            WebSlot.Content = _web;
        }
        WebSlot.Visibility = Visibility.Visible;   // the control only starts once it is shown
        _webReady ??= InitWebAsync(_web);
        if (!await _webReady)
        {
            WebSlot.Visibility = Visibility.Collapsed;
            return false;
        }
        if (version != _version) return true;

        var uri = new Uri(path).AbsoluteUri;
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) uri += "#view=FitH";
        _web.CoreWebView2.Navigate(uri);
        _webShowing = true;
        return true;
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
            core.Settings.IsZoomControlEnabled = true;
            // Only local files are shown; links inside a PDF never open pages in here or in new windows.
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            return true;
        }
        catch (Exception)
        {
            // No WebView2 runtime (very old Windows 10): fall back to preview handlers and thumbnails.
            return false;
        }
    }

    // ---- Windows preview handlers: Word, Excel, PowerPoint, Outlook… ----

    bool ShowHandler(string path)
    {
        if (PreviewHandlerHost.HandlerFor(path) is null) return false;
        if (_handler is null)
        {
            _handler = new PreviewHandlerHost();
            HandlerSlot.Content = _handler;
        }
        HandlerSlot.Visibility = Visibility.Visible;
        UpdateLayout();   // the host window must exist and have its size before the handler draws in it
        if (_handler.Open(path)) return true;
        HandlerSlot.Visibility = Visibility.Collapsed;
        return false;
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
