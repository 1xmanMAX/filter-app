using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FilterApp.Core;
using FilterApp.Intake;
using FilterApp.Interop;
using FilterApp.Preview;

namespace FilterApp;

public partial class MainWindow : Window
{
    readonly Board _board;
    readonly SessionStore _sessions;
    string _sessionId;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    /// Holding a dragged file over a folder for this long opens it.
    readonly DispatcherTimer _springTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly FolderWatcher _watcher = new();
    FolderViewModel _current;
    FolderViewModel? _springTarget;
    Point _dragStart;
    PendingItem? _dragItem;
    /// A click on an already selected item of a multi-selection: it may start a drag of all of them.
    PendingItem? _deferredSelect;
    /// Operations placing many files, between which no card is "copying" for a moment.
    int _batches;
    readonly NewTile _newFolder = new(isFolder: true);
    readonly NewTile _newName = new(isFolder: false);

    public MainWindow(Board board, SessionStore sessions, string sessionId)
    {
        InitializeComponent();
        _board = board;
        _sessions = sessions;
        _sessionId = sessionId;
        _current = board.Root;
        WireSessionsPanel();
        DataContext = board;
        Navigate(board.Root);
        _watcher.FolderActivated += _board.OnExplorerFolder;

        board.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };
        board.Notified += ShowToast;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        _springTimer.Tick += (_, _) => OnSpring();
        Activated += (_, _) => _board.RefreshDestination();
        Loaded += (_, _) => SelectFirstPendingIfNone();
        Closing += (_, e) =>
        {
            if (IsBusy)
            {
                e.Cancel = true;
                ShowToast("Espera a que termine de colocar los archivos antes de cerrar.");
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

    bool IsBusy => _board.IsCopying || _batches > 0;

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
        if (IsBusy)
        {
            ShowToast("Espera a que termine de colocar los archivos antes de cambiar de sesión.");
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
        Navigate(_board.Root);
        ClearQuick();
        SelectFirstPendingIfNone();
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
        if (isOpen && IsBusy)
        {
            ShowToast("Espera a que termine de colocar los archivos.");
            return;
        }
        var name = NameOf(id);
        if (MessageBox.Show(this,
                $"¿Eliminar la sesión «{name}»?\n\nSolo se borra la lista de tarjetas; los archivos ya colocados no se tocan.",
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
        if (_mini is not null) ExitMini();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;    // Activate alone may only flash the taskbar button
        Topmost = false;
        Focus();
    }

    // ---- Toolbar ----

    MiniWindow? _mini;

    /// Quick inbox: the app shrinks to a small window on top of everything to drop files on.
    void OnMini(object sender, RoutedEventArgs e)
    {
        if (_mini is not null) return;
        _mini = new MiniWindow(_board, AddToPendingAsync);
        _mini.ExpandRequested += ExitMini;
        _mini.Closed += (_, _) => { if (_mini is not null) { _mini = null; RestoreFromMini(); } };
        _mini.Show();
        Hide();
    }

    void ExitMini()
    {
        var mini = _mini;
        _mini = null;
        mini?.Close();
        RestoreFromMini();
    }

    void RestoreFromMini()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        SelectFirstPendingIfNone();
    }

    void OnMoveToggled(object sender, RoutedEventArgs e)
    {
        if (_board.Move)
            ShowToast("Modo Mover: los archivos salen de su carpeta original. Ctrl+Z o ✕ los devuelven a su sitio.");
        else
            ShowToast("Modo Copiar: los originales se quedan donde están.");
    }

    async void OnPreviewToggled(object sender, RoutedEventArgs e)
    {
        bool show = PreviewToggle.IsChecked == true;
        PreviewColumn.MinWidth = show ? 240 : 0;
        PreviewColumn.Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PreviewSplitterColumn.Width = new GridLength(show ? 10 : 0);
        Preview.Visibility = PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) PreviewSelectedPending();
        else await Preview.ReleaseAsync();
    }

    void OnUndoLast(object sender, RoutedEventArgs e) => UndoLast();

    void UndoLast()
    {
        if (_board.LastPlaced is not { } card)
        {
            ShowToast("No hay nada que deshacer.");
            return;
        }
        var file = card.FileName;
        var moved = card.MovedFrom is not null;
        if (_board.UndoLast() is not null && card.Status == CardStatus.Free)
            ShowToast(moved ? $"Deshecho: «{file}» volvió a su carpeta y a Pendientes." : $"Deshecho: «{file}» se quitó.");
    }

    // ---- Keyboard ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool inText = Keyboard.FocusedElement is TextBox;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            QuickBox.Focus();
            QuickBox.SelectAll();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && !inText)
        {
            e.Handled = true;
            UndoLast();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && !inText)
        {
            e.Handled = true;
            IDataObject? data = null;
            try { data = Clipboard.GetDataObject(); }
            catch (COMException) { }   // clipboard temporarily locked by another app
            if (data is null || !FileIntake.CanAccept(data)) ShowToast("El portapapeles no contiene archivos.");
            else AddToPending(data);
        }
        else if (e.Key == Key.Back && !inText && _current.Parent is { } parent)
        {
            e.Handled = true;
            Navigate(parent);
        }
    }

