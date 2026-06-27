using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.Messages;
using FileManager.Core.Configuration;
using FileManager.Core.Profiles;
using FileManager.Core.Safety;
using FileManager.Gui.Ipc;
using FileManager.Gui.Services;

namespace FileManager.Gui.ViewModels;

/// <summary>
/// A single editable path row (a Source or a Target) with a "Browse…" command that opens a folder picker
/// (the injected <see cref="IFolderBrowser"/>) seeded from the row's current value. Plain POCO; the
/// browser seam keeps it testable without a display server.
/// </summary>
public sealed partial class PathRowViewModel : ObservableObject
{
    private readonly IFolderBrowser? _browser;

    [ObservableProperty]
    private string _path = string.Empty;

    /// <summary>Creates a row for <paramref name="path"/>, optionally wired to a folder browser.</summary>
    public PathRowViewModel(string path = "", IFolderBrowser? browser = null)
    {
        _path = path;
        _browser = browser;
    }

    /// <summary>Opens the folder picker (when one is wired) and sets <see cref="Path"/> to the choice.</summary>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_browser is null)
            return;
        string? picked = await _browser.PickFolderAsync(string.IsNullOrWhiteSpace(Path) ? null : Path);
        if (!string.IsNullOrEmpty(picked))
            Path = picked;
    }
}

