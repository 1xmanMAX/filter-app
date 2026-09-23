using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FilterApp.Interop;

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IComServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellBrowser
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);   // first IOleWindow method; the rest are not needed
}

/// Reads the folder shown by an Explorer window (the active tab on Windows 11).
static class ExplorerPath
{
    static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Type? ShellType = Type.GetTypeFromProgID("Shell.Application");
    static dynamic? _shell;

    public static bool IsExplorer(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "CabinetWClass";
    }

    public static string? TryGet(IntPtr hwnd)
    {
        if (ShellType is null) return null;
        try { _shell ??= Activator.CreateInstance(ShellType); }
        catch (COMException) { return null; }

        // The first ShellTabWindowClass child is the tab in front.
        IntPtr activeTab = FindWindowEx(hwnd, IntPtr.Zero, "ShellTabWindowClass", null);
        string? fallback = null;
        foreach (object window in _shell!.Windows())
        {
            try
            {
                dynamic w = window;
                if (Convert.ToInt64(w.HWND) != hwnd.ToInt64()) continue;
                string path = w.Document.Folder.Self.Path;
                if (!Directory.Exists(path)) continue;   // "This PC", Recycle Bin, etc.
                if (activeTab == IntPtr.Zero || TabOf(window) == activeTab) return path;
                fallback ??= path;
            }
            catch (Exception e) when (e is COMException or InvalidCastException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }
        return fallback;
    }

    static IntPtr TabOf(object window)
    {
        if (window is not IComServiceProvider provider) return IntPtr.Zero;
        Guid service = SidTopLevelBrowser, iid = typeof(IShellBrowser).GUID;
        if (provider.QueryService(ref service, ref iid, out object browser) != 0) return IntPtr.Zero;
        return ((IShellBrowser)browser).GetWindow(out IntPtr tab) == 0 ? tab : IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);
}