    // ---- Tray: external drops, paste, folders ----

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

    IntakeResult ReadIntake(IDataObject data)
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
            return new IntakeResult([], []);
        }
        if (result.Unreadable > 0) ShowToast($"{result.Unreadable} archivo(s) no se pudieron leer.");
        else if (FileIntake.IsVirtual(data)) HideToast();
        return result;
    }

    /// Lists the files of dropped folders without freezing the window.
    async Task<List<PendingItem>> ScanAsync(List<string> folders)
    {
        if (folders.Count == 0) return [];
        ShowToast("Leyendo carpeta…");
        var items = await Task.Run(() => folders.SelectMany(f => FolderScanner.Scan(f)).ToList());
        var names = string.Join(", ", folders.Select(f => $"«{Path.GetFileName(Path.TrimEndingDirectorySeparator(f))}»"));
        ShowToast(items.Count == 0 ? $"{names}: no tiene archivos." : $"{items.Count} archivo(s) de {names}.");
        return items;
    }

    async void AddToPending(IDataObject data) => await AddToPendingAsync(data);

    /// Puts dropped or pasted files (and the files of dropped folders) in the tray. Returns how many were new.
    async Task<int> AddToPendingAsync(IDataObject data)
    {
        int before = _board.Pending.Count;
        var result = ReadIntake(data);
        _board.AddPending(result.Items);
        _board.AddPending(await ScanAsync(result.Folders));
        SelectFirstPendingIfNone();
        return _board.Pending.Count - before;
    }

    void OnClearPending(object sender, RoutedEventArgs e)
    {
        if (_board.Pending.Count == 0) return;
        if (_board.Pending.Count > 10 && MessageBox.Show(this,
                $"¿Quitar los {_board.Pending.Count} archivos de Pendientes?\n\nLos archivos no se borran de su carpeta.",
                "Filter App", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var item in _board.Pending.ToList()) _board.RemovePending(item);
    }

    // ---- Tray: selection, preview and dragging ----

    List<PendingItem> SelectedPending() =>
        PendingList.SelectedItems.Cast<PendingItem>().OrderBy(p => _board.Pending.IndexOf(p)).ToList();

    void SelectFirstPendingIfNone()
    {
        if (PendingList.SelectedItem is null && _board.Pending.Count > 0) PendingList.SelectedIndex = 0;
    }

    /// After files leave the tray, the next one is selected so the user can keep going with the keyboard.
    void SelectPendingAt(int index)
    {
        if (_board.Pending.Count == 0) return;
        PendingList.SelectedIndex = Math.Clamp(index, 0, _board.Pending.Count - 1);
        PendingList.ScrollIntoView(PendingList.SelectedItem);
    }

    void OnPendingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The one just clicked or arrowed to, also inside a multi-selection.
        var item = e.AddedItems.OfType<PendingItem>().LastOrDefault() ?? PendingList.SelectedItem as PendingItem;
        if (e.AddedItems.Count > 0) _filesActive = false;
        if (PreviewToggle.IsChecked == true) Preview.Show(item?.SourcePath);
    }

    void PreviewSelectedPending() => Preview.Show((PendingList.SelectedItem as PendingItem)?.SourcePath);

    void OnPendingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            int index = PendingList.SelectedIndex;
            foreach (var item in SelectedPending()) _board.RemovePending(item);
            SelectPendingAt(index);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            QuickBox.Focus();
        }
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        // Typing while the tray (or a folder's file list) has focus goes to the quick bar: select a file,
        // type where it goes, Enter.
        bool list = PendingList.IsKeyboardFocusWithin || (FileList.IsKeyboardFocusWithin && Keyboard.FocusedElement is not TextBox);
        if (list && e.Text.Length > 0 && !char.IsControl(e.Text[0]))
        {
            QuickBox.Focus();
            QuickBox.Text += e.Text;
            QuickBox.CaretIndex = QuickBox.Text.Length;
            e.Handled = true;
            return;
        }
        base.OnPreviewTextInput(e);
    }

    void OnPendingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { } item) PreviewPane.OpenExternal(item.SourcePath);
    }

    static PendingItem? ItemUnder(object source) =>
        (source as FrameworkElement)?.DataContext as PendingItem ?? (source as FrameworkContentElement)?.DataContext as PendingItem;

    static bool IsInButton(object source)
    {
        for (var d = source as DependencyObject; d is not null;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    void OnPendingMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemUnder(e.OriginalSource);
        // Clicking inside a multi-selection would drop it before the drag starts: wait for the mouse up.
        if (_dragItem is not null && PendingList.SelectedItems.Count > 1 && PendingList.SelectedItems.Contains(_dragItem) &&
            Keyboard.Modifiers == ModifierKeys.None && !IsInButton(e.OriginalSource))
        {
            _deferredSelect = _dragItem;
            e.Handled = true;
        }
    }

    void OnPendingMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect is { } item) PendingList.SelectedItem = item;
        _deferredSelect = null;
    }

    void OnPendingMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        _deferredSelect = null;
        var selected = SelectedPending();
        PendingItem[] items = selected.Contains(item) ? [.. selected] : [item];
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(PendingItem[]), items), DragDropEffects.Copy);
        EndSpring();
    }

    void OnRemovePending(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PendingItem item) _board.RemovePending(item);
    }

    static PendingItem[]? Dragged(DragEventArgs e) => e.Data.GetData(typeof(PendingItem[])) as PendingItem[];

    /// Files already placed in the tree, dragged from a folder's list.
    static CardViewModel[]? Placed(DragEventArgs e) => e.Data.GetData(typeof(CardViewModel[])) as CardViewModel[];

    static bool HasFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(PendingItem[])) || e.Data.GetDataPresent(typeof(CardViewModel[])) || FileIntake.CanAccept(e.Data);

    // ---- Placing ----

    async Task AssignAsync(PendingItem item, CardViewModel card)
    {
        int index = _board.Pending.IndexOf(item);
        if (_board.Move) await Preview.ReleaseAsync();   // a preview may keep the file open
        if (await _board.AssignAsync(item, card) && index >= 0) SelectPendingAt(index);
    }

    /// Places files in a folder with progress for big batches. Files from a dropped folder keep its
    /// subfolders when <paramref name="keepFolders"/> is set.
    async Task PlaceAsync(IReadOnlyList<PendingItem> items, FolderViewModel folder, bool keepFolders = false)
    {
        if (items.Count == 0) return;
        int index = items.Select(i => _board.Pending.IndexOf(i)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (_board.Move) await Preview.ReleaseAsync();
        _batches++;
        try
        {
            var where = folder.IsRoot ? "el destino" : $"«{folder.Name}»";
            int placed = await _board.PlaceManyAsync(items, folder, keepFolders,
                (done, total) => { if (total > 1 && done < total) ShowToast($"Colocando {done + 1} de {total}…"); });
            if (items.Count > 1 && placed == items.Count)
                ShowToast($"{placed} archivos {(_board.Holding ? "en espera" : _board.Move ? "movidos" : "copiados")} en {where}.");
            else if (items.Count > 1 && placed > 0)
                ShowToast($"{placed} de {items.Count} archivos colocados en {where}; los demás quedaron en Pendientes.");
        }
        finally
        {
            _batches--;
        }
        if (index >= 0) SelectPendingAt(index);
    }

    /// Files and folders dropped from outside straight onto a folder: dropped folders keep their tree.
    async Task PlaceExternalAsync(IDataObject data, FolderViewModel folder)
    {
        var result = ReadIntake(data);
        var items = result.Items.Concat(await ScanAsync(result.Folders)).ToList();
        await PlaceAsync(items, folder, keepFolders: true);
    }

    async Task DropOnFolderAsync(DragEventArgs e, FolderViewModel folder)
    {
        if (Placed(e) is { } placed) await RelocateAsync(placed, folder);
        else if (Dragged(e) is { } items) await PlaceAsync(items, folder);
        else if (FileIntake.CanAccept(e.Data)) await PlaceExternalAsync(e.Data, folder);
    }

    // ---- Navigating the tree ----

    void Navigate(FolderViewModel folder)
    {
        CancelTile(_newFolder);
        CancelTile(_newName);
        _current = folder;
        TreePanel.DataContext = folder;
        // The folder's own items, then the tile that adds one more.
        FolderList.ItemsSource = new CompositeCollection { new CollectionContainer { Collection = folder.Folders }, _newFolder };
        CardList.ItemsSource = new CompositeCollection { new CollectionContainer { Collection = folder.Cards }, _newName };
        RefreshBreadcrumb();
        TreeScroll.ScrollToHome();
    }

    void RefreshBreadcrumb() => Breadcrumb.ItemsSource = _current.Chain().Select(f => new Crumb(f, f == _current)).ToList();

    /// After a folder is taken off the list, the view must not stay inside it.
    void LeaveRemovedFolders()
    {
        for (var f = _current; f.Parent is { } parent; f = parent)
            if (!parent.Folders.Contains(f))
            {
                Navigate(parent);
                LeaveRemovedFolders();
                return;
            }
    }

    static FolderViewModel? FolderOf(object sender) => (sender as FrameworkElement)?.DataContext as FolderViewModel;
    static FolderViewModel? CrumbOf(object sender) => ((sender as FrameworkElement)?.DataContext as Crumb)?.Folder;

    void OnFolderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Navigate(folder);
    }

    void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (CrumbOf(sender) is { } folder) Navigate(folder);
    }

    void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var where = _current.IsRoot ? "el destino" : $"«{_current.Name}»";
        var dialog = new PromptDialog("Nueva carpeta", $"Nombre de la carpeta en {where} (usa / para crear varias: A/B):", "Crear")
            { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _board.AddNames(dialog.Value.TrimEnd('/', '\\') + "/", _current);
    }

    void OnRenameFolder(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is not { } folder) return;
        var dialog = new PromptDialog("Renombrar carpeta",
            "Nuevo nombre (lo ya colocado se queda en la carpeta anterior del disco):", "Guardar", folder.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _board.RenameFolder(folder, dialog.Value);
        RefreshBreadcrumb();
    }

    void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is not { } folder) return;
        if (!folder.IsEmpty && MessageBox.Show(this,
                $"¿Quitar la carpeta «{folder.Name}» y todo su contenido de la lista?\n\nLos archivos ya colocados no se borran del disco.",
                "Filter App", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_board.RemoveFolder(folder)) LeaveRemovedFolders();
    }

    // ---- Spring-loaded folders: hold a dragged file over a folder to open it ----

    void Spring(FolderViewModel folder)
    {
        if (_springTarget == folder || folder == _current) return;
        _springTarget = folder;
        _springTimer.Stop();
        _springTimer.Start();
    }

    void Unspring(FolderViewModel folder)
    {
        if (_springTarget != folder) return;
        _springTimer.Stop();
        _springTarget = null;
    }

    void EndSpring()
    {
        _springTimer.Stop();
        if (_springTarget is not null) _springTarget.IsDragTarget = false;
        _springTarget = null;
    }

    void OnSpring()
    {
        var target = _springTarget;
        EndSpring();
        if (target is not null) Navigate(target);
    }

    /// DragLeave also fires when moving onto a child element; only a real exit counts.
    static bool StillInside(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element) return false;
        var p = e.GetPosition(element);
        return p.X > 0 && p.Y > 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
    }

    void FolderDragOver(FolderViewModel? folder, DragEventArgs e)
    {
        bool ok = folder is not null && HasFiles(e);
        if (folder is not null)
        {
            folder.IsDragTarget = ok;
            if (ok) Spring(folder);
        }
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void FolderDragLeave(FolderViewModel? folder, object sender, DragEventArgs e)
    {
        if (folder is null || StillInside(sender, e)) return;
        folder.IsDragTarget = false;
        Unspring(folder);
    }

    async void FolderDrop(FolderViewModel? folder, DragEventArgs e)
    {
        e.Handled = true;
        EndSpring();
        if (folder is null) return;
        folder.IsDragTarget = false;
        await DropOnFolderAsync(e, folder);
    }

    void OnFolderDragOver(object sender, DragEventArgs e) => FolderDragOver(FolderOf(sender), e);
    void OnFolderDragLeave(object sender, DragEventArgs e) => FolderDragLeave(FolderOf(sender), sender, e);
    void OnFolderDrop(object sender, DragEventArgs e) => FolderDrop(FolderOf(sender), e);

    void OnCrumbDragOver(object sender, DragEventArgs e) => FolderDragOver(CrumbOf(sender), e);
    void OnCrumbDragLeave(object sender, DragEventArgs e) => FolderDragLeave(CrumbOf(sender), sender, e);
    void OnCrumbDrop(object sender, DragEventArgs e) => FolderDrop(CrumbOf(sender), e);

    // The empty space of the open folder.
    void OnCurrentDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnCurrentDragLeave(object sender, DragEventArgs e) { }

    async void OnCurrentDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        EndSpring();
        await DropOnFolderAsync(e, _current);
    }

    // ---- Named cards ----

    static CardViewModel? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as CardViewModel;

    static bool CanDropOn(CardViewModel? card, DragEventArgs e) => card is { Status: CardStatus.Free } && HasFiles(e);

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
        if (CardOf(sender) is { } card && !StillInside(sender, e)) card.IsDragTarget = false;
    }

    async void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        EndSpring();
        if (CardOf(sender) is not { } card) return;
        card.IsDragTarget = false;

        if (Placed(e) is { } placed)
        {
            await RelocateAsync(placed, card.Folder ?? _current, into: card);
            return;
        }
        if (Dragged(e) is { } dragged)
        {
            if (dragged.Length == 1) await AssignAsync(dragged[0], card);
            else ShowToast("Un nombre recibe un solo archivo. Para varios, suéltalos en una carpeta.");
            return;
        }
        if (!FileIntake.CanAccept(e.Data)) return;

        var result = ReadIntake(e.Data);
        var items = result.Items.Concat(await ScanAsync(result.Folders)).ToList();
        if (items.Count == 1)
            await AssignAsync(items[0], card);
        else if (items.Count > 1)
        {
            _board.AddPending(items);
            SelectFirstPendingIfNone();
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

    /// Clicking a placed or held file shows it.
    void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (CardOf(sender)?.PreviewPath is { } path && PreviewToggle.IsChecked == true) Preview.Show(path);
    }

    // ---- Files placed in the open folder: select, drag elsewhere, rename in place ----

    Point _fileDragStart;
    CardViewModel? _fileDragItem;
    CardViewModel? _fileDeferred;
    /// The quick bar and drops act on the folder's files when they were selected last (otherwise on the tray).
    bool _filesActive;

    List<CardViewModel> SelectedFiles() =>
        FileList.SelectedItems.Cast<CardViewModel>().OrderBy(c => _current.Files.IndexOf(c)).ToList();

    static CardViewModel? FileUnder(object source) =>
        (source as FrameworkElement)?.DataContext as CardViewModel ?? (source as FrameworkContentElement)?.DataContext as CardViewModel;

    static bool IsInTextBox(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBox) return true;
        return false;
    }

    void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<CardViewModel>().LastOrDefault() is not { } file) return;
        _filesActive = true;
        if (file.PreviewPath is { } path && PreviewToggle.IsChecked == true) Preview.Show(path);
    }

    void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsInTextBox(e.OriginalSource) && FileUnder(e.OriginalSource)?.PreviewPath is { } path) PreviewPane.OpenExternal(path);
    }

    void OnFileKeyDown(object sender, KeyEventArgs e)
    {
        if (IsInTextBox(e.OriginalSource)) return;
        var files = SelectedFiles();
        if (e.Key == Key.F2 && files is [var one])
        {
            e.Handled = true;
            StartRename(one);
        }
        else if (e.Key == Key.Delete && files.Count > 0)
        {
            e.Handled = true;
            foreach (var file in files)
                if (file.Status == CardStatus.Filled) _board.Undo(file);
                else if (file.Status == CardStatus.Held) _board.ReturnHeld(file);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            QuickBox.Focus();
        }
    }

    void OnFileMouseDown(object sender, MouseButtonEventArgs e)
    {
        _fileDragStart = e.GetPosition(null);
        _fileDragItem = IsInTextBox(e.OriginalSource) || IsInButton(e.OriginalSource) ? null : FileUnder(e.OriginalSource);
        // Clicking inside a multi-selection would drop it before the drag starts: wait for the mouse up.
        if (_fileDragItem is not null && FileList.SelectedItems.Count > 1 && FileList.SelectedItems.Contains(_fileDragItem) &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            _fileDeferred = _fileDragItem;
            e.Handled = true;
        }
    }

    void OnFileMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_fileDeferred is { } file) FileList.SelectedItem = file;
        _fileDeferred = null;
    }

    void OnFileMouseMove(object sender, MouseEventArgs e)
    {
        if (_fileDragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _fileDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var file = _fileDragItem;
        _fileDragItem = null;
        _fileDeferred = null;
        var selected = SelectedFiles();
        CardViewModel[] files = selected.Contains(file) ? [.. selected] : [file];
        _filesActive = true;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(CardViewModel[]), files), DragDropEffects.Move);
        EndSpring();
    }

    void OnRenameFile(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } file) StartRename(file);
    }

    void StartRename(CardViewModel file)
    {
        if (file.Status != CardStatus.Filled || !file.IsAuto) return;
        foreach (var other in _current.Files) other.IsEditing = false;
        file.IsEditing = true;
    }

    void OnRenameBoxShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box || box.DataContext is not CardViewModel file) return;
        box.Text = Path.GetFileNameWithoutExtension(file.FileName ?? "");
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    async void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: CardViewModel file } box) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            file.IsEditing = false;
            FileList.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(file, box.Text);
        }
    }

    async void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: CardViewModel { IsEditing: true } file } box) await CommitRenameAsync(file, box.Text);
    }

    async Task CommitRenameAsync(CardViewModel file, string name)
    {
        file.IsEditing = false;
        name = name.Trim();
        if (name.Length == 0 || name == Path.GetFileNameWithoutExtension(file.FileName)) return;
        await Preview.ReleaseAsync();   // a preview may keep the file open
        if (await _board.RenameFileAsync(file, name) && _current.Files.LastOrDefault() is { } renamed)
        {
            FileList.SelectedItem = renamed;
            FileList.Focus();
        }
    }

    /// Placed files dragged or sent elsewhere in the tree: into a folder keeping their names, or onto a name.
    async Task RelocateAsync(IReadOnlyList<CardViewModel> files, FolderViewModel folder, CardViewModel? into = null)
    {
        if (files.Count == 0) return;
        if (into is not null && files.Count > 1)
        {
            ShowToast("Un nombre recibe un solo archivo. Para varios, suéltalos en una carpeta.");
            return;
        }
        await Preview.ReleaseAsync();
        _batches++;
        try
        {
            if (into is not null)
            {
                await _board.RelocateAsync(files[0], into.Folder ?? folder, into);
                return;
            }
            int moved = await _board.RelocateManyAsync(files, folder,
                (done, total) => { if (total > 1 && done < total) ShowToast($"Moviendo {done + 1} de {total}…"); });
            if (moved > 1) ShowToast($"{moved} archivos movidos a {(folder.IsRoot ? "el destino" : $"«{folder.Name}»")}.");
        }
        finally
        {
            _batches--;
        }
    }

    async void OnRelease(object sender, RoutedEventArgs e)
    {
        var dest = _board.Destination;
        int held = _board.HeldCount;
        if (_board.Move) await Preview.ReleaseAsync();
        int copied = await _board.ReleaseAsync();
        // Failures already showed their own message; only a clean run gets the summary.
        if (copied > 0 && copied == held) ShowToast($"{copied} archivo(s) {(_board.Move ? "movidos" : "copiados")} a {dest}.");
    }

    void OnClearFilled(object sender, RoutedEventArgs e) => _board.ClearFilled();

    void OnAddNames(object sender, RoutedEventArgs e)
    {
        var dialog = new NamesDialog(_current.IsRoot ? null : _current.DisplayPath) { Owner = this };
        if (dialog.ShowDialog() == true) _board.AddNames(dialog.NamesText, _current, dialog.LeavesAreFolders);
    }

    // ---- "+ New folder" / "+ New name" tiles ----

    static NewTile? TileOf(object sender) => (sender as FrameworkElement)?.DataContext as NewTile;

    /// Stops editing a tile. Files dropped on it from outside go to the tray so they are not lost.
    void CancelTile(NewTile tile)
    {
        if (tile.Waiting is { } waiting) _board.AddPending(waiting);
        tile.Stop();
    }

    void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if (TileOf(sender) is { IsEditing: false } tile) tile.Start();
    }

    void OnTileInputShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    void OnTileLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TileOf(sender) is { } tile && tile.Text.Trim().Length == 0 && tile.Waiting is null) tile.Stop();
    }

    async void OnTileKeyDown(object sender, KeyEventArgs e)
    {
        if (TileOf(sender) is not { } tile) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTile(tile);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitTileAsync(tile, open: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
    }

    /// Creates the folder or name typed in a tile. Files waiting on the tile go into it; otherwise the tile
    /// stays open for the next one, so a whole list is typed with Enter after each. Ctrl+Enter opens the new folder.
    async Task CommitTileAsync(NewTile tile, bool open)
    {
        var name = tile.Text.Trim();
        if (name.Length == 0)
        {
            CancelTile(tile);
            return;
        }
        var waiting = tile.Waiting;
        var placed = tile.WaitingPlaced;
        bool keepFolders = tile.KeepFolders;
        var here = _current;
        if (tile.IsFolder)
        {
            _board.AddNames(name.TrimEnd('/', '\\') + "/", here);
            var folder = here.GetOrAddPath(name);
            if (waiting is not null || placed is not null || open) tile.Stop();
            else tile.Text = "";
            if (waiting is not null) await PlaceAsync(waiting, folder, keepFolders);
            if (placed is not null) await RelocateAsync(placed, folder);
            if (open) Navigate(folder);
        }
        else
        {
            // A slash here would make folders: in a name it is just a character the file system refuses.
            _board.AddNames(name.Replace('/', '-').Replace('\\', '-'), here);
            var card = here.Cards[^1];
            if (waiting is [var item])
            {
                tile.Stop();
                await AssignAsync(item, card);
            }
            else if (placed is [var file])
            {
                tile.Stop();
                await RelocateAsync([file], here, into: card);
            }
            else tile.Text = "";
        }
    }

    void OnTileDragOver(object sender, DragEventArgs e)
    {
        var tile = TileOf(sender);
        bool ok = tile is not null && HasFiles(e) &&
                  (tile.IsFolder || (Dragged(e) is not { Length: > 1 } && Placed(e) is not { Length: > 1 }));
        if (tile is not null) tile.IsDragTarget = ok;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnTileDragLeave(object sender, DragEventArgs e)
    {
        if (TileOf(sender) is { } tile && !StillInside(sender, e)) tile.IsDragTarget = false;
    }

    /// Files dropped on a tile wait there while the user types the new folder's (or name's) name.
    async void OnTileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        EndSpring();
        if (TileOf(sender) is not { } tile) return;
        tile.IsDragTarget = false;
        if (Placed(e) is { } placedFiles)
        {
            if (!tile.IsFolder && placedFiles.Length > 1) return;
            CancelTile(tile == _newFolder ? _newName : _newFolder);
            tile.StartWithPlaced(placedFiles);
            return;
        }
        List<PendingItem> items;
        bool keepFolders = false;
        if (Dragged(e) is { } dragged) items = [.. dragged];
        else if (FileIntake.CanAccept(e.Data))
        {
            var result = ReadIntake(e.Data);
            items = result.Items.Concat(await ScanAsync(result.Folders)).ToList();
            keepFolders = result.Folders.Count > 0;
        }
        else return;
        if (items.Count == 0) return;
        if (!tile.IsFolder && items.Count > 1)
        {
            _board.AddPending(items);
            ShowToast("Un nombre recibe un solo archivo. Para varios, suéltalos en «Nueva carpeta».");
            return;
        }
        CancelTile(tile == _newFolder ? _newName : _newFolder);
        tile.Start(items, keepFolders);
    }

    // ---- Quick bar ----

    void ClearQuick()
    {
        QuickBox.Text = "";
        Results.Visibility = Visibility.Collapsed;
    }

    void OnQuickTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = QuickBox.Text.Trim();
        QuickHint.Visibility = QuickBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length == 0)
        {
            Results.Visibility = Visibility.Collapsed;
            return;
        }
        var hits = _board.Search(text);
        var rows = hits.Select(QuickRow.From).ToList();
        bool isPath = text.Contains('/') || text.Contains('\\');
        if (isPath) rows.Insert(0, QuickRow.Create(text, _current));
        else if (!hits.Any(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase)))
            rows.Add(QuickRow.Create(text, _current));
        Results.ItemsSource = rows;
        Results.SelectedIndex = 0;
        Results.Visibility = Visibility.Visible;
    }

    void OnQuickKeyDown(object sender, KeyEventArgs e)
    {
        bool results = Results.Visibility == Visibility.Visible && Results.Items.Count > 0;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Down or Key.Up when results && !ctrl:
                Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case Key.Down or Key.Up:
                // Ctrl+arrows (or arrows with no results) step through the tray without leaving the box.
                if (_board.Pending.Count > 0)
                    SelectPendingAt(Math.Max(PendingList.SelectedIndex, 0) + (e.Key == Key.Down ? 1 : -1));
                break;
            case Key.Enter when results && Results.SelectedItem is QuickRow row:
                Execute(row, navigate: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Escape when QuickBox.Text.Length > 0:
                ClearQuick();
                break;
            case Key.Escape:
                PendingList.Focus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is QuickRow row) Execute(row, navigate: true);
    }

    /// Enter on a result sends the selected tray files there; with nothing selected (or Shift) it goes there.
    async void Execute(QuickRow row, bool navigate)
    {
        // Files of the open folder selected last: they are sorted from here into subfolders or names.
        if (!navigate && _filesActive && SelectedFiles() is { Count: > 0 } files)
        {
            await ExecuteOnPlacedAsync(row, files);
            return;
        }
        var items = SelectedPending();
        bool send = !navigate && items.Count > 0;
        if (row.Hit is { } hit)
        {
            if (!send)
            {
                Navigate(hit.Folder);
                ClearQuick();
                return;
            }
            if (hit.Card is { } card)
            {
                if (items.Count > 1)
                {
                    ShowToast("Un nombre recibe un solo archivo. Para varios, elige una carpeta.");
                    return;
                }
                ClearQuick();
                await AssignAsync(items[0], card);
            }
            else
            {
                ClearQuick();
                await PlaceAsync(items, hit.Folder);
            }
        }
        else if (row.CreatePath is { } path)
        {
            ClearQuick();
            if (!send)
            {
                int added = _board.AddNames(path, _current);
                ShowToast(added > 0 ? $"Creado: {path}" : "Ya existía.");
            }
            else if (items.Count == 1) await SendToPathAsync(items[0], path);
            else await PlaceAsync(items, _current.GetOrAddPath(path));
        }
        QuickBox.Focus();
    }

    async Task ExecuteOnPlacedAsync(QuickRow row, IReadOnlyList<CardViewModel> files)
    {
        ClearQuick();
        if (row.Hit is { Card: { } card }) await RelocateAsync(files, card.Folder ?? _current, into: card);
        else if (row.Hit is { } hit) await RelocateAsync(files, hit.Folder);
        else if (row.CreatePath is { } path)
        {
            if (files is [var one])
            {
                await Preview.ReleaseAsync();
                await _board.RelocateToPathAsync(one, _current, path);
            }
            else await RelocateAsync(files, _current.GetOrAddPath(path));
        }
        // The next file left in this folder is ready to be sent on.
        if (_current.Files.Count > 0 && FileList.SelectedItem is null) FileList.SelectedIndex = 0;
        QuickBox.Focus();
    }

    async Task SendToPathAsync(PendingItem item, string path)
    {
        int index = _board.Pending.IndexOf(item);
        if (_board.Move) await Preview.ReleaseAsync();
        if (await _board.SendToPathAsync(item, _current, path) && index >= 0) SelectPendingAt(index);
    }

    // Dropping on a result row.
    void OnResultDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(typeof(PendingItem[])) || e.Data.GetDataPresent(typeof(CardViewModel[]));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnResultDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not QuickRow row) return;
        if (Placed(e) is { } placed)
        {
            await ExecuteOnPlacedAsync(row, placed);
            return;
        }
        if (Dragged(e) is not { } items) return;
        _filesActive = false;
        PendingList.SelectedItems.Clear();
        foreach (var item in items) PendingList.SelectedItems.Add(item);
        Execute(row, navigate: false);
    }
}
