using FilterApp.Core;

namespace FilterApp.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("filterapp-sessions-").FullName;
    SessionStore NewStore() => new(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static AppState WithCards(string name, int filled, int free)
    {
        var state = new AppState { Name = name };
        for (int i = 0; i < filled; i++) state.Cards.Add(new CardData { Name = $"F{i}", DestPath = $@"C:\d\F{i}.txt" });
        for (int i = 0; i < free; i++) state.Cards.Add(new CardData { Name = $"L{i}" });
        return state;
    }

    [Fact]
    public void First_open_without_anything_creates_Mi_sesion()
    {
        var store = NewStore();

        var id = store.OpenCurrent(out var state);

        Assert.Equal("Mi sesión", state.Name);
        Assert.Equal(id, store.CurrentId);
        Assert.Single(store.List());
    }

    [Fact]
    public void First_open_migrates_legacy_state_into_Mi_sesion()
    {
        StateStore.Save(Path.Combine(_root, "state.json"), WithCards("", filled: 2, free: 1));
        var store = NewStore();

        store.OpenCurrent(out var state);

        Assert.Equal("Mi sesión", state.Name);
        Assert.Equal(3, state.Cards.Count);
        var info = Assert.Single(store.List());
        Assert.Equal((2, 3), (info.Filled, info.Total));
    }

    [Fact]
    public void Create_save_and_reload_keep_each_session_apart()
    {
        var store = NewStore();
        var a = store.Create("Clientes marzo");
        var b = store.Create("Contratos abril");
        store.Save(a, WithCards("Clientes marzo", filled: 7, free: 3));
        store.Save(b, WithCards("Contratos abril", filled: 2, free: 13));

        var list = store.List();

        Assert.Equal(["Clientes marzo", "Contratos abril"], list.Select(s => s.Name).Order());
        var first = list.Single(s => s.Id == a);
        Assert.Equal((7, 10), (first.Filled, first.Total));
        Assert.Equal(15, store.Load(b).Cards.Count);
    }

    [Fact]
    public void Current_session_is_remembered_between_runs()
    {
        var store = NewStore();
        store.OpenCurrent(out _);
        var other = store.Create("Otra");
        store.CurrentId = other;

        var id = NewStore().OpenCurrent(out var state);

        Assert.Equal(other, id);
        Assert.Equal("Otra", state.Name);
    }

    [Fact]
    public void Missing_current_session_falls_back_to_an_existing_one()
    {
        var store = NewStore();
        var keep = store.Create("Queda");
        store.CurrentId = "does-not-exist";

        Assert.Equal(keep, NewStore().OpenCurrent(out _));
    }

    [Fact]
    public void Delete_removes_only_that_session()
    {
        var store = NewStore();
        var a = store.Create("A");
        var b = store.Create("B");

        store.Delete(a);

        Assert.Equal([b], store.List().Select(s => s.Id));
    }
}
