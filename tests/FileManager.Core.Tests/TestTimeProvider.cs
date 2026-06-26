namespace FileManager.Core.Tests;

/// <summary>
/// A minimal settable <see cref="TimeProvider"/> for deterministic schedule/watcher tests — no real
/// wall clock. Only <see cref="GetUtcNow"/> is overridden (the only member the M5 components consume).
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; }

    public TestTimeProvider(DateTimeOffset start) => UtcNow = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    /// <summary>Advances the virtual clock by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta) => UtcNow += delta;
}
