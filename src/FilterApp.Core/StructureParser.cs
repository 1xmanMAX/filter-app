namespace FilterApp.Core;

/// Builds folders and names from text, one entry per line. No special characters are needed:
///   a line with indented lines under it   is a folder holding them
///   any other line                         is a name (or a folder, with <c>leavesAreFolders</c>)
/// Power users can still write "Carpeta/" (always a folder) or paths like "A/B/Nombre".
/// Folders that already exist (same name, any case) are reused, never duplicated.
public static class StructureParser
{
    public sealed record Result(int Folders, int Names)
    {
        public int Total => Folders + Names;
    }

    public static Result Apply(FolderViewModel into, string text, bool leavesAreFolders = false)
    {
        int folders = 0, names = 0;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0)
                        .Select(l => (Indent: Indent(l), Content: l.Trim())).ToList();
        // Folders opened by previous lines, with the indentation of the line that opened them.
        var open = new Stack<(int Indent, FolderViewModel Folder)>();

        for (int i = 0; i < lines.Count; i++)
        {
            var (indent, content) = lines[i];
            while (open.Count > 0 && open.Peek().Indent >= indent) open.Pop();
            var parent = open.Count > 0 ? open.Peek().Folder : into;

            bool hasChildren = i + 1 < lines.Count && lines[i + 1].Indent > indent;
            bool isFolder = content.EndsWith('/') || content.EndsWith('\\') || hasChildren || leavesAreFolders;
            var parts = content.Split('/', '\\').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) continue;
            var folderParts = isFolder ? parts : parts[..^1];

            var folder = parent;
            foreach (var part in folderParts)
            {
                if (folder.FindFolder(part) is null) folders++;
                folder = folder.GetOrAddFolder(part);
            }
            if (isFolder) open.Push((indent, folder));
            else
            {
                folder.Cards.Add(new CardViewModel(parts[^1]));
                names++;
            }
        }
        return new Result(folders, names);
    }

    static int Indent(string line)
    {
        int width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4;
            else break;
        }
        return width;
    }
}
