using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FilterApp.Core;
using FilterApp.Intake;
using FilterApp.Interop;

namespace FilterApp;

public partial class MainWindow : Window
{
    readonly Board _board;
    readonly SessionStore _sessions;
    string _sessionId;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly FolderWatcher _watcher = new();
    Point _dragStart;
    PendingItem? _dragItem;

    public MainWindow(Board board, SessionStore sessions, string sessionId)
    {
        InitializeComponent();
        _board = board;
        _sessions = sessions;
        _sessionId = sessionId;
        WireSessionsPanel();
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
            if (!ConfirmUnsaved("cerrar"))
            {
                e.Cancel = true;
                return;
            }
            _watcher.Dispose();
            Save();
        };
    }

    bool Save()
    {
        _saveTimer.Stop();
        try
        {
            _sessions.Save(_sessionId, _board.ToState());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"No se pudo guardar la sesión: {e.Message}");
            return false;
        }
    }

    /// Temp files (pasted images, Outlook attachments) cannot be saved; ask before losing them.
    bool ConfirmUnsaved(string action) =>
        _board.UnsavedCount == 0 || MessageBox.Show(this,
            $"{_board.UnsavedCount} archivo(s) pegados o de Outlook no se pueden guardar y se perderán al {action}. ¿Continuar?",
            "Filter App", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    // ---- Sessions ----

    void WireSessionsPanel()
    {
        Sessions.OpenRequested += id => { SessionsPopup.IsOpen = false; SwitchTo(id); };
        Sessions.CreateRequested += name => { SessionsPopup.IsOpen = false; NewSession(name); };
        Sessions.RenameRequested += id => { SessionsPopup.IsOpen = false; RenameSession(id); };
        Sessions.DeleteRequested += id => { SessionsPopup.IsOpen = false; DeleteSession(id); };
        Sessions.CloseRequested += () => SessionsPopup.IsOpen = false;
        // Left-aligned under the button. Placement="Bottom" would follow the Windows "handedness" setting
        // and open the panel to the left of the button on many PCs.
        SessionsPopup.CustomPopupPlacementCallback = (_, target, _) =>
            [new CustomPopupPlacement(new Point(-10, target.Height), PopupPrimaryAxis.Horizontal)];
    }

    void OnSessionMenu(object sender, RoutedEventArgs e)
    {
        Save();   // so the list shows this session's latest progress
        var now = DateTime.Now;
        Sessions.Show(_sessions.List().Select(s => SessionRow.From(s, _sessionId, now)));
        SessionsPopup.IsOpen = true;
    }

    /// Saves the open session before loading another. Refused while copying.
    bool CanLeaveSession()
    {
        if (_board.IsCopying)
        {
            ShowToast("Espera a que termine la copia antes de cambiar de sesión.");
            return false;
        }
        return ConfirmUnsaved("cambiar de sesión") && Save();
    }

    void Open(string id, AppState state)
    {
        _sessionId = id;
        _sessions.CurrentId = id;
        _board.LoadState(state);
        _board.RefreshDestination();
    }

    void SwitchTo(string id)
    {
        if (id == _sessionId || !CanLeaveSession()) return;
        if (!_sessions.Exists(id))
        {
            ShowToast("Esa sesión ya no existe.");
            return;
        }
        Open(id, _sessions.Load(id));
        ShowToast($"Sesión «{_board.Name}» abierta.");
    }

    void NewSession(string name)
    {
        if (!CanLeaveSession()) return;
        // The destination carries over: a new list usually goes to the folder already open.
        var state = new AppState { Destination = _board.Destination };
        Open(_sessions.Create(name, state), state);
        ShowToast($"Sesión «{name}» creada. La anterior quedó guardada.");
    }

    string NameOf(string id) => id == _sessionId ? _board.Name : _sessions.Load(id).Name;

    void RenameSession(string id)
    {
        var dialog = new PromptDialog("Renombrar sesión", "Nuevo nombre:", "Guardar", NameOf(id)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (id == _sessionId)
        {
            _board.Name = dialog.Value;   // saved with the board
            return;
        }
        try { _sessions.Rename(id, dialog.Value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"No se pudo renombrar la sesión: {e.Message}");
        }
    }

    void DeleteSession(string id)
    {
        bool isOpen = id == _sessionId;
        if (isOpen && _board.IsCopying)
        {
            ShowToast("Espera a que termine la copia.");
            return;
        }
        var name = NameOf(id);
        if (MessageBox.Show(this,
                $"¿Eliminar la sesión «{name}»?\n\nSolo se borra la lista de tarjetas; los archivos ya copiados no se tocan.",
                "Filter App", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (isOpen) _saveTimer.Stop();   // a pending save would bring the deleted file back
        try { _sessions.Delete(id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"No se pudo eliminar la sesión: {e.Message}");
            return;
        }
        if (!isOpen)
        {
            ShowToast($"Sesión «{name}» eliminada.");
            return;
        }
        var next = _sessions.List().FirstOrDefault();
        if (next is not null) Open(next.Id, _sessions.Load(next.Id));
        else
        {
            var state = new AppState { Destination = _board.Destination };
            Open(_sessions.Create(SessionStore.DefaultName, state), state);
        }
        ShowToast($"Sesión eliminada. Ahora estás en «{_board.Name}».");
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
