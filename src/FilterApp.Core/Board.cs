using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;

namespace FilterApp.Core;

/// A search result: a folder, or a free named card inside <see cref="Folder"/>.
public sealed record SearchHit(FolderViewModel Folder, CardViewModel? Card)
{
    public bool IsFolder => Card is null;
    public string Name => Card?.Name ?? Folder.Name;
    /// Where it is: "Clientes / Juan" (empty at the top level).
    public string Location => Card is null ? Folder.Parent?.DisplayPath ?? "" : Folder.DisplayPath;
}

/// Main view-model: the destination tree, pending tray, destination and the assign/undo rules.
public sealed class Board : Observable
{
    const string NoDestination = "Haz clic en una carpeta del Explorador para elegir el destino.";

    string _name = "";
    string? _destination;
    bool _locked;
    bool _holding;
    bool _move;
    bool _releasing;
    readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    /// Filled cards in the order they were filled, for Ctrl+Z.
    readonly List<CardViewModel> _history = [];

    /// The destination folder itself. Its subfolders, cards and files form the tree.
    public FolderViewModel Root { get; } = new("");
    /// Named cards at the top level.
    public ObservableCollection<CardViewModel> Cards => Root.Cards;
    public ObservableCollection<PendingItem> Pending { get; } = [];

    public Board()
    {
        // The tray is part of the saved session.
        Pending.CollectionChanged += OnPendingChanged;
    }

    /// Raised when something that must be persisted changed.
    public event Action? Changed;
    /// Raised with a short message for the user.
    public event Action<string>? Notified;

