using FileManager.Contracts.Messages;

namespace FileManager.Gui.Services;

/// <summary>
/// The seam that raises the spec §3.2 always-prompt profile chooser for a pending manual shell
/// invocation and returns the user's choice. Keeping it an interface lets
/// <see cref="ViewModels.MainWindowViewModel"/> drive the chooser flow without referencing Avalonia, so
/// the always-prompt handshake is unit-testable with no display server; production renders the actual
/// <c>ProfileChooserDialog</c>.
/// </summary>
public interface IManualInvocationPresenter
{
    /// <summary>
    /// Shows the chooser for <paramref name="pending"/> and resolves to the chosen Profile id, or null
    /// when the user cancelled (or no choice could be made). The caller relays the answer to the service.
    /// </summary>
    public Task<string?> ChooseAsync(ManualInvocationPending pending);
}
