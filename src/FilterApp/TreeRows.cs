using System.IO;
using System.Windows.Media;
using FilterApp.Core;

namespace FilterApp;

/// One folder of the path shown above the cards.
public sealed record Crumb(FolderViewModel Folder, bool IsCurrent)
{
    public bool IsRoot => Folder.IsRoot;
    public string Label => Folder.IsRoot ? "Destino" : Folder.Name;
}

/// A row of the quick bar: a search hit, or "create this path".
public sealed record QuickRow(string Icon, Brush IconColor, string Title, string Subtitle, SearchHit? Hit, string? CreatePath)
{
    static readonly Brush FolderColor = Frozen("#BF8700");
    static readonly Brush NameColor = Frozen("#0969DA");
    static readonly Brush CreateColor = Frozen("#1A7F37");

    static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    static string Where(string location) => location.Length == 0 ? "Destino" : $"Destino / {location}";

    public static QuickRow From(SearchHit hit) => hit.IsFolder
        ? new("", FolderColor, hit.Name, $"Carpeta en {Where(hit.Location)}", hit, null)
        : new("", NameColor, hit.Name, $"Nombre en {Where(hit.Location)}", hit, null);

    public static QuickRow Create(string path, FolderViewModel current)
    {
        path = path.Trim();
        bool keepName = path.EndsWith('/') || path.EndsWith('\\');
        var how = keepName ? "conserva el nombre del archivo" : "la última parte es el nombre del archivo";
        return new("", CreateColor, $"Crear: {path}", $"En {Where(current.DisplayPath)} · {how}", null, path);
    }
}

/// The "+ New folder" / "+ New name" tile at the end of the open folder: click, type, Enter.
/// Files dropped on it wait there until the new folder or name is typed, then go into it.
public sealed class NewTile(bool isFolder) : FilterApp.Core.Observable
{
    bool _isEditing, _isDragTarget;
    string _text = "";
    string _hint = "";

    public bool IsFolder { get; } = isFolder;
    public string Label => IsFolder ? "Nueva carpeta" : "Nuevo nombre";
    public string Icon => IsFolder ? "\uE8F4" : "\uE8A5";
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    public bool IsDragTarget { get => _isDragTarget; set => Set(ref _isDragTarget, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Hint { get => _hint; set => Set(ref _hint, value); }
    /// Files dropped on the tile, waiting for the name.
    public IReadOnlyList<PendingItem>? Waiting { get; private set; }
    /// The waiting files came from a dropped folder and keep its subfolders.
    public bool KeepFolders { get; private set; }
    /// Files already placed in the tree, dropped on the tile to go into the new folder (or take the new name).
    public IReadOnlyList<CardViewModel>? WaitingPlaced { get; private set; }

    public void StartWithPlaced(IReadOnlyList<CardViewModel> files)
    {
        Start(files.Select(f => new PendingItem(f.PreviewPath ?? "", f.FileName ?? "", false)).ToList());
        Waiting = null;
        WaitingPlaced = files;
    }

    public void Start(IReadOnlyList<PendingItem>? waiting = null, bool keepFolders = false)
    {
        Waiting = waiting;
        KeepFolders = keepFolders;
        Hint = (IsFolder, waiting?.Count ?? 0) switch
        {
            (true, 0) => "Nombre · Enter crea otra",
            (true, var n) => n == 1 ? "Carpeta nueva para 1 archivo:" : $"Carpeta nueva para {n} archivos:",
            (false, 0) => "Nombre · Enter crea otro",
            _ => "Nombre para este archivo:",
        };
        Text = !IsFolder && waiting is [var one] ? Path.GetFileNameWithoutExtension(one.DisplayName) : "";
        IsEditing = true;
    }

    public void Stop()
    {
        Waiting = null;
        WaitingPlaced = null;
        KeepFolders = false;
        Text = "";
        IsEditing = false;
        IsDragTarget = false;
    }
}
