using FilterApp.Core;

namespace FilterApp.Core.Tests;

public sealed class StateStoreTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("filterapp-state-").FullName;
    string StatePath => Path.Combine(_root, "sub", "state.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Missing_file_gives_empty_state()
    {
        var s = StateStore.Load(StatePath);
        Assert.Empty(s.Cards);
        Assert.Null(s.Destination);
        Assert.False(s.Locked);
    }

    [Fact]
    public void Round_trips_and_creates_directory()
    {
        var state = new AppState
        {
            Destination = @"C:\dest",
            Locked = true,
            Cards =
            [
                new CardData { Name = "A" },
                new CardData { Name = "B", DestPath = @"C:\dest\B.pdf", OriginalName = "doc.pdf" },
            ],
        };
        StateStore.Save(StatePath, state);
        var loaded = StateStore.Load(StatePath);
        Assert.Equal(@"C:\dest", loaded.Destination);
        Assert.True(loaded.Locked);
        Assert.Equal(2, loaded.Cards.Count);
        Assert.Null(loaded.Cards[0].DestPath);
        Assert.Equal("doc.pdf", loaded.Cards[1].OriginalName);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_gives_empty_state_and_is_kept_as_bad()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{ not json");
        var s = StateStore.Load(StatePath);
        Assert.Empty(s.Cards);
        Assert.True(File.Exists(StatePath + ".bad"));
    }

    [Fact]
    public void Null_cards_in_file_become_empty_list()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, """{ "Cards": null, "Destination": null, "Locked": false }""");
        Assert.Empty(StateStore.Load(StatePath).Cards);
    }
}
