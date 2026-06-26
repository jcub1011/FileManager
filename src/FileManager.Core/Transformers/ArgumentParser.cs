using System.Text;
using FileManager.Core.Tokens;

namespace FileManager.Core.Transformers;

/// <summary>
/// Builds the argv for a <see cref="Profiles.ArgumentMode.Literal"/> step. The order is the whole
/// security property (§9, §12): the raw <c>Arguments</c> template is split into argv elements
/// <b>first</b> (honoring author quoting in the template), then each element is token-expanded. Because
/// expansion happens after splitting, a token value is inserted as a single element and is never
/// re-split on the spaces, quotes, or <c>$(...)</c> it might contain — so a hostile filename cannot
/// inject extra arguments, and there is no shell to interpret metacharacters at all.
/// </summary>
public static class ArgumentParser
{
    /// <summary>Splits <paramref name="arguments"/> into argv, then expands tokens within each element.</summary>
    public static IReadOnlyList<string> Parse(string arguments, TokenContext context)
    {
        List<string> rawArgs = Tokenize(arguments);
        var expanded = new List<string>(rawArgs.Count);
        foreach (string raw in rawArgs)
            expanded.Add(TokenExpander.Expand(raw, context));
        return expanded;
    }

    /// <summary>
    /// Splits a template into argv on unquoted whitespace. Single and double quotes group a run into
    /// one element and are removed; adjacent quoted/unquoted runs concatenate (shell-like). Backslash
    /// is <b>not</b> an escape — it stays literal so Windows paths in the template survive intact.
    /// Tokens are left in place here; substitution happens afterward in <see cref="Parse"/>.
    /// </summary>
    private static List<string> Tokenize(string template)
    {
        var args = new List<string>();
        if (string.IsNullOrEmpty(template))
            return args;

        var current = new StringBuilder();
        bool inArg = false;       // distinguishes "" (an empty arg) from no arg at all
        char quote = '\0';        // '\0' = not quoted, else the active quote char

        foreach (char c in template)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    inArg = true;
                    break;
                case ' ':
                case '\t':
                case '\r':
                case '\n':
                    if (inArg)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                        inArg = false;
                    }
                    break;
                default:
                    current.Append(c);
                    inArg = true;
                    break;
            }
        }

        if (inArg)
            args.Add(current.ToString());

        return args;
    }
}
