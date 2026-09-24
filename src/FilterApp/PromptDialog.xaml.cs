using System.Windows;

namespace FilterApp;

/// Asks for a single line of text (a session name).
public partial class PromptDialog : Window
{
    public PromptDialog(string title, string label, string okText, string initial = "")
    {
        InitializeComponent();
        Title = title;
        Label.Text = label;
        Ok.Content = okText;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    public string Value => Box.Text.Trim();

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length > 0) DialogResult = true;
    }
}
