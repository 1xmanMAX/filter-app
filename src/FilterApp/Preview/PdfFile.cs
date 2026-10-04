using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilterApp.Preview;

/// A PDF opened with PDFium, the engine Chrome uses: pages look exactly like the original.
/// The file is read on demand (never loaded whole into memory) and pages are drawn one at a time,
/// only when they are shown. PDFium is not thread-safe, so every call goes through one thread.
public sealed class PdfFile : IDisposable
{
    static readonly BlockingCollection<Action> Queue = new();
    static readonly Lazy<bool> Started = new(() =>
    {
        var thread = new Thread(() =>
        {
            Native.FPDF_InitLibrary();
            foreach (var work in Queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = "PDF" };
        thread.Start();
        return true;
    });

    readonly FileStream _stream;
    readonly Native.GetBlock _getBlock;   // kept alive: PDFium calls it for as long as the document is open
    readonly byte[] _buffer = new byte[64 * 1024];
    IntPtr _document;
    bool _closed;

    /// Width and height of each page, in points.
    public IReadOnlyList<Size> Pages { get; private set; } = [];

    PdfFile(FileStream stream)
    {
        _stream = stream;
        _getBlock = Read;
    }

    static Task<T> Run<T>(Func<T> work)
    {
        _ = Started.Value;
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    public static Task<PdfFile> OpenAsync(string path) => Run(() =>
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        var file = new PdfFile(stream);
        var access = new Native.FileAccess
        {
            Length = (uint)Math.Min(stream.Length, uint.MaxValue),
            GetBlock = Marshal.GetFunctionPointerForDelegate(file._getBlock),
        };
        var document = Native.FPDF_LoadCustomDocument(ref access, null);
        if (document == IntPtr.Zero)
        {
            stream.Dispose();
            throw new InvalidDataException(Native.FPDF_GetLastError() == 4 ? "El PDF tiene contraseña." : "PDF dañado o no compatible.");
        }
        int count = Native.FPDF_GetPageCount(document);
        var pages = new Size[count];
        for (int i = 0; i < count; i++)
        {
            Native.FPDF_GetPageSizeByIndex(document, i, out var w, out var h);
            pages[i] = new Size(w, h);
        }
        file._document = document;
        file.Pages = pages;
        return file;
    });

    int Read(IntPtr param, uint position, IntPtr buffer, uint size)
    {
        try
        {
            _stream.Position = position;
            while (size > 0)
            {
                int n = _stream.Read(_buffer, 0, (int)Math.Min(size, (uint)_buffer.Length));
                if (n <= 0) return 0;
                Marshal.Copy(_buffer, 0, buffer, n);
                buffer += n;
                size -= (uint)n;
            }
            return 1;
        }
        catch (IOException) { return 0; }
    }

    /// Draws a page <paramref name="pixelWidth"/> pixels wide. Null if cancelled or the file was closed meanwhile.
    public Task<BitmapSource?> RenderAsync(int index, int pixelWidth, CancellationToken cancel) => Run<BitmapSource?>(() =>
    {
        if (_closed || cancel.IsCancellationRequested) return null;
        var size = Pages[index];
        int width = Math.Max(1, pixelWidth);
        int height = Math.Max(1, (int)Math.Round(width * size.Height / size.Width));
        var page = Native.FPDF_LoadPage(_document, index);
        if (page == IntPtr.Zero) return null;
        var bitmap = Native.FPDFBitmap_Create(width, height, 0);
        try
        {
            Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Native.FPDF_ANNOT | Native.FPDF_LCD_TEXT);
            int stride = Native.FPDFBitmap_GetStride(bitmap);
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
                                             Native.FPDFBitmap_GetBuffer(bitmap), stride * height, stride);
            source.Freeze();
            return source;
        }
        finally
        {
            Native.FPDFBitmap_Destroy(bitmap);
            Native.FPDF_ClosePage(page);
        }
    });

    public void Dispose() => _ = CloseAsync();

    /// Closes the document and the file. Queued behind any render in progress, so the document is never
    /// closed under it; awaiting it guarantees the file is no longer open.
    public Task CloseAsync() => Run(() =>
        {
            if (_closed) return 0;
            _closed = true;
            if (_document != IntPtr.Zero) Native.FPDF_CloseDocument(_document);
            _document = IntPtr.Zero;
            _stream.Dispose();
            GC.KeepAlive(_getBlock);
            return 0;
        });

    static class Native
    {
        public const int FPDF_ANNOT = 0x01, FPDF_LCD_TEXT = 0x02;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetBlock(IntPtr param, uint position, IntPtr buffer, uint size);

        [StructLayout(LayoutKind.Sequential)]
        public struct FileAccess
        {
            public uint Length;       // unsigned long: 32 bits on Windows
            public IntPtr GetBlock;
            public IntPtr Param;
        }

        const string Dll = "pdfium.dll";
        [DllImport(Dll)] public static extern void FPDF_InitLibrary();
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadCustomDocument(ref FileAccess access, [MarshalAs(UnmanagedType.LPStr)] string? password);
        [DllImport(Dll)] public static extern uint FPDF_GetLastError();
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Dll)] public static extern int FPDF_GetPageSizeByIndex(IntPtr document, int index, out double width, out double height);
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Dll)]
        public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
    }
}
