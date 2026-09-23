# Filter App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows desktop app where files dropped/pasted into it are assigned to named cards and copied to the destination folder renamed to the card's name.

**Architecture:** Two projects. `FilterApp.Core` (plain `net10.0`, no WPF) holds all testable logic: name resolution, copying, persisted state and the view-models (`Board`, `CardViewModel`, `PendingItem`). `FilterApp` (`net10.0-windows`, WPF) holds XAML, drag/drop + clipboard intake, and Win32/COM interop that detects the active Explorer folder. xUnit tests cover Core; UI/interop is verified manually.

**Tech Stack:** C# / .NET 10, WPF, xUnit. No third-party runtime packages.

**Spec:** `docs/superpowers/specs/2026-09-23-filter-app-design.md`

## Global Constraints

- Windows only. `net10.0` for Core/tests, `net10.0-windows` + `UseWPF` for the app. No NuGet packages beyond the xUnit test template's.
- Always **copy**, never move; source file is never modified.
- Final name = sanitized card name + original extension (unchanged case); conflict → ` (2)`, ` (3)`…; never overwrite.
- A card with a file (Filled) or being copied (Copying) rejects drops.
- Destination follows the last Explorer window used unless `Locked`.
- State file: `%APPDATA%\FilterApp\state.json`; temp files: `%TEMP%\FilterApp\<guid>\`.
- UI text is Spanish.
- UI thread never does the file copy (copy runs on `Task.Run`).

## Review Focus

1. Card name that sanitizes to nothing or to a reserved device name (`"..."`, `"   "`, `"CON"`) → file still saved with a valid name (`_`, `_CON`). Pinned in Task 1.
2. Destination folder deleted/renamed after it was chosen → copy fails cleanly, card stays free, file back in Pendientes, message shown. Pinned in Task 4.
3. Source file vanished before assignment (Telegram temp cleared) → same clean failure. Pinned in Task 4.
4. User deletes the copied file in Explorer, then presses ✕ → card is freed without error. Pinned in Tasks 2 and 4.
5. Corrupt `state.json` (crash mid-write, manual edit) → app starts empty and keeps the bad file as `state.json.bad`. Pinned in Task 3.

---

## File Structure

```
FilterApp.slnx
.gitignore
src/FilterApp.Core/
  FilterApp.Core.csproj
  NameResolver.cs        sanitize card name, build unique destination path
  Copier.cs              copy without overwrite (race-safe), undo
  StateStore.cs          AppState/CardData + JSON load/save (atomic)
  Observable.cs          INotifyPropertyChanged base
  PendingItem.cs         a file waiting in the tray
  CardViewModel.cs       one card (CardStatus enum)
  Board.cs               main view-model: cards, pending, destination, lock, assign/undo
src/FilterApp/
  FilterApp.csproj
  App.xaml / App.xaml.cs            startup, styles
  MainWindow.xaml / .xaml.cs        layout, drag/drop, paste, toast, save debounce
  NamesDialog.xaml / .xaml.cs       "+ Nombres" dialog
  Intake/FileIntake.cs              IDataObject → PendingItems (FileDrop, virtual, bitmap)
  Intake/VirtualFileReader.cs       FileGroupDescriptorW + FileContents materialization
  Interop/FolderWatcher.cs          foreground-window hook
  Interop/ExplorerPath.cs           Explorer HWND → folder path (tab aware)
tests/FilterApp.Core.Tests/
  NameResolverTests.cs  CopierTests.cs  StateStoreTests.cs  BoardTests.cs
```

---

### Task 1: Solution scaffold + NameResolver

**Files:**
- Create: `FilterApp.slnx`, `.gitignore`, `src/FilterApp.Core/FilterApp.Core.csproj`, `src/FilterApp.Core/NameResolver.cs`
- Test: `tests/FilterApp.Core.Tests/NameResolverTests.cs`

**Interfaces:**
- Produces: `static string NameResolver.Sanitize(string cardName)`; `static string NameResolver.Resolve(string destDir, string cardName, string sourceFileName, Func<string,bool> exists)`

- [ ] **Step 1: Scaffold**

```bash
cd "F:/THE FORGE/FILTER APP"
git init
dotnet new gitignore
echo "publish/" >> .gitignore
dotnet new sln -n FilterApp
dotnet new classlib -n FilterApp.Core -o src/FilterApp.Core -f net10.0
dotnet new xunit -n FilterApp.Core.Tests -o tests/FilterApp.Core.Tests -f net10.0
rm src/FilterApp.Core/Class1.cs tests/FilterApp.Core.Tests/UnitTest1.cs
dotnet sln add src/FilterApp.Core tests/FilterApp.Core.Tests
dotnet add tests/FilterApp.Core.Tests reference src/FilterApp.Core
```

- [ ] **Step 2: Write the failing tests** — `tests/FilterApp.Core.Tests/NameResolverTests.cs`

```csharp
using FilterApp.Core;

namespace FilterApp.Core.Tests;

public class NameResolverTests
{
    const string Dir = @"C:\dest";
    static readonly Func<string, bool> Nothing = _ => false;

    [Fact]
    public void Uses_card_name_and_original_extension() =>
        Assert.Equal(Path.Combine(Dir, "Factura Enero.jpg"), NameResolver.Resolve(Dir, "Factura Enero", "foto.jpg", Nothing));

    [Fact]
    public void Keeps_extension_case() =>
        Assert.Equal(Path.Combine(Dir, "X.JPG"), NameResolver.Resolve(Dir, "X", "IMG_1.JPG", Nothing));

    [Fact]
    public void File_without_extension_gets_none() =>
        Assert.Equal(Path.Combine(Dir, "X"), NameResolver.Resolve(Dir, "X", "README", Nothing));

