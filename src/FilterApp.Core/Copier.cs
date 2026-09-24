namespace FilterApp.Core;

/// Copies a file into the destination under the card's name. Never overwrites.
public static class Copier
{
    const int ErrorFileExists = unchecked((int)0x80070050);
    const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    public static Task<string> CopyAsync(string sourcePath, string destDir, string cardName) =>
        Task.Run(() => Copy(sourcePath, destDir, cardName));

    internal static string Copy(string sourcePath, string destDir, string cardName, Action<string, string>? copyFile = null)
    {
        copyFile ??= (source, target) => File.Copy(source, target, overwrite: false);
        var fileName = Path.GetFileName(sourcePath);
        // Copy under a temporary name first, so an interrupted copy (crash, app closed, disk full)
        // never leaves a truncated file under the card's name.
        var partial = Path.Combine(destDir, $".{Guid.NewGuid():N}.partial");
        try
        {
            copyFile(sourcePath, partial);
            while (true)
            {
                var dest = NameResolver.Resolve(destDir, cardName, fileName, File.Exists);
                try
                {
                    File.Move(partial, dest, overwrite: false);   // atomic name reservation
                    return dest;
                }
                catch (IOException e) when (e.HResult is ErrorFileExists or ErrorAlreadyExists)
                {
                    // Another copy took this name between Resolve and Move; try the next one.
                }
            }
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Undo(string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
    }
}
