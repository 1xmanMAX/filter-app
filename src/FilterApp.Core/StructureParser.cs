namespace FilterApp.Core;

/// Builds folders and names from pasted text, one entry per line:
///   "Nombre"            a name (card)
///   "Carpeta/"          a folder
///   "A/B/Nombre"        folders A and B with the name inside
///   indented lines      go inside the closest folder above with less indentation
/// Folders that already exist (same name, any case) are reused, never duplicated.
public static class StructureParser
{
    public sealed record Result(int Folders, int Names)
    {
        public int Total => Folders + Names;
    }

    public static Result Apply(FolderViewModel into, string text)
    {
        int folders = 0, names = 0;
        // Folders opened by previous lines, with the indentation of the line that opened them.
        var open = new Stack<(int Indent, FolderViewModel Folder)>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var content = line.Trim();
            if (content.Length == 0) continue;
            int indent = Indent(line);
            while (open.Count > 0 && open.Peek().Indent >= indent) open.Pop();
            var parent = open.Count > 0 ? open.Peek().Folder : into;

            bool isFolder = content.EndsWith('/') || content.EndsWith('\\');
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
