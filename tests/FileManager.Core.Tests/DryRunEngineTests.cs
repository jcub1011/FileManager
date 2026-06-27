using System.IO;
using System.Security.Cryptography;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using FileManager.Core.Simulation;
using FileManager.Core.Tokens;
using FileManager.Core.Transformers;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Verifies the §8 dry-run engine: it produces the full preview report (matches + deciding filters,
/// expanded Transformer commands, Target writes with actions, Mirror deletions, source disposition) and
/// — the headline §12 criterion — makes ZERO filesystem changes. Also pins command-preview parity with
/// the live transformer path so preview and reality cannot drift.
/// </summary>
public sealed class DryRunEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    // A self-describing snapshot of one file: its relative path, size, content hash, and mtime ticks.
    private sealed record FileSnapshot(string RelativePath, long Length, string Sha256, long MTimeTicks);

    private static IReadOnlyList<FileSnapshot> SnapshotTree(string root)
    {
        var snapshots = new List<FileSnapshot>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p))
        {
            byte[] bytes = File.ReadAllBytes(file);
            using var sha = SHA256.Create();
            snapshots.Add(new FileSnapshot(
                System.IO.Path.GetRelativePath(root, file),
                bytes.LongLength,
                Convert.ToHexString(sha.ComputeHash(bytes)),
                File.GetLastWriteTimeUtc(file).Ticks));
        }

        return snapshots;
    }

    private static DryRunEngine NewEngine(IFileOperations files, string trashRoot) => new(files, trashRoot);

    private static IReadOnlyList<string> FilesUnder(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();

    [Fact]
    public void Simulate_MakesZeroFilesystemChanges_AcrossTransformCopyMirrorAndDelete()
    {
        // A populated sandbox: a Source with files, a pre-populated Target (to force overwrite + a
        // surplus Mirror deletion), under a Profile that would transform, copy, mirror, and dispose.
        using var temp = new TempDir("dryrun-zerofs");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string trashRoot = temp.MakeDir("trash");

        temp.WriteFile("src/keep.txt", "alpha");
        temp.WriteFile("src/sub/nested.txt", "bravo");
        // Pre-existing Target file that the matching source would overwrite.
        temp.WriteFile("dst/keep.txt", "OLD-target-content");
        // Surplus Target file with no matching Source — Mirror would route it to trash.
        temp.WriteFile("dst/surplus.txt", "orphan");

        var policies = TestProfiles.DefaultPolicies with
        {
            ConflictResolution = ConflictResolution.Overwrite,
            OnSuccess = OnSuccess.PermanentDelete, // the most destructive disposition
        };
        Profile profile = TestProfiles.Build(
            sources: new[] { sourceDir },
            targets: new[] { targetDir },
            policies: policies) with
        {
            SyncMode = SyncMode.Mirror, // enables Mirror deletion preview
            Transformers = new[]
            {
                TestTransformers.InPlace(1, "/usr/bin/true", "--touch $filename_current"),
            },
        };

        var files = new SystemFileOperations();
        IReadOnlyList<FileSnapshot> before = SnapshotTree(temp.Root);

        DryRunReport report = NewEngine(files, trashRoot).Simulate(profile, FilesUnder(sourceDir), Now);

        IReadOnlyList<FileSnapshot> after = SnapshotTree(temp.Root);

        // The whole tree is byte-for-byte identical: same files, sizes, content hashes, and mtimes.
        Assert.Equal(before, after);

        // And the report is genuinely populated (so we know the engine actually did the work).
        Assert.Equal(2, report.Matches.Count);
        Assert.NotEmpty(report.CommandPreviews);
        Assert.NotEmpty(report.TargetWrites);
        Assert.NotEmpty(report.Dispositions);
        Assert.Contains(report.Deletions, d => System.IO.Path.GetFileName(d.FilePath) == "surplus.txt");
    }

    [Fact]
    public void Simulate_LiteralCommandPreview_EqualsLiveArgumentParserOutput()
    {
        using var temp = new TempDir("dryrun-literal-parity");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string file = temp.WriteFile("src/track.wav", "x");

        // A hostile-looking filename token in a Literal step: parity must hold for the exact argv.
        var step = TestTransformers.InPlace(
            1, "/usr/bin/ffmpeg", "-i $filename_current --stem $filename_stem", ArgumentMode.Literal);
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }) with
        {
            Transformers = new[] { step },
        };

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { file }, Now);

        DryRunCommandPreview preview = Assert.Single(report.CommandPreviews);
        Assert.True(preview.Literal);
        Assert.Equal("/usr/bin/ffmpeg", preview.ExecutablePath);

        // The LIVE path: the engine's working file name for step 1 is the source file name, and these
        // tokens ($filename_current / $filename_stem) do not depend on the step paths — so the identical
        // TokenContext + ArgumentParser.Parse reproduces exactly what the runner would launch.
        var contextForParity = TokenContext.ForFile("track.wav", PathNormalizer.Normalize(sourceDir));
        IReadOnlyList<string> liveArgv = ArgumentParser.Parse(step.Arguments, contextForParity);

        Assert.Equal(liveArgv, preview.Arguments);
        // And it really expanded (no raw token left).
        Assert.Contains("track.wav", preview.Arguments);
        Assert.Contains("track", preview.Arguments);
    }

    [Fact]
    public void Simulate_ShellCommandPreview_EqualsLiveShellCommandBuilderOutput()
    {
        using var temp = new TempDir("dryrun-shell-parity");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string file = temp.WriteFile("src/clip.mp4", "x");

        var step = TestTransformers.InPlace(
            1, "/usr/bin/convert", "process $filename_current > out", ArgumentMode.Shell);
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }) with
        {
            Transformers = new[] { step },
        };

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { file }, Now);

        DryRunCommandPreview preview = Assert.Single(report.CommandPreviews);
        Assert.False(preview.Literal);
        Assert.Equal(ShellCommandBuilder.ShellPath, preview.ExecutablePath);

        var contextForParity = TokenContext.ForFile("clip.mp4", PathNormalizer.Normalize(sourceDir));
        string liveCommand = ShellCommandBuilder.Build(step.ExecutablePath, step.Arguments, contextForParity);

        // The runner's ProcessLaunchSpec.Arguments for a shell step is { flag, command }.
        Assert.Equal(new[] { ShellCommandBuilder.ShellCommandFlag, liveCommand }, preview.Arguments);
    }

    [Fact]
    public void Simulate_RecordsDecidingFilter_ForMatchesAndScreenedOut()
    {
        using var temp = new TempDir("dryrun-filters");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string kept = temp.WriteFile("src/song.mp3", "x");
        string rejected = temp.WriteFile("src/notes.txt", "x");

        var filters = new FilterSet { Include = new[] { "*.mp3" } };
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }, filters: filters);

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { kept, rejected }, Now);

        DryRunMatch match = Assert.Single(report.Matches);
        Assert.Equal(kept, match.SourcePath);
        Assert.Equal("Pass", match.DecidingFilter);

        DryRunScreenedOut screened = Assert.Single(report.ScreenedOut);
        Assert.Equal(rejected, screened.SourcePath);
        Assert.Equal("Include", screened.DecidingFilter);
    }

    [Fact]
    public void Simulate_RecordsCorrectTargetAction_WrittenVsOverwritten()
    {
        using var temp = new TempDir("dryrun-targetaction");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string fresh = temp.WriteFile("src/new.txt", "x");
        string clashing = temp.WriteFile("src/exists.txt", "x");
        temp.WriteFile("dst/exists.txt", "prior"); // forces Overwritten for the second file

        var policies = TestProfiles.DefaultPolicies with { ConflictResolution = ConflictResolution.Overwrite };
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }, policies: policies);

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { fresh, clashing }, Now);

        DryRunTargetWrite freshWrite = Assert.Single(report.TargetWrites, w => w.SourcePath == fresh);
        Assert.Equal(TargetAction.Written, freshWrite.Action);

        DryRunTargetWrite overwrite = Assert.Single(report.TargetWrites, w => w.SourcePath == clashing);
        Assert.Equal(TargetAction.Overwritten, overwrite.Action);
    }

    [Fact]
    public void Simulate_PreviewsDisposition_MoveToTrashReportsTrashFolder()
    {
        using var temp = new TempDir("dryrun-disposition");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string trashRoot = temp.Path("trash");
        string file = temp.WriteFile("src/a.txt", "x");

        var policies = TestProfiles.DefaultPolicies with { OnSuccess = OnSuccess.MoveToTrash };
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }, policies: policies);

        DryRunReport report = NewEngine(new SystemFileOperations(), trashRoot).Simulate(
            profile, new[] { file }, Now);

        DryRunDisposition disposition = Assert.Single(report.Dispositions);
        Assert.Equal(OnSuccess.MoveToTrash, disposition.Action);
        Assert.Equal(trashRoot, disposition.DestinationFolder);
    }

    [Fact]
    public void Simulate_NonMirror_PlansNoDeletions()
    {
        using var temp = new TempDir("dryrun-nomirror");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        temp.WriteFile("src/a.txt", "x");
        temp.WriteFile("dst/surplus.txt", "orphan");

        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir });
        Assert.Equal(SyncMode.AdditiveArchive, profile.SyncMode);

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, FilesUnder(sourceDir), Now);

        Assert.Empty(report.Deletions);
    }

    [Fact]
    public void Simulate_WhenEveryTargetSkipped_RecordsNoDisposition_MirroringLiveAnyWritten()
    {
        // ConflictResolution.Skip against an existing Target file means nothing is written; the live
        // JobEngine then leaves the source in place (anyWritten == false). The preview must NOT claim a
        // PermanentDelete the engine would never perform (FIX 1).
        using var temp = new TempDir("dryrun-skip-nodispose");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string file = temp.WriteFile("src/a.txt", "x");
        temp.WriteFile("dst/a.txt", "prior"); // existing → Skip policy yields a skipped write

        var policies = TestProfiles.DefaultPolicies with
        {
            ConflictResolution = ConflictResolution.Skip,
            OnSuccess = OnSuccess.PermanentDelete,
        };
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }, policies: policies);

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { file }, Now);

        DryRunTargetWrite write = Assert.Single(report.TargetWrites);
        Assert.Equal(TargetAction.Skipped, write.Action);
        // No disposition recorded — the source would survive.
        Assert.Empty(report.Dispositions);
        // And the source file is genuinely untouched (sanity: zero FS changes).
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Simulate_PartialSkip_StillRecordsDisposition_WhenAnyTargetWritten()
    {
        // Two Targets: one existing (Skip) and one free (Written). anyWritten is true, so the disposition
        // IS recorded — matching the live engine.
        using var temp = new TempDir("dryrun-partial-skip");
        string sourceDir = temp.MakeDir("src");
        string skipTarget = temp.MakeDir("dstSkip");
        string freeTarget = temp.MakeDir("dstFree");
        string file = temp.WriteFile("src/a.txt", "x");
        temp.WriteFile("dstSkip/a.txt", "prior");

        var policies = TestProfiles.DefaultPolicies with
        {
            ConflictResolution = ConflictResolution.Skip,
            OnSuccess = OnSuccess.PermanentDelete,
        };
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { skipTarget, freeTarget }, policies: policies);

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { file }, Now);

        Assert.Contains(report.TargetWrites, w => w.Action == TargetAction.Skipped);
        Assert.Contains(report.TargetWrites, w => w.Action == TargetAction.Written);
        Assert.Single(report.Dispositions);
    }

    [Fact]
    public void Simulate_OverwriteIfNewer_WithTransformer_TreatsProducedFileAsNow_AndOverwrites()
    {
        // OverwriteIfNewer compares the incoming file's mtime to the existing Target's. For a TRANSFORMER
        // Profile the produced file's mtime is ≈ the run instant, so even against a Target NEWER than the
        // source, the run instant wins → Overwritten. Using the source mtime would wrongly predict Skip
        // (FIX 4).
        using var temp = new TempDir("dryrun-ifnewer-transform");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string file = temp.WriteFile("src/a.txt", "x");
        string existing = temp.WriteFile("dst/a.txt", "prior");

        // Make the SOURCE old and the existing TARGET newer than the source but older than `now`.
        File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddDays(-10));
        File.SetLastWriteTimeUtc(existing, Now.UtcDateTime.AddDays(-1));

        var policies = TestProfiles.DefaultPolicies with { ConflictResolution = ConflictResolution.OverwriteIfNewer };
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }, policies: policies) with
        {
            Transformers = new[] { TestTransformers.InPlace(1, "/usr/bin/true", "noop $filename_current") },
        };

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { file }, Now);

        DryRunTargetWrite write = Assert.Single(report.TargetWrites);
        Assert.Equal(TargetAction.Overwritten, write.Action);
    }

    [Fact]
    public void Simulate_OverwriteIfNewer_CopyOnly_UsesSourceMtime()
    {
        // Sanity / counterpart to FIX 4: for a COPY-only Profile the placed file IS the source, so an
        // older source against a newer Target correctly predicts Skip (the source mtime is exact).
        using var temp = new TempDir("dryrun-ifnewer-copy");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string file = temp.WriteFile("src/a.txt", "x");
        string existing = temp.WriteFile("dst/a.txt", "prior");
        File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddDays(-10));
        File.SetLastWriteTimeUtc(existing, Now.UtcDateTime.AddDays(-1));

        var policies = TestProfiles.DefaultPolicies with { ConflictResolution = ConflictResolution.OverwriteIfNewer };
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }, policies: policies);

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { file }, Now);

        DryRunTargetWrite write = Assert.Single(report.TargetWrites);
        Assert.Equal(TargetAction.Skipped, write.Action); // source older than existing → not newer → skip
        Assert.Empty(report.Dispositions); // and no write ⇒ no disposition (FIX 1)
    }

    [Fact]
    public void Simulate_StepPathTokens_UseDocumentedSyntheticWorkspacePlaceholder()
    {
        // Pins the documented synthetic step-path placeholder so the preview's $step_input_path can't
        // silently change shape (reviewer's m3). The placeholder root is "<dry-run-workspace>".
        using var temp = new TempDir("dryrun-step-placeholder");
        string sourceDir = temp.MakeDir("src");
        string targetDir = temp.MakeDir("dst");
        string file = temp.WriteFile("src/clip.mp4", "x");

        var step = TestTransformers.NewFile(
            1, "/usr/bin/conv", "-i $step_input_path -o $step_output_path", ".mov", ArgumentMode.Literal);
        Profile profile = TestProfiles.Build(new[] { sourceDir }, new[] { targetDir }) with
        {
            Transformers = new[] { step },
        };

        DryRunReport report = NewEngine(new SystemFileOperations(), temp.Root).Simulate(
            profile, new[] { file }, Now);

        DryRunCommandPreview preview = Assert.Single(report.CommandPreviews);
        Assert.Contains(preview.Arguments, a => a.Contains("<dry-run-workspace>"));
        Assert.Contains(preview.Arguments, a => a.Contains("clip.mp4")); // step input is the working file
        Assert.Contains(preview.Arguments, a => a.Contains("clip.mov")); // NewFile output extension applied
    }
}
