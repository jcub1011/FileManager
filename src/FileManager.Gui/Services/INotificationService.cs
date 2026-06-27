namespace FileManager.Gui.Services;

/// <summary>
/// Raises a user-facing notification (spec §7 failure notification). The LOGIC of <em>when</em> to
/// notify lives in the view-models and is what tests assert against a capturing fake; the actual native
/// rendering is best-effort, mirroring M6's tray approach — an unavailable notifier degrades to a safe
/// no-op/log rather than failing.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows a notification with the given <paramref name="title"/> and <paramref name="message"/>.</summary>
    public void Notify(string title, string message);
}