    [Fact]
    public void Adds_counter_when_name_is_taken()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Dir, "X.pdf"), Path.Combine(Dir, "X (2).pdf"),
        };
        Assert.Equal(Path.Combine(Dir, "X (3).pdf"), NameResolver.Resolve(Dir, "X", "a.pdf", taken.Contains));
    }

    [Theory]
    [InlineData("Contrato/Juan", "Contrato_Juan")]
    [InlineData("a:b*c?d", "a_b_c_d")]
    [InlineData("  Nombre  ", "Nombre")]
    [InlineData("Nombre. . ", "Nombre")]
    [InlineData("...", "_")]
    [InlineData("   ", "_")]
    [InlineData("CON", "_CON")]
    [InlineData("com1", "_com1")]
    public void Sanitize_produces_a_valid_windows_name(string input, string expected) =>
        Assert.Equal(expected, NameResolver.Sanitize(input));
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test`
Expected: build error — `NameResolver` does not exist.

- [ ] **Step 4: Implement** — `src/FilterApp.Core/NameResolver.cs`

```csharp
namespace FilterApp.Core;

/// Turns a card name into a safe, unique destination path.
public static class NameResolver
{
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string cardName)
    {
        var chars = cardName.Select(c => Array.IndexOf(Invalid, c) >= 0 ? '_' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "_";
        return Reserved.Contains(name) ? "_" + name : name;
    }

    public static string Resolve(string destDir, string cardName, string sourceFileName, Func<string, bool> exists)
    {
        var baseName = Sanitize(cardName);
        var ext = Path.GetExtension(sourceFileName);
        var candidate = Path.Combine(destDir, baseName + ext);
        for (int n = 2; exists(candidate); n++)
            candidate = Path.Combine(destDir, $"{baseName} ({n}){ext}");
        return candidate;
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test`
Expected: all NameResolverTests PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: scaffold solution and NameResolver"
```

---

### Task 2: Copier

**Files:**
- Create: `src/FilterApp.Core/Copier.cs`
- Test: `tests/FilterApp.Core.Tests/CopierTests.cs`

**Interfaces:**
- Consumes: `NameResolver.Resolve`
- Produces: `static Task<string> Copier.CopyAsync(string sourcePath, string destDir, string cardName)` (returns final path; throws `IOException`/`FileNotFoundException`/`DirectoryNotFoundException`/`UnauthorizedAccessException`); `static void Copier.Undo(string destPath)`

- [ ] **Step 1: Write the failing tests** — `tests/FilterApp.Core.Tests/CopierTests.cs`

```csharp
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter CopierTests`
Expected: build error — `Copier` does not exist.

- [ ] **Step 3: Implement** — `src/FilterApp.Core/Copier.cs`

```csharp
namespace FilterApp.Core;

/// Copies a file into the destination under the card's name. Never overwrites.
public static class Copier
{
    const int ErrorFileExists = unchecked((int)0x80070050);
    const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    public static Task<string> CopyAsync(string sourcePath, string destDir, string cardName) =>
        Task.Run(() => Copy(sourcePath, destDir, cardName));

    static string Copy(string sourcePath, string destDir, string cardName)
    {
        var fileName = Path.GetFileName(sourcePath);
        while (true)
        {
            var dest = NameResolver.Resolve(destDir, cardName, fileName, File.Exists);
            try
            {
                // CopyFileEx with FAIL_IF_EXISTS: kernel-speed and atomic name reservation.
                File.Copy(sourcePath, dest, overwrite: false);
                return dest;
            }
            catch (IOException e) when (e.HResult is ErrorFileExists or ErrorAlreadyExists)
            {
                // Another copy took this name between Resolve and Copy; try the next one.
            }
        }
    }

    public static void Undo(string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add Copier with no-overwrite copy and undo"
```

---

### Task 3: StateStore

**Files:**
- Create: `src/FilterApp.Core/StateStore.cs`
- Test: `tests/FilterApp.Core.Tests/StateStoreTests.cs`

**Interfaces:**
- Produces: `class CardData { string Name; string? DestPath; string? OriginalName }`; `class AppState { List<CardData> Cards; string? Destination; bool Locked }`; `static string StateStore.DefaultPath`; `static AppState StateStore.Load(string path)`; `static void StateStore.Save(string path, AppState state)`

- [ ] **Step 1: Write the failing tests** — `tests/FilterApp.Core.Tests/StateStoreTests.cs`

```csharp
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter StateStoreTests`
Expected: build error — `StateStore` does not exist.

- [ ] **Step 3: Implement** — `src/FilterApp.Core/StateStore.cs`

```csharp
using System.Text.Json;

namespace FilterApp.Core;

public sealed class CardData
{
    public string Name { get; set; } = "";
    public string? DestPath { get; set; }
    public string? OriginalName { get; set; }
}

public sealed class AppState
{
    public List<CardData> Cards { get; set; } = [];
    public string? Destination { get; set; }
    public bool Locked { get; set; }
}

public static class StateStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FilterApp", "state.json");

    public static AppState Load(string path)
    {
        if (!File.Exists(path)) return new AppState();
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Options) ?? new AppState();
            state.Cards ??= [];
            return state;
        }
        catch (JsonException)
        {
            File.Move(path, path + ".bad", overwrite: true);
            return new AppState();
        }
    }

    public static void Save(string path, AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add StateStore with atomic save and corrupt-file recovery"
```

---

### Task 4: View-models (Board, CardViewModel, PendingItem)

**Files:**
- Create: `src/FilterApp.Core/Observable.cs`, `src/FilterApp.Core/PendingItem.cs`, `src/FilterApp.Core/CardViewModel.cs`, `src/FilterApp.Core/Board.cs`
- Test: `tests/FilterApp.Core.Tests/BoardTests.cs`

**Interfaces:**
- Consumes: `Copier.CopyAsync`, `Copier.Undo`, `AppState`, `CardData`
- Produces:
  - `class PendingItem(string sourcePath, string displayName, bool isTemp)` with `SourcePath`, `DisplayName`, `IsTemp` (reference equality)
  - `enum CardStatus { Free, Copying, Filled }`
  - `class CardViewModel(string name)`: `Name`, `Status`, `DestPath`, `OriginalName`, `IsDragTarget` (all notify), `Fill(string destPath, string originalName)`, `Clear()`
  - `class Board`: `ObservableCollection<CardViewModel> Cards`, `ObservableCollection<PendingItem> Pending`, `string? Destination`, `bool Locked`, `event Action? Changed`, `event Action<string>? Notified`, `int AddNames(string text)`, `void AddPending(IEnumerable<PendingItem>)`, `void RemovePending(PendingItem)`, `void OnExplorerFolder(string path)`, `Task<bool> AssignAsync(PendingItem, CardViewModel)`, `void Undo(CardViewModel)`, `void RemoveCard(CardViewModel)`, `void ClearFilled()`, `AppState ToState()`, `static Board FromState(AppState)`

- [ ] **Step 1: Write the failing tests** — `tests/FilterApp.Core.Tests/BoardTests.cs`

```csharp
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter BoardTests`
Expected: build error — `Board` does not exist.

- [ ] **Step 3: Implement `Observable.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FilterApp.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
```

- [ ] **Step 4: Implement `PendingItem.cs`**

```csharp
namespace FilterApp.Core;

/// A file waiting in the tray. Reference equality on purpose: the UI tracks instances.
public sealed class PendingItem(string sourcePath, string displayName, bool isTemp)
{
    public string SourcePath { get; } = sourcePath;
    public string DisplayName { get; } = displayName;
    /// True when the app materialized the file in %TEMP% and must delete it after use.
    public bool IsTemp { get; } = isTemp;
}
```

- [ ] **Step 5: Implement `CardViewModel.cs`**

```csharp
namespace FilterApp.Core;

public enum CardStatus { Free, Copying, Filled }

public sealed class CardViewModel(string name) : Observable
{
    CardStatus _status;
    string? _destPath;
    string? _originalName;
    bool _isDragTarget;

    public string Name { get; } = name;
    public CardStatus Status { get => _status; set => Set(ref _status, value); }
    public string? DestPath { get => _destPath; private set => Set(ref _destPath, value); }
    public string? OriginalName { get => _originalName; private set => Set(ref _originalName, value); }
    /// UI-only: a file is being dragged over this card.
    public bool IsDragTarget { get => _isDragTarget; set => Set(ref _isDragTarget, value); }

    public void Fill(string destPath, string originalName)
    {
        DestPath = destPath;
        OriginalName = originalName;
        Status = CardStatus.Filled;
    }

    public void Clear()
    {
        DestPath = null;
        OriginalName = null;
        Status = CardStatus.Free;
    }
}
```

- [ ] **Step 6: Implement `Board.cs`**

```csharp
using System.Collections.ObjectModel;

namespace FilterApp.Core;

/// Main view-model: cards, pending tray, destination and the assign/undo rules.
public sealed class Board : Observable
{
    string? _destination;
    bool _locked;

    public ObservableCollection<CardViewModel> Cards { get; } = [];
    public ObservableCollection<PendingItem> Pending { get; } = [];

    /// Raised when something that must be persisted changed.
    public event Action? Changed;
    /// Raised with a short message for the user.
    public event Action<string>? Notified;

    public string? Destination
    {
        get => _destination;
        set { if (Set(ref _destination, value)) Changed?.Invoke(); }
    }

    public bool Locked
    {
        get => _locked;
        set { if (Set(ref _locked, value)) Changed?.Invoke(); }
    }

    public int AddNames(string text)
    {
        var names = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var name in names) Cards.Add(new CardViewModel(name));
        if (names.Count > 0) Changed?.Invoke();
        return names.Count;
    }

    public void AddPending(IEnumerable<PendingItem> items)
    {
        foreach (var item in items)
            if (!Pending.Any(p => string.Equals(p.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase)))
                Pending.Add(item);
    }

    public void RemovePending(PendingItem item)
    {
        if (Pending.Remove(item) && item.IsTemp) TryDelete(item.SourcePath);
    }

    public void OnExplorerFolder(string path)
    {
        if (!Locked) Destination = path;
    }

    public async Task<bool> AssignAsync(PendingItem item, CardViewModel card)
    {
        if (card.Status != CardStatus.Free)
            return Fail(item, $"«{card.Name}» ya tiene un archivo.");
        var dest = Destination;
        if (dest is null || !Directory.Exists(dest))
            return Fail(item, "Haz clic en una carpeta del Explorador para elegir el destino.");

        card.Status = CardStatus.Copying;
        Pending.Remove(item);
        try
        {
            var path = await Copier.CopyAsync(item.SourcePath, dest, card.Name);
            card.Fill(path, item.DisplayName);
            if (item.IsTemp) TryDelete(item.SourcePath);
            Changed?.Invoke();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            card.Status = CardStatus.Free;
            return Fail(item, $"No se pudo copiar «{item.DisplayName}»: {e.Message}");
        }
    }

    public void Undo(CardViewModel card)
    {
        if (card.Status != CardStatus.Filled) return;
        try
        {
            Copier.Undo(card.DestPath!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notified?.Invoke($"No se pudo borrar la copia: {e.Message}");
            return;
        }
        card.Clear();
        Changed?.Invoke();
    }

    public void RemoveCard(CardViewModel card)
    {
        if (card.Status == CardStatus.Free && Cards.Remove(card)) Changed?.Invoke();
    }

    public void ClearFilled()
    {
        foreach (var card in Cards.Where(c => c.Status == CardStatus.Filled).ToList()) Cards.Remove(card);
        Changed?.Invoke();
    }

    public AppState ToState() => new()
    {
        Destination = Destination,
        Locked = Locked,
        Cards = Cards.Select(c => c.Status == CardStatus.Filled
            ? new CardData { Name = c.Name, DestPath = c.DestPath, OriginalName = c.OriginalName }
            : new CardData { Name = c.Name }).ToList(),
    };

    public static Board FromState(AppState state)
    {
        var board = new Board { _destination = state.Destination, _locked = state.Locked };
        foreach (var data in state.Cards)
        {
            var card = new CardViewModel(data.Name);
            if (data.DestPath is not null) card.Fill(data.DestPath, data.OriginalName ?? "");
            board.Cards.Add(card);
        }
        return board;
    }

    /// Every failed assignment leaves the file in the tray so nothing is lost.
    bool Fail(PendingItem item, string message)
    {
        if (!Pending.Contains(item)) Pending.Insert(0, item);
        Notified?.Invoke(message);
        return false;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 7: Run to verify pass**

Run: `dotnet test`
Expected: all tests PASS.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat: add Board view-model with assign, undo and persistence mapping"
```

---

### Task 5: WPF shell — window, cards, names dialog, persistence

No automated tests (pure UI). Verified by building and launching.

**Files:**
- Create: `src/FilterApp/FilterApp.csproj`, `src/FilterApp/App.xaml`, `src/FilterApp/App.xaml.cs`, `src/FilterApp/MainWindow.xaml`, `src/FilterApp/MainWindow.xaml.cs`, `src/FilterApp/NamesDialog.xaml`, `src/FilterApp/NamesDialog.xaml.cs`
- The template's `App*`/`MainWindow*` files are overwritten; `AssemblyInfo.cs` is kept.

**Interfaces:**
- Consumes: everything in `FilterApp.Core`.
- Produces: `MainWindow(Board board)`; XAML handler names used in Task 6/7.

- [ ] **Step 1: Create project**

```bash
cd "F:/THE FORGE/FILTER APP"
dotnet new wpf -n FilterApp -o src/FilterApp -f net10.0
dotnet sln add src/FilterApp
dotnet add src/FilterApp reference src/FilterApp.Core
```

Replace `src/FilterApp/FilterApp.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <Product>Filter App</Product>
    <TieredPGO>true</TieredPGO>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\FilterApp.Core\FilterApp.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: `App.xaml`** (styles live here)

```xml
<Application x:Class="FilterApp.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Startup="OnStartup">
  <Application.Resources>
    <FontFamily x:Key="IconFont">Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>

    <Style x:Key="FlatButton" TargetType="Button">
      <Setter Property="Background" Value="White"/>
      <Setter Property="Foreground" Value="#24292F"/>
      <Setter Property="BorderBrush" Value="#D0D7DE"/>
      <Setter Property="BorderThickness" Value="1"/>
      <Setter Property="Padding" Value="12,6"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="B" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="B" Property="Opacity" Value="0.8"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource FlatButton}">
      <Setter Property="Background" Value="#0969DA"/>
      <Setter Property="Foreground" Value="White"/>
      <Setter Property="BorderBrush" Value="#0969DA"/>
    </Style>

    <Style x:Key="IconButton" TargetType="Button" BasedOn="{StaticResource FlatButton}">
      <Setter Property="FontFamily" Value="{StaticResource IconFont}"/>
      <Setter Property="FontSize" Value="12"/>
      <Setter Property="Padding" Value="6"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Foreground" Value="#57606A"/>
    </Style>

    <Style x:Key="LockToggle" TargetType="ToggleButton">
      <Setter Property="FontFamily" Value="{StaticResource IconFont}"/>
      <Setter Property="FontSize" Value="16"/>
      <Setter Property="Content" Value="&#xE785;"/>
      <Setter Property="Background" Value="#F6F8FA"/>
      <Setter Property="Foreground" Value="#57606A"/>
      <Setter Property="ToolTip" Value="Destino automático: sigue al Explorador. Clic para fijarlo."/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ToggleButton">
            <Border Width="38" Height="34" CornerRadius="6" Background="{TemplateBinding Background}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
      <Style.Triggers>
        <Trigger Property="IsChecked" Value="True">
          <Setter Property="Content" Value="&#xE72E;"/>
          <Setter Property="Background" Value="#FFF1E5"/>
          <Setter Property="Foreground" Value="#BC4C00"/>
          <Setter Property="ToolTip" Value="Destino fijo. Clic para volver a seguir al Explorador."/>
        </Trigger>
      </Style.Triggers>
    </Style>
  </Application.Resources>
</Application>
```

- [ ] **Step 3: `App.xaml.cs`**

```csharp
using System.Windows;
using FilterApp.Core;

namespace FilterApp;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        var board = Board.FromState(StateStore.Load(StateStore.DefaultPath));
        new MainWindow(board).Show();
    }
}
```

- [ ] **Step 4: `MainWindow.xaml`**

```xml
<Window x:Class="FilterApp.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:core="clr-namespace:FilterApp.Core;assembly=FilterApp.Core"
        Title="Filter App" Width="1040" Height="660" MinWidth="680" MinHeight="420"
        Background="#F4F5F7" FontFamily="Segoe UI" FontSize="14"
        AllowDrop="True" DragOver="OnWindowDragOver" Drop="OnWindowDrop"
        PreviewKeyDown="OnPreviewKeyDown">
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <!-- Destination bar -->
    <Border Background="White" BorderBrush="#E1E4E8" BorderThickness="0,0,0,1" Padding="16,10">
      <DockPanel>
        <ToggleButton DockPanel.Dock="Left" Style="{StaticResource LockToggle}" IsChecked="{Binding Locked}"
                      Margin="0,0,12,0" VerticalAlignment="Center"/>
        <TextBlock DockPanel.Dock="Right" VerticalAlignment="Center" Foreground="#57606A">
          <Run Text="Pendientes: "/><Run Text="{Binding Pending.Count, Mode=OneWay}" FontWeight="SemiBold"/>
        </TextBlock>
        <StackPanel VerticalAlignment="Center">
          <TextBlock Text="DESTINO" FontSize="11" FontWeight="SemiBold" Foreground="#57606A"/>
          <TextBlock Text="{Binding Destination, TargetNullValue='Haz clic en una carpeta del Explorador'}"
                     FontSize="16" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"
                     ToolTip="{Binding Destination}"/>
        </StackPanel>
      </DockPanel>
    </Border>

    <Grid Grid.Row="1" Margin="16">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="270"/>
        <ColumnDefinition Width="16"/>
        <ColumnDefinition Width="*"/>
      </Grid.ColumnDefinitions>

      <!-- Pending tray -->
      <Border Background="White" CornerRadius="8" BorderBrush="#D0D7DE" BorderThickness="1">
        <DockPanel>
          <TextBlock DockPanel.Dock="Top" Text="PENDIENTES" Margin="12,10,12,4"
                     FontSize="11" FontWeight="SemiBold" Foreground="#57606A"/>
          <Grid>
            <TextBlock Text="Arrastra archivos aquí&#x0a;o pulsa Ctrl+V" TextAlignment="Center"
                       VerticalAlignment="Center" Foreground="#8C959F">
              <TextBlock.Style>
                <Style TargetType="TextBlock">
                  <Setter Property="Visibility" Value="Collapsed"/>
                  <Style.Triggers>
                    <DataTrigger Binding="{Binding Pending.Count}" Value="0">
                      <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                  </Style.Triggers>
                </Style>
              </TextBlock.Style>
            </TextBlock>
            <ListBox ItemsSource="{Binding Pending}" BorderThickness="0" Background="Transparent"
                     HorizontalContentAlignment="Stretch"
                     PreviewMouseLeftButtonDown="OnPendingMouseDown" PreviewMouseMove="OnPendingMouseMove">
              <ListBox.ItemTemplate>
                <DataTemplate DataType="{x:Type core:PendingItem}">
                  <DockPanel Margin="2" Cursor="SizeAll" ToolTip="{Binding SourcePath}">
                    <Button DockPanel.Dock="Right" Style="{StaticResource IconButton}" Content="&#xE711;"
                            ToolTip="Quitar" Click="OnRemovePending"/>
                    <TextBlock Text="{Binding DisplayName}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                  </DockPanel>
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
          </Grid>
        </DockPanel>
      </Border>

      <!-- Cards -->
      <DockPanel Grid.Column="2">
        <DockPanel DockPanel.Dock="Top" Margin="0,0,0,10">
          <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
            <Button Style="{StaticResource FlatButton}" Content="Limpiar llenas" Margin="0,0,8,0" Click="OnClearFilled"/>
            <Button Style="{StaticResource AccentButton}" Content="+ Nombres" Click="OnAddNames"/>
          </StackPanel>
          <TextBlock Text="TARJETAS" VerticalAlignment="Center" FontSize="11" FontWeight="SemiBold" Foreground="#57606A"/>
        </DockPanel>
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <ItemsControl ItemsSource="{Binding Cards}">
            <ItemsControl.ItemsPanel>
              <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
              <DataTemplate DataType="{x:Type core:CardViewModel}">
                <Border x:Name="Card" Width="184" Height="96" Margin="0,0,10,10" Padding="12,10" CornerRadius="8"
                        Background="White" BorderBrush="#D0D7DE" BorderThickness="1"
                        AllowDrop="True" DragEnter="OnCardDragOver" DragOver="OnCardDragOver"
                        DragLeave="OnCardDragLeave" Drop="OnCardDrop">
                  <Grid>
                    <Grid.RowDefinitions>
                      <RowDefinition Height="*"/>
                      <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextWrapping="Wrap"
                               TextTrimming="CharacterEllipsis" ToolTip="{Binding Name}"/>
                    <DockPanel Grid.Row="1">
                      <Button x:Name="Action" DockPanel.Dock="Right" Style="{StaticResource IconButton}"
                              Content="&#xE74D;" ToolTip="Eliminar tarjeta" Click="OnCardAction"/>
                      <TextBlock x:Name="Sub" Text="libre" FontSize="12" Foreground="#8C959F"
                                 TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                    </DockPanel>
                  </Grid>
                </Border>
                <DataTemplate.Triggers>
                  <DataTrigger Binding="{Binding Status}" Value="Copying">
                    <Setter TargetName="Card" Property="Background" Value="#FFF8C5"/>
                    <Setter TargetName="Sub" Property="Text" Value="copiando…"/>
                    <Setter TargetName="Action" Property="Visibility" Value="Collapsed"/>
                  </DataTrigger>
                  <DataTrigger Binding="{Binding Status}" Value="Filled">
                    <Setter TargetName="Card" Property="Background" Value="#DAFBE1"/>
                    <Setter TargetName="Card" Property="BorderBrush" Value="#2DA44E"/>
                    <Setter TargetName="Sub" Property="Text" Value="{Binding OriginalName, StringFormat='✔ {0}'}"/>
                    <Setter TargetName="Sub" Property="Foreground" Value="#1A7F37"/>
                    <Setter TargetName="Sub" Property="ToolTip" Value="{Binding DestPath}"/>
                    <Setter TargetName="Action" Property="Content" Value="&#xE711;"/>
                    <Setter TargetName="Action" Property="ToolTip" Value="Deshacer: borra la copia y libera la tarjeta"/>
                  </DataTrigger>
                  <DataTrigger Binding="{Binding IsDragTarget}" Value="True">
                    <Setter TargetName="Card" Property="Background" Value="#DDF4FF"/>
                    <Setter TargetName="Card" Property="BorderBrush" Value="#0969DA"/>
                    <Setter TargetName="Card" Property="BorderThickness" Value="2"/>
                  </DataTrigger>
                </DataTemplate.Triggers>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </ScrollViewer>
      </DockPanel>
    </Grid>

    <!-- Toast -->
    <Border x:Name="Toast" Grid.Row="2" Background="#24292F" Padding="16,8" Visibility="Collapsed">
      <TextBlock x:Name="ToastText" Foreground="White" TextWrapping="Wrap"/>
    </Border>
  </Grid>
