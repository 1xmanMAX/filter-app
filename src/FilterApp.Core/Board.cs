using System.Collections.ObjectModel;

namespace FilterApp.Core;

/// Main view-model: cards, pending tray, destination and the assign/undo rules.
public sealed class Board : Observable
{
    string? _destination;
    bool _locked;

    public ObservableCollection<CardViewModel> Cards { get; } = [];
    public ObservableCollection<PendingItem> Pending { get; } = [];

    /// Raised when something that must be persisted changed.
    public event Action? Changed;
    /// Raised with a short message for the user.
    public event Action<string>? Notified;

    public string? Destination
    {
        get => _destination;
        set { if (Set(ref _destination, value)) Changed?.Invoke(); }
    }

    public bool Locked
    {
        get => _locked;
        set { if (Set(ref _locked, value)) Changed?.Invoke(); }
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
            if (!Pending.Any(p => string.Equals(p.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase)))
                Pending.Add(item);
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
        var dest = Destination;
        if (dest is null || !Directory.Exists(dest))
            return Fail(item, "Haz clic en una carpeta del Explorador para elegir el destino.");

        card.Status = CardStatus.Copying;
        Pending.Remove(item);
        try
        {
            var path = await Copier.CopyAsync(item.SourcePath, dest, card.Name);
            card.Fill(path, item.DisplayName);
            if (item.IsTemp) TryDelete(item.SourcePath);
            Changed?.Invoke();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            card.Status = CardStatus.Free;
            return Fail(item, $"No se pudo copiar «{item.DisplayName}»: {e.Message}");
        }
    }

    public void Undo(CardViewModel card)
    {
        if (card.Status != CardStatus.Filled) return;
        try
        {
            Copier.Undo(card.DestPath!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notified?.Invoke($"No se pudo borrar la copia: {e.Message}");
            return;
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

    public AppState ToState() => new()
    {
        Destination = Destination,
        Locked = Locked,
        Cards = Cards.Select(c => c.Status == CardStatus.Filled
            ? new CardData { Name = c.Name, DestPath = c.DestPath, OriginalName = c.OriginalName }
            : new CardData { Name = c.Name }).ToList(),
    };

    public static Board FromState(AppState state)
    {
        var board = new Board { _destination = state.Destination, _locked = state.Locked };
        foreach (var data in state.Cards)
        {
            var card = new CardViewModel(data.Name);
            if (data.DestPath is not null) card.Fill(data.DestPath, data.OriginalName ?? "");
            board.Cards.Add(card);
        }
        return board;
    }

    /// Every failed assignment leaves the file in the tray so nothing is lost.
    bool Fail(PendingItem item, string message)
    {
        if (!Pending.Contains(item)) Pending.Insert(0, item);
        Notified?.Invoke(message);
        return false;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
