using FileManager.Core.Tokens;

namespace FileManager.Core.Transformers;

/// <summary>
/// Builds the single command string for a <see cref="Profiles.ArgumentMode.Shell"/> step, which the
/// engine hands to <c>cmd.exe /c</c> (Windows) or <c>/bin/sh -c</c> (Unix). Shell mode is the opt-in,
/// higher-risk path (§9): the author's <c>Arguments</c> is treated as shell syntax (pipes,
/// redirection, globbing), so only the <b>substituted token values</b> can be escaped — and that
/// escaping is best-effort, with the full hardening review deferred to M9. The executable path is
/// quoted too. Literal mode (<see cref="ArgumentParser"/>) remains the safe default.
/// </summary>
public static class ShellCommandBuilder
{
    /// <summary>The OS shell executable used to interpret Shell-mode commands.</summary>
    public static string ShellPath =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "/bin/sh";

    /// <summary>The shell flag that runs the following single argument as a command.</summary>
    public static string ShellCommandFlag => OperatingSystem.IsWindows() ? "/c" : "-c";

    /// <summary>
    /// Produces <c>&lt;quoted executable&gt; &lt;expanded arguments&gt;</c>. Token values are escaped for
    /// the host shell before substitution (by pre-escaping the values in a derived
    /// <see cref="TokenContext"/>), so the surrounding author-written shell syntax is preserved
    /// verbatim while injected paths are neutralized as far as the platform allows.
    /// </summary>
    public static string Build(string executablePath, string arguments, TokenContext context)
    {
        TokenContext escaped = EscapeContextValues(context);
        string expandedArgs = TokenExpander.Expand(arguments, escaped);
        return $"{Quote(executablePath)} {expandedArgs}";
    }

    private static TokenContext EscapeContextValues(TokenContext context) => context with
    {
        FilenameStem = Quote(context.FilenameStem),
        Extension = Quote(context.Extension),
        SourceRootPath = Quote(context.SourceRootPath),
        StepInputPath = context.StepInputPath is null ? null : Quote(context.StepInputPath),
        StepOutputPath = context.StepOutputPath is null ? null : Quote(context.StepOutputPath),
    };

    /// <summary>
    /// Wraps a value so the host shell treats it as one literal token. POSIX <c>sh</c> single-quoting
    /// is robust (the classic <c>'\''</c> trick closes, escapes a quote, reopens). <c>cmd.exe</c> has
    /// no sound quoting model — double-quote wrapping with <c>""</c>-doubling neutralizes
    /// <c>&amp; | &lt; &gt; ( )</c> but cannot fully tame <c>%var%</c> expansion; this residual gap is
    /// exactly why Shell mode is opt-in and revisited in M9.
    /// </summary>
    private static string Quote(string value) =>
        OperatingSystem.IsWindows()
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : "'" + value.Replace("'", "'\\''") + "'";
}
