namespace FilterApp.Core;

/// A file waiting in the tray. Reference equality on purpose: the UI tracks instances.
public sealed class PendingItem(string sourcePath, string displayName, bool isTemp)
{
    public string SourcePath { get; } = sourcePath;
    public string DisplayName { get; } = displayName;
    /// True when the app materialized the file in %TEMP% and must delete it after use.
    public bool IsTemp { get; } = isTemp;
}
