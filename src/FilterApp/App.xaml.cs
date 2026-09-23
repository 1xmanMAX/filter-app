using System.Windows;
using FilterApp.Core;

namespace FilterApp;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        var board = Board.FromState(StateStore.Load(StateStore.DefaultPath));
        new MainWindow(board).Show();
    }
}
