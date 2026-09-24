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
    public void Rename_changes_name_but_not_last_used()
    {
        var store = NewStore();
        var id = store.Create("Viejo", WithCards("", filled: 1, free: 1));
        var before = store.List().Single().LastUsed;

        store.Rename(id, "Nuevo");

        var info = store.List().Single();
        Assert.Equal("Nuevo", info.Name);
        Assert.Equal(before, info.LastUsed);
        Assert.Equal(2, store.Load(id).Cards.Count);
    }

    [Fact]
    public void List_orders_by_last_use_and_flags_complete_sessions()
    {
        var store = NewStore();
        var old = store.Create("Vieja", WithCards("", filled: 2, free: 0));
        var recent = store.Create("Reciente", WithCards("", filled: 1, free: 1));
        var oldState = store.Load(old);
        oldState.LastUsed = DateTime.Now.AddDays(-3);
        store.Save(old, oldState, touch: false);

        var list = store.List();

        Assert.Equal([recent, old], list.Select(s => s.Id));
        Assert.True(list[1].IsComplete);
        Assert.False(list[0].IsComplete);
        Assert.False(new SessionInfo("x", "Vacía", 0, 0, DateTime.Now).IsComplete);
    }

    [Theory]
    [InlineData(0, "hoy")]
    [InlineData(1, "ayer")]
    [InlineData(4, "hace 4 días")]
    public void When_text_is_relative_for_recent_days(int daysAgo, string expected)
    {
        var now = new DateTime(2026, 9, 23, 10, 0, 0);
        Assert.Equal(expected, SessionInfo.WhenText(now.AddDays(-daysAgo).AddHours(-1), now));
    }

    [Fact]
    public void When_text_shows_the_date_for_older_days()
    {
        var now = new DateTime(2026, 9, 23, 10, 0, 0);
        Assert.Equal("5 sep", SessionInfo.WhenText(new DateTime(2026, 9, 5), now));
        Assert.Equal("5 sep 2025", SessionInfo.WhenText(new DateTime(2025, 9, 5), now));
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