</Window>
```

- [ ] **Step 5: `MainWindow.xaml.cs`** (intake handlers are stubs until Task 6)

```csharp
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FilterApp.Core;

namespace FilterApp;

public partial class MainWindow : Window
{
    readonly Board _board;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    Point _dragStart;
    PendingItem? _dragItem;

    public MainWindow(Board board)
    {
        InitializeComponent();
        _board = board;
        DataContext = board;

        board.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };
        board.Notified += ShowToast;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        Closing += (_, _) => Save();
    }

    void Save()
    {
        try { StateStore.Save(StateStore.DefaultPath, _board.ToState()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"No se pudo guardar el estado: {e.Message}");
        }
    }

    void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---- Paste and external drops into the tray (filled in Task 6) ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e) { }
    void OnWindowDragOver(object sender, DragEventArgs e) { e.Effects = DragDropEffects.None; e.Handled = true; }
    void OnWindowDrop(object sender, DragEventArgs e) { e.Handled = true; }

    // ---- Dragging a pending item to a card ----

    void OnPendingMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as PendingItem;
    }

    void OnPendingMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(PendingItem), item), DragDropEffects.Copy);
    }

    void OnRemovePending(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PendingItem item) _board.RemovePending(item);
    }

    // ---- Cards ----

    static CardViewModel? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as CardViewModel;

    static bool CanDropOn(CardViewModel? card, DragEventArgs e) =>
        card is { Status: CardStatus.Free } && e.Data.GetDataPresent(typeof(PendingItem));

    void OnCardDragOver(object sender, DragEventArgs e)
    {
        var card = CardOf(sender);
        var ok = CanDropOn(card, e);
        if (card is not null) card.IsDragTarget = ok;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnCardDragLeave(object sender, DragEventArgs e)
    {
        if (CardOf(sender) is { } card) card.IsDragTarget = false;
    }

    async void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CardOf(sender) is not { } card) return;
        card.IsDragTarget = false;
        if (e.Data.GetData(typeof(PendingItem)) is PendingItem item)
            await _board.AssignAsync(item, card);
    }

    void OnCardAction(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        if (card.Status == CardStatus.Filled) _board.Undo(card);
        else if (card.Status == CardStatus.Free) _board.RemoveCard(card);
    }

    void OnClearFilled(object sender, RoutedEventArgs e) => _board.ClearFilled();

    void OnAddNames(object sender, RoutedEventArgs e)
    {
        var dialog = new NamesDialog { Owner = this };
        if (dialog.ShowDialog() == true) _board.AddNames(dialog.NamesText);
    }
}
```

- [ ] **Step 6: `NamesDialog.xaml` + `.xaml.cs`**

```xml
<Window x:Class="FilterApp.NamesDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Agregar tarjetas" Width="420" Height="440" WindowStartupLocation="CenterOwner"
        ShowInTaskbar="False" FontFamily="Segoe UI" FontSize="14" Background="#F4F5F7">
  <DockPanel Margin="16">
    <TextBlock DockPanel.Dock="Top" Text="Escribe o pega un nombre por línea:" Margin="0,0,0,8"/>
    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
      <Button Style="{StaticResource FlatButton}" Content="Cancelar" IsCancel="True" Margin="0,0,8,0"/>
      <Button Style="{StaticResource AccentButton}" Content="Agregar" Click="OnOk"/>
    </StackPanel>
    <TextBox x:Name="Box" AcceptsReturn="True" VerticalScrollBarVisibility="Auto" Padding="6"/>
  </DockPanel>
