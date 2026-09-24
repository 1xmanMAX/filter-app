using FilterApp.Core;

namespace FilterApp.Core.Tests;

public sealed class CopierTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("filterapp-copier-").FullName;

    string Dest => Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;

    string Source(string name, string content = "hola")
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Copies_with_card_name_and_keeps_source()
    {
        var src = Source("foto.jpg", "abc");
        var result = await Copier.CopyAsync(src, Dest, "Factura Enero");
        Assert.Equal(Path.Combine(Dest, "Factura Enero.jpg"), result);
        Assert.Equal("abc", File.ReadAllText(result));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_file()
    {
        File.WriteAllText(Path.Combine(Dest, "X.txt"), "old");
        var result = await Copier.CopyAsync(Source("a.txt", "new"), Dest, "X");
        Assert.Equal("X (2).txt", Path.GetFileName(result));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Dest, "X.txt")));
    }

    [Fact]
    public async Task Concurrent_copies_to_same_card_name_get_distinct_files()
    {
        var sources = Enumerable.Range(0, 5).Select(i => Source($"f{i}.txt", $"{i}")).ToList();
        var results = await Task.WhenAll(sources.Select(s => Copier.CopyAsync(s, Dest, "X")));
        Assert.Equal(5, results.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Missing_source_throws_and_leaves_nothing()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Copier.CopyAsync(Path.Combine(_root, "nope.txt"), Dest, "X"));
        Assert.Empty(Directory.GetFiles(Dest));
    }

    [Fact]
    public async Task Missing_destination_throws() =>
        await Assert.ThrowsAnyAsync<IOException>(() => Copier.CopyAsync(Source("a.txt"), Path.Combine(_root, "gone"), "X"));

    [Fact]
    public void Interrupted_copy_never_leaves_a_file_under_the_final_name()
    {
        var dest = Dest;
        void HalfCopyThenFail(string source, string target)
        {
            File.WriteAllText(target, "half");
            throw new IOException("disk gone");
        }

        Assert.Throws<IOException>(() => Copier.Copy(Source("big.bin"), dest, "X", HalfCopyThenFail));

        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task Undo_deletes_the_copy()
    {
        var result = await Copier.CopyAsync(Source("a.txt"), Dest, "X");
        Copier.Undo(result);
        Assert.False(File.Exists(result));
    }

    [Fact]
    public void Undo_of_already_deleted_file_does_not_throw() =>
        Copier.Undo(Path.Combine(_root, "missing", "X.txt"));
}
