using System.Windows;
using FilterApp.Core;

namespace FilterApp;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        // Last-resort safety net: an unexpected error must never close the app mid-work.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "Filter App", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        FilterApp.Intake.FileIntake.CleanTemp();
        var board = Board.FromState(StateStore.Load(StateStore.DefaultPath));
        new MainWindow(board).Show();
    }
}
