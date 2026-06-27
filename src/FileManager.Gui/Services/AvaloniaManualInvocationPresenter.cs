using Avalonia.Controls;
using FileManager.Contracts.Messages;
using FileManager.Gui.Ipc;
using FileManager.Gui.ViewModels;
using FileManager.Gui.Views;

namespace FileManager.Gui.Services;

/// <summary>
/// The production <see cref="IManualInvocationPresenter"/>: shows the <see cref="ProfileChooserDialog"/>
/// as a modal owned by the main window, awaits the user's explicit choice (or cancel) via the
/// <see cref="ProfileChooserViewModel"/>, then closes the dialog and returns the chosen Profile id. The
/// "Create Profile…" action seeds a real <see cref="ProfileEditorViewModel"/> with the invoked path. This
/// type references Avalonia, so it lives behind the <see cref="IManualInvocationPresenter"/> seam and is
/// never touched by the headless view-model tests.
/// </summary>
public sealed class AvaloniaManualInvocationPresenter : IManualInvocationPresenter
{
    private readonly Func<Window> _ownerAccessor;
    private readonly IServiceClient _client;

    /// <summary>
    /// Creates a presenter that parents the dialog on the window returned by <paramref name="ownerAccessor"/>
    /// and seeds the "Create Profile…" editor with <paramref name="client"/> for its reload-on-save.
    /// </summary>
    public AvaloniaManualInvocationPresenter(Func<Window> ownerAccessor, IServiceClient client)
    {
        _ownerAccessor = ownerAccessor;
        _client = client;
    }

    /// <inheritdoc/>
    public async Task<string?> ChooseAsync(ManualInvocationPending pending)
    {
        var viewModel = new ProfileChooserViewModel(
            pending,
            seedPath => ProfileEditorViewModel.SeededWithPath(seedPath, _client));
        var dialog = new ProfileChooserDialog { DataContext = viewModel };

        Task<string?> choice = viewModel.Completion;
        Window owner = _ownerAccessor();
        Task show = dialog.ShowDialog(owner);

        string? chosen = await choice.ConfigureAwait(true);
        dialog.Close();
        await show.ConfigureAwait(true);
        return chosen;
    }
}
