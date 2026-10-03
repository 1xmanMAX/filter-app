namespace FilterApp.Core;

public sealed record TransferResult(string DestPath, bool Moved);

/// Puts a file into a folder under a name, copying or moving it. Never overwrites.
public static class Transfer
{
    const int ErrorFileExists = unchecked((int)0x80070050);
    const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    public static Task<TransferResult> RunAsync(string sourcePath, string destDir, string name, bool move) =>
        Task.Run(() => Run(sourcePath, destDir, name, move));

    /// Moving inside one drive is an instant rename. Across drives the file is copied safely first and the
    /// original deleted after; if that delete fails the result says the file was only copied.
    internal static TransferResult Run(string sourcePath, string destDir, string name, bool move)
    {
        Directory.CreateDirectory(destDir);
        if (!move) return new(Copier.Copy(sourcePath, destDir, name), Moved: false);
        if (SameVolume(sourcePath, destDir)) return new(Rename(sourcePath, destDir, name), Moved: true);

        var dest = Copier.Copy(sourcePath, destDir, name);
        try
        {
            File.Delete(sourcePath);
            return new(dest, Moved: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(dest, Moved: false);
        }
    }

    /// Moves a placed file back to where it came from. If that name is taken now, "Name (2)" is used.
    /// Returns the path it ended up at.
    public static string MoveBack(string placedPath, string originalPath)
    {
        var dir = Path.GetDirectoryName(originalPath)!;
        return Run(placedPath, dir, Path.GetFileNameWithoutExtension(originalPath), move: true).DestPath;
    }

    static string Rename(string sourcePath, string destDir, string name)
    {
        var fileName = Path.GetFileName(sourcePath);
        // The file already has this name in this folder: nothing to do (and no "Name (2)").
        var same = NameResolver.Resolve(destDir, name, fileName, _ => false);
        if (string.Equals(Path.GetFullPath(same), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            return sourcePath;
        while (true)
        {
            var dest = NameResolver.Resolve(destDir, name, fileName, File.Exists);
            try
            {
                File.Move(sourcePath, dest, overwrite: false);
                return dest;
            }
            catch (IOException e) when (e.HResult is ErrorFileExists or ErrorAlreadyExists)
            {
                // Another file took this name between Resolve and Move; try the next one.
            }
        }
    }

    static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);
}
