namespace FilterApp.Core;

/// Turns a card name into a safe, unique destination path.
public static class NameResolver
{
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string cardName)
    {
        var chars = cardName.Select(c => Array.IndexOf(Invalid, c) >= 0 ? '_' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "_";
        return Reserved.Contains(name) ? "_" + name : name;
    }

    public static string Resolve(string destDir, string cardName, string sourceFileName, Func<string, bool> exists)
    {
        var baseName = Sanitize(cardName);
        var ext = Path.GetExtension(sourceFileName);
        var candidate = Path.Combine(destDir, baseName + ext);
        for (int n = 2; exists(candidate); n++)
            candidate = Path.Combine(destDir, $"{baseName} ({n}){ext}");
        return candidate;
    }
}
