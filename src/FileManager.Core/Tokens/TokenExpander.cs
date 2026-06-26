using System.IO;
using System.Text;

namespace FileManager.Core.Tokens;

/// <summary>
/// Per-file values that filename tokens (§5.2) expand to. Built once per Job from the file's
/// current name and its owning Source root; M2 extends this with per-step input/output paths.
/// </summary>
public sealed record TokenContext
{
    /// <summary>Base name without extension (e.g. <c>track</c>).</summary>
    public required string FilenameStem { get; init; }

    /// <summary>Extension <b>including</b> the leading dot (e.g. <c>.wav</c>), or empty if none.</summary>
    public required string Extension { get; init; }

    /// <summary>Absolute path of the Source directory that contained this file.</summary>
    public required string SourceRootPath { get; init; }

    /// <summary>Full base name: <see cref="FilenameStem"/> + <see cref="Extension"/>.</summary>
    public string FilenameCurrent => FilenameStem + Extension;

    /// <summary>Builds a context from a file name and the absolute Source root path.</summary>
    public static TokenContext ForFile(string fileName, string sourceRootPath)
    {
        (string stem, string ext) = TokenExpander.SplitName(fileName);
        return new TokenContext
        {
            FilenameStem = stem,
            Extension = ext,
            SourceRootPath = sourceRootPath,
        };
    }
}

/// <summary>
/// Owns the shared §5.2 token rules so preview (M7) and runtime cannot drift, and so M2's step
/// tokens extend exactly one component. Rules: the delimiter is <c>$name</c>; <c>$$</c> is a literal
/// dollar; names are case-sensitive; each token expands to a single value. Unknown tokens are left
/// verbatim (M2 introduces additional names).
/// </summary>
public static class TokenExpander
{
    /// <summary>Expands all known filename tokens in <paramref name="template"/>.</summary>
    public static string Expand(string template, TokenContext context)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf('$') < 0)
            return template;

        var sb = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '$')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // We are at a '$'.
            if (i + 1 < template.Length && template[i + 1] == '$')
            {
                sb.Append('$'); // "$$" -> literal '$'
                i += 2;
                continue;
            }

            int nameStart = i + 1;
            int nameEnd = nameStart;
            while (nameEnd < template.Length && IsNameChar(template[nameEnd]))
                nameEnd++;

            // Bare '$' with no following name char: emit it literally.
            if (nameEnd == nameStart)
            {
                sb.Append('$');
                i++;
                continue;
            }

            string name = template[nameStart..nameEnd];
            if (TryResolve(name, context, out string? value))
                sb.Append(value);
            else
                sb.Append('$').Append(name); // unknown token: leave verbatim

            i = nameEnd;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits a base file name into stem and extension (extension includes the leading dot).
    /// Shared with <see cref="FileManager.Core.Routing.ConflictResolver"/> so suffix renaming and
    /// token expansion agree on what "stem" and "extension" mean.
    /// </summary>
    public static (string Stem, string Extension) SplitName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return (stem, ext);
    }

    private static bool TryResolve(string name, TokenContext context, out string? value)
    {
        // Case-sensitive by spec: "$FileName_Stem" must not expand.
        switch (name)
        {
            case "filename_stem":
                value = context.FilenameStem;
                return true;
            case "extension":
                value = context.Extension;
                return true;
            case "filename_current":
                value = context.FilenameCurrent;
                return true;
            case "source_root_path":
                value = context.SourceRootPath;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static bool IsNameChar(char c) =>
        c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
