using FileManager.Contracts.Messages;
using FileManager.Shell;

// The thin shell integration CLI (M6 submit + M8 manual invocation & registration). Usage:
//   FileManager.Shell <path> [--profile <profileId>] [--recursive] [--manual]
//   FileManager.Shell --register-shell      register the OS right-click entries + autostart (per-user)
//   FileManager.Shell --unregister-shell    remove them

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  FileManager.Shell <path> [--profile <profileId>] [--recursive] [--manual]\n" +
        "  FileManager.Shell --register-shell | --unregister-shell");
    return 2;
}

switch (args[0].ToLowerInvariant())
{
    case "--register-shell":
        foreach (string line in new RegistrationInstaller().Register())
            Console.WriteLine(line);
        return 0;

    case "--unregister-shell":
        foreach (string line in new RegistrationInstaller().Unregister())
            Console.WriteLine(line);
        return 0;

    default:
        return await SubmitAsync(args).ConfigureAwait(false);
}

static async Task<int> SubmitAsync(string[] args)
{
    string path = args[0];
    string? profileId = null;
    bool recursive = false;
    bool manual = false;

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--profile" when i + 1 < args.Length:
                profileId = args[++i];
                break;
            case "--recursive" or "-r":
                recursive = true;
                break;
            case "--manual":
                manual = true;
                break;
        }
    }

    // A manual (right-click) invocation must reach the always-prompt chooser (§3.2): ensure the GUI is
    // running so a subscriber exists to raise it, then submit IsManual:true (the service registers a
    // pending invocation and pushes ManualInvocationPending rather than auto-running).
    var launcher = new FallbackLauncher(ensureGuiForManual: manual);
    SubmitPayloadResult result = await launcher
        .SubmitAsync(new SubmitPayload(path, profileId, recursive, IsManual: manual))
        .ConfigureAwait(false);

    if (result.PendingInvocationId is not null)
    {
        Console.WriteLine($"Awaiting profile choice (invocation {result.PendingInvocationId}).");
        return 0;
    }

    if (result.Accepted)
    {
        Console.WriteLine($"Accepted: queued {result.QueuedCount} job(s).");
        return 0;
    }

    Console.Error.WriteLine($"Rejected: {result.Reason}");
    return 1;
}