</Window>
```

```csharp
using System.Windows;

namespace FilterApp;

public partial class NamesDialog : Window
{
    public NamesDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Box.Focus();
    }

    public string NamesText => Box.Text;

    void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
```

- [ ] **Step 7: Build and launch**

Run: `dotnet build` → Expected: 0 errors. Then `dotnet test` → all PASS.
Launch `src/FilterApp/bin/Debug/net10.0-windows/FilterApp.exe`, add names "A", "B" via "+ Nombres", toggle the lock, close and reopen → cards and lock state are restored (check `%APPDATA%\FilterApp\state.json`).

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat: add WPF window, cards, names dialog and state persistence"
```

---

### Task 6: File intake — external drops, Ctrl+V, virtual files, images

No automated tests (depends on OLE data objects). Verified manually.

**Files:**
- Create: `src/FilterApp/Intake/FileIntake.cs`, `src/FilterApp/Intake/VirtualFileReader.cs`
- Modify: `src/FilterApp/App.xaml.cs` (clean temp at startup), `src/FilterApp/MainWindow.xaml.cs` (paste, window drop, external drop on card)

**Interfaces:**
- Produces: `record IntakeResult(List<PendingItem> Items, int RejectedFolders)`; `static bool FileIntake.CanAccept(IDataObject)`; `static IntakeResult FileIntake.Read(IDataObject)`; `static void FileIntake.CleanTemp()`; `static List<string> VirtualFileReader.Extract(IDataObject data, string tempDir)`

