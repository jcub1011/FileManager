namespace FileManager.Gui.Services;

/// <summary>
/// Presents a folder-picker to the user and returns the chosen absolute path (or null if cancelled). The
/// seam that lets the Profile editor offer a "Browse…" action on each Source/Target/archive row while
/// staying unit-testable: production uses the Avalonia storage-provider dialog
/// (<see cref="AvaloniaFolderBrowser"/>); tests inject a fake that returns a canned path with no display
/// server. The starting directory is a hint the implementation may honor.
/// </summary>
public interface IFolderBrowser
{
    /// <summary>Prompts for a folder, starting at <paramref name="startingDirectory"/> when possible.</summary>
    public Task<string?> PickFolderAsync(string? startingDirectory);
}
