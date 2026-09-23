namespace FilterApp.Core;

/// Copies a file into the destination under the card's name. Never overwrites.
public static class Copier
{
    const int ErrorFileExists = unchecked((int)0x80070050);
    const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    public static Task<string> CopyAsync(string sourcePath, string destDir, string cardName) =>
        Task.Run(() => Copy(sourcePath, destDir, cardName));

    static string Copy(string sourcePath, string destDir, string cardName)
    {
        var fileName = Path.GetFileName(sourcePath);
        while (true)
        {
            var dest = NameResolver.Resolve(destDir, cardName, fileName, File.Exists);
            try
            {
                // CopyFileEx with FAIL_IF_EXISTS: kernel-speed and atomic name reservation.
                File.Copy(sourcePath, dest, overwrite: false);
                return dest;
            }
            catch (IOException e) when (e.HResult is ErrorFileExists or ErrorAlreadyExists)
            {
                // Another copy took this name between Resolve and Copy; try the next one.
            }
        }
    }

    public static void Undo(string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
    }
}