- [ ] **Step 1: `Intake/FileIntake.cs`**

```csharp
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FilterApp.Core;

namespace FilterApp.Intake;

public sealed record IntakeResult(List<PendingItem> Items, int RejectedFolders);

/// Converts whatever was dropped or pasted into pending files.
public static class FileIntake
{
    const string VirtualFormat = "FileGroupDescriptorW";
    static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "FilterApp");

    public static bool CanAccept(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ||
        data.GetDataPresent(VirtualFormat) ||
        data.GetDataPresent(DataFormats.Bitmap);

    public static IntakeResult Read(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var items = new List<PendingItem>();
            int folders = 0;
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) folders++;
                else if (File.Exists(path)) items.Add(new PendingItem(path, Path.GetFileName(path), isTemp: false));
            }
            return new IntakeResult(items, folders);
        }
        if (data.GetDataPresent(VirtualFormat))
        {
            var files = VirtualFileReader.Extract(data, NewTempDir());
            return new IntakeResult(files.Select(p => new PendingItem(p, Path.GetFileName(p), isTemp: true)).ToList(), 0);
        }
        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return new IntakeResult([SaveBitmap(bitmap)], 0);
        return new IntakeResult([], 0);
    }

    public static void CleanTemp()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static PendingItem SaveBitmap(BitmapSource bitmap)
    {
        var path = Path.Combine(NewTempDir(), $"Imagen {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return new PendingItem(path, Path.GetFileName(path), isTemp: true);
    }

    static string NewTempDir() =>
        Directory.CreateDirectory(Path.Combine(TempRoot, Guid.NewGuid().ToString("N"))).FullName;
}
```

