using FilterApp.Core;

namespace FilterApp.Core.Tests;

public sealed class TreeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("filterapp-tree-").FullName;
    readonly List<string> _messages = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    Board NewBoard()
    {
        var board = new Board();
        board.Notified += _messages.Add;
        board.Destination = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        return board;
    }

    PendingItem Source(string relative, string content = "x")
    {
        var path = Path.Combine(_root, "src", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new PendingItem(path, Path.GetFileName(path), isTemp: false);
    }

    // ---- Structure parser ----

    [Fact]
    public void Parser_builds_folders_from_paths_and_indentation()
    {
        var b = NewBoard();
        int added = b.AddNames("Clientes/\n  Juan Perez/\n    DNI\n    Contrato\n  Ana/\nFacturas/2026/Enero\nSuelto");

        var clientes = b.Root.FindFolder("Clientes")!;
        Assert.Equal(["Juan Perez", "Ana"], clientes.Folders.Select(f => f.Name));
        Assert.Equal(["DNI", "Contrato"], clientes.FindFolder("Juan Perez")!.Cards.Select(c => c.Name));
        Assert.Equal(["Enero"], b.Root.FindFolder("Facturas")!.FindFolder("2026")!.Cards.Select(c => c.Name));
        Assert.Equal(["Suelto"], b.Cards.Select(c => c.Name));
        Assert.Equal(5 + 4, added);   // Clientes, Juan Perez, Ana, Facturas, 2026 + 4 names
    }

    [Fact]
    public void Parser_makes_a_line_with_indented_lines_a_folder_without_any_slash()
    {
        var b = NewBoard();
        b.AddNames("Clientes\n\tJuan Perez\n\t\tDNI\n\t\tContrato\n\tAna\nSuelto");

        var juan = b.Root.FindFolder("Clientes")!.FindFolder("Juan Perez")!;
        Assert.Equal(["DNI", "Contrato"], juan.Cards.Select(c => c.Name));
        Assert.Equal(["Ana"], b.Root.FindFolder("Clientes")!.Cards.Select(c => c.Name));
        Assert.Equal(["Suelto"], b.Cards.Select(c => c.Name));
    }

    [Fact]
    public void Parser_can_make_every_line_a_folder()
    {
        var b = NewBoard();
        b.AddNames("Fotos\n  2024\n  2025\nVideos", leavesAreFolders: true);

        Assert.Equal(["Fotos", "Videos"], b.Root.Folders.Select(f => f.Name));
        Assert.Equal(["2024", "2025"], b.Root.FindFolder("Fotos")!.Folders.Select(f => f.Name));
        Assert.Empty(b.Cards);
    }

    [Fact]
    public void Parser_reuses_existing_folders_ignoring_case()
    {
        var b = NewBoard();
        b.AddNames("Clientes/\n  A");
        b.AddNames("clientes/B");
        Assert.Single(b.Root.Folders);
        Assert.Equal(["A", "B"], b.Root.Folders[0].Cards.Select(c => c.Name));
    }

    [Fact]
    public void Parser_adds_into_the_given_folder()
    {
        var b = NewBoard();
        var f = b.AddFolder(b.Root, "Obra");
        b.AddNames("Planos/\n  Fachada", f);
        Assert.Equal("Fachada", f.FindFolder("Planos")!.Cards[0].Name);
    }

    // ---- Folder progress ----

    [Fact]
    public async Task Folder_summary_counts_recursively()
    {
        var b = NewBoard();
        b.AddNames("A/\n  B/\n    Uno\n    Dos");
        var a = b.Root.FindFolder("A")!;
        Assert.Equal(2, a.NameCount);
        Assert.Equal(0, a.FilledNames);

        await b.AssignAsync(Source("1.pdf"), a.Folders[0].Cards[0]);
        await b.PlaceAsync(Source("foto.jpg"), a.Folders[0]);

        Assert.Equal(1, a.FilledNames);
        Assert.Equal(1, a.FileCount);
        Assert.Equal("1 carpeta · 1/2 nombres · 1 archivo", a.Summary);
    }

    // ---- Placing ----

    [Fact]
    public async Task Assign_in_a_subfolder_creates_the_folders_on_disk()
    {
        var b = NewBoard();
        b.AddNames("Clientes/Juan/DNI");
        var card = b.Root.FindFolder("Clientes")!.FindFolder("Juan")!.Cards[0];

        Assert.True(await b.AssignAsync(Source("scan.pdf", "pdf"), card));

        Assert.Equal(Path.Combine(b.Destination!, "Clientes", "Juan", "DNI.pdf"), card.DestPath);
        Assert.Equal("pdf", File.ReadAllText(card.DestPath!));
    }

    [Fact]
    public async Task Place_in_folder_keeps_the_file_name_and_never_overwrites()
    {
        var b = NewBoard();
        var fotos = b.AddFolder(b.Root, "Fotos");

        await b.PlaceAsync(Source("a/foto.jpg", "1"), fotos);
        await b.PlaceAsync(Source("b/foto.jpg", "2"), fotos);

        Assert.Equal(["foto.jpg", "foto (2).jpg"], fotos.Files.Select(c => c.FileName));
        Assert.All(fotos.Files, c => Assert.True(c.IsAuto));
    }

    [Fact]
    public async Task Failed_place_removes_the_file_entry_and_keeps_the_item_pending()
    {
        var b = NewBoard();
        var fotos = b.AddFolder(b.Root, "Fotos");
        var gone = new PendingItem(Path.Combine(_root, "nope.jpg"), "nope.jpg", false);

        Assert.False(await b.PlaceAsync(gone, fotos));

        Assert.Empty(fotos.Files);
        Assert.Contains(gone, b.Pending);
    }

    [Fact]
    public async Task Place_many_keeping_folders_rebuilds_the_dropped_tree()
    {
        var b = NewBoard();
        var target = b.AddFolder(b.Root, "Archivo");
        Source("Viaje/a.jpg");
        Source("Viaje/Dia 2/b.jpg");
        var items = FolderScanner.Scan(Path.Combine(_root, "src", "Viaje"));

        Assert.Equal(2, await b.PlaceManyAsync(items, target, keepFolders: true));

        Assert.True(File.Exists(Path.Combine(b.Destination!, "Archivo", "Viaje", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(b.Destination!, "Archivo", "Viaje", "Dia 2", "b.jpg")));
        Assert.Equal("Dia 2", target.FindFolder("Viaje")!.Folders[0].Name);
    }

    [Fact]
    public async Task Send_to_path_creates_folders_and_name()
    {
        var b = NewBoard();
        Assert.True(await b.SendToPathAsync(Source("x.pdf"), b.Root, "Clientes/Juan/DNI"));
        Assert.True(await b.SendToPathAsync(Source("y.pdf"), b.Root, "Clientes\\Juan\\"));

        var juan = b.Root.FindFolder("Clientes")!.FindFolder("Juan")!;
        Assert.Equal("DNI.pdf", Path.GetFileName(juan.Cards[0].DestPath));
        Assert.Equal("y.pdf", juan.Files[0].FileName);
    }

    [Fact]
    public async Task Send_to_path_uses_an_existing_free_card()
    {
        var b = NewBoard();
        b.AddNames("Clientes/DNI");
        await b.SendToPathAsync(Source("x.pdf"), b.Root, "clientes/dni");
        var cards = b.Root.FindFolder("Clientes")!.Cards;
        Assert.Single(cards);
        Assert.Equal(CardStatus.Filled, cards[0].Status);
    }

    [Fact]
    public async Task Held_file_in_folder_returns_to_tray_and_its_entry_goes_away()
    {
        var b = NewBoard();
        b.Holding = true;
        var fotos = b.AddFolder(b.Root, "Fotos");
        var item = Source("foto.jpg");

        await b.PlaceAsync(item, fotos);
        Assert.Equal(1, b.HeldCount);
        b.ReturnHeld(fotos.Files[0]);

        Assert.Empty(fotos.Files);
        Assert.Contains(item, b.Pending);
    }

    [Fact]
    public async Task Release_places_held_files_in_their_folders()
    {
        var b = NewBoard();
        b.Holding = true;
        var fotos = b.AddFolder(b.Root, "Fotos");
        await b.PlaceAsync(Source("foto.jpg"), fotos);

        Assert.Equal(1, await b.ReleaseAsync());
        Assert.True(File.Exists(Path.Combine(b.Destination!, "Fotos", "foto.jpg")));
    }

    // ---- Sorting inside a folder ----

    [Fact]
    public async Task Files_dropped_in_a_folder_can_then_go_into_a_subfolder()
    {
        var b = NewBoard();
        var docs = b.AddFolder(b.Root, "Documentos");
        var facturas = b.AddFolder(docs, "Facturas");
        await b.PlaceManyAsync([Source("a.pdf", "A"), Source("b.pdf", "B")], docs);

        Assert.Equal(1, await b.RelocateManyAsync([docs.Files[0]], facturas));

        Assert.Equal(["b.pdf"], docs.Files.Select(f => f.FileName));
        Assert.Equal(["a.pdf"], facturas.Files.Select(f => f.FileName));
        Assert.Equal("A", File.ReadAllText(Path.Combine(b.Destination!, "Documentos", "Facturas", "a.pdf")));
        Assert.False(File.Exists(Path.Combine(b.Destination!, "Documentos", "a.pdf")));
    }

    [Fact]
    public async Task A_placed_file_dropped_on_a_name_takes_that_name()
    {
        var b = NewBoard();
        var docs = b.AddFolder(b.Root, "Docs");
        b.AddNames("Juan/\n  DNI", docs);
        await b.PlaceAsync(Source("scan001.pdf"), docs);
        var dni = docs.FindFolder("Juan")!.Cards[0];

        Assert.True(await b.RelocateAsync(docs.Files[0], dni.Folder!, into: dni));

        Assert.Empty(docs.Files);
        Assert.Equal(Path.Combine(b.Destination!, "Docs", "Juan", "DNI.pdf"), dni.DestPath);
        Assert.Equal("scan001.pdf", dni.OriginalName);
    }

    [Fact]
    public async Task Rename_in_place_keeps_the_extension_and_never_overwrites()
    {
        var b = NewBoard();
        var docs = b.AddFolder(b.Root, "Docs");
        await b.PlaceManyAsync([Source("x/scan1.jpg"), Source("y/scan2.jpg"), Source("z/Recibo.jpg")], docs);

        Assert.True(await b.RenameFileAsync(docs.Files[0], "Recibo"));

        Assert.Contains("Recibo (2).jpg", docs.Files.Select(f => f.FileName));
        Assert.True(File.Exists(Path.Combine(b.Destination!, "Docs", "Recibo (2).jpg")));
        Assert.False(File.Exists(Path.Combine(b.Destination!, "Docs", "scan1.jpg")));
    }

    [Fact]
    public async Task Undo_after_sorting_a_moved_file_sends_it_back_to_its_origin()
    {
        var b = NewBoard();
        b.Move = true;
        var docs = b.AddFolder(b.Root, "Docs");
        var sub = b.AddFolder(docs, "Sub");
        var item = Source("orig/a.txt", "data");
        await b.PlaceAsync(item, docs);
        await b.RelocateAsync(docs.Files[0], sub);

        b.UndoLast();

        Assert.Equal("data", File.ReadAllText(item.SourcePath));
        Assert.Empty(sub.Files);
        Assert.Empty(Directory.EnumerateFiles(b.Destination!, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_held_file_changes_folder_without_touching_disk()
    {
        var b = NewBoard();
        b.Holding = true;
        var docs = b.AddFolder(b.Root, "Docs");
        var sub = b.AddFolder(docs, "Sub");
        await b.PlaceAsync(Source("a.txt"), docs);

        Assert.True(await b.RelocateAsync(docs.Files[0], sub));

        Assert.Empty(docs.Files);
        Assert.Equal(CardStatus.Held, sub.Files[0].Status);
        Assert.Empty(Directory.EnumerateFiles(b.Destination!, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Relocate_to_path_creates_what_is_missing()
    {
        var b = NewBoard();
        var docs = b.AddFolder(b.Root, "Docs");
        await b.PlaceAsync(Source("a.pdf"), docs);

        Assert.True(await b.RelocateToPathAsync(docs.Files[0], docs, "2026/Marzo/Factura"));

        var card = docs.FindFolder("2026")!.FindFolder("Marzo")!.Cards.Single();
        Assert.Equal(Path.Combine(b.Destination!, "Docs", "2026", "Marzo", "Factura.pdf"), card.DestPath);
    }

    // ---- Moving ----

    [Fact]
    public async Task Move_mode_moves_the_file()
    {
        var b = NewBoard();
        b.Move = true;
        b.AddNames("Factura");
        var item = Source("doc.pdf", "pdf");

        await b.AssignAsync(item, b.Cards[0]);

        Assert.False(File.Exists(item.SourcePath));
        Assert.Equal("pdf", File.ReadAllText(b.Cards[0].DestPath!));
        Assert.Equal(item.SourcePath, b.Cards[0].MovedFrom);
    }

    [Fact]
    public async Task Undo_of_a_move_puts_the_file_back_and_in_the_tray()
    {
        var b = NewBoard();
        b.Move = true;
        var fotos = b.AddFolder(b.Root, "Fotos");
        var item = Source("foto.jpg", "img");
        await b.PlaceAsync(item, fotos);

        Assert.Same(fotos.Files[0], b.UndoLast());

        Assert.Equal("img", File.ReadAllText(item.SourcePath));
        Assert.Empty(fotos.Files);
        Assert.Contains(b.Pending, p => p.SourcePath == item.SourcePath);
        Assert.False(b.CanUndo);
    }

    [Fact]
    public async Task Undo_last_skips_cards_already_undone()
    {
        var b = NewBoard();
        b.AddNames("A\nB");
        await b.AssignAsync(Source("1.txt"), b.Cards[0]);
        await b.AssignAsync(Source("2.txt"), b.Cards[1]);
        b.Undo(b.Cards[1]);

        Assert.Same(b.Cards[0], b.UndoLast());
        Assert.Null(b.UndoLast());
    }

    [Fact]
    public void Move_back_never_overwrites_a_file_that_took_the_old_name()
    {
        var placed = Source("dest/a.txt", "moved");
        var original = Source("orig/a.txt", "new one").SourcePath;

        var back = Transfer.MoveBack(placed.SourcePath, original);

        Assert.Equal(Path.Combine(Path.GetDirectoryName(original)!, "a (2).txt"), back);
        Assert.Equal("new one", File.ReadAllText(original));
    }

    [Fact]
    public void Moving_a_file_onto_its_own_name_leaves_it_alone()
    {
        var item = Source("same/a.txt");
        var result = Transfer.Run(item.SourcePath, Path.GetDirectoryName(item.SourcePath)!, "a", move: true);
        Assert.Equal(item.SourcePath, result.DestPath);
        Assert.True(File.Exists(item.SourcePath));
    }

    // ---- Folders in the list ----

    [Fact]
    public async Task Remove_folder_is_refused_while_a_file_waits_inside()
    {
        var b = NewBoard();
        b.Holding = true;
        var f = b.AddFolder(b.Root, "F");
        await b.PlaceAsync(Source("a.txt"), f);

        Assert.False(b.RemoveFolder(f));
        b.Holding = false;
        b.ReturnHeld(f.Files[0]);
        Assert.True(b.RemoveFolder(f));
        Assert.Empty(b.Root.Folders);
    }

    [Fact]
    public void Rename_folder_refuses_a_sibling_name()
    {
        var b = NewBoard();
        var a = b.AddFolder(b.Root, "A");
        b.AddFolder(b.Root, "B");
        b.RenameFolder(a, "b");
        Assert.Equal("A", a.Name);
        Assert.NotEmpty(_messages);
    }

    [Fact]
    public async Task Clear_filled_empties_every_level_but_keeps_folders()
    {
        var b = NewBoard();
        b.AddNames("F/\n  N");
        var f = b.Root.FindFolder("F")!;
        await b.AssignAsync(Source("1.txt"), f.Cards[0]);
        await b.PlaceAsync(Source("2.txt"), f);

        b.ClearFilled();

        Assert.Empty(f.Cards);
        Assert.Empty(f.Files);
        Assert.Single(b.Root.Folders);
    }

    [Fact]
    public void Search_finds_folders_and_free_names_ignoring_accents()
    {
        var b = NewBoard();
        b.AddNames("Clientes/\n  José/\n    DNI José\nOtros/jose");
        var hits = b.Search("jose");

        Assert.Equal(["José", "jose", "DNI José"], hits.Select(h => h.Name));
        Assert.True(hits[0].IsFolder);
        Assert.Equal("Clientes", hits[0].Location);
        Assert.Equal("Clientes / José", hits[2].Location);
    }

    // ---- Saving ----

    [Fact]
    public async Task Tree_round_trips_through_saved_state()
    {
        var b = NewBoard();
        b.Move = true;
        b.AddNames("Clientes/\n  Juan/\n    DNI");
        var juan = b.Root.FindFolder("Clientes")!.FindFolder("Juan")!;
        await b.AssignAsync(Source("a.pdf"), juan.Cards[0]);
        await b.PlaceAsync(Source("b.jpg"), juan);
        var path = Path.Combine(_root, "s.json");
        StateStore.Save(path, b.ToState());

        var r = Board.FromState(StateStore.Load(path));

        var rj = r.Root.FindFolder("Clientes")!.FindFolder("Juan")!;
        Assert.True(r.Move);
        Assert.Equal(CardStatus.Filled, rj.Cards[0].Status);
        Assert.NotNull(rj.Cards[0].MovedFrom);
        Assert.Equal("b.jpg", rj.Files.Single().FileName);
        Assert.True(rj.Files[0].IsAuto);
    }

    [Fact]
    public void Session_progress_counts_names_in_every_folder()
    {
        var store = new SessionStore(_root);
        var b = NewBoard();
        b.AddNames("A\nF/\n  B\n  C");
        var id = store.Create("s", b.ToState());

        var info = store.List().Single(s => s.Id == id);
        Assert.Equal(3, info.Total);
    }

    [Fact]
    public void Pending_groups_are_saved()
    {
        var b = NewBoard();
        var file = Source("Viaje/a.jpg");
        b.AddPending([new PendingItem(file.SourcePath, "a.jpg", false, "Viaje")]);
        var r = Board.FromState(b.ToState());
        Assert.Equal("Viaje", r.Pending.Single().Group);
    }

    // ---- Folder intake ----

    [Fact]
    public void Scan_lists_files_depth_first_with_their_folder_and_skips_hidden()
    {
        Source("Disco/b.txt");
        Source("Disco/a.txt");
        Source("Disco/Sub/c.txt");
        var hidden = Source("Disco/desktop.ini").SourcePath;
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var items = FolderScanner.Scan(Path.Combine(_root, "src", "Disco"));

        Assert.Equal(["a.txt", "b.txt", "c.txt"], items.Select(i => i.DisplayName));
        Assert.Equal(["Disco", "Disco", Path.Combine("Disco", "Sub")], items.Select(i => i.Group));
        File.SetAttributes(hidden, FileAttributes.Normal);
    }

    [Fact]
    public void Adding_thousands_of_pending_files_is_fast()
    {
        var b = NewBoard();
        var items = Enumerable.Range(0, 20_000).Select(i => new PendingItem($@"C:\x\{i}.txt", $"{i}.txt", false)).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        b.AddPending(items);
        b.AddPending(items);   // all duplicates
        Assert.Equal(20_000, b.Pending.Count);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }
}
