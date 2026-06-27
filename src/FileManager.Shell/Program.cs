using FileManager.Contracts.Messages;
using FileManager.Shell;

// The thin shell integration CLI (M6): submit a file/directory path to the running service (starting it
// first if needed). Usage:
//   FileManager.Shell <path> [--profile <profileId>] [--recursive]

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: FileManager.Shell <path> [--profile <profileId>] [--recursive]");
    return 2;
}

string path = args[0];
string? profileId = null;
bool recursive = false;

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
    }
}

var launcher = new FallbackLauncher();
SubmitPayloadResult result = await launcher
    .SubmitAsync(new SubmitPayload(path, profileId, recursive))
    .ConfigureAwait(false);

if (result.Accepted)
{
    Console.WriteLine($"Accepted: queued {result.QueuedCount} job(s).");
    return 0;
}

Console.Error.WriteLine($"Rejected: {result.Reason}");
return 1;
