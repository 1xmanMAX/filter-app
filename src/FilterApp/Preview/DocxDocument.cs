using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using WpfTable = System.Windows.Documents.Table;

namespace FilterApp.Preview;

/// Reads a Word document (.docx) by itself, with its look: headings, bold/italic/underline, sizes and colours,
/// alignment, lists, tables and pictures. Used at once while Word's own previewer loads, and instead of it when
/// Word refuses a file or Office is not installed. Reading runs off the UI thread into a small model;
/// <see cref="Build"/> turns that into a document on the UI thread.
public static class DocxDocument
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    const double EmuPerDip = 9525;   // 914400 EMU per inch / 96 DIP per inch
    const int MaxBlocks = 4000;      // a preview, not a reader: very long documents are cut

    // ---- Model (plain data, safe to build on any thread) ----

    public abstract record Block;
    public sealed record Para(List<Piece> Pieces, Style Style, TextAlignment Align, double SpaceAfter, double Indent, string? Marker) : Block;
    public sealed record Grid(List<double> Columns, List<List<Cell>> Rows) : Block;
    /// A table cell: its content, its four borders (null: none), background, and how many columns/rows it covers.
    public sealed record Cell(List<Block> Blocks, Line? Top, Line? Left, Line? Bottom, Line? Right, Color? Fill,
                              Thickness Padding, int Span = 1, int RowSpan = 1);
    public sealed record Line(Color Color, double Width);
    public abstract record Piece;
    public sealed record Text(string Value, Style Style) : Piece;
    public sealed record Break : Piece;
    public sealed record Picture(BitmapSource Image, double Width, double Height) : Piece;

    public sealed record Style(bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
                               double? Size = null, Color? Color = null)
    {
        /// The run's own settings over the paragraph style's.
        public Style Over(Style b) => new(Bold || b.Bold, Italic || b.Italic, Underline || b.Underline, Strike || b.Strike,
                                          Size ?? b.Size, Color ?? b.Color);
    }

    public static List<Block> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var doc = Load(zip, "word/document.xml") ?? throw new InvalidDataException("No es un documento de Word.");
        var reader = new Reader(zip);
        var body = doc.Root?.Element(W + "body");
        return body is null ? [] : reader.Blocks(body);
    }

    static XDocument? Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    sealed class Reader
    {
        readonly ZipArchive _zip;
        readonly Dictionary<string, (Style Run, TextAlignment? Align, int Heading)> _styles = [];
        Dictionary<string, XElement> _rawStyles = [];
        readonly Dictionary<string, string> _images = [];      // relationship id → zip path
        readonly Dictionary<(string Num, int Level), string> _formats = [];
        readonly Dictionary<(string Num, int Level), int> _counters = [];
        int _count;

        public Reader(ZipArchive zip)
        {
            _zip = zip;
            ReadStyles();
            ReadRelationships();
            ReadNumbering();
        }

        void ReadStyles()
        {
            var styles = Load(_zip, "word/styles.xml")?.Root;
            if (styles is null) return;
            var raw = _rawStyles = styles.Elements(W + "style").Where(s => (string?)s.Attribute(W + "styleId") is not null)
                            .ToDictionary(s => (string)s.Attribute(W + "styleId")!);
            (Style, TextAlignment?, int) Resolve(string id, int depth)
            {
                if (!raw.TryGetValue(id, out var s) || depth > 10) return (new Style(), null, 0);
                var basedOn = (string?)s.Element(W + "basedOn")?.Attribute(W + "val");
                var (style, align, heading) = basedOn is null ? (new Style(), (TextAlignment?)null, 0) : Resolve(basedOn, depth + 1);
                var name = ((string?)s.Element(W + "name")?.Attribute(W + "val") ?? "").ToLowerInvariant();
                if (name == "title") heading = 1;
                else if (name.StartsWith("heading ") && int.TryParse(name[8..], out var level)) heading = level;
                var own = RunStyle(s.Element(W + "rPr"));
                return (own.Over(style), Alignment(s.Element(W + "pPr")) ?? align, heading);
            }
            foreach (var id in raw.Keys) _styles[id] = Resolve(id, 0);
        }

        void ReadRelationships()
        {
            var rels = Load(_zip, "word/_rels/document.xml.rels")?.Root;
            if (rels is null) return;
            XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
            foreach (var rel in rels.Elements(pr + "Relationship"))
                if ((string?)rel.Attribute("Id") is { } id && (string?)rel.Attribute("Target") is { } target)
                    _images[id] = target.StartsWith('/') ? target.TrimStart('/') : "word/" + target;
        }

        void ReadNumbering()
        {
            var numbering = Load(_zip, "word/numbering.xml")?.Root;
            if (numbering is null) return;
            var abstracts = numbering.Elements(W + "abstractNum")
                .Where(a => (string?)a.Attribute(W + "abstractNumId") is not null)
                .ToDictionary(a => (string)a.Attribute(W + "abstractNumId")!);
            foreach (var num in numbering.Elements(W + "num"))
            {
                var id = (string?)num.Attribute(W + "numId");
                var abs = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val");
                if (id is null || abs is null || !abstracts.TryGetValue(abs, out var a)) continue;
                foreach (var lvl in a.Elements(W + "lvl"))
                    if (int.TryParse((string?)lvl.Attribute(W + "ilvl"), out var level))
                        _formats[(id, level)] = (string?)lvl.Element(W + "numFmt")?.Attribute(W + "val") ?? "bullet";
            }
        }

        public List<Block> Blocks(XElement container)
        {
            var blocks = new List<Block>();
            foreach (var e in container.Elements())
            {
                if (_count++ > MaxBlocks) break;
                if (e.Name == W + "p") blocks.Add(Paragraph(e));
                else if (e.Name == W + "tbl") blocks.Add(Table(e));
                else if (e.Name == W + "sdt" && e.Element(W + "sdtContent") is { } content) blocks.AddRange(Blocks(content));
            }
            return blocks;
        }

        Grid Table(XElement tbl)
        {
            var tblPr = tbl.Element(W + "tblPr");
            var styleId = (string?)tblPr?.Element(W + "tblStyle")?.Attribute(W + "val");
            var borders = tblPr?.Element(W + "tblBorders") ?? TableStyle(styleId, "tblBorders");
            var margins = Margins(tblPr?.Element(W + "tblCellMar") ?? TableStyle(styleId, "tblCellMar"), new Thickness(7, 2, 7, 2));
            var columns = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol")
                             .Select(c => double.TryParse((string?)c.Attribute(W + "w"), out var w) ? w / 15 : 0).ToList() ?? [];

            var trs = tbl.Elements(W + "tr").ToList();
            var rows = new List<List<Cell>>();
            var above = new Dictionary<int, Cell>();   // grid column → the cell that starts there, for vertical merges
            int width = Math.Max(columns.Count, trs.Count == 0 ? 0 : trs.Max(tr => tr.Elements(W + "tc").Sum(Span)));
            for (int r = 0; r < trs.Count; r++)
            {
                var row = new List<Cell>();
                int col = 0;
                bool lastRow = r == trs.Count - 1;
                foreach (var tc in trs[r].Elements(W + "tc"))
                {
                    var tcPr = tc.Element(W + "tcPr");
                    int span = Span(tc);
                    var merge = tcPr?.Element(W + "vMerge");
                    if (merge is not null && (string?)merge.Attribute(W + "val") is null or "continue" && above.TryGetValue(col, out var top))
                    {
                        // Continues the cell above: that one grows instead.
                        int i = rows.FindIndex(x => x.Contains(top));
                        rows[i][rows[i].IndexOf(top)] = above[col] = top with
                        {
                            RowSpan = top.RowSpan + 1,
                            Bottom = lastRow ? Edge(tcPr, "bottom", borders, "bottom") : top.Bottom,
                        };
                        col += span;
                        continue;
                    }
                    var own = tcPr?.Element(W + "tcBorders");
                    bool lastCol = col + span >= width;
                    var cell = new Cell(Blocks(tc),
                        Edge(own, "top", borders, r == 0 ? "top" : "insideH"),
                        Edge(own, "left", borders, col == 0 ? "left" : "insideV", "start"),
                        lastRow ? Edge(own, "bottom", borders, "bottom") : null,
                        lastCol ? Edge(own, "right", borders, "right", "end") : null,
                        Hex((string?)tcPr?.Element(W + "shd")?.Attribute(W + "fill")),
                        Margins(tcPr?.Element(W + "tcMar"), margins), span);
                    row.Add(cell);
                    above[col] = cell;
                    col += span;
                }
                rows.Add(row);
            }
            return new Grid(columns, rows);
        }

        static int Span(XElement tc) =>
            int.TryParse((string?)tc.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val"), out var n) ? Math.Max(1, n) : 1;

        /// A cell side: the cell's own border, else the table's (or its table style's). Null when there is none.
        static Line? Edge(XElement? own, string side, XElement? table, string tableSide, string? alias = null)
        {
            var e = own?.Element(W + side) ?? (alias is null ? null : own?.Element(W + alias));
            if (e is null)
            {
                e = table?.Element(W + tableSide);
                if (e is null && tableSide is "left" or "right") e = table?.Element(W + (tableSide == "left" ? "start" : "end"));
            }
            var kind = (string?)e?.Attribute(W + "val");
            if (e is null || kind is null or "nil" or "none") return null;
            // Size in eighths of a point; never thinner than a pixel so it always shows.
            double width = double.TryParse((string?)e.Attribute(W + "sz"), out var sz) ? sz / 6 : 0.67;
            if (kind is "double" or "thickThinSmallGap" or "thinThickSmallGap") width *= 2;
            return new Line(Hex((string?)e.Attribute(W + "color")) ?? Colors.Black, Math.Clamp(width, 1, 6));
        }

        static Thickness Margins(XElement? mar, Thickness fallback)
        {
            if (mar is null) return fallback;
            double Side(string name, string alias, double other) =>
                double.TryParse((string?)(mar.Element(W + name) ?? mar.Element(W + alias))?.Attribute(W + "w"), out var w) ? w / 15 : other;
            return new Thickness(Side("left", "start", fallback.Left), Side("top", "top", fallback.Top),
                                 Side("right", "end", fallback.Right), Side("bottom", "bottom", fallback.Bottom));
        }

        /// A setting of a table style (following the styles it is based on), such as its borders.
        XElement? TableStyle(string? id, string name)
        {
            for (int depth = 0; id is not null && depth < 10 && _rawStyles.TryGetValue(id, out var s); depth++)
            {
                if (s.Element(W + "tblPr")?.Element(W + name) is { } found) return found;
                id = (string?)s.Element(W + "basedOn")?.Attribute(W + "val");
            }
            return null;
        }

        static Color? Hex(string? hex) =>
            hex is { Length: 6 } && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
                ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : null;

        Para Paragraph(XElement p)
        {
            var pPr = p.Element(W + "pPr");
            var styleId = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val");
            var (styleRun, styleAlign, heading) = styleId is not null && _styles.TryGetValue(styleId, out var s)
                ? s : (new Style(), (TextAlignment?)null, 0);
            if (heading > 0)
                styleRun = styleRun.Over(new Style(Bold: true, Size: heading switch { 1 => 22, 2 => 18, 3 => 15, _ => 13 }));
            var paraRun = RunStyle(pPr?.Element(W + "rPr")).Over(styleRun);

            var pieces = new List<Piece>();
            foreach (var r in p.Descendants(W + "r"))
            {
                if (r.Ancestors(W + "p").FirstOrDefault() != p) continue;   // runs of nested text boxes
                var style = RunStyle(r.Element(W + "rPr")).Over(styleRun);
                foreach (var c in r.Elements())
                {
                    if (c.Name == W + "t") pieces.Add(new Text(c.Value, style));
                    else if (c.Name == W + "tab") pieces.Add(new Text("\t", style));
                    else if (c.Name == W + "br" || c.Name == W + "cr") pieces.Add(new Break());
                    else if (c.Name == W + "drawing" && Image(c) is { } picture) pieces.Add(picture);
                }
            }

            double after = double.TryParse((string?)pPr?.Element(W + "spacing")?.Attribute(W + "after"), out var twips) ? twips / 15 : 6;
            double indent = double.TryParse((string?)pPr?.Element(W + "ind")?.Attribute(W + "left"), out var left) ? left / 15 : 0;
            return new Para(pieces, paraRun, Alignment(pPr) ?? styleAlign ?? TextAlignment.Left,
                            Math.Clamp(after, 0, 30), Math.Clamp(indent, 0, 200), Marker(pPr));
        }

        string? Marker(XElement? pPr)
        {
            var numPr = pPr?.Element(W + "numPr");
            var num = (string?)numPr?.Element(W + "numId")?.Attribute(W + "val");
            if (num is null || num == "0") return null;
            int level = int.TryParse((string?)numPr!.Element(W + "ilvl")?.Attribute(W + "val"), out var l) ? l : 0;
            var format = _formats.GetValueOrDefault((num, level), "bullet");
            if (format is "bullet" or "none") return level % 2 == 0 ? "•" : "◦";
            // Deeper levels restart when a shallower item comes.
            foreach (var key in _counters.Keys.Where(k => k.Num == num && k.Level > level).ToList()) _counters.Remove(key);
            int n = _counters[(num, level)] = _counters.GetValueOrDefault((num, level)) + 1;
            return format switch
            {
                "lowerLetter" => $"{(char)('a' + (n - 1) % 26)})",
                "upperLetter" => $"{(char)('A' + (n - 1) % 26)})",
                "lowerRoman" => $"{Roman(n).ToLowerInvariant()}.",
                "upperRoman" => $"{Roman(n)}.",
                _ => $"{n}.",
            };
        }

        static string Roman(int n)
        {
            var text = "";
            foreach (var (value, symbol) in new[] { (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
                while (n >= value) { text += symbol; n -= value; }
            return text;
        }

        Picture? Image(XElement drawing)
        {
            var id = (string?)drawing.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
            if (id is null || !_images.TryGetValue(id, out var target) || _zip.GetEntry(target) is not { } entry) return null;
            var extent = drawing.Descendants(WP + "extent").FirstOrDefault();
            double w = double.TryParse((string?)extent?.Attribute("cx"), out var cx) ? cx / EmuPerDip : 200;
            double h = double.TryParse((string?)extent?.Attribute("cy"), out var cy) ? cy / EmuPerDip : 150;
            try
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = copy;
                image.DecodePixelWidth = (int)Math.Min(1600, Math.Max(16, w * 2));
                image.EndInit();
                image.Freeze();
                return new Picture(image, w, h);
            }
            catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
            {
                return null;   // EMF/WMF and other formats WPF cannot draw
            }
        }

        static Style RunStyle(XElement? rPr)
        {
            if (rPr is null) return new Style();
            static bool On(XElement? e) => e is not null && (string?)e.Attribute(W + "val") is null or "1" or "true" or "on";
            double? size = double.TryParse((string?)rPr.Element(W + "sz")?.Attribute(W + "val"), out var half) ? half / 2 : null;
            Color? color = null;
            var hex = (string?)rPr.Element(W + "color")?.Attribute(W + "val");
            if (hex is { Length: 6 } && hex != "auto" && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            var u = (string?)rPr.Element(W + "u")?.Attribute(W + "val");
            return new Style(On(rPr.Element(W + "b")), On(rPr.Element(W + "i")), u is not null && u != "none",
                             On(rPr.Element(W + "strike")), size, color);
        }

        static TextAlignment? Alignment(XElement? pPr) => (string?)pPr?.Element(W + "jc")?.Attribute(W + "val") switch
        {
            "center" => TextAlignment.Center,
            "right" or "end" => TextAlignment.Right,
            "both" or "distribute" => TextAlignment.Justify,
            "left" or "start" => TextAlignment.Left,
            _ => null,
        };
    }

    // ---- Building the document (UI thread) ----

    public static FlowDocument Build(List<Block> blocks)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Calibri, Segoe UI"),
            FontSize = 11 * 96 / 72.0,
            PagePadding = new Thickness(28, 24, 28, 24),   // a narrow pane keeps room for tables
            Background = Brushes.White,
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
        };
        foreach (var block in blocks) doc.Blocks.Add(ToBlock(block));
        return doc;
    }

    static System.Windows.Documents.Block ToBlock(Block block) => block switch
    {
        Para p => ToParagraph(p),
        Grid g => ToTable(g),
        _ => new Paragraph(),
    };

    static Paragraph ToParagraph(Para p)
    {
        var paragraph = new Paragraph
        {
            TextAlignment = p.Align,
            Margin = new Thickness(p.Indent + (p.Marker is null ? 0 : 18), 0, 0, p.SpaceAfter),
            TextIndent = p.Marker is null ? 0 : -18,
        };
        Apply(paragraph, p.Style);
        if (p.Marker is not null) paragraph.Inlines.Add(new Run(p.Marker + "\t"));
        foreach (var piece in p.Pieces)
        {
            switch (piece)
            {
                case Text t:
                    var run = new Run(t.Value);
                    Apply(run, t.Style);
                    paragraph.Inlines.Add(run);
                    break;
                case Break:
                    paragraph.Inlines.Add(new LineBreak());
                    break;
                case Picture pic:
                    paragraph.Inlines.Add(new InlineUIContainer(new System.Windows.Controls.Image
                    {
                        Source = pic.Image, Width = pic.Width, Height = pic.Height, Stretch = Stretch.Uniform, MaxWidth = 900,
                    }));
                    break;
            }
        }
        return paragraph;
    }

    static WpfTable ToTable(Grid g)
    {
        var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
        int columns = Math.Max(g.Columns.Count, g.Rows.Count == 0 ? 0 : g.Rows.Max(r => r.Sum(c => c.Span)));
        // Word's proportions, stretched to the pane's width (star sizes kept small: large ones are not honoured).
        double total = g.Columns.Where(w => w > 0).Sum();
        for (int i = 0; i < columns; i++)
            table.Columns.Add(new TableColumn
            {
                Width = new GridLength(i < g.Columns.Count && g.Columns[i] > 0 ? g.Columns[i] / total * columns : 1, GridUnitType.Star),
            });
        var group = new TableRowGroup();
        foreach (var row in g.Rows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row)
            {
                var line = cell.Top ?? cell.Left ?? cell.Right ?? cell.Bottom;
                var tableCell = new TableCell
                {
                    ColumnSpan = cell.Span,
                    RowSpan = cell.RowSpan,
                    Padding = cell.Padding,
                    BorderBrush = line is null ? null : new SolidColorBrush(line.Color),
                    BorderThickness = new Thickness(cell.Left?.Width ?? 0, cell.Top?.Width ?? 0, cell.Right?.Width ?? 0, cell.Bottom?.Width ?? 0),
                    Background = cell.Fill is { } fill ? new SolidColorBrush(fill) : null,
                };
                foreach (var block in cell.Blocks) tableCell.Blocks.Add(ToBlock(block));
                tableRow.Cells.Add(tableCell);
            }
            group.Rows.Add(tableRow);
        }
        table.RowGroups.Add(group);
        return table;
    }

    static void Apply(TextElement element, Style style)
    {
        if (style.Bold) element.FontWeight = FontWeights.Bold;
        if (style.Italic) element.FontStyle = FontStyles.Italic;
        if (style.Size is { } size) element.FontSize = size * 96 / 72.0;
        if (style.Color is { } color) element.Foreground = new SolidColorBrush(color);
        if (element is Inline inline && (style.Underline || style.Strike))
        {
            var decorations = new TextDecorationCollection();
            if (style.Underline) decorations.Add(TextDecorations.Underline);
            if (style.Strike) decorations.Add(TextDecorations.Strikethrough);
            inline.TextDecorations = decorations;
        }
    }
}
