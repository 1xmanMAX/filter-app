using System.Threading;
using System.Windows.Threading;

namespace FilterApp.Preview;

/// A background thread with its own message loop. Preview handlers and shell thumbnails are COM objects
/// that need one; running them here means a slow Word or Excel preview never freezes the window.
/// Work runs one item at a time, in order.
public sealed class StaWorker
{
    readonly Dispatcher _dispatcher;

    public StaWorker(string name)
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public Task<T> RunAsync<T>(Func<T> work) => _dispatcher.InvokeAsync(work).Task;
    public Task RunAsync(Action work) => _dispatcher.InvokeAsync(work).Task;
    public void Post(Action work) => _dispatcher.BeginInvoke(work);
    /// Waits for the work: only for teardown, where the result must be known before going on.
    public void Run(Action work) => _dispatcher.Invoke(work);
}
