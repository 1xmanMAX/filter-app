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
}
