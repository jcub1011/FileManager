using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace FileManager.Gui.Services;

/// <summary>
/// The production <see cref="IFolderBrowser"/>: opens the OS folder-picker via the attached
/// <see cref="TopLevel"/>'s <see cref="IStorageProvider"/>. The <see cref="PathPickerService"/> (over
/// <c>IFileSystemService</c>) supplies the suggested start location. Returns null when no TopLevel is
/// attached (headless) or the user cancels; never throws.
/// </summary>
public sealed class AvaloniaFolderBrowser : IFolderBrowser
{
    private readonly System.Threading.Lock _gate = new();
    private TopLevel? _topLevel;

    /// <summary>Attaches the app's <see cref="TopLevel"/> (the main window) used to host the dialog.</summary>
    public void AttachTopLevel(TopLevel? topLevel)
    {
        lock (_gate)
            _topLevel = topLevel;
    }

    /// <inheritdoc/>
    public async Task<string?> PickFolderAsync(string? startingDirectory)
    {
        TopLevel? topLevel;
        lock (_gate)
            topLevel = _topLevel;

        if (topLevel is null)
            return null;

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startingDirectory))
        {
            try
            {
                start = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startingDirectory)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                start = null;
            }
        }

        IReadOnlyList<IStorageFolder> picked = await topLevel.StorageProvider
            .OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, SuggestedStartLocation = start })
            .ConfigureAwait(true);

        return picked.Count > 0 ? picked[0].Path.LocalPath : null;
    }
}
