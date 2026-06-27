namespace FileManager.Service.Tray;

/// <summary>
/// The optional system-tray status indicator (spec §1.1). The service attaches one only when a tray is
/// available; its absence is a pure no-op and the service is fully functional headless. M6 ships the
/// abstraction plus a no-op implementation — actual tray rendering is deferred to M7 (Avalonia returns
/// then), so no UI/native dependency is taken now.
/// </summary>
public interface ITrayIndicator : IDisposable
{
    /// <summary>Shows the indicator (a no-op for the null implementation).</summary>
    public void Show();

    /// <summary>Updates the displayed status text (a no-op for the null implementation).</summary>
    public void SetStatus(string status);
}

/// <summary>
/// The no-op <see cref="ITrayIndicator"/> used when no tray is available (or always, in M6). Every member
/// is a no-op so the service runs identically with or without a tray — the §1.1 "service never depends on
/// the tray" guarantee in code.
/// </summary>
public sealed class NullTrayIndicator : ITrayIndicator
{
    /// <summary>A shared instance — the null indicator is stateless.</summary>
    public static NullTrayIndicator Instance { get; } = new();

    /// <inheritdoc/>
    public void Show()
    {
    }

    /// <inheritdoc/>
    public void SetStatus(string status)
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
