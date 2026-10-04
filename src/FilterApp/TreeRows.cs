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
