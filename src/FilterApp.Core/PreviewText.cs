using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FilterApp.Core;

public enum PreviewKind { Image, Text, Web, Docx, Other }

/// Decides how a file is previewed, and reads the text of the ones shown as text.
public static class PreviewText
{
    public const int MaxChars = 200_000;

    static readonly HashSet<string> Images = Set(".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib",
                                                 ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".jxr", ".wdp");
    static readonly HashSet<string> Texts = Set(".txt", ".text", ".md", ".markdown", ".csv", ".tsv", ".log", ".json",
                                                ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml", ".sql",
                                                ".cs", ".js", ".ts", ".py", ".java", ".c", ".cpp", ".h", ".css",
                                                ".bat", ".cmd", ".ps1", ".sh", ".srt", ".vtt", ".tex", ".bib", ".htm", ".html");
    /// Shown by the Edge engine: documents, vector images and media it plays by itself. Web pages are shown
    /// as text on purpose: rendering them would run their scripts.
    static readonly HashSet<string> Web = Set(".pdf", ".svg", ".mp4", ".m4v", ".webm", ".mp3", ".m4a",
                                              ".wav", ".ogg", ".oga", ".opus", ".aac", ".flac");

    static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    public static PreviewKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return PreviewKind.Image;
        if (Texts.Contains(ext)) return PreviewKind.Text;
        if (Web.Contains(ext)) return PreviewKind.Web;
        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Docx;
        return PreviewKind.Other;
    }

    /// The start of a text file. UTF-8 (with or without BOM) and UTF-16 with BOM are detected;
    /// anything else is read as Windows Latin, which never fails.
    public static string ReadText(string path, int maxChars = MaxChars)
    {
        var buffer = new byte[maxChars];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var bytes = buffer.AsSpan(0, read);

        if (bytes.StartsWith(Encoding.UTF8.Preamble)) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith(Encoding.Unicode.Preamble)) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        try
        {
            // The cut at maxChars can split a character in two: ignore an incomplete one at the very end.
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(TrimPartialUtf8(bytes));
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    static ReadOnlySpan<byte> TrimPartialUtf8(ReadOnlySpan<byte> bytes)
    {
        // Walk back over continuation bytes (10xxxxxx) to the start of the last character.
        int i = bytes.Length - 1, back = 0;
        while (i >= 0 && back < 4 && (bytes[i] & 0xC0) == 0x80) { i--; back++; }
        if (i < 0) return bytes;
        int need = bytes[i] >= 0xF0 ? 4 : bytes[i] >= 0xE0 ? 3 : bytes[i] >= 0xC0 ? 2 : 1;
        return need > back + 1 ? bytes[..i] : bytes;
    }

    /// The text of a Word document, paragraph by paragraph (no formatting). Works without Office.
    public static string ReadDocx(string path, int maxChars = MaxChars)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("No es un documento de Word.");
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var text = new StringBuilder();
        while (!reader.EOF && text.Length < maxChars)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                text.Append(reader.ReadElementContentAsString());   // already moves to the next node
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tab") text.Append('\t');
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "br" or "cr") text.Append('\n');
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") text.Append('\n');
            reader.Read();
        }
        return text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString();
    }

    /// "1,2 MB", "530 KB", "12 bytes".
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}
