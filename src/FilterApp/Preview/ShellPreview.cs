using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilterApp.Preview;

/// Hosts a Windows preview handler: the component Explorer's preview pane uses for Word, Excel,
/// PowerPoint, Outlook messages and anything else an installed app registered.
/// The handler lives on <see cref="Worker"/>, so loading a heavy document never freezes the window, and it is
/// kept for the next file of the same type (opening Word's previewer is the slow part). An idle handler is
/// let go after a while so it does not hold memory.
public sealed class PreviewHandlerHost : HwndHost
{
    /// Runs every preview handler and shell thumbnail call.
    public static StaWorker Worker { get; } = new("Preview");
    static readonly TimeSpan KeepIdle = TimeSpan.FromSeconds(45);

    IntPtr _hwnd;
    // Only touched on the worker thread:
    IPreviewHandler? _shown;
    object? _cached;
    Guid _cachedClsid;
    System.Windows.Threading.DispatcherTimer? _idle;

    /// The handler registered for this file type, if any.
    public static Guid? HandlerFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return null;
        uint size = 64;
        var text = new StringBuilder((int)size);
        int hr = Native.AssocQueryString(Native.ASSOCF_INIT_DEFAULTTOSTAR, Native.ASSOCSTR_SHELLEXTENSION, ext,
                                         "{8895b1c6-b41f-4c1c-a562-0d564250836f}", text, ref size);
        return hr == 0 && Guid.TryParse(text.ToString(), out var clsid) ? clsid : null;
    }

    /// Shows the file in this host. False if no handler could open it.
    public Task<bool> OpenAsync(string path)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || HandlerFor(path) is not { } clsid) return Task.FromResult(false);
        Native.GetClientRect(hwnd, out var rect);
        return Worker.RunAsync(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool ok = Open(path, clsid, hwnd, rect);
            System.Diagnostics.Trace.WriteLine($"preview handler {(ok ? "ok" : "falló")} en {clock.ElapsedMilliseconds} ms: {Path.GetFileName(path)}");
            return ok;
        });
    }

    bool Open(string path, Guid clsid, IntPtr hwnd, Native.RECT rect)
    {
        Unload();
        _idle?.Stop();
        try
        {
            bool reused = _cached is not null && _cachedClsid == clsid;
            if (!reused)
            {
                ReleaseCached();
                _cached = Create(clsid);
                _cachedClsid = clsid;
                if (_cached is null) return false;
            }
            if (!Initialize(_cached!, path))
            {
                if (!reused) return ReleaseCached();
                // Some handlers can only be initialized once: start a fresh one.
                ReleaseCached();
                _cached = Create(clsid);
                if (_cached is null || !Initialize(_cached, path)) return ReleaseCached();
            }
            var handler = (IPreviewHandler)_cached!;
            handler.SetWindow(hwnd, ref rect);
            handler.DoPreview();
            _shown = handler;
            return true;
        }
        // Handlers report failures with any HRESULT, which .NET turns into many exception types
        // (E_NOTIMPL becomes NotImplementedException): every one of them just means "cannot show this file".
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"preview handler {Path.GetFileName(path)}: 0x{e.HResult:X8} {e.Message}");
            _shown = null;
            return ReleaseCached();
        }
    }

    static bool Initialize(object handler, string path)
    {
        try
        {
            if (handler is IInitializeWithFile withFile) withFile.Initialize(path, Native.STGM_READ);
            else if (handler is IInitializeWithStream withStream)
            {
                Native.SHCreateStreamOnFileEx(path, Native.STGM_READ | Native.STGM_SHARE_DENY_NONE, 0, false, null, out var stream);
                withStream.Initialize(stream, Native.STGM_READ);
            }
            else if (handler is IInitializeWithItem withItem)
            {
                Native.CreateShellItem(path, out var item);
                withItem.Initialize(item, Native.STGM_READ);
            }
            else return false;
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"preview handler init {Path.GetFileName(path)}: 0x{e.HResult:X8} {e.Message}");
            return false;
        }
    }

    /// Out of process first, like Explorer: a handler that crashes takes its host down, not this app.
    static object? Create(Guid clsid)
    {
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        if (Native.CoCreateInstance(ref clsid, IntPtr.Zero, Native.CLSCTX_LOCAL_SERVER, ref unknown, out var obj) == 0) return obj;
        return Native.CoCreateInstance(ref clsid, IntPtr.Zero, Native.CLSCTX_INPROC_SERVER, ref unknown, out obj) == 0 ? obj : null;
    }

    bool ReleaseCached()
    {
        if (_cached is not null && Marshal.IsComObject(_cached)) Marshal.FinalReleaseComObject(_cached);
        _cached = null;
        _shown = null;
        return false;
    }

    /// Stops showing the file and lets go of it (the handler itself is kept for a while for the next one).
    public Task UnloadAsync() => Worker.RunAsync(Unload);

    void Unload()
    {
        if (_shown is null) return;
        try { _shown.Unload(); }
        catch (Exception e) when (e is not OutOfMemoryException) { ReleaseCached(); }
        _shown = null;
        if (_idle is null)
        {
            _idle = new System.Windows.Threading.DispatcherTimer { Interval = KeepIdle };
            _idle.Tick += (_, _) => { _idle.Stop(); if (_shown is null) ReleaseCached(); };
        }
        _idle.Stop();
        _idle.Start();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = Native.CreateWindowEx(0, "static", "", Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN,
                                      0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Worker.Run(() => { Unload(); ReleaseCached(); });
        Native.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_hwnd == IntPtr.Zero) return;
        Native.GetClientRect(_hwnd, out var rect);
        Worker.Post(() =>
        {
            try { _shown?.SetRect(ref rect); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        });
    }
}

