using System.Runtime.InteropServices;

namespace FilterApp.Interop;

/// Reports the folder of the Explorer window the user works with, driven by the system
/// foreground-change event (no polling). When focus leaves Explorer, the last Explorer
/// window is re-read so navigation done inside it is also picked up.
public sealed class FolderWatcher : IDisposable
{
    const uint EventSystemForeground = 0x0003, WinEventOutOfContext = 0x0000;

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    readonly WinEventProc _proc;   // must stay referenced: native code only holds a pointer
    readonly IntPtr _hook;
    IntPtr _lastExplorer;

    public event Action<string>? FolderActivated;

    public FolderWatcher()
    {
        _proc = OnForeground;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WinEventOutOfContext);
    }

    void OnForeground(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Called from native code: nothing may escape, or the process dies.
        try
        {
            if (ExplorerPath.IsExplorer(hwnd)) _lastExplorer = hwnd;
            else if (_lastExplorer == IntPtr.Zero || !IsWindow(_lastExplorer)) return;

            var path = ExplorerPath.TryGet(_lastExplorer);
            if (path is not null) FolderActivated?.Invoke(path);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindow(IntPtr hwnd);
}
