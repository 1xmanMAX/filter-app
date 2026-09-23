using System.Windows;

namespace FilterApp;

public partial class NamesDialog : Window
{
    public NamesDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Box.Focus();
    }

    public string NamesText => Box.Text;

    void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
