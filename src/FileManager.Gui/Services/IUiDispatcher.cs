namespace FileManager.Gui.Services;

/// <summary>
/// Marshals an action onto the UI thread. The single seam that lets view-models mutate observable
/// collections from background callbacks (IPC event delivery, async command continuations) while staying
/// testable: production posts to the Avalonia dispatcher; tests inject a synchronous fake so no UI thread
/// is required. View-models depend on this interface, never on Avalonia's <c>Dispatcher</c> directly.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread (synchronously if already on it).</summary>
    public void Post(Action action);
}
