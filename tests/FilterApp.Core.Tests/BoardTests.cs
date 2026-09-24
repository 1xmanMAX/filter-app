using FilterApp.Core;

namespace FilterApp.Core.Tests;

public sealed class BoardTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("filterapp-board-").FullName;
    readonly List<string> _messages = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    Board NewBoard(bool withDestination = true)
    {
        var board = new Board();
        board.Notified += _messages.Add;
        if (withDestination)
            board.Destination = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        return board;
    }

    PendingItem Source(string name, string content = "x", bool isTemp = false)
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new PendingItem(path, name, isTemp);
    }

    [Fact]
    public void AddNames_creates_one_card_per_nonempty_line()
    {
        var b = NewBoard();
        Assert.Equal(3, b.AddNames("A\r\n\r\n  B  \nC"));
        Assert.Equal(["A", "B", "C"], b.Cards.Select(c => c.Name));
        Assert.All(b.Cards, c => Assert.Equal(CardStatus.Free, c.Status));
    }

    [Fact]
    public void AddPending_skips_files_already_pending()
    {
        var b = NewBoard();
        var item = Source("a.txt");
        b.AddPending([item]);
        b.AddPending([new PendingItem(item.SourcePath.ToUpperInvariant(), "a.txt", false)]);
        Assert.Single(b.Pending);
    }

    [Fact]
    public async Task Assign_copies_renames_and_fills_card()
    {
        var b = NewBoard();
        b.AddNames("Factura");
        var item = Source("doc.pdf", "pdf");
        b.AddPending([item]);

        Assert.True(await b.AssignAsync(item, b.Cards[0]));

        var card = b.Cards[0];
        Assert.Equal(CardStatus.Filled, card.Status);
        Assert.Equal(Path.Combine(b.Destination!, "Factura.pdf"), card.DestPath);
        Assert.Equal("doc.pdf", card.OriginalName);
        Assert.Equal("pdf", File.ReadAllText(card.DestPath!));
        Assert.Empty(b.Pending);
    }

    [Fact]
    public async Task Filled_card_rejects_and_item_stays_pending()
    {
        var b = NewBoard();
        b.AddNames("A");
        await b.AssignAsync(Source("1.txt"), b.Cards[0]);
        var second = Source("2.txt");
        b.AddPending([second]);

        Assert.False(await b.AssignAsync(second, b.Cards[0]));
        Assert.Contains(second, b.Pending);
        Assert.NotEmpty(_messages);
    }

    [Fact]
    public async Task Without_destination_item_goes_to_pending_and_user_is_told()
    {
        var b = NewBoard(withDestination: false);
        b.AddNames("A");
        var item = Source("a.txt");   // dropped straight on the card, never in Pending

        Assert.False(await b.AssignAsync(item, b.Cards[0]));
        Assert.Contains(item, b.Pending);
        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
        Assert.NotEmpty(_messages);
    }

    [Fact]
    public async Task Deleted_destination_fails_cleanly()
    {
        var b = NewBoard();
        b.AddNames("A");
        Directory.Delete(b.Destination!);
        var item = Source("a.txt");
        b.AddPending([item]);

        Assert.False(await b.AssignAsync(item, b.Cards[0]));
        Assert.Contains(item, b.Pending);
        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
    }

    [Fact]
    public async Task Vanished_source_fails_cleanly()
    {
        var b = NewBoard();
        b.AddNames("A");
        var item = Source("a.txt");
        b.AddPending([item]);
        File.Delete(item.SourcePath);

        Assert.False(await b.AssignAsync(item, b.Cards[0]));
        Assert.Contains(item, b.Pending);
        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
        Assert.NotEmpty(_messages);
    }

    [Fact]
    public async Task Unexpected_copy_error_frees_card_and_keeps_item()
    {
        var b = NewBoard();
        var card = new CardViewModel(null!);   // e.g. hand-edited state.json
        b.Cards.Add(card);
        var item = Source("a.txt");
        b.AddPending([item]);

        Assert.False(await b.AssignAsync(item, card));
        Assert.Equal(CardStatus.Free, card.Status);
        Assert.Contains(item, b.Pending);
        Assert.NotEmpty(_messages);
    }

    [Fact]
    public void IsCopying_reports_cards_in_progress()
    {
        var b = NewBoard();
        b.AddNames("A\nB");
        Assert.False(b.IsCopying);
        b.Cards[1].Status = CardStatus.Copying;
        Assert.True(b.IsCopying);
    }

    [Fact]
    public async Task Temp_source_is_deleted_after_copy()
    {
        var b = NewBoard();
        b.AddNames("A");
        var item = Source("img.png", isTemp: true);
        await b.AssignAsync(item, b.Cards[0]);
        Assert.False(File.Exists(item.SourcePath));
    }

    [Fact]
    public async Task Undo_deletes_copy_and_frees_card()
    {
        var b = NewBoard();
        b.AddNames("A");
        await b.AssignAsync(Source("a.txt"), b.Cards[0]);
        var copy = b.Cards[0].DestPath!;

        b.Undo(b.Cards[0]);

        Assert.False(File.Exists(copy));
        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
        Assert.Null(b.Cards[0].DestPath);
    }

    [Fact]
    public async Task Undo_after_user_deleted_copy_still_frees_card()
    {
        var b = NewBoard();
        b.AddNames("A");
        await b.AssignAsync(Source("a.txt"), b.Cards[0]);
        File.Delete(b.Cards[0].DestPath!);

        b.Undo(b.Cards[0]);

        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
    }

    [Fact]
    public void Explorer_folder_is_followed_only_when_unlocked()
    {
        var b = NewBoard(withDestination: false);
        b.OnExplorerFolder(@"C:\one");
        Assert.Equal(@"C:\one", b.Destination);
        b.Locked = true;
        b.OnExplorerFolder(@"C:\two");
        Assert.Equal(@"C:\one", b.Destination);
    }

    [Fact]
    public async Task RemoveCard_only_removes_free_cards_and_ClearFilled_only_filled()
    {
        var b = NewBoard();
        b.AddNames("A\nB\nC");
        await b.AssignAsync(Source("a.txt"), b.Cards[0]);

        b.RemoveCard(b.Cards[0]);           // filled: ignored
        Assert.Equal(3, b.Cards.Count);
        b.RemoveCard(b.Cards[2]);           // free: removed
        Assert.Equal(["A", "B"], b.Cards.Select(c => c.Name));
        b.ClearFilled();
        Assert.Equal(["B"], b.Cards.Select(c => c.Name));
    }

    [Fact]
    public async Task State_round_trip_restores_board()
    {
        var b = NewBoard();
        b.AddNames("A\nB");
        await b.AssignAsync(Source("f.pdf"), b.Cards[0]);
        b.Locked = true;

        var r = Board.FromState(b.ToState());

        Assert.Equal(b.Destination, r.Destination);
        Assert.True(r.Locked);
        Assert.Equal(CardStatus.Filled, r.Cards[0].Status);
        Assert.Equal("f.pdf", r.Cards[0].OriginalName);
        Assert.Equal(b.Cards[0].DestPath, r.Cards[0].DestPath);
        Assert.Equal(CardStatus.Free, r.Cards[1].Status);
    }

    [Fact]
    public void Changes_raise_Changed()
    {
        var b = NewBoard();
        int count = 0;
        b.Changed += () => count++;
        b.AddNames("A");
        b.Locked = true;
        b.Destination = @"C:\x";
        Assert.Equal(3, count);
    }

    // ---- Hold mode ----

    [Fact]
    public async Task Holding_parks_file_on_card_without_copying()
    {
        var b = NewBoard(withDestination: false);   // no destination needed to hold
        b.Holding = true;
        b.AddNames("A");
        var item = Source("a.txt");
        b.AddPending([item]);

        Assert.True(await b.AssignAsync(item, b.Cards[0]));

        Assert.Equal(CardStatus.Held, b.Cards[0].Status);
        Assert.Same(item, b.Cards[0].HeldItem);
        Assert.Empty(b.Pending);
        Assert.Equal(1, b.HeldCount);
    }

    [Fact]
    public async Task Held_card_rejects_another_file()
    {
        var b = NewBoard();
        b.Holding = true;
        b.AddNames("A");
        await b.AssignAsync(Source("1.txt"), b.Cards[0]);
        var second = Source("2.txt");

        Assert.False(await b.AssignAsync(second, b.Cards[0]));
        Assert.Contains(second, b.Pending);
    }

    [Fact]
    public async Task Release_copies_every_held_file_to_destination()
    {
        var b = NewBoard();
        b.Holding = true;
        b.AddNames("A\nB");
        await b.AssignAsync(Source("a.txt", "1"), b.Cards[0]);
        await b.AssignAsync(Source("b.txt", "2", isTemp: true), b.Cards[1]);
        var temp = Path.Combine(_root, "src", "b.txt");

        Assert.Equal(2, await b.ReleaseAsync());

        Assert.All(b.Cards, c => Assert.Equal(CardStatus.Filled, c.Status));
        Assert.Equal("1", File.ReadAllText(Path.Combine(b.Destination!, "A.txt")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(b.Destination!, "B.txt")));
        Assert.Equal("a.txt", b.Cards[0].OriginalName);
        Assert.False(File.Exists(temp));
        Assert.Equal(0, b.HeldCount);
    }

    [Fact]
    public async Task Release_failure_returns_that_file_to_pending_and_continues()
    {
        var b = NewBoard();
        b.Holding = true;
        b.AddNames("A\nB");
        var gone = Source("gone.txt");
        await b.AssignAsync(gone, b.Cards[0]);
        await b.AssignAsync(Source("ok.txt"), b.Cards[1]);
        File.Delete(gone.SourcePath);

        Assert.Equal(1, await b.ReleaseAsync());

        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
        Assert.Contains(gone, b.Pending);
        Assert.Equal(CardStatus.Filled, b.Cards[1].Status);
        Assert.NotEmpty(_messages);
    }

    [Fact]
    public async Task Release_without_destination_keeps_files_held()
    {
        var b = NewBoard(withDestination: false);
        b.Holding = true;
        b.AddNames("A");
        await b.AssignAsync(Source("a.txt"), b.Cards[0]);

        Assert.Equal(0, await b.ReleaseAsync());

        Assert.Equal(CardStatus.Held, b.Cards[0].Status);
        Assert.NotEmpty(_messages);
    }

    [Fact]
    public async Task ReturnHeld_puts_file_back_in_pending_and_frees_card()
    {
        var b = NewBoard();
        b.Holding = true;
        b.AddNames("A");
        var item = Source("a.txt");
        await b.AssignAsync(item, b.Cards[0]);

        b.ReturnHeld(b.Cards[0]);

        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
        Assert.Null(b.Cards[0].HeldItem);
        Assert.Contains(item, b.Pending);
        Assert.Equal(0, b.HeldCount);
    }

    [Fact]
    public async Task Held_cards_are_saved_as_free_and_holding_is_remembered()
    {
        var b = NewBoard();
        b.Holding = true;
        b.AddNames("A");
        await b.AssignAsync(Source("a.txt"), b.Cards[0]);

        var r = Board.FromState(b.ToState());

        Assert.True(r.Holding);
        Assert.Equal(CardStatus.Free, r.Cards[0].Status);
    }

    // ---- Sessions ----

    [Fact]
    public async Task Pending_and_held_files_are_saved_as_pending_but_temp_files_are_not()
    {
        var b = NewBoard();
        b.Name = "Clientes";
        b.AddNames("A");
        var pending = Source("p.txt");
        var temp = Source("clip.png", isTemp: true);
        b.AddPending([pending, temp]);
        b.Holding = true;
        var held = Source("h.txt");
        await b.AssignAsync(held, b.Cards[0]);

        Assert.Equal(1, b.UnsavedCount);
        var r = Board.FromState(b.ToState());

        Assert.Equal("Clientes", r.Name);
        Assert.Equal([pending.SourcePath, held.SourcePath], r.Pending.Select(p => p.SourcePath));
        Assert.Equal(["p.txt", "h.txt"], r.Pending.Select(p => p.DisplayName));
        Assert.Equal(CardStatus.Free, r.Cards[0].Status);
    }

    [Fact]
    public void Tray_changes_trigger_a_save()
    {
        var b = NewBoard();
        int count = 0;
        b.Changed += () => count++;
        var item = Source("a.txt");

        b.AddPending([item]);
        b.RemovePending(item);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Saved_pending_files_that_no_longer_exist_are_dropped()
    {
        var b = NewBoard();
        var gone = Source("gone.txt");
        b.AddPending([gone, Source("ok.txt")]);
        var state = b.ToState();
        File.Delete(gone.SourcePath);

        Assert.Equal(["ok.txt"], Board.FromState(state).Pending.Select(p => p.DisplayName));
    }

    [Fact]
    public async Task LoadState_replaces_the_whole_board()
    {
        var b = NewBoard();
        b.AddNames("A\nB");
        var temp = Source("clip.png", isTemp: true);
        b.AddPending([temp]);
        await b.AssignAsync(Source("x.txt"), b.Cards[0]);

        b.LoadState(new AppState { Name = "Otra", Destination = @"C:\otra", Cards = [new CardData { Name = "Z" }] });

        Assert.Equal("Otra", b.Name);
        Assert.Equal(@"C:\otra", b.Destination);
        Assert.Equal(["Z"], b.Cards.Select(c => c.Name));
        Assert.Empty(b.Pending);
        Assert.False(File.Exists(temp.SourcePath));   // unsaved temp files are cleaned up, not leaked
    }

    // ---- Fixes ----

    [Fact]
    public async Task Undo_does_not_delete_a_file_another_card_points_to()
    {
        var b = NewBoard();
        b.AddNames("X\nX");
        await b.AssignAsync(Source("1.txt"), b.Cards[0]);
        File.Delete(b.Cards[0].DestPath!);                   // user removed it by hand
        await b.AssignAsync(Source("2.txt", "keep"), b.Cards[1]);   // reuses X.txt
        Assert.Equal(b.Cards[0].DestPath, b.Cards[1].DestPath);

        b.Undo(b.Cards[0]);

        Assert.Equal(CardStatus.Free, b.Cards[0].Status);
        Assert.Equal("keep", File.ReadAllText(b.Cards[1].DestPath!));
    }

    [Fact]
    public async Task Failed_drop_of_already_pending_file_does_not_duplicate_it()
    {
        var b = NewBoard(withDestination: false);
        b.AddNames("A");
        var item = Source("a.txt");
        b.AddPending([item]);
        var sameFile = new PendingItem(item.SourcePath, "a.txt", false);   // dropped again from Explorer

        Assert.False(await b.AssignAsync(sameFile, b.Cards[0]));

        Assert.Single(b.Pending);
    }

    [Fact]
    public void DestinationExists_tracks_the_folder()
    {
        var b = NewBoard();
        Assert.False(NewBoard(withDestination: false).DestinationMissing);   // nothing chosen yet is not "missing"
        Assert.True(b.DestinationExists);
        Assert.False(b.DestinationMissing);
        Directory.Delete(b.Destination!);
        b.RefreshDestination();
        Assert.False(b.DestinationExists);
        Assert.True(b.DestinationMissing);
    }

    [Fact]
    public void Choosing_destination_removes_leftover_partial_files()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "d2")).FullName;
        var leftover = Path.Combine(dir, $".{Guid.NewGuid():N}.partial");
        var userFile = Path.Combine(dir, ".notes.partial");
        File.WriteAllText(leftover, "");
        File.WriteAllText(userFile, "");
        var b = NewBoard(withDestination: false);

        b.OnExplorerFolder(dir);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(userFile));
    }
}
