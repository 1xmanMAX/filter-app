namespace FilterApp.Core;

/// A file waiting in the tray. Reference equality on purpose: the UI tracks instances.
public sealed class PendingItem(string sourcePath, string displayName, bool isTemp, string group = "")
{
    public string SourcePath { get; } = sourcePath;
    public string DisplayName { get; } = displayName;
    /// True when the app materialized the file in %TEMP% and must delete it after use.
    public bool IsTemp { get; } = isTemp;
    /// For files that came inside a dropped folder: their folder relative to it, e.g. "Fotos\Viaje".
    public string Group { get; } = group;
}
