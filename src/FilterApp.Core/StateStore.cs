using System.Text.Json;

namespace FilterApp.Core;

public sealed class CardData
{
    public string Name { get; set; } = "";
    public string? DestPath { get; set; }
    public string? OriginalName { get; set; }
}

public sealed class AppState
{
    public List<CardData> Cards { get; set; } = [];
    public string? Destination { get; set; }
    public bool Locked { get; set; }
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
            state.Cards = (state.Cards ?? []).Where(c => c is not null).ToList();
            foreach (var card in state.Cards) card.Name ??= "";
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

    public static void Save(string path, AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
