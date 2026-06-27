using Avalonia.Controls;
using FileManager.Gui.ViewModels;

namespace FileManager.Gui.Views;

/// <summary>
/// The spec §3.2 always-prompt profile chooser dialog: lists the matching Profiles, requires an explicit
/// pick, and always offers "Create Profile…". All logic lives in
/// <see cref="ProfileChooserViewModel"/>; this is the thin Avalonia shell that closes itself when the
/// view-model's choice/cancel completes.
/// </summary>
/// <remarks>
/// A window close via the title-bar X / Alt+F4 is treated as Cancel (<see cref="OnClosed"/>): the
/// view-model's idempotent <see cref="ProfileChooserViewModel.CancelChoice"/> completes the choice with
/// null so the presenter still sends <c>ResolveManualInvocation(id, null)</c> and the service discards the
/// pending promptly — the never-silently-dropped invariant. Choosing/cancelling via the buttons already
/// completes the choice first, so this close handler is a harmless no-op in those paths.
/// </remarks>
public sealed partial class ProfileChooserDialog : Window
{
    /// <summary>Initializes the dialog from its XAML.</summary>
    public ProfileChooserDialog() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnClosed(System.EventArgs e)
    {
        if (DataContext is ProfileChooserViewModel viewModel)
            viewModel.CancelChoice();
        base.OnClosed(e);
    }
}
