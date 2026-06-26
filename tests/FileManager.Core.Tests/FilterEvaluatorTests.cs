using System.IO;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class FilterEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<TargetSpec> NoTargets = Array.Empty<TargetSpec>();
    private readonly FilterEvaluator _eval = new(new SystemFileOperations());

    private static FileMetadata Meta(
        long length = 100,
        DateTime? modifiedUtc = null,
        DateTime? createdUtc = null,
        bool hidden = false,
        bool system = false,
        bool symlink = false) => new()
    {
        Length = length,
        LastWriteTimeUtc = modifiedUtc ?? Now.UtcDateTime,
        CreationTimeUtc = createdUtc ?? Now.UtcDateTime,
        IsHidden = hidden,
        IsSystem = system,
        IsSymlink = symlink,
    };

    private static FilterCandidate Candidate(
        string name = "track.wav",
        string relativePath = "track.wav",
        int depth = 0,
        FileMetadata? meta = null) => new()
    {
        FileName = name,
        RelativePath = relativePath,
        Depth = depth,
        FullPath = @"C:\src\" + relativePath,
        Metadata = meta ?? Meta(),
    };

    private FilterDecision Eval(FilterSet filters, FilterCandidate candidate) =>
        _eval.Evaluate(filters, candidate, NoTargets, Now);

    [Fact]
    public void EmptyFilters_PassesEverything()
    {
        Assert.True(Eval(new FilterSet(), Candidate()).Included);
    }

    [Fact]
    public void Include_NoMatch_RejectsWithDecidingFilter()
    {
        var filters = new FilterSet { Include = new[] { "*.flac" } };
        FilterDecision d = Eval(filters, Candidate("track.wav"));
        Assert.False(d.Included);
        Assert.Equal("Include", d.DecidingFilter);
    }

    [Fact]
    public void Include_Match_Passes()
    {
        var filters = new FilterSet { Include = new[] { "*.wav", "*.flac" } };
        Assert.True(Eval(filters, Candidate("track.wav")).Included);
    }

    [Fact]
    public void ExcludeGlob_Match_Rejects()
    {
        var filters = new FilterSet { ExcludeGlob = new[] { "Thumbs.db" } };
        FilterDecision d = Eval(filters, Candidate("Thumbs.db"));
        Assert.False(d.Included);
        Assert.Equal("ExcludeGlob", d.DecidingFilter);
        Assert.Equal("Thumbs.db", d.Detail);
    }

    [Fact]
    public void IncludeRegex_NoMatch_Rejects()
    {
        var filters = new FilterSet { IncludeRegex = new[] { @"^\d+_.*" } };
        FilterDecision d = Eval(filters, Candidate("track.wav"));
        Assert.False(d.Included);
        Assert.Equal("IncludeRegex", d.DecidingFilter);
    }

    [Fact]
    public void ExcludeRegex_Match_Rejects()
    {
        var filters = new FilterSet { ExcludeRegex = new[] { @"tmp" } };
        FilterDecision d = Eval(filters, Candidate("my-tmp-file.wav"));
        Assert.False(d.Included);
        Assert.Equal("ExcludeRegex", d.DecidingFilter);
    }

    [Fact]
    public void Size_BelowMin_Rejects()
    {
        var filters = new FilterSet { MinSizeBytes = 1000 };
        Assert.Equal("MinSizeBytes", Eval(filters, Candidate(meta: Meta(length: 500))).DecidingFilter);
    }

    [Fact]
    public void Size_AboveMax_Rejects()
    {
        var filters = new FilterSet { MaxSizeBytes = 100 };
        Assert.Equal("MaxSizeBytes", Eval(filters, Candidate(meta: Meta(length: 500))).DecidingFilter);
    }

    [Fact]
    public void ModifiedWithin_TooOld_Rejects()
    {
        var filters = new FilterSet { ModifiedWithin = "1d" };
        FileMetadata old = Meta(modifiedUtc: Now.UtcDateTime.AddDays(-3));
        Assert.Equal("ModifiedWithin", Eval(filters, Candidate(meta: old)).DecidingFilter);
    }

    [Fact]
    public void ModifiedWithin_Recent_Passes()
    {
        var filters = new FilterSet { ModifiedWithin = "7d" };
        FileMetadata recent = Meta(modifiedUtc: Now.UtcDateTime.AddDays(-1));
        Assert.True(Eval(filters, Candidate(meta: recent)).Included);
    }

    [Fact]
    public void ModifiedOlderThan_TooRecent_Rejects()
    {
        var filters = new FilterSet { ModifiedOlderThan = "30d" };
        FileMetadata recent = Meta(modifiedUtc: Now.UtcDateTime.AddDays(-1));
        Assert.Equal("ModifiedOlderThan", Eval(filters, Candidate(meta: recent)).DecidingFilter);
    }

    [Fact]
    public void Attributes_HiddenByDefault_Rejects()
    {
        FilterDecision d = Eval(new FilterSet(), Candidate(meta: Meta(hidden: true)));
        Assert.Equal("Attributes.Hidden", d.DecidingFilter);
    }

    [Fact]
    public void Attributes_HiddenAllowed_Passes()
    {
        var filters = new FilterSet
        {
            Attributes = new AttributeFilter { IncludeHidden = true, IncludeSystem = false, FollowSymlinks = false },
        };
        Assert.True(Eval(filters, Candidate(meta: Meta(hidden: true))).Included);
    }

    [Fact]
    public void MaxDepth_TooDeep_Rejects()
    {
        var filters = new FilterSet { MaxDepth = 1 };
        FilterDecision d = Eval(filters, Candidate(relativePath: "a/b/c.wav", depth: 2));
        Assert.Equal("MaxDepth", d.DecidingFilter);
    }

    [Fact]
    public void ContentHashDedupe_DuplicateInTarget_Rejects()
    {
        using var temp = new TempDir("dedupe");
        string src = temp.WriteFile("src/track.wav", "identical-bytes");
        string targetDir = temp.MakeDir("target");
        File.WriteAllText(Path.Combine(targetDir, "copy.wav"), "identical-bytes");

        var filters = new FilterSet { ContentHashDedupe = true };
        var candidate = new FilterCandidate
        {
            FileName = "track.wav",
            RelativePath = "track.wav",
            Depth = 0,
            FullPath = src,
            Metadata = Meta(),
        };

        FilterDecision d = _eval.Evaluate(
            filters, candidate, new[] { new TargetSpec { Path = targetDir } }, Now);

        Assert.False(d.Included);
        Assert.Equal("ContentHashDedupe", d.DecidingFilter);
    }

    [Fact]
    public void ContentHashDedupe_IgnoresAtomicTempFiles()
    {
        using var temp = new TempDir("dedupe-temp");
        string src = temp.WriteFile("src/track.wav", "identical-bytes");
        string targetDir = temp.MakeDir("target");
        // An orphaned atomic-write temp with identical bytes must NOT count as a duplicate.
        File.WriteAllText(Path.Combine(targetDir, "." + Guid.NewGuid().ToString("N") + AtomicFileWriter.TempSuffix), "identical-bytes");

        var filters = new FilterSet { ContentHashDedupe = true };
        var candidate = new FilterCandidate
        {
            FileName = "track.wav",
            RelativePath = "track.wav",
            Depth = 0,
            FullPath = src,
            Metadata = Meta(),
        };

        FilterDecision d = _eval.Evaluate(
            filters, candidate, new[] { new TargetSpec { Path = targetDir } }, Now);

        Assert.True(d.Included);
    }

    [Fact]
    public void ResolveEffective_PerSourceOverridesGlobal()
    {
        var global = new FilterSet { Include = new[] { "*.wav" } };
        var perSource = new FilterSet { Include = new[] { "*.flac" } };

        FilterSet effective = FilterEvaluator.ResolveEffective(global, perSource);
        Assert.Same(perSource, effective);

        // A .wav passes global but is rejected by the overriding per-Source filter.
        Assert.False(Eval(effective, Candidate("track.wav")).Included);
        Assert.True(Eval(effective, Candidate("track.flac")).Included);
    }

    [Fact]
    public void ResolveEffective_NullPerSource_UsesGlobal()
    {
        var global = new FilterSet { Include = new[] { "*.wav" } };
        Assert.Same(global, FilterEvaluator.ResolveEffective(global, null));
    }
}