- [ ] **Step 2: `Intake/VirtualFileReader.cs`**

```csharp
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace FilterApp.Intake;

/// Writes "virtual" files (FileGroupDescriptorW + FileContents, e.g. Outlook attachments,
/// browser images) to a temp folder so they can be handled like normal files.
static class VirtualFileReader
{
    // FILEDESCRIPTORW layout: flags@0, attributes@36, sizeHigh@64, sizeLow@68, name@72 (260 WCHAR), 592 bytes total.
    const int DescriptorSize = 592, AttributesOffset = 36, SizeHighOffset = 64, SizeLowOffset = 68, NameOffset = 72, NameChars = 260;
    const uint FdAttributes = 0x4, FdFileSize = 0x40, DirectoryAttribute = 0x10;
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static List<string> Extract(IDataObject data, string tempDir)
    {
        var result = new List<string>();
        if (data.GetData("FileGroupDescriptorW") is not MemoryStream descriptor || data is not ComDataObject com)
            return result;

        var bytes = descriptor.ToArray();
        int count = BitConverter.ToInt32(bytes, 0);
        short contentsFormat = (short)DataFormats.GetDataFormat("FileContents").Id;

        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * DescriptorSize;
            if (o + DescriptorSize > bytes.Length) break;

            uint flags = BitConverter.ToUInt32(bytes, o);
            uint attributes = BitConverter.ToUInt32(bytes, o + AttributesOffset);
            if ((flags & FdAttributes) != 0 && (attributes & DirectoryAttribute) != 0) continue;

            long? size = (flags & FdFileSize) != 0
                ? ((long)BitConverter.ToUInt32(bytes, o + SizeHighOffset) << 32) | BitConverter.ToUInt32(bytes, o + SizeLowOffset)
                : null;

            var rawName = Encoding.Unicode.GetString(bytes, o + NameOffset, NameChars * 2).Split('\0')[0];
            var name = new string(rawName.Split('\\', '/').Last().Select(c => Array.IndexOf(Invalid, c) >= 0 ? '_' : c).ToArray());
            if (name.Length == 0) continue;

            var path = Path.Combine(tempDir, name);
            if (File.Exists(path)) path = Path.Combine(tempDir, $"{i}_{name}");

            try
            {
                if (TryWriteContents(com, contentsFormat, i, path, size)) result.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException) { }
        }
        return result;
    }

    static bool TryWriteContents(ComDataObject com, short format, int index, string path, long? size)
    {
        var formatEtc = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        com.GetData(ref formatEtc, out STGMEDIUM medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try
                {
                    using var output = File.Create(path);
                    CopyStream(stream, output);
                }
                finally { Marshal.ReleaseComObject(stream); }
                return true;
            }
            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                using var output = File.Create(path);
                CopyHGlobal(medium.unionmember, output, size);
                return true;
            }
            return false;
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    static void CopyStream(IStream source, Stream output)
    {
        var buffer = new byte[1 << 16];
        IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                source.Read(buffer, buffer.Length, read);
                int n = Marshal.ReadInt32(read);
                if (n <= 0) break;
                output.Write(buffer, 0, n);
            }
        }
        finally { Marshal.FreeCoTaskMem(read); }
    }

    static void CopyHGlobal(IntPtr hGlobal, Stream output, long? size)
    {
        long length = (long)(ulong)GlobalSize(hGlobal);
        if (size is long declared && declared < length) length = declared;   // GlobalSize may round up
        IntPtr pointer = GlobalLock(hGlobal);
        try
        {
            var buffer = new byte[1 << 16];
            for (long offset = 0; offset < length; offset += buffer.Length)
            {
                int n = (int)Math.Min(buffer.Length, length - offset);
                Marshal.Copy(pointer + (nint)offset, buffer, 0, n);
                output.Write(buffer, 0, n);
            }
        }
        finally { GlobalUnlock(hGlobal); }
    }

    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalUnlock(IntPtr hMem);
}
```

