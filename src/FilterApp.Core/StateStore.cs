using System.Text.Json;

namespace FilterApp.Core;

public sealed class CardData
{
    public string Name { get; set; } = "";
    public string? DestPath { get; set; }
    public string? OriginalName { get; set; }
    public string? MovedFrom { get; set; }
}

public sealed class FolderData
{
    public string Name { get; set; } = "";
    public List<FolderData> Folders { get; set; } = [];
    public List<CardData> Cards { get; set; } = [];
    public List<CardData> Files { get; set; } = [];
}

public sealed class PendingData
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
}

/// Everything a session saves: its name, tree of folders and cards, tray and destination.
/// <see cref="Cards"/>, <see cref="Files"/> and <see cref="Folders"/> are the root level of the tree
/// (sessions from before folders existed only have Cards).
public sealed class AppState
{
    public string Name { get; set; } = "";
    public List<CardData> Cards { get; set; } = [];
    public List<CardData> Files { get; set; } = [];
    public List<FolderData> Folders { get; set; } = [];
    public List<PendingData> Pending { get; set; } = [];
    public string? Destination { get; set; }
    public bool Locked { get; set; }
    public bool Holding { get; set; }
    /// Move files instead of copying them.
    public bool Move { get; set; }
    public DateTime? LastUsed { get; set; }

    /// Every named card of the tree (not the files that keep their own name).
    public IEnumerable<CardData> AllNames() => Cards.Concat(Folders.SelectMany(NamesIn));

    static IEnumerable<CardData> NamesIn(FolderData folder) => folder.Cards.Concat(folder.Folders.SelectMany(NamesIn));
}

public static class StateStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FilterApp", "state.json");

    public static AppState Load(string path)
    {
        if (!File.Exists(path)) return new AppState();
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Options) ?? new AppState();
            state.Name ??= "";
            state.Cards = CleanCards(state.Cards);
            state.Files = CleanCards(state.Files);
            state.Folders = CleanFolders(state.Folders);
            state.Pending = (state.Pending ?? []).Where(p => p is { Path: not null }).ToList();
            foreach (var item in state.Pending)
            {
                item.Name ??= System.IO.Path.GetFileName(item.Path);
                item.Group ??= "";
            }
            return state;
        }
        catch (JsonException)
        {
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new AppState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AppState();
        }
    }

    static List<CardData> CleanCards(List<CardData>? cards)
    {
        var list = (cards ?? []).Where(c => c is not null).ToList();
        foreach (var card in list) card.Name ??= "";
        return list;
    }

    static List<FolderData> CleanFolders(List<FolderData>? folders)
    {
        var list = (folders ?? []).Where(f => f is not null).ToList();
        foreach (var folder in list)
        {
            folder.Name ??= "";
            folder.Cards = CleanCards(folder.Cards);
            folder.Files = CleanCards(folder.Files);
            folder.Folders = CleanFolders(folder.Folders);
        }
        return list;
    }

    public static void Save(string path, AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
