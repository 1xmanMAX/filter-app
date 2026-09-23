namespace FilterApp.Core;

public enum CardStatus { Free, Copying, Filled }

public sealed class CardViewModel(string name) : Observable
{
    CardStatus _status;
    string? _destPath;
    string? _originalName;
    bool _isDragTarget;

    public string Name { get; } = name;
    public CardStatus Status { get => _status; set => Set(ref _status, value); }
    public string? DestPath { get => _destPath; private set => Set(ref _destPath, value); }
    public string? OriginalName { get => _originalName; private set => Set(ref _originalName, value); }
    /// UI-only: a file is being dragged over this card.
    public bool IsDragTarget { get => _isDragTarget; set => Set(ref _isDragTarget, value); }

    public void Fill(string destPath, string originalName)
    {
        DestPath = destPath;
        OriginalName = originalName;
        Status = CardStatus.Filled;
    }

    public void Clear()
    {
        DestPath = null;
        OriginalName = null;
        Status = CardStatus.Free;
    }
}
