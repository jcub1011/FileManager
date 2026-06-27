using CoreSim = FileManager.Core.Simulation;
using WireMessages = FileManager.Contracts.Messages;

namespace FileManager.Service.Ipc;

/// <summary>
/// Maps the Core domain <see cref="CoreSim.DryRunReport"/> (rich engine model) onto the dependency-free
/// wire <see cref="WireMessages.DryRunReport"/> the IPC layer ships. The mapping is purely structural —
/// enums become their string names so the contract stays self-contained — and lives in the Service
/// because only the Service references both Core and Contracts. It performs no I/O.
/// </summary>
internal static class DryRunReportMapper
{
    /// <summary>Projects a domain report onto the wire DTO, marking it <c>Implemented</c>.</summary>
    public static WireMessages.DryRunReport ToWire(CoreSim.DryRunReport report) => new()
    {
        Implemented = true,
        ProfileId = report.ProfileId,
        Matches = report.Matches
            .Select(m => new WireMessages.DryRunMatchDto(m.SourcePath, m.RelativePath, m.DecidingFilter))
            .ToList(),
        ScreenedOut = report.ScreenedOut
            .Select(s => new WireMessages.DryRunScreenedOutDto(s.SourcePath, s.DecidingFilter, s.Detail))
            .ToList(),
        Commands = report.CommandPreviews
            .Select(c => new WireMessages.DryRunCommandDto(
                c.SourcePath, c.Step, c.Name, c.ExecutablePath, c.Literal, c.Arguments.ToList()))
            .ToList(),
        TargetWrites = report.TargetWrites
            .Select(w => new WireMessages.DryRunTargetWriteDto(
                w.SourcePath, w.TargetRoot, w.FinalPath, w.Action.ToString()))
            .ToList(),
        Deletions = report.Deletions
            .Select(d => new WireMessages.DryRunDeletionDto(d.TargetRoot, d.FilePath, d.RelativeKey))
            .ToList(),
        Dispositions = report.Dispositions
            .Select(d => new WireMessages.DryRunDispositionDto(
                d.SourcePath, d.Action.ToString(), d.DestinationFolder))
            .ToList(),
    };
}
