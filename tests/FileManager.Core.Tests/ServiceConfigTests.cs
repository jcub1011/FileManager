using System.IO;
using FileManager.Core.Configuration;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class ServiceConfigTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var config = new ServiceConfig();

        Assert.Equal(Environment.ProcessorCount, config.MaxWorkers);
        Assert.Null(config.Allowlist);
        Assert.Equal(ServiceConfig.DefaultLogRotationSizeBytes, config.LogRotationSizeBytes);
        Assert.Equal(ServiceConfig.DefaultAuditRotationSizeBytes, config.AuditRotationSizeBytes);
        Assert.True(ServiceConfigStore.Validate(config).IsValid);
    }

    [Fact]
    public void LoadFrom_AbsentFile_ReturnsValidDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");

        ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

        Assert.False(result.FileExisted);
        Assert.True(result.IsValid);
        Assert.Equal(Environment.ProcessorCount, result.Config.MaxWorkers);
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var original = new ServiceConfig
        {
            MaxWorkers = 4,
            Allowlist = new[] { @"C:\tools\ffmpeg.exe", "/usr/bin/convert" },
            LogDirectory = @"C:\logs",
            JournalDirectory = @"C:\journal",
            AuditLogPath = @"C:\audit\audit.log",
            LogRotationSizeBytes = 1_000_000,
            AuditRotationSizeBytes = 2_000_000,
        };

        string json = ProfileSerializer.Serialize(original);
        ServiceConfig? roundTripped = ProfileSerializer.DeserializeServiceConfig(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(4, roundTripped!.MaxWorkers);
        Assert.Equal(original.Allowlist, roundTripped.Allowlist);
        Assert.Equal(@"C:\logs", roundTripped.LogDirectory);
        Assert.Equal(@"C:\journal", roundTripped.JournalDirectory);
        Assert.Equal(@"C:\audit\audit.log", roundTripped.AuditLogPath);
        Assert.Equal(1_000_000, roundTripped.LogRotationSizeBytes);
        Assert.Equal(2_000_000, roundTripped.AuditRotationSizeBytes);
    }

    [Fact]
    public void LoadFrom_PresentFile_ParsesAndValidates()
    {
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "MaxWorkers": 8, "Allowlist": ["/usr/bin/ffmpeg"] }""");

        try
        {
            ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

            Assert.True(result.FileExisted);
            Assert.True(result.IsValid, string.Join("|", result.Validation.Errors));
            Assert.Equal(8, result.Config.MaxWorkers);
            Assert.Equal(new[] { "/usr/bin/ffmpeg" }, result.Config.Allowlist);
            // Omitted fields fall back to documented defaults.
            Assert.Equal(ServiceConfig.DefaultLogRotationSizeBytes, result.Config.LogRotationSizeBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFrom_OmittedFields_FallBackToDefaults()
    {
        // Regression: STJ source-gen does not run property initializers, so the loader must
        // restore documented defaults for omitted numeric keys (rather than leaving them 0).
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "LogDirectory": "/var/log/fp" }""");

        try
        {
            ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

            Assert.True(result.IsValid, string.Join("|", result.Validation.Errors));
            Assert.Equal(Environment.ProcessorCount, result.Config.MaxWorkers);
            Assert.Equal(ServiceConfig.DefaultLogRotationSizeBytes, result.Config.LogRotationSizeBytes);
            Assert.Equal(ServiceConfig.DefaultAuditRotationSizeBytes, result.Config.AuditRotationSizeBytes);
            Assert.Equal("/var/log/fp", result.Config.LogDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JournalRotationSizeBytes_DefaultsAndRoundTrips()
    {
        Assert.Equal(ServiceConfig.DefaultJournalRotationSizeBytes, new ServiceConfig().JournalRotationSizeBytes);

        var original = new ServiceConfig { JournalRotationSizeBytes = 3_000_000 };
        string json = ProfileSerializer.Serialize(original);
        ServiceConfig? roundTripped = ProfileSerializer.DeserializeServiceConfig(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(3_000_000, roundTripped!.JournalRotationSizeBytes);
    }

    [Fact]
    public void LoadFrom_OmittedJournalRotation_FallsBackToDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "MaxWorkers": 2 }""");

        try
        {
            ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

            Assert.True(result.IsValid, string.Join("|", result.Validation.Errors));
            Assert.Equal(ServiceConfig.DefaultJournalRotationSizeBytes, result.Config.JournalRotationSizeBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFrom_InvalidJournalRotation_ReportsValidationError()
    {
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "JournalRotationSizeBytes": 0 }""");

        try
        {
            ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Validation.Errors, e => e.Path == nameof(ServiceConfig.JournalRotationSizeBytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MinFreeSpaceMarginBytes_DefaultsToZero_AndRoundTrips()
    {
        Assert.Equal(0, new ServiceConfig().MinFreeSpaceMarginBytes);

        var original = new ServiceConfig { MinFreeSpaceMarginBytes = 5_000_000 };
        string json = ProfileSerializer.Serialize(original);
        ServiceConfig? roundTripped = ProfileSerializer.DeserializeServiceConfig(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(5_000_000, roundTripped!.MinFreeSpaceMarginBytes);
    }

    [Fact]
    public void LoadFrom_NegativeMinFreeSpaceMargin_ReportsValidationError()
    {
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "MinFreeSpaceMarginBytes": -1 }""");

        try
        {
            ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Validation.Errors, e => e.Path == nameof(ServiceConfig.MinFreeSpaceMarginBytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFrom_InvalidMaxWorkers_ReportsValidationError()
    {
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "MaxWorkers": 0 }""");

        try
        {
            ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

            Assert.True(result.FileExisted);
            Assert.False(result.IsValid);
            Assert.Contains(result.Validation.Errors, e => e.Path == nameof(ServiceConfig.MaxWorkers));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFrom_MalformedJson_ReturnsDefaultsWithError()
    {
        string path = Path.Combine(Path.GetTempPath(), "fp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ not json");

        try
        {
            ServiceConfigLoadResult result = ServiceConfigStore.LoadFrom(path);

            Assert.True(result.FileExisted);
            Assert.False(result.IsValid);
            Assert.Equal(Environment.ProcessorCount, result.Config.MaxWorkers); // defaults supplied
        }
        finally
        {
            File.Delete(path);
        }
    }
}
