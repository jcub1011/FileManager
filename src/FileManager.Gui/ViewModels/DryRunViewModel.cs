using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.Messages;
using FileManager.Gui.Ipc;

namespace FileManager.Gui.ViewModels;

/// <summary>
/// Drives a dry-run (§8) and renders the report sections: matches + deciding filters, fully
/// token-expanded Transformer commands, Target writes (with action), deletions/overwrites, and the
/// planned source disposition. A plain POCO view-model: it issues the request through the injected
/// <see cref="IServiceClient"/> and exposes the report shape as observable collections, with no Avalonia
/// dependency, so it is fully unit-testable without a UI thread.
/// </summary>
public sealed partial class DryRunViewModel : ViewModelBase
{
    private readonly IServiceClient _client;

    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string? _profileId;
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private int _overwriteCount;
    [ObservableProperty] private int _deletionCount;

    /// <summary>Matched files, each with the deciding filter (§8).</summary>
    public ObservableCollection<DryRunMatchDto> Matches { get; } = new();

    /// <summary>Files screened out, with the deciding rule.</summary>
    public ObservableCollection<DryRunScreenedOutDto> ScreenedOut { get; } = new();

    /// <summary>Fully token-expanded Transformer command previews.</summary>
    public ObservableCollection<DryRunCommandDto> Commands { get; } = new();

    /// <summary>Planned Target writes (with the conflict action).</summary>
    public ObservableCollection<DryRunTargetWriteDto> TargetWrites { get; } = new();

    /// <summary>Planned Mirror deletions.</summary>
    public ObservableCollection<DryRunDeletionDto> Deletions { get; } = new();

    /// <summary>Planned source disposition per matched file.</summary>
    public ObservableCollection<DryRunDispositionDto> Dispositions { get; } = new();

    /// <summary>Creates the dry-run view-model over <paramref name="client"/>.</summary>
    public DryRunViewModel(IServiceClient client) => _client = client;

    /// <summary>
    /// Requests a dry-run for the current <see cref="Path"/> / <see cref="ProfileId"/> /
    /// <see cref="Recursive"/> and populates the report sections. Never throws — an unreachable service
    /// or a not-implemented report is reflected in <see cref="Status"/>.
    /// </summary>
    [RelayCommand]
    public async Task RunAsync()
    {
        IsRunning = true;
        Status = "Running dry-run…";
        try
        {
            DryRunReport? report = await _client
                .DryRunAsync(new DryRunRequest(Path, ProfileId, Recursive))
                .ConfigureAwait(true);

            if (report is null)
            {
                Status = "Service unreachable.";
                return;
            }

            Populate(report);
            Status = report.Implemented
                ? $"{Matches.Count} match(es), {TargetWrites.Count} write(s), {OverwriteCount} overwrite(s), {DeletionCount} deletion(s)."
                : report.Note ?? "Dry-run not available.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Replaces the rendered sections with <paramref name="report"/>'s contents.</summary>
    public void Populate(DryRunReport report)
    {
        Replace(Matches, report.Matches);
        Replace(ScreenedOut, report.ScreenedOut);
        Replace(Commands, report.Commands);
        Replace(TargetWrites, report.TargetWrites);
        Replace(Deletions, report.Deletions);
        Replace(Dispositions, report.Dispositions);
        OverwriteCount = report.TargetWrites.Count(w => w.Action == "Overwritten");
        DeletionCount = report.Deletions.Count;
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (T item in source)
            target.Add(item);
    }
}
