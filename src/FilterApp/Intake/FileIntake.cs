using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FilterApp.Core;

namespace FilterApp.Intake;

/// <paramref name="Folders"/> are dropped folders: their files are listed with <see cref="FolderScanner"/>.
public sealed record IntakeResult(List<PendingItem> Items, List<string> Folders, int Unreadable = 0);

/// Converts whatever was dropped or pasted into pending files.
public static class FileIntake
{
    const string VirtualFormat = "FileGroupDescriptorW";
    static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "FilterApp");

    public static bool CanAccept(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ||
        data.GetDataPresent(VirtualFormat) ||
        data.GetDataPresent(DataFormats.Bitmap);

    /// True when reading the data means extracting file contents (slow), not just taking paths.
    public static bool IsVirtual(IDataObject data) =>
        !data.GetDataPresent(DataFormats.FileDrop) && data.GetDataPresent(VirtualFormat);

    public static IntakeResult Read(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var items = new List<PendingItem>();
            var folders = new List<string>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) folders.Add(path);
                else if (File.Exists(path)) items.Add(new PendingItem(path, Path.GetFileName(path), isTemp: false));
            }
            return new IntakeResult(items, folders);
        }
        if (data.GetDataPresent(VirtualFormat))
        {
            var files = VirtualFileReader.Extract(data, NewTempDir(), out int unreadable);
            return new IntakeResult(files.Select(p => new PendingItem(p, Path.GetFileName(p), isTemp: true)).ToList(), [], unreadable);
        }
        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return new IntakeResult([SaveBitmap(bitmap)], []);
        return new IntakeResult([], []);
    }

    public static void CleanTemp()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static PendingItem SaveBitmap(BitmapSource bitmap)
    {
        var path = Path.Combine(NewTempDir(), $"Imagen {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return new PendingItem(path, Path.GetFileName(path), isTemp: true);
    }

    static string NewTempDir() =>
        Directory.CreateDirectory(Path.Combine(TempRoot, Guid.NewGuid().ToString("N"))).FullName;
}
