using System.Threading;
using System.Windows;
using FilterApp.Core;

namespace FilterApp;

public partial class App : Application
{
    const string InstanceName = "FilterApp.SingleInstance";
    // Kept in fields for the whole run: while the mutex lives, other launches know this one exists.
    Mutex? _instance;
    EventWaitHandle? _showSignal;

    void OnStartup(object sender, StartupEventArgs e)
    {
        // A second copy would share state.json and could delete the other one's in-progress copies:
        // hand over to the running window instead.
        _instance = new Mutex(true, InstanceName, out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        // Last-resort safety net: an unexpected error must never close the app mid-work.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "Filter App", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        FilterApp.Intake.FileIntake.CleanTemp();
        var sessions = SessionStore.Default;
        var sessionId = sessions.OpenCurrent(out var state);
        var window = new MainWindow(Board.FromState(state), sessions, sessionId);
        window.Show();

        var signal = _showSignal;
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.BeginInvoke(window.BringToFront),
                                               null, Timeout.Infinite, executeOnlyOnce: false);
    }
}
