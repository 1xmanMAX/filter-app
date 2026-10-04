using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FilterApp.Core;

namespace FilterApp.Preview;

/// One page of the open PDF. Its picture exists only while the page is on screen.
public sealed class PdfPage(int index, Size points) : Observable
{
    double _displayWidth, _displayHeight;
    BitmapSource? _image;
    CancellationTokenSource? _cancel;
    int _renderedWidth;

    public int Index { get; } = index;
    public Size Points { get; } = points;
    public double DisplayWidth { get => _displayWidth; set => Set(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; set => Set(ref _displayHeight, value); }
    public BitmapSource? Image { get => _image; private set => Set(ref _image, value); }

    /// Draws the page at this width unless it already is. The old picture stays until the new one is ready.
    public async void Request(PdfFile file, int pixelWidth)
    {
        if (_renderedWidth == pixelWidth && Image is not null) return;
        _cancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        try
        {
            var image = await file.RenderAsync(Index, pixelWidth, cancel.Token);
            if (cancel.IsCancellationRequested || image is null) return;
            Image = image;
            _renderedWidth = pixelWidth;
        }
        catch (Exception) when (cancel.IsCancellationRequested) { }
        catch (Exception) { /* a damaged page stays blank; the others still show */ }
    }

    public void Release()
    {
        _cancel?.Cancel();
        _cancel = null;
        Image = null;
        _renderedWidth = 0;
    }
}

/// Shows a PDF as it really looks (PDFium), page after page, fitted to the width. Ctrl+wheel zooms.
/// Only the pages on screen are drawn, so a 500-page PDF opens as fast and uses as little memory as a 1-page one.
public partial class PdfView : UserControl
{
    const double Gap = 12;   // Margin of each page (both sides) plus the border
    readonly DispatcherTimer _relayout = new() { Interval = TimeSpan.FromMilliseconds(150) };
    PdfFile? _file;
    List<PdfPage> _pages = [];
    double _zoom = 1;
    ScrollViewer? _scroll;
    bool _dragReady;

    public PdfView()
    {
        InitializeComponent();
        _relayout.Tick += (_, _) => { _relayout.Stop(); Relayout(); };
        SizeChanged += (_, e) => { if (e.WidthChanged && _file is not null) { _relayout.Stop(); _relayout.Start(); } };
        PreviewMouseWheel += OnWheel;
        // Drag with the middle or left button to move around the pages.
        Loaded += (_, _) => EnsureDrag();
    }

    /// Opens the PDF and shows its first page. <paramref name="stillWanted"/> is asked once it is open:
    /// if the user moved on meanwhile, the file is closed again. Throws if the PDF cannot be read.
    public async Task OpenAsync(string path, Func<bool> stillWanted)
    {
        var file = await PdfFile.OpenAsync(path);
        if (!stillWanted())
        {
            await file.CloseAsync();
            return;
        }
        await CloseAsync();
        _file = file;
        _zoom = 1;
        _pages = file.Pages.Select((size, i) => new PdfPage(i, size)).ToList();
        FitPages();
        Pages.ItemsSource = _pages;
        UpdateLayout();
        EnsureDrag();
        Scroll?.ScrollToHome();
        UpdatePageText();
    }

    /// The scroll viewer only exists once the view has been shown.
    void EnsureDrag()
    {
        if (_dragReady || Scroll is not { } scroll) return;
        DragScroll.Attach(scroll, leftButtonToo: true);
        _dragReady = true;
    }

    /// Lets go of the file (and every page picture).
    public async Task CloseAsync()
    {
        _relayout.Stop();
        Pages.ItemsSource = null;
        foreach (var page in _pages) page.Release();
        _pages = [];
        var file = _file;
        _file = null;
        if (file is not null) await file.CloseAsync();
    }

    ScrollViewer? Scroll => _scroll ??= Pages.Template.FindName("Scroll", Pages) as ScrollViewer;

    double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void FitPages()
    {
        double width = Math.Max(120, (ActualWidth - 2 * Gap - SystemParameters.VerticalScrollBarWidth) * _zoom);
        foreach (var page in _pages)
        {
            page.DisplayWidth = width;
            page.DisplayHeight = width * page.Points.Height / Math.Max(1, page.Points.Width);
        }
    }

    int PixelWidth(PdfPage page) => (int)Math.Min(3000, page.DisplayWidth * Dpi);

    /// New size or zoom: pages on screen are drawn again, sharp at the new size.
    void Relayout()
    {
        if (_file is null) return;
        FitPages();
        foreach (var page in _pages.Where(p => p.Image is not null)) page.Request(_file, PixelWidth(page));
    }

    void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PdfPage page && _file is not null) page.Request(_file, PixelWidth(page));
    }

    void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PdfPage page) page.Release();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _file is null) return;
        e.Handled = true;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.4, 4);
        FitPages();
        _relayout.Stop();
        _relayout.Start();
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdatePageText();

    void UpdatePageText()
    {
        if (_pages.Count == 0 || Scroll is null)
        {
            PageText.Text = "";
            return;
        }
        // Pages are scrolled by pixel: walk the heights to find the one in the middle of the view.
        double middle = Scroll.VerticalOffset + Scroll.ViewportHeight / 2, y = 4;
        int current = _pages.Count;
        for (int i = 0; i < _pages.Count; i++)
        {
            y += _pages[i].DisplayHeight + Gap;
            if (y >= middle) { current = i + 1; break; }
        }
        PageText.Text = $"{current} / {_pages.Count}";
    }
}
