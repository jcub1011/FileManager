using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.Messages;

namespace FileManager.Gui.ViewModels;

/// <summary>One selectable Profile row in the manual-invocation chooser.</summary>
/// <param name="ProfileId">The Profile's stable id (sent back on choose).</param>
/// <param name="Name">The Profile's display name.</param>
public sealed record ProfileChoice(string ProfileId, string Name);

/// <summary>
/// The view-model behind the spec §3.2 always-prompt profile chooser, raised when a
/// <see cref="ManualInvocationPending"/> arrives for a manual right-click invocation. It lists every
/// Profile whose Source owns the invoked path and requires an EXPLICIT pick — there is no default
/// auto-select, so nothing ever runs without a choice. It ALWAYS offers a "Create Profile…" action (even
/// when <see cref="Matches"/> is empty — a path outside every configured Source), which seeds the M7
/// Profile editor with the invoked path so the prompt is never a dead end. Choosing yields the chosen
/// Profile id; cancelling yields null. A plain POCO with an injected
/// <see cref="ProfileEditorViewModel"/> factory, fully testable without a display server.
/// </summary>
public sealed partial class ProfileChooserViewModel : ObservableObject
{
    private readonly Func<string, ProfileEditorViewModel> _editorFactory;
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    [ObservableProperty]
    private ProfileChoice? _selected;

    [ObservableProperty]
    private ProfileEditorViewModel? _createdEditor;

    /// <summary>The id of the pending manual invocation this chooser answers.</summary>
    public string InvocationId { get; }

    /// <summary>The absolute path the user invoked on (file or directory).</summary>
    public string InvokedPath { get; }

    /// <summary>Whether a directory invocation should descend recursively.</summary>
    public bool Recursive { get; }

    /// <summary>The matching Profiles (may be empty — "Create Profile…" is still available).</summary>
    public ObservableCollection<ProfileChoice> Matches { get; } = new();

    /// <summary>True when no Profile matched the path — the editor is the only path forward.</summary>
    public bool HasNoMatches => Matches.Count == 0;

    /// <summary>
    /// Completes with the chosen Profile id once <see cref="Choose"/> runs, or null once
    /// <see cref="Cancel"/> runs. The host awaits this to send the <see cref="ResolveManualInvocation"/>.
    /// </summary>
    public Task<string?> Completion => _completion.Task;

    /// <summary>
    /// Creates a chooser for <paramref name="pending"/>. <paramref name="editorFactory"/> builds a
    /// Profile editor seeded with the invoked path for the "Create Profile…" action (production wires
    /// <see cref="ProfileEditorViewModel.SeededWithPath"/>; tests inject a stub).
    /// </summary>
    public ProfileChooserViewModel(
        ManualInvocationPending pending,
        Func<string, ProfileEditorViewModel> editorFactory)
    {
        InvocationId = pending.InvocationId;
        InvokedPath = pending.Path;
        Recursive = pending.Recursive;
        _editorFactory = editorFactory;
        foreach (ProfileSummary match in pending.Matches)
            Matches.Add(new ProfileChoice(match.ProfileId, match.Name));
    }

    /// <summary>True when a Profile is selected so <see cref="Choose"/> may run (no implicit default).</summary>
    public bool CanChoose => Selected is not null;

    /// <summary>Confirms the explicitly selected Profile, completing <see cref="Completion"/> with its id.</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose()
    {
        if (Selected is { } choice)
            _completion.TrySetResult(choice.ProfileId);
    }

    /// <summary>Cancels the prompt, completing <see cref="Completion"/> with null (no Profile runs).</summary>
    [RelayCommand]
    private void Cancel() => CancelChoice();

    /// <summary>
    /// Cancels the prompt (completes <see cref="Completion"/> with null). Idempotent — safe to call from
    /// both the Cancel command and a window-close handler, so dismissing the dialog via the title-bar X /
    /// Alt+F4 still sends <see cref="ResolveManualInvocation"/>(id, null) and the service discards the
    /// pending promptly rather than leaking it until TTL.
    /// </summary>
    public void CancelChoice() => _completion.TrySetResult(null);

    /// <summary>
    /// Opens the Profile editor seeded with the invoked path (always available, §3.2). Surfaces the
    /// editor on <see cref="CreatedEditor"/> so the view can host it; the prompt remains open until the
    /// user chooses a Profile or cancels, so the just-created Profile can then be picked.
    /// </summary>
    [RelayCommand]
    private void CreateProfile() => CreatedEditor = _editorFactory(InvokedPath);
}
