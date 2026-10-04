using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FilterApp.Core;
using Microsoft.Win32;

namespace FilterApp;

/// A folder or a name in the live preview of the dialog.
public sealed record PlanNode(string Name, bool IsFolder, List<PlanNode> Children)
{
    static readonly Brush FolderColor = Frozen(Color.FromRgb(0xBF, 0x87, 0x00));
    static readonly Brush NameColor = Frozen(Color.FromRgb(0x09, 0x69, 0xDA));

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public string Icon => IsFolder ? "" : "";
    public Brush IconColor => IsFolder ? FolderColor : NameColor;

    public static List<PlanNode> From(FolderViewModel folder) =>
        folder.Folders.Select(f => new PlanNode(f.Name, true, From(f)))
              .Concat(folder.Cards.Select(c => new PlanNode(c.Name, false, [])))
              .ToList();
}

public partial class NamesDialog : Window
{
    const string IndentUnit = "    ";

    /// <paramref name="location"/>: the folder the names go into, or null for the top level.
    public NamesDialog(string? location = null)
    {
        InitializeComponent();
        Where.Text = location is null ? "Se agregan en: Destino" : $"Se agregan en: Destino / {location}";
        Loaded += (_, _) => Box.Focus();
        UpdatePlan();
    }

    public string NamesText => Box.Text;
    /// Lines with nothing inside are folders instead of names.
    public bool LeavesAreFolders => LeavesFolders.IsChecked == true;

    void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePlan();
    void OnModeChanged(object sender, RoutedEventArgs e) => UpdatePlan();

    /// Shows what will be created, exactly as the board will build it.
    void UpdatePlan()
    {
        if (Tree is null || Box is null) return;   // during InitializeComponent
        Example.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var root = new FolderViewModel("");
        var result = StructureParser.Apply(root, Box.Text, LeavesAreFolders);
        Tree.ItemsSource = PlanNode.From(root);
        Summary.Text = result.Total == 0
            ? "Así quedará"
            : $"Así quedará: {Count(result.Folders, "carpeta", "carpetas")}, {Count(result.Names, "nombre", "nombres")}";
        OkButton.IsEnabled = result.Total > 0;
    }

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// Tab / Shift+Tab move the lines in or out a level; Enter keeps the level of the line above.
    void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            Indent(out_: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            int lineStart = Box.Text.LastIndexOf('\n', Math.Max(0, Box.CaretIndex - 1)) + 1;
            if (Box.CaretIndex == 0) lineStart = 0;
            var line = Box.Text[lineStart..Box.CaretIndex];
            var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            Insert(Environment.NewLine + indent);
        }
    }

    void Insert(string text)
    {
        int caret = Box.CaretIndex;
        Box.SelectedText = text;
        Box.CaretIndex = caret + text.Length;
        Box.SelectionLength = 0;
    }

    /// Indents (or outdents) every line touched by the selection, keeping it selected.
    void Indent(bool out_)
    {
        var text = Box.Text;
        int start = Box.SelectionStart, end = start + Box.SelectionLength;
        int first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int last = text.IndexOf('\n', Math.Max(first, end - (Box.SelectionLength > 0 ? 1 : 0)));
        if (last < 0) last = text.Length;

        var lines = text[first..last].Split('\n');
        int caretShift = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!out_)
            {
                lines[i] = IndentUnit + lines[i];
                if (i == 0) caretShift = IndentUnit.Length;
            }
            else
            {
                int remove = lines[i].StartsWith('\t') ? 1 : CountLeadingSpaces(lines[i], 4);
                lines[i] = lines[i][remove..];
                if (i == 0) caretShift = -remove;
            }
        }
        var block = string.Join('\n', lines);
        Box.Text = text[..first] + block + text[last..];
        if (Box.SelectionLength == 0 && start == end)
            Box.CaretIndex = Math.Clamp(start + caretShift, first, first + block.Length);
        else
            Box.Select(first, block.Length);
    }

    static int CountLeadingSpaces(string line, int max)
    {
        int n = 0;
        while (n < max && n < line.Length && line[n] == ' ') n++;
        return n;
    }

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
