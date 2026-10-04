using System.IO.Compression;
using System.Text;
using FilterApp.Core;

namespace FilterApp.Core.Tests;

public sealed class PreviewTextTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("filterapp-preview-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string FileWith(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData("a.JPG", PreviewKind.Image)]
    [InlineData("a.csv", PreviewKind.Text)]
    [InlineData("a.pdf", PreviewKind.Pdf)]
    [InlineData("a.mp4", PreviewKind.Media)]
    [InlineData("a.MP3", PreviewKind.Media)]
    [InlineData("a.webm", PreviewKind.Web)]
    [InlineData("a.HTML", PreviewKind.Web)]
    [InlineData("a.htm", PreviewKind.Web)]
    [InlineData("a.mhtml", PreviewKind.Web)]
    [InlineData("a.docx", PreviewKind.Docx)]
    [InlineData("a.xlsx", PreviewKind.Other)]
    [InlineData("sin extension", PreviewKind.Other)]
    public void Classify_by_extension(string name, PreviewKind kind) => Assert.Equal(kind, PreviewText.Classify(name));

    [Fact]
    public void Reads_utf8_and_latin_text()
    {
        Assert.Equal("año ñ", PreviewText.ReadText(FileWith("u.txt", Encoding.UTF8.GetBytes("año ñ"))));
        Assert.Equal("año", PreviewText.ReadText(FileWith("l.txt", Encoding.Latin1.GetBytes("año"))));
        Assert.Equal("año", PreviewText.ReadText(FileWith("b.txt", [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("año")])));
    }

    [Fact]
    public void A_cut_in_the_middle_of_a_character_is_not_read_as_latin()
    {
        var path = FileWith("cut.txt", Encoding.UTF8.GetBytes("aaañ"));   // ñ is 2 bytes: cut after its first
        Assert.Equal("aaa", PreviewText.ReadText(path, maxChars: 4));
    }

    [Fact]
    public void Reads_paragraphs_of_a_docx()
    {
        var path = Path.Combine(_root, "d.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
            writer.Write("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                <w:p><w:r><w:t>Hola</w:t></w:r><w:r><w:t xml:space="preserve"> mundo</w:t></w:r></w:p>
                <w:p><w:r><w:t>A</w:t><w:tab/><w:t>B</w:t></w:r></w:p>
                </w:body></w:document>
                """);

        Assert.Equal("Hola mundo\nA\tB\n", PreviewText.ReadDocx(path));
    }

    [Fact]
    public void Structure_lists_subfolders_indented()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Plantilla", "B"));
        Directory.CreateDirectory(Path.Combine(_root, "Plantilla", "A", "A1"));
        File.WriteAllText(Path.Combine(_root, "Plantilla", "file.txt"), "");

        var text = FolderScanner.Structure(Path.Combine(_root, "Plantilla"));

        Assert.Equal(["A/", "  A1/", "B/"], text.Split(Environment.NewLine));
        var b = new Board();
        b.AddNames(text);
        Assert.Equal("A1", b.Root.FindFolder("A")!.Folders[0].Name);
    }
}
