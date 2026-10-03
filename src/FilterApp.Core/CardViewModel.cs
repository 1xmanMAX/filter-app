namespace FilterApp.Core;

public enum CardStatus { Free, Copying, Filled, Held }

public sealed class CardViewModel(string name) : Observable
{
    CardStatus _status;
    string? _destPath;
    string? _originalName;
    string? _movedFrom;
    PendingItem? _heldItem;
    bool _isDragTarget;

    public string Name { get; } = name;
    /// The folder of the tree this card lives in (set when it is added to one).
    public FolderViewModel? Folder { get; internal set; }
    /// Created for a file dropped on a folder: it keeps the file's own name and goes away when emptied.
    public bool IsAuto { get; init; }
    public CardStatus Status { get => _status; set => Set(ref _status, value); }
    public string? DestPath { get => _destPath; private set => Set(ref _destPath, value); }
    public string? OriginalName { get => _originalName; private set => Set(ref _originalName, value); }
    /// Where the file was before it was moved here; null when it was copied.
    public string? MovedFrom { get => _movedFrom; private set => Set(ref _movedFrom, value); }
    /// The file parked on this card while in hold mode, waiting to be released.
    public PendingItem? HeldItem { get => _heldItem; private set => Set(ref _heldItem, value); }
    /// UI-only: a file is being dragged over this card.
    public bool IsDragTarget { get => _isDragTarget; set => Set(ref _isDragTarget, value); }
    /// The file on this card, as shown in lists.
    public string? FileName => DestPath is not null ? Path.GetFileName(DestPath) : HeldItem?.DisplayName;
    /// The file to show in the preview: the placed copy, or the held original.
    public string? PreviewPath => DestPath ?? HeldItem?.SourcePath;

    public void Fill(string destPath, string originalName, string? movedFrom = null)
    {
        HeldItem = null;
        DestPath = destPath;
        OriginalName = originalName;
        MovedFrom = movedFrom;
        Status = CardStatus.Filled;
        FileChanged();
    }

    public void Hold(PendingItem item)
    {
        HeldItem = item;
        Status = CardStatus.Held;
        FileChanged();
    }

    public void Clear()
    {
        HeldItem = null;
        DestPath = null;
        OriginalName = null;
        MovedFrom = null;
        Status = CardStatus.Free;
        FileChanged();
    }

    void FileChanged()
    {
        Notify(nameof(FileName));
        Notify(nameof(PreviewPath));
    }
}