- [ ] **Step 3: Clean temp at startup** — in `App.xaml.cs` `OnStartup`, first line:

```csharp
        FilterApp.Intake.FileIntake.CleanTemp();
```

- [ ] **Step 4: Wire intake in `MainWindow.xaml.cs`**

Add usings:

```csharp
using System.Runtime.InteropServices;
using FilterApp.Intake;
```

Replace the three stubs `OnPreviewKeyDown`, `OnWindowDragOver`, `OnWindowDrop` with:

```csharp
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        IDataObject? data = null;
        try { data = Clipboard.GetDataObject(); }
        catch (COMException) { }   // clipboard temporarily locked by another app
        if (data is null || !FileIntake.CanAccept(data)) ShowToast("El portapapeles no contiene archivos.");
        else AddToPending(data);
    }

    void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = FileIntake.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnWindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (FileIntake.CanAccept(e.Data)) AddToPending(e.Data);
    }

    List<PendingItem> ReadIntake(IDataObject data)
    {
        var result = FileIntake.Read(data);
        if (result.RejectedFolders > 0) ShowToast("Las carpetas no se admiten; solo archivos.");
        return result.Items;
    }

    void AddToPending(IDataObject data) => _board.AddPending(ReadIntake(data));
```

Replace `CanDropOn` and `OnCardDrop` with (external files may also land directly on a card):

```csharp
    static bool CanDropOn(CardViewModel? card, DragEventArgs e) =>
        card is { Status: CardStatus.Free } &&
        (e.Data.GetDataPresent(typeof(PendingItem)) || FileIntake.CanAccept(e.Data));

    async void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CardOf(sender) is not { } card) return;
        card.IsDragTarget = false;

        if (e.Data.GetData(typeof(PendingItem)) is PendingItem item)
        {
            await _board.AssignAsync(item, card);
            return;
        }
        if (!FileIntake.CanAccept(e.Data)) return;

        var items = ReadIntake(e.Data);
        if (items.Count == 1)
            await _board.AssignAsync(items[0], card);
        else if (items.Count > 1)
        {
            _board.AddPending(items);
            ShowToast("Varios archivos: quedaron en Pendientes para repartirlos.");
        }
    }
```

- [ ] **Step 5: Build and verify manually**

