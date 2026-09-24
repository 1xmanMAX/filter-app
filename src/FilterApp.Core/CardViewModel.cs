namespace FilterApp.Core;

public enum CardStatus { Free, Copying, Filled, Held }

public sealed class CardViewModel(string name) : Observable
{
    CardStatus _status;
    string? _destPath;
    string? _originalName;
    PendingItem? _heldItem;
    bool _isDragTarget;

    public string Name { get; } = name;
    public CardStatus Status { get => _status; set => Set(ref _status, value); }
    public string? DestPath { get => _destPath; private set => Set(ref _destPath, value); }
    public string? OriginalName { get => _originalName; private set => Set(ref _originalName, value); }
    /// The file parked on this card while in hold mode, waiting to be released.
    public PendingItem? HeldItem { get => _heldItem; private set => Set(ref _heldItem, value); }
    /// UI-only: a file is being dragged over this card.
    public bool IsDragTarget { get => _isDragTarget; set => Set(ref _isDragTarget, value); }

    public void Fill(string destPath, string originalName)
    {
        HeldItem = null;
        DestPath = destPath;
        OriginalName = originalName;
        Status = CardStatus.Filled;
    }

    public void Hold(PendingItem item)
    {
        HeldItem = item;
        Status = CardStatus.Held;
    }

    public void Clear()
    {
        HeldItem = null;
        DestPath = null;
        OriginalName = null;
        Status = CardStatus.Free;
    }
}