    /// Name of the session this board belongs to.
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Changed?.Invoke(); }
    }

    public string? Destination
    {
        get => _destination;
        set
        {
            if (!Set(ref _destination, value)) return;
            // A copy in progress owns its .partial file; leftovers can only be cleaned when idle.
            if (value is not null && !IsCopying) Copier.CleanPartials(value);
            RefreshDestination();
            Changed?.Invoke();
        }
    }

    public bool Locked
    {
        get => _locked;
        set { if (Set(ref _locked, value)) Changed?.Invoke(); }
    }

    /// Hold mode: files dropped on cards wait there until <see cref="ReleaseAsync"/>.
    public bool Holding
    {
        get => _holding;
        set { if (Set(ref _holding, value)) Changed?.Invoke(); }
    }

    /// Move files instead of copying them (the original disappears from where it was).
    public bool Move
    {
        get => _move;
        set { if (Set(ref _move, value)) Changed?.Invoke(); }
    }

    public bool DestinationExists => Destination is not null && Directory.Exists(Destination);
    /// A destination was chosen but the folder is gone (deleted, renamed, drive unplugged).
    public bool DestinationMissing => Destination is not null && !DestinationExists;
    public int HeldCount => AllCards().Count(c => c.Status == CardStatus.Held);
    public bool CanRelease => !_releasing && HeldCount > 0 && DestinationExists;
    public bool IsCopying => AllCards().Any(c => c.Status == CardStatus.Copying);
    /// Files that live only in the app's temp folder (pasted images, Outlook attachments) and are not saved.
    public int UnsavedCount => WaitingItems().Count(p => p.IsTemp);
    public bool CanUndo => _history.Any(c => c.Status == CardStatus.Filled);

    public IEnumerable<CardViewModel> AllCards() => Root.AllCards();

    /// Re-checks the destination folder (it may have been deleted or renamed outside the app).
    public void RefreshDestination()
    {
        Notify(nameof(DestinationExists));
        Notify(nameof(DestinationMissing));
        Notify(nameof(CanRelease));
    }

    // ---- Building the tree ----

    /// Adds names and folders from text (see <see cref="StructureParser"/>). Returns how many were added.
    public int AddNames(string text, FolderViewModel? into = null)
    {
        var result = StructureParser.Apply(into ?? Root, text);
        if (result.Total > 0) Changed?.Invoke();
        return result.Total;
    }

    public FolderViewModel AddFolder(FolderViewModel parent, string name)
    {
        var folder = parent.GetOrAddFolder(name);
        Changed?.Invoke();
        return folder;
    }

    /// Only renames the folder in the list: what was already placed stays where it is on disk.
    public void RenameFolder(FolderViewModel folder, string name)
    {
        name = name.Trim();
        if (folder.IsRoot || name.Length == 0) return;
        if (folder.Parent!.FindFolder(name) is { } other && other != folder)
        {
            Notified?.Invoke($"Ya hay una carpeta «{name}» aquí.");
            return;
        }
        folder.Name = name;
        Changed?.Invoke();
    }

    /// Removes a folder from the list (never from disk). Refused while files inside are copying or held.
    public bool RemoveFolder(FolderViewModel folder)
    {
        if (folder.IsRoot) return false;
        if (folder.IsBusy)
        {
            Notified?.Invoke($"«{folder.Name}» tiene archivos en espera o copiándose.");
            return false;
        }
        folder.Parent!.Folders.Remove(folder);
        Changed?.Invoke();
        return true;
    }

    /// Folders and free named cards whose name contains <paramref name="text"/> (ignoring case and accents),
    /// names that start with it first.
    public IReadOnlyList<SearchHit> Search(string text, int limit = 50)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        int Rank(string name) => compare.IsPrefix(name, text, options) ? 0 : compare.IndexOf(name, text, options) >= 0 ? 1 : -1;

        var hits = new List<(int Rank, SearchHit Hit)>();
        foreach (var folder in Root.AllFolders().Prepend(Root))
        {
            if (!folder.IsRoot && Rank(folder.Name) is var r and >= 0) hits.Add((r, new SearchHit(folder, null)));
            foreach (var card in folder.Cards.Where(c => c.Status == CardStatus.Free))
                if (Rank(card.Name) is var rc and >= 0) hits.Add((rc, new SearchHit(folder, card)));
        }
        return hits.OrderBy(h => h.Rank).Select(h => h.Hit).Take(limit).ToList();
    }

    // ---- Tray ----

    public void AddPending(IEnumerable<PendingItem> items)
    {
        foreach (var item in items)
            if (!IsPending(item)) Pending.Add(item);
    }

    public void RemovePending(PendingItem item)
    {
        if (Pending.Remove(item) && item.IsTemp) TryDelete(item.SourcePath);
    }

    public void OnExplorerFolder(string path)
    {
        if (!Locked) Destination = path;
    }

    // ---- Placing files ----

    /// Puts a file on a named card: it takes the card's name.
    public async Task<bool> AssignAsync(PendingItem item, CardViewModel card)
    {
        if (card.Status != CardStatus.Free)
            return Fail(item, $"«{card.Name}» ya tiene un archivo.");
        if (Holding)
        {
            Pending.Remove(item);
            card.Hold(item);
            HeldChanged();
            return true;
        }
        var dest = Destination;
        if (dest is null || !Directory.Exists(dest)) return Fail(item, NoDestination);

        Pending.Remove(item);
        return await TransferToCardAsync(item, card, dest);
    }

    /// Puts a file in a folder of the tree. It keeps its own name unless <paramref name="name"/> is given.
    public async Task<bool> PlaceAsync(PendingItem item, FolderViewModel folder, string? name = null)
    {
        if (!Holding && !DestinationExists) return Fail(item, NoDestination);
        var card = new CardViewModel(name ?? Path.GetFileNameWithoutExtension(item.DisplayName)) { IsAuto = true };
        folder.Files.Add(card);
        bool ok = await AssignAsync(item, card);
        if (!ok) folder.Files.Remove(card);
        return ok;
    }

    /// Places several files in a folder, one after another. With <paramref name="keepFolders"/> each file goes
    /// into the subfolder its <see cref="PendingItem.Group"/> names (a folder dropped whole keeps its tree).
    /// <paramref name="progress"/> receives (done, total). Returns how many were placed.
    public async Task<int> PlaceManyAsync(IReadOnlyList<PendingItem> items, FolderViewModel folder,
                                          bool keepFolders = false, Action<int, int>? progress = null)
    {
        if (!Holding && !DestinationExists)
        {
            AddPending(items);
            Notified?.Invoke(NoDestination);
            return 0;
        }
        int placed = 0, done = 0;
        foreach (var item in items)
        {
            var target = keepFolders && item.Group.Length > 0 ? folder.GetOrAddPath(item.Group) : folder;
            if (await PlaceAsync(item, target)) placed++;
            progress?.Invoke(++done, items.Count);
        }
        return placed;
    }

    /// Sends a file to a path typed below <paramref name="from"/>, creating what is missing:
    /// "Clientes/Juan/DNI" puts it in Clientes/Juan named DNI; "Clientes/Juan/" keeps its own name.
    public async Task<bool> SendToPathAsync(PendingItem item, FolderViewModel from, string path)
    {
        bool keepName = path.TrimEnd().EndsWith('/') || path.TrimEnd().EndsWith('\\');
        var parts = path.Split('/', '\\').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return await PlaceAsync(item, from);

        var folder = from.GetOrAddPath(string.Join('/', keepName ? parts : parts[..^1]));
        Changed?.Invoke();
        if (keepName) return await PlaceAsync(item, folder);

        var name = parts[^1];
        var card = folder.Cards.FirstOrDefault(c => c.Status == CardStatus.Free &&
                                                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            card = new CardViewModel(name);
            folder.Cards.Add(card);
        }
        return await AssignAsync(item, card);
    }

    /// Copies (or moves) every held file into the current destination. Returns how many were placed.
    public async Task<int> ReleaseAsync()
    {
        if (_releasing || HeldCount == 0) return 0;
        var dest = Destination;
        if (dest is null || !Directory.Exists(dest))
        {
            Notified?.Invoke(NoDestination);
            return 0;
        }

        _releasing = true;
        HeldChanged();
        int copied = 0;
        try
        {
            foreach (var card in AllCards().Where(c => c.Status == CardStatus.Held).ToList())
            {
                // The user may have sent it back to the tray while earlier files were copying.
                if (card.Status != CardStatus.Held || card.HeldItem is not { } item) continue;
                if (await TransferToCardAsync(item, card, dest)) copied++;
            }
        }
        finally
        {
            _releasing = false;
            HeldChanged();
        }
        return copied;
    }

    public void ReturnHeld(CardViewModel card)
    {
        if (card.Status != CardStatus.Held || card.HeldItem is not { } item) return;
        card.Clear();
        RemoveIfAuto(card);
        AddPending([item]);
        HeldChanged();
    }

    // ---- Undo ----

    /// Copied: deletes the copy. Moved: puts the file back where it was and in the tray.
    public void Undo(CardViewModel card)
    {
        if (card.Status != CardStatus.Filled) return;
        if (card.MovedFrom is { } from)
        {
            if (File.Exists(card.DestPath))
            {
                try
                {
                    var back = Transfer.MoveBack(card.DestPath!, from);
                    AddPending([new PendingItem(back, Path.GetFileName(back), isTemp: false)]);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Notified?.Invoke($"No se pudo devolver el archivo: {e.Message}");
                    return;
                }
            }
        }
        else if (!IsShared(card))
        {
            try
            {
                Copier.Undo(card.DestPath!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Notified?.Invoke($"No se pudo borrar la copia: {e.Message}");
                return;
            }
        }
        card.Clear();
        RemoveIfAuto(card);
        _history.Remove(card);
        Notify(nameof(CanUndo));
        Changed?.Invoke();
    }

    /// Undoes the most recent placement that is still there. Returns its card, or null if nothing to undo.
    public CardViewModel? UndoLast()
    {
        var card = _history.LastOrDefault(c => c.Status == CardStatus.Filled);
        if (card is not null) Undo(card);
        return card;
    }

    public void RemoveCard(CardViewModel card)
    {
        if (card.Status == CardStatus.Free && card.Folder is { } folder && folder.Cards.Remove(card)) Changed?.Invoke();
    }

    /// Takes the finished cards and files off the list (the files stay on disk). Folders stay.
    public void ClearFilled()
    {
        foreach (var folder in Root.AllFolders().Prepend(Root).ToList())
        {
            foreach (var card in folder.Cards.Where(c => c.Status == CardStatus.Filled).ToList()) folder.Cards.Remove(card);
            foreach (var card in folder.Files.Where(c => c.Status == CardStatus.Filled).ToList()) folder.Files.Remove(card);
        }
        _history.Clear();
        Notify(nameof(CanUndo));
        Changed?.Invoke();
    }

    // ---- Saving ----

    /// Held files are saved back into the tray (they were never copied). Temp files are not saved.
    public AppState ToState() => new()
    {
        Name = Name,
        Destination = Destination,
        Locked = Locked,
        Holding = Holding,
        Move = Move,
        Cards = Root.Cards.Select(ToData).ToList(),
        Files = FilledFiles(Root),
        Folders = Root.Folders.Select(ToData).ToList(),
        Pending = WaitingItems().Where(p => !p.IsTemp)
            .Select(p => new PendingData { Path = p.SourcePath, Name = p.DisplayName, Group = p.Group }).ToList(),
    };

    static CardData ToData(CardViewModel c) => c.Status == CardStatus.Filled
        ? new CardData { Name = c.Name, DestPath = c.DestPath, OriginalName = c.OriginalName, MovedFrom = c.MovedFrom }
        : new CardData { Name = c.Name };

    static List<CardData> FilledFiles(FolderViewModel folder) =>
        folder.Files.Where(c => c.Status == CardStatus.Filled).Select(ToData).ToList();

    static FolderData ToData(FolderViewModel f) => new()
    {
        Name = f.Name,
        Folders = f.Folders.Select(ToData).ToList(),
        Cards = f.Cards.Select(ToData).ToList(),
        Files = FilledFiles(f),
    };

    public static Board FromState(AppState state)
    {
        var board = new Board();
        board.LoadState(state);
        return board;
    }

    /// Replaces the whole board with a saved session. Unsaved temp files of the old one are deleted.
    public void LoadState(AppState state)
    {
        foreach (var item in WaitingItems().Where(p => p.IsTemp).ToList()) TryDelete(item.SourcePath);
        Root.Folders.Clear();
        Root.Cards.Clear();
        Root.Files.Clear();
        Pending.Clear();
        _history.Clear();

        _name = state.Name;
        _destination = state.Destination;
        _locked = state.Locked;
        _holding = state.Holding;
        _move = state.Move;
        Fill(Root, state.Cards, state.Files, state.Folders);
        // Files can be moved or deleted between sessions; only the ones still there come back.
        AddPending(state.Pending.Where(p => File.Exists(p.Path))
                                .Select(p => new PendingItem(p.Path, p.Name, isTemp: false, p.Group)));
        if (state.Destination is not null) Copier.CleanPartials(state.Destination);
        Notify(string.Empty);   // every property changed
    }

    static void Fill(FolderViewModel folder, List<CardData> cards, List<CardData> files, List<FolderData> folders)
    {
        foreach (var data in folders)
        {
            var sub = folder.GetOrAddFolder(data.Name);
            Fill(sub, data.Cards, data.Files, data.Folders);
        }
        foreach (var data in cards) folder.Cards.Add(FromData(data, auto: false));
        foreach (var data in files.Where(f => f.DestPath is not null)) folder.Files.Add(FromData(data, auto: true));
    }

    static CardViewModel FromData(CardData data, bool auto)
    {
        var card = new CardViewModel(data.Name) { IsAuto = auto };
        if (data.DestPath is not null) card.Fill(data.DestPath, data.OriginalName ?? "", data.MovedFrom);
        return card;
    }

    // ---- Internals ----

    /// Files not copied yet: the tray plus the ones held on cards.
    IEnumerable<PendingItem> WaitingItems() =>
        Pending.Concat(AllCards().Where(c => c.Status == CardStatus.Held).Select(c => c.HeldItem!));

    async Task<bool> TransferToCardAsync(PendingItem item, CardViewModel card, string dest)
    {
        card.Status = CardStatus.Copying;
        try
        {
            var dir = card.Folder?.DirIn(dest) ?? dest;
            // Pasted images and Outlook attachments only exist in the temp folder: always copied, then deleted.
            var result = await Transfer.RunAsync(item.SourcePath, dir, card.Name, Move && !item.IsTemp);
            card.Fill(result.DestPath, item.DisplayName, result.Moved ? item.SourcePath : null);
            if (item.IsTemp) TryDelete(item.SourcePath);
            if (Move && !item.IsTemp && !result.Moved)
                Notified?.Invoke($"«{item.DisplayName}» se copió, pero el original no se pudo borrar.");
            _history.Add(card);
            Notify(nameof(CanUndo));
            Changed?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            // Any failure, expected or not, must leave the card usable and the file in the tray.
            card.Clear();
            RemoveIfAuto(card);
            return Fail(item, $"No se pudo {(Move ? "mover" : "copiar")} «{item.DisplayName}»: {e.Message}");
        }
    }

    static void RemoveIfAuto(CardViewModel card)
    {
        if (card.IsAuto) card.Folder?.Files.Remove(card);
    }

    /// Two cards can end up pointing at the same file (the user deleted the first copy by hand and
    /// a card with the same name reused the name); that file belongs to the other card now.
    bool IsShared(CardViewModel card) =>
        AllCards().Any(c => c != card && c.Status == CardStatus.Filled &&
                            string.Equals(c.DestPath, card.DestPath, StringComparison.OrdinalIgnoreCase));

    /// Every failed assignment leaves the file in the tray so nothing is lost.
    bool Fail(PendingItem item, string message)
    {
        if (!IsPending(item)) Pending.Insert(0, item);
        Notified?.Invoke(message);
        return false;
    }

    bool IsPending(PendingItem item) => _pendingPaths.Contains(item.SourcePath);

    void OnPendingChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _pendingPaths.Clear();
            foreach (var p in Pending) _pendingPaths.Add(p.SourcePath);
        }
        else
        {
            foreach (PendingItem p in e.OldItems ?? Array.Empty<PendingItem>()) _pendingPaths.Remove(p.SourcePath);
            foreach (PendingItem p in e.NewItems ?? Array.Empty<PendingItem>()) _pendingPaths.Add(p.SourcePath);
        }
        Changed?.Invoke();
    }

    void HeldChanged()
    {
        Notify(nameof(HeldCount));
        Notify(nameof(CanRelease));
        Changed?.Invoke();   // held files are saved with the session
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
