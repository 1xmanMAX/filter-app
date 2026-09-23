using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FilterApp.Core;

namespace FilterApp.Intake;

public sealed record IntakeResult(List<PendingItem> Items, int RejectedFolders);

/// Converts whatever was dropped or pasted into pending files.
public static class FileIntake
{
    const string VirtualFormat = "FileGroupDescriptorW";
    static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "FilterApp");

    public static bool CanAccept(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ||
        data.GetDataPresent(VirtualFormat) ||
        data.GetDataPresent(DataFormats.Bitmap);

    public static IntakeResult Read(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var items = new List<PendingItem>();
            int folders = 0;
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) folders++;
                else if (File.Exists(path)) items.Add(new PendingItem(path, Path.GetFileName(path), isTemp: false));
            }
            return new IntakeResult(items, folders);
        }
        if (data.GetDataPresent(VirtualFormat))
        {
            var files = VirtualFileReader.Extract(data, NewTempDir());
            return new IntakeResult(files.Select(p => new PendingItem(p, Path.GetFileName(p), isTemp: true)).ToList(), 0);
        }
        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return new IntakeResult([SaveBitmap(bitmap)], 0);
        return new IntakeResult([], 0);
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
