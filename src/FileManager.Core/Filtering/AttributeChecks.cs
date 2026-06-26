using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Filtering;

/// <summary>
/// Attribute-based screening (§5.1 <c>Attributes</c>): hidden, system, and symlink handling.
/// </summary>
public static class AttributeChecks
{
    /// <summary>The default attribute policy when a <see cref="FilterSet"/> omits <c>Attributes</c>.</summary>
    public static AttributeFilter Defaults { get; } = new()
    {
        IncludeHidden = false,
        IncludeSystem = false,
        FollowSymlinks = false,
    };

    /// <summary>
    /// Returns the name of the first attribute rule that rejects <paramref name="meta"/>, or
    /// <c>null</c> if the file is acceptable under <paramref name="attrs"/>.
    /// </summary>
    public static string? FindBlockingAttribute(FileMetadata meta, AttributeFilter attrs)
    {
        if (meta.IsHidden && !attrs.IncludeHidden)
            return "Attributes.Hidden";
        if (meta.IsSystem && !attrs.IncludeSystem)
            return "Attributes.System";
        if (meta.IsSymlink && !attrs.FollowSymlinks)
            return "Attributes.Symlink";
        return null;
    }
}
