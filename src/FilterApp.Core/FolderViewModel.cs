using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FilterApp.Core;

/// A folder of the destination tree. The board's root is a nameless folder: the destination itself.
public sealed class FolderViewModel : Observable
{
    string _name;
    bool _isDragTarget;

    public FolderViewModel(string name)
    {
        _name = name;
        Folders.CollectionChanged += OnFoldersChanged;
        Cards.CollectionChanged += OnCardsChanged;
        Files.CollectionChanged += OnCardsChanged;
    }

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Notify(nameof(DisplayPath)); }
    }

    public FolderViewModel? Parent { get; private set; }
    public bool IsRoot => Parent is null;

    public ObservableCollection<FolderViewModel> Folders { get; } = [];
    /// Named slots: the file dropped here takes the card's name.
    public ObservableCollection<CardViewModel> Cards { get; } = [];
    /// Files dropped on the folder itself: they keep their own name.
    public ObservableCollection<CardViewModel> Files { get; } = [];

    /// UI-only: a file is being dragged over this folder.
    public bool IsDragTarget { get => _isDragTarget; set => Set(ref _isDragTarget, value); }

    /// From the root down to this folder.
    public IEnumerable<FolderViewModel> Chain()
    {
        var chain = new List<FolderViewModel>();
        for (var f = this; f is not null; f = f.Parent) chain.Add(f);
        chain.Reverse();
        return chain;
    }

    /// Path below the destination, with every segment made safe for the file system.
    public string RelativePath => Path.Combine(Chain().Skip(1).Select(f => NameResolver.Sanitize(f.Name)).ToArray());
    public string DisplayPath => string.Join(" / ", Chain().Skip(1).Select(f => f.Name));
    public string DirIn(string destination) => Path.Combine(destination, RelativePath);

    public IEnumerable<CardViewModel> AllCards() =>
        Cards.Concat(Files).Concat(Folders.SelectMany(f => f.AllCards()));

    public IEnumerable<FolderViewModel> AllFolders() => Folders.SelectMany(f => f.AllFolders().Prepend(f));

    public FolderViewModel? FindFolder(string name) =>
        Folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public FolderViewModel GetOrAddFolder(string name)
    {
        name = name.Trim();
        if (FindFolder(name) is { } existing) return existing;
        var folder = new FolderViewModel(name);
        Folders.Add(folder);
        return folder;
    }

    /// Walks (and creates) a path like "Clientes/Juan" or "Clientes\Juan" below this folder.
    public FolderViewModel GetOrAddPath(string relative)
    {
        var folder = this;
        foreach (var part in relative.Split('/', '\\').Select(p => p.Trim()).Where(p => p.Length > 0))
            folder = folder.GetOrAddFolder(part);
        return folder;
    }

    // ---- Progress shown on the folder card (recursive) ----

    public int NameCount => Cards.Count + Folders.Sum(f => f.NameCount);
    public int FilledNames => Cards.Count(c => c.Status == CardStatus.Filled) + Folders.Sum(f => f.FilledNames);
    public int FileCount => Files.Count(c => c.Status == CardStatus.Filled) + Folders.Sum(f => f.FileCount);
    public int HeldCount => AllCards().Count(c => c.Status == CardStatus.Held);
    /// Copying or held files inside: the folder cannot be removed now.
    public bool IsBusy => AllCards().Any(c => c.Status is CardStatus.Copying or CardStatus.Held);
    public bool IsEmpty => Folders.Count == 0 && Cards.Count == 0 && Files.Count == 0;

    /// One line for the folder card: "2 carpetas · 3/5 nombres · 12 archivos".
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Folders.Count > 0) parts.Add(Folders.Count == 1 ? "1 carpeta" : $"{Folders.Count} carpetas");
            if (NameCount > 0) parts.Add($"{FilledNames}/{NameCount} nombres");
            if (FileCount > 0) parts.Add(FileCount == 1 ? "1 archivo" : $"{FileCount} archivos");
            if (HeldCount > 0) parts.Add($"⏸ {HeldCount}");
            return parts.Count == 0 ? "vacía" : string.Join(" · ", parts);
        }
    }

    /// Re-announces the progress of this folder and every folder above it.
    public void Refresh()
    {
        for (var f = this; f is not null; f = f.Parent)
        {
            f.Notify(nameof(NameCount));
            f.Notify(nameof(FilledNames));
            f.Notify(nameof(FileCount));
            f.Notify(nameof(HeldCount));
            f.Notify(nameof(IsEmpty));
            f.Notify(nameof(Summary));
        }
    }

    void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (FolderViewModel f in e.NewItems ?? Array.Empty<FolderViewModel>()) f.Parent = this;
        Refresh();
    }

    void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (CardViewModel c in e.OldItems ?? Array.Empty<CardViewModel>()) c.PropertyChanged -= OnCardChanged;
        foreach (CardViewModel c in e.NewItems ?? Array.Empty<CardViewModel>())
        {
            c.Folder = this;
            c.PropertyChanged += OnCardChanged;
        }
        Refresh();
    }

    void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardViewModel.Status)) Refresh();
    }
}