Run: `dotnet build` → 0 errors; `dotnet test` → all PASS.
Manual:
1. Select 3 files in Explorer, Ctrl+C, focus app, Ctrl+V → 3 items in Pendientes.
2. Drag one pending item onto a card → card turns green, file appears in destination with the card name.
3. Drag several files from Telegram onto the window → go to Pendientes. Drag a single file from Telegram directly onto a free card → copied immediately.
4. Take a screenshot (Win+Shift+S), Ctrl+V in app → `Imagen <fecha>.png` in Pendientes.
5. Drop a folder → toast "Las carpetas no se admiten".

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: accept dropped, pasted, virtual and image files"
```

---

### Task 7: Explorer folder detection

No automated tests (needs live Explorer windows). Verified manually.

**Files:**
- Create: `src/FilterApp/Interop/ExplorerPath.cs`, `src/FilterApp/Interop/FolderWatcher.cs`
- Modify: `src/FilterApp/MainWindow.xaml.cs` (create/dispose watcher)

**Interfaces:**
- Consumes: `Board.OnExplorerFolder(string)`
- Produces: `sealed class FolderWatcher : IDisposable` with `event Action<string>? FolderActivated`; `static bool ExplorerPath.IsExplorer(IntPtr)`; `static string? ExplorerPath.TryGet(IntPtr)`

- [ ] **Step 1: `Interop/ExplorerPath.cs`**

```csharp
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FilterApp.Interop;

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IComServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellBrowser
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);   // first IOleWindow method; the rest are not needed
}

/// Reads the folder shown by an Explorer window (the active tab on Windows 11).
static class ExplorerPath
{
    static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Type? ShellType = Type.GetTypeFromProgID("Shell.Application");
    static dynamic? _shell;

    public static bool IsExplorer(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "CabinetWClass";
    }

    public static string? TryGet(IntPtr hwnd)
    {
        if (ShellType is null) return null;
        try { _shell ??= Activator.CreateInstance(ShellType); }
        catch (COMException) { return null; }

        // The first ShellTabWindowClass child is the tab in front.
        IntPtr activeTab = FindWindowEx(hwnd, IntPtr.Zero, "ShellTabWindowClass", null);
        string? fallback = null;
        foreach (object window in _shell!.Windows())
        {
            try
            {
                dynamic w = window;
                if (Convert.ToInt64(w.HWND) != hwnd.ToInt64()) continue;
                string path = w.Document.Folder.Self.Path;
                if (!Directory.Exists(path)) continue;   // "This PC", Recycle Bin, etc.
                if (activeTab == IntPtr.Zero || TabOf(window) == activeTab) return path;
                fallback ??= path;
            }
            catch (Exception e) when (e is COMException or InvalidCastException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }
        return fallback;
    }

    static IntPtr TabOf(object window)
    {
        if (window is not IComServiceProvider provider) return IntPtr.Zero;
        Guid service = SidTopLevelBrowser, iid = typeof(IShellBrowser).GUID;
        if (provider.QueryService(ref service, ref iid, out object browser) != 0) return IntPtr.Zero;
        return ((IShellBrowser)browser).GetWindow(out IntPtr tab) == 0 ? tab : IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);
}
```

- [ ] **Step 2: `Interop/FolderWatcher.cs`**

```csharp
using System.Runtime.InteropServices;

namespace FilterApp.Interop;

/// Reports the folder of the Explorer window the user works with, driven by the system
/// foreground-change event (no polling). When focus leaves Explorer, the last Explorer
/// window is re-read so navigation done inside it is also picked up.
public sealed class FolderWatcher : IDisposable
{
    const uint EventSystemForeground = 0x0003, WinEventOutOfContext = 0x0000;

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    readonly WinEventProc _proc;   // must stay referenced: native code only holds a pointer
    readonly IntPtr _hook;
    IntPtr _lastExplorer;

    public event Action<string>? FolderActivated;

    public FolderWatcher()
    {
        _proc = OnForeground;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WinEventOutOfContext);
    }

    void OnForeground(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ExplorerPath.IsExplorer(hwnd)) _lastExplorer = hwnd;
        else if (_lastExplorer == IntPtr.Zero || !IsWindow(_lastExplorer)) return;

        var path = ExplorerPath.TryGet(_lastExplorer);
        if (path is not null) FolderActivated?.Invoke(path);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindow(IntPtr hwnd);
}
```

- [ ] **Step 3: Wire into `MainWindow.xaml.cs`**

Add `using FilterApp.Interop;`, a field `readonly FolderWatcher _watcher = new();` and in the constructor after `DataContext = board;`:

```csharp
        _watcher.FolderActivated += _board.OnExplorerFolder;
```

Change the `Closing` handler to:

```csharp
        Closing += (_, _) => { _watcher.Dispose(); Save(); };
```

- [ ] **Step 4: Build and verify manually**

Run: `dotnet build` → 0 errors; `dotnet test` → all PASS.
Manual:
1. Open two Explorer windows on different folders; click each → destination bar follows.
2. In Win11, open two tabs in one Explorer window; switch tab, click the app → destination is the visible tab.
3. Navigate inside Explorer to a subfolder, then click Telegram → destination updates to the subfolder.
4. Click "Este equipo" / Papelera → destination unchanged.
5. Lock, click another Explorer folder → destination unchanged; unlock → follows again.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: follow the active Explorer folder as destination"
```

---

### Task 8: Release build + end-to-end check

**Files:**
- Create: `publish.cmd`

- [ ] **Step 1: `publish.cmd`**

```bat
@echo off
dotnet publish src\FilterApp\FilterApp.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
```

- [ ] **Step 2: Publish and run**

Run: `cmd /c publish.cmd` → Expected: `publish\FilterApp.exe` exists.
Launch `publish\FilterApp.exe`; cold start should show the window in < 1 s.

- [ ] **Step 3: End-to-end manual checklist**

1. Add 3 names, click a destination folder in Explorer, drag 3 files from Telegram → Pendientes; assign each → 3 renamed copies in the folder; originals intact.
2. Drop onto a green card → rejected ("no" cursor).
3. ✕ on a green card → copy deleted, card free.
4. Assign two cards with the same name → `Nombre.ext` and `Nombre (2).ext`.
5. Copy a >1 GB file → card shows "copiando…" and the window stays responsive.
6. Close and reopen → cards, states, destination and lock restored.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "build: add release publish script"
```