/// The picture Explorer shows for a file: a thumbnail of its content, or its icon.
public static class ShellThumbnail
{
    /// Made on the preview thread: some thumbnails come from the same slow handlers as previews.
    public static Task<BitmapSource?> GetAsync(string path, int size) =>
        PreviewHandlerHost.Worker.RunAsync(() => Get(path, size));

    static BitmapSource? Get(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj) != 0) return null;
        var factory = (IShellItemImageFactory)obj;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new Native.SIZE { cx = size, cy = size }, Native.SIIGBF_BIGGERSIZEOK, out hbitmap) != 0)
                return null;
            return ToBitmap(hbitmap);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        finally
        {
            if (hbitmap != IntPtr.Zero) Native.DeleteObject(hbitmap);
            Marshal.FinalReleaseComObject(factory);
        }
    }

    /// Copies the pixels with their transparency (CreateBitmapSourceFromHBitmap paints it black).
    static BitmapSource? ToBitmap(IntPtr hbitmap)
    {
        Native.GetObject(hbitmap, Marshal.SizeOf<Native.BITMAP>(), out Native.BITMAP bmp);
        int w = bmp.bmWidth, h = bmp.bmHeight;
        if (w <= 0 || h <= 0) return null;
        var info = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var dc = Native.GetDC(IntPtr.Zero);
        try { Native.GetDIBits(dc, hbitmap, 0, (uint)h, pixels, ref info, 0); }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref Native.RECT rect);
    void SetRect(ref Native.RECT rect);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr phwnd);
    [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
}

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInitializeWithStream
{
    void Initialize(IStream pstream, uint grfMode);
}

[ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInitializeWithItem
{
    void Initialize(IShellItem psi, uint grfMode);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem { }

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(Native.SIZE size, int flags, out IntPtr phbm);
}

static class Native
{
    public const int ASSOCF_INIT_DEFAULTTOSTAR = 0x4;
    public const int ASSOCSTR_SHELLEXTENSION = 16;
    public const uint STGM_READ = 0;
    public const uint STGM_SHARE_DENY_NONE = 0x40;
    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    public const int SIIGBF_BIGGERSIZEOK = 0x1;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
                                              [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IStream? template, out IStream stream);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
                                                         [MarshalAs(UnmanagedType.Interface)] out object item);

    public static void CreateShellItem(string path, out IShellItem item)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj));
        item = (IShellItem)obj;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y,
                                               int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}
