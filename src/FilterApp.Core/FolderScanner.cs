namespace FilterApp.Core;

/// Lists every file inside a dropped folder, subfolders included.
public static class FolderScanner
{
    static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// Each file's <see cref="PendingItem.Group"/> is its folder path starting at the dropped folder's
    /// name ("Fotos" or "Fotos\Viaje"). Hidden and system files (desktop.ini, Thumbs.db) are skipped,
    /// and so are links to other folders, which could point back up and never end.
    public static List<PendingItem> Scan(string folder, CancellationToken cancel = default)
    {
        var root = Path.GetFullPath(folder);
        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if (rootName.Length == 0) rootName = root.TrimEnd('\\', ':', '/');   // a whole drive: "D"

        var items = new List<PendingItem>();
        var stack = new Stack<(string Dir, string Group)>();
        stack.Push((root, rootName));
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (dir, group) = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", Options).Order(StringComparer.OrdinalIgnoreCase))
                    items.Add(new PendingItem(file, Path.GetFileName(file), isTemp: false, group));
                var subdirs = new DirectoryInfo(dir).EnumerateDirectories("*", Options)
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase);   // stack: first pops first
                foreach (var sub in subdirs) stack.Push((sub.FullName, Path.Combine(group, sub.Name)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return items;
    }

    /// The subfolders of a folder as indented text for <see cref="StructureParser"/> ("Name/" per folder,
    /// two spaces per level), to reuse an existing folder layout. Files are not included.
    public static string Structure(string folder, int maxFolders = 5000)
    {
        var lines = new List<string>();
        void Walk(string dir, int level)
        {
            IEnumerable<DirectoryInfo> subdirs;
            try
            {
                subdirs = new DirectoryInfo(dir).EnumerateDirectories("*", Options)
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var sub in subdirs)
            {
                if (lines.Count >= maxFolders) return;
                lines.Add(new string(' ', level * 2) + sub.Name + "/");
                Walk(sub.FullName, level + 1);
            }
        }
        Walk(Path.GetFullPath(folder), 0);
        return string.Join(Environment.NewLine, lines);
    }
}
