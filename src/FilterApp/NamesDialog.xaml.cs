using System.IO;
using System.Windows;
using FilterApp.Core;
using Microsoft.Win32;

namespace FilterApp;

public partial class NamesDialog : Window
{
    /// <paramref name="location"/>: the folder the names go into, or null for the top level.
    public NamesDialog(string? location = null)
    {
        InitializeComponent();
        Where.Text = location is null ? "Se agregan en: Destino" : $"Se agregan en: Destino / {location}";
        Box.TextChanged += (_, _) => Example.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Box.Focus();
    }

    public string NamesText => Box.Text;

    void OnCopyStructure(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Elige la carpeta cuya estructura quieres repetir" };
        if (dialog.ShowDialog(this) != true) return;
        var text = FolderScanner.Structure(dialog.FolderName);
        if (text.Length == 0)
        {
            MessageBox.Show(this, "Esa carpeta no tiene subcarpetas.", "Filter App", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Lines added under a folder typed last would nest inside it: start them at the left margin.
        var prefix = Box.Text.Length == 0 || Box.Text.EndsWith('\n') ? "" : Environment.NewLine;
        Box.AppendText(prefix + text);
        Box.Focus();
        Box.CaretIndex = Box.Text.Length;
        Box.ScrollToEnd();
    }

    void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
