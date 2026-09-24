using System.Globalization;

namespace FilterApp.Core;

public sealed record SessionInfo(string Id, string Name, int Filled, int Total, DateTime LastUsed)
{
    public bool IsComplete => Total > 0 && Filled == Total;

    static readonly string[] Months = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    /// "hoy", "ayer", "hace 3 días", "5 sep" or "5 sep 2025".
    public static string WhenText(DateTime when, DateTime now)
    {
        int days = (now.Date - when.Date).Days;
        if (days <= 0) return "hoy";
        if (days == 1) return "ayer";
        if (days < 7) return $"hace {days} días";
        var text = $"{when.Day.ToString(CultureInfo.InvariantCulture)} {Months[when.Month - 1]}";
        return when.Year == now.Year ? text : $"{text} {when.Year}";
    }
}

/// Named sessions, one JSON file each under &lt;root&gt;/sessions, plus which one is open.
public sealed class SessionStore(string root)
{
    public const string DefaultName = "Mi sesión";

    public static SessionStore Default { get; } = new(Path.GetDirectoryName(StateStore.DefaultPath)!);

    string SessionsDir => Path.Combine(root, "sessions");
    string CurrentFile => Path.Combine(root, "current.txt");
    /// Where the single board lived before sessions existed.
    string LegacyFile => Path.Combine(root, "state.json");
    string FileOf(string id) => Path.Combine(SessionsDir, id + ".json");

    public string? CurrentId
    {
        get
        {
            try { return File.Exists(CurrentFile) ? File.ReadAllText(CurrentFile).Trim() : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        set
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(CurrentFile, value ?? "");
        }
    }

    public bool Exists(string id) => File.Exists(FileOf(id));

    /// Most recently used first.
    public IReadOnlyList<SessionInfo> List()
    {
        if (!Directory.Exists(SessionsDir)) return [];
        return Directory.EnumerateFiles(SessionsDir, "*.json")
            .Select(path =>
            {
                var state = StateStore.Load(path);
                return new SessionInfo(Path.GetFileNameWithoutExtension(path), state.Name,
                                       state.Cards.Count(c => c.DestPath is not null), state.Cards.Count,
                                       state.LastUsed ?? File.GetLastWriteTime(path));
            })
            .OrderByDescending(s => s.LastUsed)
            .ToList();
    }

    public AppState Load(string id) => StateStore.Load(FileOf(id));

    /// Saves a session; <paramref name="touch"/> marks it as used now.
    public void Save(string id, AppState state, bool touch = true)
    {
        if (touch) state.LastUsed = DateTime.Now;
        StateStore.Save(FileOf(id), state);
    }

    public string Create(string name, AppState? state = null)
    {
        var id = Guid.NewGuid().ToString("N");
        state ??= new AppState();
        state.Name = name;
        Save(id, state);
        return id;
    }

    /// Renaming is not "using" a session: its place in the list stays the same.
    public void Rename(string id, string name)
    {
        var state = Load(id);
        state.Name = name;
        Save(id, state, touch: false);
    }

    public void Delete(string id)
    {
        if (File.Exists(FileOf(id))) File.Delete(FileOf(id));
    }

    /// Opens the session used last time. The first run turns the old single board into "Mi sesión".
    public string OpenCurrent(out AppState state)
    {
        if (CurrentId is { Length: > 0 } current && Exists(current))
        {
            state = Load(current);
            return current;
        }

        string id;
        if (List().FirstOrDefault() is { } recent)
            id = recent.Id;
        else
            id = Create(DefaultName, File.Exists(LegacyFile) ? StateStore.Load(LegacyFile) : null);
        CurrentId = id;
        state = Load(id);
        return id;
    }
}
