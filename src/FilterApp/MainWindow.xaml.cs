using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FilterApp.Core;

namespace FilterApp;

public partial class MainWindow : Window
{
    readonly Board _board;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    Point _dragStart;
    PendingItem? _dragItem;

    public MainWindow(Board board)
    {
        InitializeComponent();
        _board = board;
        DataContext = board;

        board.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };
        board.Notified += ShowToast;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        Closing += (_, _) => Save();
    }

    void Save()
    {
        try { StateStore.Save(StateStore.DefaultPath, _board.ToState()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"No se pudo guardar el estado: {e.Message}");
        }
    }

    void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---- Paste and external drops into the tray (filled in Task 6) ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e) { }
    void OnWindowDragOver(object sender, DragEventArgs e) { e.Effects = DragDropEffects.None; e.Handled = true; }
    void OnWindowDrop(object sender, DragEventArgs e) { e.Handled = true; }

    // ---- Dragging a pending item to a card ----

    void OnPendingMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as PendingItem;
    }

    void OnPendingMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(PendingItem), item), DragDropEffects.Copy);
    }

    void OnRemovePending(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PendingItem item) _board.RemovePending(item);
    }

    // ---- Cards ----

    static CardViewModel? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as CardViewModel;

    static bool CanDropOn(CardViewModel? card, DragEventArgs e) =>
        card is { Status: CardStatus.Free } && e.Data.GetDataPresent(typeof(PendingItem));

    void OnCardDragOver(object sender, DragEventArgs e)
    {
        var card = CardOf(sender);
        var ok = CanDropOn(card, e);
        if (card is not null) card.IsDragTarget = ok;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnCardDragLeave(object sender, DragEventArgs e)
    {
        if (CardOf(sender) is { } card) card.IsDragTarget = false;
    }

    async void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CardOf(sender) is not { } card) return;
        card.IsDragTarget = false;
        if (e.Data.GetData(typeof(PendingItem)) is PendingItem item)
            await _board.AssignAsync(item, card);
    }

    void OnCardAction(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        if (card.Status == CardStatus.Filled) _board.Undo(card);
        else if (card.Status == CardStatus.Free) _board.RemoveCard(card);
    }

    void OnClearFilled(object sender, RoutedEventArgs e) => _board.ClearFilled();

    void OnAddNames(object sender, RoutedEventArgs e)
    {
        var dialog = new NamesDialog { Owner = this };
        if (dialog.ShowDialog() == true) _board.AddNames(dialog.NamesText);
    }
}
