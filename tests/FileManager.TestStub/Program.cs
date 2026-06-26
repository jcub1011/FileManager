using System.Globalization;

// A tiny, deterministic cross-platform stand-in for a real transformer CLI (ffmpeg, mid3v2, …) so the
// engine's Phase-3 behavior can be exercised end to end without third-party binaries. The first
// argument selects a mode; the rest are mode-specific. Exit codes drive the success/abort tests.
if (args.Length == 0)
{
    Console.Error.WriteLine("stub: missing mode");
    return 64;
}

switch (args[0])
{
    // copy <in> <out> — NewFile step: produce a distinct output file from the input.
    case "copy":
        File.Copy(args[1], args[2], overwrite: true);
        Console.Out.WriteLine($"copied {Path.GetFileName(args[1])} -> {Path.GetFileName(args[2])}");
        return 0;

    // tag <file> — InPlace step: mutate the working file and carry it forward.
    case "tag":
        File.AppendAllText(args[1], "[tagged]");
        Console.Error.WriteLine($"tagged {Path.GetFileName(args[1])}");
        return 0;

    // sleep <seconds> — runs long enough to trip a shorter TimeoutSeconds.
    case "sleep":
        Thread.Sleep(TimeSpan.FromSeconds(double.Parse(args[1], CultureInfo.InvariantCulture)));
        return 0;

    // exit <code> — deterministic non-zero (or zero) exit for success-check tests.
    case "exit":
        return int.Parse(args[1], CultureInfo.InvariantCulture);

    // dumpargs <outfile> [args…] — writes each trailing argument verbatim, one per line. Proves how
    // many argv elements the process actually received (the injection-immunity probe).
    case "dumpargs":
        File.WriteAllLines(args[1], args[2..]);
        return 0;

    default:
        Console.Error.WriteLine($"stub: unknown mode '{args[0]}'");
        return 65;
}
