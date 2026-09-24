using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FilterApp.Core;
using FilterApp.Intake;
using FilterApp.Interop;

namespace FilterApp;

public partial class MainWindow : Window
{
    readonly Board _board;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly FolderWatcher _watcher = new();
    Point _dragStart;
    PendingItem? _dragItem;

    public MainWindow(Board board)
    {
        InitializeComponent();
        _board = board;
        DataContext = board;
        _watcher.FolderActivated += _board.OnExplorerFolder;

        board.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };
        board.Notified += ShowToast;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        Activated += (_, _) => _board.RefreshDestination();
        Closing += (_, e) =>
        {
            if (_board.IsCopying)
            {
                e.Cancel = true;
                ShowToast("Espera a que termine la copia antes de cerrar.");
                return;
            }
            if (_board.HeldCount > 0 && MessageBox.Show(this,
                    $"Hay {_board.HeldCount} archivo(s) en espera que aún no se copiaron. ¿Cerrar de todos modos?",
                    "Filter App", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _watcher.Dispose();
            Save();
        };
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

    void HideToast()
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    /// Brings the window to the front (a second launch of the app lands here).
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;    // Activate alone may only flash the taskbar button
        Topmost = false;
        Focus();
    }

    // ---- Paste and external drops into the tray ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        IDataObject? data = null;
        try { data = Clipboard.GetDataObject(); }
        catch (COMException) { }   // clipboard temporarily locked by another app
        if (data is null || !FileIntake.CanAccept(data)) ShowToast("El portapapeles no contiene archivos.");
        else AddToPending(data);
    }

    void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = FileIntake.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnWindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (FileIntake.CanAccept(e.Data)) AddToPending(e.Data);
    }

    List<PendingItem> ReadIntake(IDataObject data)
    {
        if (FileIntake.IsVirtual(data))
        {
            // Virtual files (Outlook, browsers) must be read on this thread, while the drop's data object is
            // alive, and can take a while: show that the app is working before blocking.
            ShowToast("Leyendo archivos…");
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
        IntakeResult result;
        try { result = FileIntake.Read(data); }
        catch (Exception e)
        {
            ShowToast($"No se pudieron leer los archivos: {e.Message}");
            return [];
        }
        if (result.Unreadable > 0) ShowToast($"{result.Unreadable} archivo(s) no se pudieron leer.");
        else if (result.RejectedFolders > 0) ShowToast("Las carpetas no se admiten; solo archivos.");
        else if (FileIntake.IsVirtual(data)) HideToast();
        return result.Items;
    }

    void AddToPending(IDataObject data) => _board.AddPending(ReadIntake(data));

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
        card is { Status: CardStatus.Free } &&
        (e.Data.GetDataPresent(typeof(PendingItem)) || FileIntake.CanAccept(e.Data));

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
        {
            await _board.AssignAsync(item, card);
            return;
        }
        if (!FileIntake.CanAccept(e.Data)) return;

        var items = ReadIntake(e.Data);
        if (items.Count == 1)
            await _board.AssignAsync(items[0], card);
        else if (items.Count > 1)
        {
            _board.AddPending(items);
            ShowToast("Varios archivos: quedaron en Pendientes para repartirlos.");
        }
    }

    void OnCardAction(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        if (card.Status == CardStatus.Filled) _board.Undo(card);
        else if (card.Status == CardStatus.Held) _board.ReturnHeld(card);
        else if (card.Status == CardStatus.Free) _board.RemoveCard(card);
    }

    async void OnRelease(object sender, RoutedEventArgs e)
    {
        var dest = _board.Destination;
        int held = _board.HeldCount;
        int copied = await _board.ReleaseAsync();
        // Failures already showed their own message; only a clean run gets the summary.
        if (copied > 0 && copied == held) ShowToast($"{copied} archivo(s) copiados a {dest}.");
    }

    void OnClearFilled(object sender, RoutedEventArgs e) => _board.ClearFilled();

    void OnAddNames(object sender, RoutedEventArgs e)
    {
        var dialog = new NamesDialog { Owner = this };
        if (dialog.ShowDialog() == true) _board.AddNames(dialog.NamesText);
    }
}
