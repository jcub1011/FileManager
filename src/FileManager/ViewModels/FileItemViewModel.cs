using System;
using FileManager.Models;

namespace FileManager.ViewModels;

/// <summary>
/// Binding wrapper around a <see cref="FileSystemEntry"/>. Read-only view data, so it
/// exposes plain properties rather than observable ones.
/// </summary>
public sealed class FileItemViewModel(FileSystemEntry entry)
{
    public FileSystemEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>Glyph hint for the list — folder vs. file. Kept text-only for the first pass.</summary>
    public string Glyph => Entry.IsDirectory ? "📁" : "📄";

    public string SizeDisplay => Entry.IsDirectory ? string.Empty : FormatSize(Entry.Size);

    public string ModifiedDisplay =>
        Entry.Modified == DateTime.MinValue ? string.Empty : Entry.Modified.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }
}
