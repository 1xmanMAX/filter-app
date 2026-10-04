using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FilterApp.Preview;

/// Grab and move: hold the middle button (or the left one, where there is nothing to select) and drag
/// to move the content, like the hand tool of a PDF reader.
public static class DragScroll
{
    public static void Attach(ScrollViewer scroll, bool leftButtonToo)
    {
        Point start = default;
        double startH = 0, startV = 0;
        MouseButton? button = null;

        if (leftButtonToo) scroll.Cursor = Cursors.Hand;
        scroll.PreviewMouseDown += (_, e) =>
        {
            bool ours = e.ChangedButton == MouseButton.Middle || (leftButtonToo && e.ChangedButton == MouseButton.Left);
            if (!ours || button is not null || OnScrollBar(e.OriginalSource)) return;
            if (e.ChangedButton == MouseButton.Left && e.ClickCount > 1) return;   // double-click is for zoom
            start = e.GetPosition(scroll);
            startH = scroll.HorizontalOffset;
            startV = scroll.VerticalOffset;
            if (!scroll.CaptureMouse()) return;
            button = e.ChangedButton;
            scroll.Cursor = Cursors.ScrollAll;
            e.Handled = e.ChangedButton == MouseButton.Middle;   // a left click still reaches double-click handlers
        };
        scroll.PreviewMouseMove += (_, e) =>
        {
            if (button is null) return;
            var p = e.GetPosition(scroll);
            scroll.ScrollToHorizontalOffset(startH - (p.X - start.X));
            scroll.ScrollToVerticalOffset(startV - (p.Y - start.Y));
        };
        scroll.PreviewMouseUp += (_, e) =>
        {
            if (button != e.ChangedButton) return;
            scroll.ReleaseMouseCapture();
            e.Handled = e.ChangedButton == MouseButton.Middle;
        };
        scroll.LostMouseCapture += (_, _) =>
        {
            button = null;
            scroll.Cursor = leftButtonToo ? Cursors.Hand : null;
        };
    }

    static bool OnScrollBar(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollBar) return true;
        return false;
    }
}
