using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FilterApp.Intake;

namespace FilterApp;

/// Quick inbox: a small window that stays on top. Whatever is dropped on it (files or whole folders) goes to
/// Pendientes, to be sorted later in the full window. It can be dragged anywhere and remembers its place.
public partial class MiniWindow : Window
{
    static readonly string PlaceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FilterApp", "mini.txt");

    readonly Func<IDataObject, Task<int>> _add;
    readonly DispatcherTimer _reset = new() { Interval = TimeSpan.FromSeconds(2.5) };

    /// <paramref name="add"/> puts the dropped data in the tray and says how many files that was.
    public MiniWindow(object board, Func<IDataObject, Task<int>> add)
    {
        InitializeComponent();
        DataContext = board;
        _add = add;
        _reset.Tick += (_, _) => { _reset.Stop(); Say("", "Suelta archivos aquí"); };
        PlaceOnScreen();
        LocationChanged += (_, _) => SavePlace();
    }

    /// Raised when the user wants the full window back.
    public event Action? ExpandRequested;

    void OnExpand(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ExpandRequested?.Invoke();
        else DragMove();
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = FileIntake.CanAccept(e.Data);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Highlight(ok);
    }

    void OnDragLeave(object sender, DragEventArgs e) => Highlight(false);

    async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Highlight(false);
        if (!FileIntake.CanAccept(e.Data)) return;
        Say("", "Leyendo…");
        int added = await _add(e.Data);
        Say(added > 0 ? "" : "",
            added switch { 0 => "Nada nuevo", 1 => "+1 archivo", _ => $"+{added} archivos" });
        _reset.Stop();
        _reset.Start();
    }

    void Say(string icon, string text)
    {
        StateIcon.Text = icon;
        Headline.Text = text;
    }

    void Highlight(bool on)
    {
        Frame.Background = new SolidColorBrush(on ? Color.FromRgb(0xDD, 0xF4, 0xFF) : Color.FromRgb(0xF9, 0xFA, 0xFB));
        Dashes.Stroke = new SolidColorBrush(on ? Color.FromRgb(0x09, 0x69, 0xDA) : Color.FromRgb(0xAF, 0xB8, 0xC1));
    }

    /// Where it was last time, if that is still on a screen; otherwise the bottom right corner.
    void PlaceOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
        try
        {
            var parts = File.ReadAllText(PlaceFile).Split(',');
            double left = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            double top = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            if (left >= SystemParameters.VirtualScreenLeft && top >= SystemParameters.VirtualScreenTop &&
                left + Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                top + Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            {
                Left = left;
                Top = top;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { }
    }

    void SavePlace()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlaceFile)!);
            File.WriteAllText(PlaceFile, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Left},{Top}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