/// <summary>
/// Edits a single Profile against the full §5.1 schema, with inline validation
/// (<see cref="ProfileValidator"/>), the §6.1 safety warnings (<see cref="SafetyAnalyzer"/>: a BLOCKING
/// state for <c>None</c>+<c>PermanentDelete</c> that gates Save until acknowledged, a non-blocking banner
/// for <c>None</c>+<c>MoveToTrash</c>, and a visible destructive flag for <see cref="SyncMode.Mirror"/>),
/// and a Save that serializes via <see cref="ProfileSerializer"/> to
/// <c>profiles/&lt;ProfileId&gt;.json</c> then triggers a service reload over IPC.
/// </summary>
/// <remarks>
/// A plain POCO view-model (CommunityToolkit source-generated): it holds no Avalonia reference and is
/// fully unit-testable without a UI thread. The editor can be constructed pre-seeded with a path (M8's
/// "Create Profile…" handoff) — the seed becomes the first Source AND first Target.
/// </remarks>
public sealed partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly IServiceClient? _client;
    private readonly string _profilesDirectory;
    private readonly PathPickerService? _pathPicker;
    private readonly IFolderBrowser? _browser;

    // --- Identity / top-level (§5.1) ---
    [ObservableProperty] private string _profileId = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "New Profile";
    [ObservableProperty] private bool _active = true;

    [NotifyPropertyChangedFor(nameof(IsMirror))]
    [NotifyPropertyChangedFor(nameof(IsDestructiveMirror))]
    [ObservableProperty] private SyncMode _syncMode = SyncMode.AdditiveArchive;

    [ObservableProperty] private TargetLayout _targetLayout = TargetLayout.PreserveStructure;

    // --- Triggers (§5.1) ---
    [ObservableProperty] private bool _manualShell = true;
    [ObservableProperty] private bool _watcher;

    // --- Policies (§5.1) ---
    [ObservableProperty] private ConflictResolution _conflictResolution = ConflictResolution.Overwrite;
    [ObservableProperty] private OverwriteHandling _overwriteHandling = OverwriteHandling.DirectOverwrite;

    [NotifyPropertyChangedFor(nameof(Safety))]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [ObservableProperty] private VerificationMethod _verificationMethod = VerificationMethod.SizeTimestamp;

    [NotifyPropertyChangedFor(nameof(Safety))]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [ObservableProperty] private OnSuccess _onSuccess = OnSuccess.KeepSource;

    [ObservableProperty] private string? _archiveFolder;
    [ObservableProperty] private MetadataOnConflict _metadataOnConflict = MetadataOnConflict.WarnAndContinue;

    // --- Logging (§5.1) ---
    [ObservableProperty] private Verbosity _verbosity = Verbosity.FailuresAndSkips;
    [ObservableProperty] private bool _notifyOnFailure = true;

    // The user's explicit acknowledgement of a BLOCKING (§6.1) risk. Save stays disabled until this is
    // set whenever the configuration is blocking.
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [ObservableProperty] private bool _blockingRiskAcknowledged;

    [ObservableProperty] private string? _saveStatus;

    /// <summary>The editable Source path rows (at least one required by the validator).</summary>
    public ObservableCollection<PathRowViewModel> Sources { get; } = new();

    /// <summary>The editable Target path rows (at least one required by the validator).</summary>
    public ObservableCollection<TransformerStepViewModel> Transformers { get; } = new();

    /// <summary>The editable Target path rows (at least one required by the validator).</summary>
    public ObservableCollection<PathRowViewModel> Targets { get; } = new();

    /// <summary>The inline validation errors for the current editor state (empty when valid).</summary>
    public ObservableCollection<string> ValidationErrors { get; } = new();

    /// <summary>The path picker (over <c>IFileSystemService</c>) the editor exposes for browsing.</summary>
    public PathPickerService? PathPicker => _pathPicker;

    /// <summary>
    /// Creates an editor. <paramref name="client"/> may be null for an offline/preview editor (Save then
    /// only writes the file and reports the reload could not be triggered).
    /// <paramref name="profilesDirectory"/> defaults to the per-OS config profiles directory.
    /// <paramref name="pathPicker"/> (over <c>IFileSystemService</c>) and <paramref name="browser"/> wire
    /// the per-row "Browse…" action; both may be null for a headless/offline editor.
    /// </summary>
    public ProfileEditorViewModel(
        IServiceClient? client = null,
        string? profilesDirectory = null,
        PathPickerService? pathPicker = null,
        IFolderBrowser? browser = null)
    {
        _client = client;
        _profilesDirectory = profilesDirectory ?? ConfigPaths.GetProfilesDirectory();
        _pathPicker = pathPicker;
        _browser = browser;
        Sources.Add(NewRow());
        Targets.Add(NewRow());
    }

    /// <summary>
    /// Creates an editor pre-seeded with <paramref name="seedPath"/> as the first Source AND first Target
    /// (M8's "Create Profile…" handoff from an invoked path).
    /// </summary>
    public static ProfileEditorViewModel SeededWithPath(
        string seedPath,
        IServiceClient? client = null,
        string? profilesDirectory = null,
        PathPickerService? pathPicker = null,
        IFolderBrowser? browser = null)
    {
        var vm = new ProfileEditorViewModel(client, profilesDirectory, pathPicker, browser);
        vm.Sources.Clear();
        vm.Targets.Clear();
        vm.Sources.Add(vm.NewRow(seedPath));
        vm.Targets.Add(vm.NewRow(seedPath));
        return vm;
    }

    // Builds a path row wired to this editor's folder browser so its Browse command works.
    private PathRowViewModel NewRow(string path = "") => new(path, _browser);

    /// <summary>The §6.1 safety assessment for the current policy selections.</summary>
    public SafetyAssessment Safety => SafetyAnalyzer.Evaluate(BuildProfile());

    /// <summary>True when the configuration is the unrecoverable §6.1 combination (None+PermanentDelete).</summary>
    public bool IsBlocking => Safety.Level == SafetyLevel.Blocking;

    /// <summary>True for the recoverable §6.1 warning (None+MoveToTrash) — a non-blocking banner.</summary>
    public bool IsWarning => Safety.Level == SafetyLevel.Warning;

    /// <summary>True when Save is currently blocked: a blocking risk that has not been acknowledged.</summary>
    public bool IsBlocked => IsBlocking && !BlockingRiskAcknowledged;

    /// <summary>True when the Profile uses the destructive <see cref="SyncMode.Mirror"/> mode (§3.1.1).</summary>
    public bool IsMirror => SyncMode == SyncMode.Mirror;

    /// <summary>Alias surfacing Mirror as a visible destructive flag in the editor.</summary>
    public bool IsDestructiveMirror => IsMirror;

    /// <summary>The safety message to surface, or null when safe.</summary>
    public string? SafetyMessage => Safety.Reason;

    /// <summary>Whether Save may run: not blocked by an unacknowledged §6.1 risk.</summary>
    public bool CanSave => !IsBlocked;

    /// <summary>Re-runs <see cref="ProfileValidator"/> over the current editor state into <see cref="ValidationErrors"/>.</summary>
    public ValidationResult Validate()
    {
        ValidationResult result = ProfileValidator.Validate(BuildProfile());
        ValidationErrors.Clear();
        foreach (ValidationError error in result.Errors)
            ValidationErrors.Add(error.ToString());
        return result;
    }

    /// <summary>Adds an empty Source row.</summary>
    [RelayCommand]
    private void AddSource() => Sources.Add(NewRow());

    /// <summary>Adds an empty Target row.</summary>
    [RelayCommand]
    private void AddTarget() => Targets.Add(NewRow());

    /// <summary>Adds a new transformer step (numbered after the current count).</summary>
    [RelayCommand]
    private void AddTransformer() =>
        Transformers.Add(new TransformerStepViewModel { Step = Transformers.Count + 1 });

    /// <summary>
    /// Validates, then (when valid and not blocked) serializes the Profile to
    /// <c>profiles/&lt;ProfileId&gt;.json</c> and triggers a service reload over IPC. Disabled while a
    /// §6.1 blocking risk is unacknowledged (<see cref="CanSave"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ValidationResult validation = Validate();
        if (!validation.IsValid)
        {
            SaveStatus = $"Cannot save: {validation.Errors.Count} validation error(s).";
            return;
        }

        if (IsBlocked)
        {
            SaveStatus = "Cannot save: acknowledge the data-loss risk first.";
            return;
        }

        Profile profile = BuildProfile();
        Directory.CreateDirectory(_profilesDirectory);
        string path = Path.Combine(_profilesDirectory, ProfileId + ".json");
        await File.WriteAllTextAsync(path, ProfileSerializer.Serialize(profile)).ConfigureAwait(false);

        if (_client is null)
        {
            SaveStatus = "Saved. (No service connection — reload not triggered.)";
            return;
        }

        ReloadResult? reload = await _client.ReloadProfilesAsync().ConfigureAwait(false);
        SaveStatus = reload is null
            ? "Saved. (Service unreachable — reload not triggered.)"
            : $"Saved. Service reloaded {reload.LoadedCount} Profile(s).";
    }

    /// <summary>Builds an immutable <see cref="Profile"/> from the current editor state (for validation/safety/save).</summary>
    public Profile BuildProfile() => new()
    {
        SchemaVersion = ProfileValidator.SupportedSchemaVersion,
        ProfileId = ProfileId,
        Name = Name,
        Active = Active,
        SyncMode = SyncMode,
        TargetLayout = TargetLayout,
        Triggers = new TriggerSet { ManualShell = ManualShell, Watcher = Watcher, Schedule = null },
        Sources = Sources
            .Select(s => new SourceSpec { Path = s.Path, SettleDelaySeconds = 0, StabilityIntervalMs = 0 })
            .ToList(),
        Transformers = Transformers.Count == 0 ? null : Transformers.Select(t => t.ToStep()).ToList(),
        Targets = Targets.Select(t => new TargetSpec { Path = t.Path }).ToList(),
        Policies = new PolicySet
        {
            ConflictResolution = ConflictResolution,
            OverwriteHandling = OverwriteHandling,
            VerificationMethod = VerificationMethod,
            OnSuccess = OnSuccess,
            ArchiveFolder = string.IsNullOrWhiteSpace(ArchiveFolder) ? null : ArchiveFolder,
            OnFailure = OnFailure.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict,
        },
        Filters = new FilterSet(),
        Logging = new LoggingSpec { Verbosity = Verbosity, NotifyOnFailure = NotifyOnFailure },
    };
}
