using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace FilterApp.Intake;

/// Writes "virtual" files (FileGroupDescriptorW + FileContents, e.g. Outlook attachments,
/// browser images) to a temp folder so they can be handled like normal files.
static class VirtualFileReader
{
    // FILEDESCRIPTORW layout: flags@0, attributes@36, sizeHigh@64, sizeLow@68, name@72 (260 WCHAR), 592 bytes total.
    const int DescriptorSize = 592, AttributesOffset = 36, SizeHighOffset = 64, SizeLowOffset = 68, NameOffset = 72, NameChars = 260;
    const uint FdAttributes = 0x4, FdFileSize = 0x40, DirectoryAttribute = 0x10;
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static List<string> Extract(IDataObject data, string tempDir, out int unreadable)
    {
        var result = new List<string>();
        unreadable = 0;
        if (data.GetData("FileGroupDescriptorW") is not MemoryStream descriptor || data is not ComDataObject com)
            return result;

        var bytes = descriptor.ToArray();
        int count = BitConverter.ToInt32(bytes, 0);
        short contentsFormat = (short)DataFormats.GetDataFormat("FileContents").Id;

        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * DescriptorSize;
            if (o + DescriptorSize > bytes.Length) break;

            uint flags = BitConverter.ToUInt32(bytes, o);
            uint attributes = BitConverter.ToUInt32(bytes, o + AttributesOffset);
            if ((flags & FdAttributes) != 0 && (attributes & DirectoryAttribute) != 0) continue;

            long? size = (flags & FdFileSize) != 0
                ? ((long)BitConverter.ToUInt32(bytes, o + SizeHighOffset) << 32) | BitConverter.ToUInt32(bytes, o + SizeLowOffset)
                : null;

            var rawName = Encoding.Unicode.GetString(bytes, o + NameOffset, NameChars * 2).Split('\0')[0];
            var name = new string(rawName.Split('\\', '/').Last().Select(c => Array.IndexOf(Invalid, c) >= 0 ? '_' : c).ToArray());
            if (name.Length == 0) continue;

            var path = Path.Combine(tempDir, name);
            if (File.Exists(path)) path = Path.Combine(tempDir, $"{i}_{name}");

            try
            {
                if (TryWriteContents(com, contentsFormat, i, path, size)) result.Add(path);
                else unreadable++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
            {
                unreadable++;
                TryDelete(path);   // a half-written file must not look like a good one
            }
        }
        return result;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static bool TryWriteContents(ComDataObject com, short format, int index, string path, long? size)
    {
        var formatEtc = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        com.GetData(ref formatEtc, out STGMEDIUM medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try
                {
                    using var output = File.Create(path);
                    CopyStream(stream, output);
                }
                finally { Marshal.ReleaseComObject(stream); }
                return true;
            }
            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                using var output = File.Create(path);
                CopyHGlobal(medium.unionmember, output, size);
                return true;
            }
            return false;
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    static void CopyStream(IStream source, Stream output)
    {
        var buffer = new byte[1 << 16];
        IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                source.Read(buffer, buffer.Length, read);
                int n = Marshal.ReadInt32(read);
                if (n <= 0) break;
                output.Write(buffer, 0, n);
            }
        }
        finally { Marshal.FreeCoTaskMem(read); }
    }

    static void CopyHGlobal(IntPtr hGlobal, Stream output, long? size)
    {
        long length = (long)(ulong)GlobalSize(hGlobal);
        if (size is long declared && declared < length) length = declared;   // GlobalSize may round up
        IntPtr pointer = GlobalLock(hGlobal);
        try
        {
            var buffer = new byte[1 << 16];
            for (long offset = 0; offset < length; offset += buffer.Length)
            {
                int n = (int)Math.Min(buffer.Length, length - offset);
                Marshal.Copy(pointer + (nint)offset, buffer, 0, n);
                output.Write(buffer, 0, n);
            }
        }
        finally { GlobalUnlock(hGlobal); }
    }

    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalUnlock(IntPtr hMem);
}
