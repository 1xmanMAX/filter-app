using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FilterApp.Core;

namespace FilterApp;

/// One line of the sessions panel.
public sealed record SessionRow(string Id, string Name, double Progress, string Subtitle, bool IsCurrent, bool IsComplete)
{
    public static SessionRow From(SessionInfo info, string currentId, DateTime now) => new(
        info.Id, info.Name,
        info.Total == 0 ? 0 : (double)info.Filled / info.Total,
        $"{info.Filled} de {info.Total} · {SessionInfo.WhenText(info.LastUsed, now)}",
        info.Id == currentId, info.IsComplete);
}

/// Drop-down panel listing the saved sessions. It only raises requests; the window acts on them.
public partial class SessionsPanel : UserControl
{
    public SessionsPanel() => InitializeComponent();

    public event Action<string>? OpenRequested;
    public event Action<string>? CreateRequested;
    public event Action<string>? RenameRequested;
    public event Action<string>? DeleteRequested;
    public event Action? CloseRequested;

    public void Show(IEnumerable<SessionRow> rows)
    {
        List.ItemsSource = rows.ToList();
        NewName.Text = "";
        NewBox.Visibility = Visibility.Collapsed;
    }

    static SessionRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as SessionRow;

    void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { } row) OpenRequested?.Invoke(row.Id);
    }

    void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) RenameRequested?.Invoke(row.Id);
    }

    void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) DeleteRequested?.Invoke(row.Id);
    }

    void OnNewClick(object sender, RoutedEventArgs e)
    {
        NewBox.Visibility = Visibility.Visible;
        // Focus once the box is laid out, or the keyboard stays on the button.
        Dispatcher.BeginInvoke(() => Keyboard.Focus(NewName), DispatcherPriority.Input);
    }

    void OnNewNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var name = NewName.Text.Trim();
        if (name.Length > 0) CreateRequested?.Invoke(name);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (NewBox.Visibility == Visibility.Visible) NewBox.Visibility = Visibility.Collapsed;
        else CloseRequested?.Invoke();
    }
}
