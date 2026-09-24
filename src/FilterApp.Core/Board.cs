using System.Collections.ObjectModel;

namespace FilterApp.Core;

/// Main view-model: cards, pending tray, destination and the assign/undo rules.
public sealed class Board : Observable
{
    const string NoDestination = "Haz clic en una carpeta del Explorador para elegir el destino.";

    string _name = "";
    string? _destination;
    bool _locked;
    bool _holding;
    bool _releasing;

    public ObservableCollection<CardViewModel> Cards { get; } = [];
    public ObservableCollection<PendingItem> Pending { get; } = [];

    public Board()
    {
        // The tray is part of the saved session.
        Pending.CollectionChanged += (_, _) => Changed?.Invoke();
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

    public bool DestinationExists => Destination is not null && Directory.Exists(Destination);
    /// A destination was chosen but the folder is gone (deleted, renamed, drive unplugged).
    public bool DestinationMissing => Destination is not null && !DestinationExists;
    public int HeldCount => Cards.Count(c => c.Status == CardStatus.Held);
    public bool CanRelease => !_releasing && HeldCount > 0 && DestinationExists;
    public bool IsCopying => Cards.Any(c => c.Status == CardStatus.Copying);
    /// Files that live only in the app's temp folder (pasted images, Outlook attachments) and are not saved.
    public int UnsavedCount => WaitingItems().Count(p => p.IsTemp);

    /// Re-checks the destination folder (it may have been deleted or renamed outside the app).
    public void RefreshDestination()
    {
        Notify(nameof(DestinationExists));
        Notify(nameof(DestinationMissing));
        Notify(nameof(CanRelease));
    }

    public int AddNames(string text)
    {
        var names = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var name in names) Cards.Add(new CardViewModel(name));
        if (names.Count > 0) Changed?.Invoke();
        return names.Count;
    }

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
        return await CopyToCardAsync(item, card, dest);
    }

    /// Copies every held file into the current destination. Returns how many were copied.
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
            foreach (var card in Cards.Where(c => c.Status == CardStatus.Held).ToList())
            {
                // The user may have sent it back to the tray while earlier files were copying.
                if (card.Status != CardStatus.Held || card.HeldItem is not { } item) continue;
                if (await CopyToCardAsync(item, card, dest)) copied++;
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
        AddPending([item]);
        HeldChanged();
    }

    public void Undo(CardViewModel card)
    {
        if (card.Status != CardStatus.Filled) return;
        // Two cards can end up pointing at the same file (the user deleted the first copy by hand and
        // a card with the same name reused the name); that file belongs to the other card now.
        bool shared = Cards.Any(c => c != card && c.Status == CardStatus.Filled &&
                                     string.Equals(c.DestPath, card.DestPath, StringComparison.OrdinalIgnoreCase));
        if (!shared)
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
        Changed?.Invoke();
    }

    public void RemoveCard(CardViewModel card)
    {
        if (card.Status == CardStatus.Free && Cards.Remove(card)) Changed?.Invoke();
    }

    public void ClearFilled()
    {
        foreach (var card in Cards.Where(c => c.Status == CardStatus.Filled).ToList()) Cards.Remove(card);
        Changed?.Invoke();
    }

    /// Held files are saved back into the tray (they were never copied). Temp files are not saved.
    public AppState ToState() => new()
    {
        Name = Name,
        Destination = Destination,
        Locked = Locked,
        Holding = Holding,
        Cards = Cards.Select(c => c.Status == CardStatus.Filled
            ? new CardData { Name = c.Name, DestPath = c.DestPath, OriginalName = c.OriginalName }
            : new CardData { Name = c.Name }).ToList(),
        Pending = WaitingItems().Where(p => !p.IsTemp)
            .Select(p => new PendingData { Path = p.SourcePath, Name = p.DisplayName }).ToList(),
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
        Cards.Clear();
        Pending.Clear();

        _name = state.Name;
        _destination = state.Destination;
        _locked = state.Locked;
        _holding = state.Holding;
        foreach (var data in state.Cards)
        {
            var card = new CardViewModel(data.Name);
            if (data.DestPath is not null) card.Fill(data.DestPath, data.OriginalName ?? "");
            Cards.Add(card);
        }
        // Files can be moved or deleted between sessions; only the ones still there come back.
        AddPending(state.Pending.Where(p => File.Exists(p.Path)).Select(p => new PendingItem(p.Path, p.Name, isTemp: false)));
        if (state.Destination is not null) Copier.CleanPartials(state.Destination);
        Notify(string.Empty);   // every property changed
    }

    /// Files not copied yet: the tray plus the ones held on cards.
    IEnumerable<PendingItem> WaitingItems() =>
        Pending.Concat(Cards.Where(c => c.Status == CardStatus.Held).Select(c => c.HeldItem!));

    async Task<bool> CopyToCardAsync(PendingItem item, CardViewModel card, string dest)
    {
        card.Status = CardStatus.Copying;
        try
        {
            var path = await Copier.CopyAsync(item.SourcePath, dest, card.Name);
            card.Fill(path, item.DisplayName);
            if (item.IsTemp) TryDelete(item.SourcePath);
            Changed?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            // Any failure, expected or not, must leave the card usable and the file in the tray.
            card.Clear();
            return Fail(item, $"No se pudo copiar «{item.DisplayName}»: {e.Message}");
        }
    }

    /// Every failed assignment leaves the file in the tray so nothing is lost.
    bool Fail(PendingItem item, string message)
    {
        if (!IsPending(item)) Pending.Insert(0, item);
        Notified?.Invoke(message);
        return false;
    }

    bool IsPending(PendingItem item) =>
        Pending.Any(p => string.Equals(p.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase));

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
