using System.Diagnostics;
using System.Runtime.InteropServices;
using FileManager.Service;
using FileManager.Service.Autostart;

// The thin Core Service entry point (M6). Default: run the service under the single-instance guard,
// stopping gracefully on Ctrl+C / SIGTERM / SIGINT. --install/--uninstall register or remove the
// per-OS autostart entry. This stays a minimal hand-rolled host (no generic host / DI / NuGet), so the
// whole executable is AOT-clean.

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";

switch (command)
{
    case "--install" or "install":
        Console.WriteLine(InstallAutostart());
        return 0;

    case "--uninstall" or "uninstall":
        Console.WriteLine(UninstallAutostart());
        return 0;

    default:
        return await RunServiceAsync().ConfigureAwait(false);
}

static async Task<int> RunServiceAsync()
{
    // One service per user: a second instance detects the first via the endpoint-keyed guard and exits.
    using var guard = SingleInstanceGuard.Acquire();
    if (!guard.IsPrimaryInstance)
    {
        Console.Error.WriteLine("FileManager service is already running for this user.");
        return 1;
    }

    await using var host = new ServiceHost();

    using var shutdown = new CancellationTokenSource();

    // Ctrl+C (console) and POSIX SIGTERM/SIGINT (systemd stop / kill) all request a graceful stop.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // we handle the shutdown ourselves rather than aborting.
        shutdown.Cancel();
    };

    using PosixSignalRegistration sigterm =
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
    using PosixSignalRegistration sigint =
        PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; shutdown.Cancel(); });

    await host.StartAsync(shutdown.Token).ConfigureAwait(false);
    Console.WriteLine($"FileManager service running on '{host.Endpoint}'. Press Ctrl+C to stop.");

    try
    {
        await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // Shutdown requested.
    }

    await host.StopAsync().ConfigureAwait(false);
    return 0;
}

static string InstallAutostart()
{
    string exe = CurrentExecutablePath();
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return WindowsLogonTask.Install(exe);
    return LinuxSystemdUserUnit.Install(exe);
}

static string UninstallAutostart()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return WindowsLogonTask.Uninstall();
    return LinuxSystemdUserUnit.Uninstall();
}

static string CurrentExecutablePath() =>
    Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "FileManager.Service";
