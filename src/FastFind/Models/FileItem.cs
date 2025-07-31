using System.IO;

namespace FastFind.Models;

/// <summary>
/// Represents a file or directory item with comprehensive metadata
/// </summary>
public record FileItem
{
    /// <summary>
    /// Full path to the file or directory
    /// </summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// File or directory name (without path)
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Directory path containing this file
    /// </summary>
    public required string DirectoryPath { get; init; }

    /// <summary>
    /// File extension (including the dot, e.g., ".txt")
    /// </summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>
    /// File size in bytes (0 for directories)
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// File creation time
    /// </summary>
    public DateTime CreatedTime { get; init; }

    /// <summary>
    /// File last modification time
    /// </summary>
    public DateTime ModifiedTime { get; init; }

    /// <summary>
    /// File last access time
    /// </summary>
    public DateTime AccessedTime { get; init; }

    /// <summary>
    /// File system attributes
    /// </summary>
    public FileAttributes Attributes { get; init; }

    /// <summary>
    /// Drive letter (Windows) or mount point identifier
    /// </summary>
    public char DriveLetter { get; init; }

    /// <summary>
    /// Platform-specific file record number (e.g., MFT record number on NTFS)
    /// </summary>
    public ulong? FileRecordNumber { get; init; }

    /// <summary>
    /// Whether this item represents a directory
    /// </summary>
    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

    /// <summary>
    /// Whether this item is hidden
    /// </summary>
    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);

    /// <summary>
    /// Whether this item is a system file
    /// </summary>
    public bool IsSystem => Attributes.HasFlag(FileAttributes.System);

    /// <summary>
    /// Formatted file size for display
    /// </summary>
    public string SizeFormatted => FormatFileSize(Size);

    /// <summary>
    /// File type description based on extension
    /// </summary>
    public string FileType => IsDirectory ? "Folder" : GetFileTypeDescription(Extension);

    /// <summary>
    /// Formats file size in human-readable format
    /// </summary>
    private static string FormatFileSize(long bytes)
    {
        if (bytes == 0) return "0 bytes";

        string[] suffixes = { "bytes", "KB", "MB", "GB", "TB" };
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:N1} {suffixes[suffixIndex]}";
    }

    /// <summary>
    /// Gets file type description based on extension
    /// </summary>
    private static string GetFileTypeDescription(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".txt" => "Text Document",
            ".pdf" => "PDF Document",
            ".doc" or ".docx" => "Word Document",
            ".xls" or ".xlsx" => "Excel Spreadsheet",
            ".ppt" or ".pptx" => "PowerPoint Presentation",
            ".jpg" or ".jpeg" => "JPEG Image",
            ".png" => "PNG Image",
            ".gif" => "GIF Image",
            ".mp3" => "MP3 Audio",
            ".mp4" => "MP4 Video",
            ".zip" => "ZIP Archive",
            ".exe" => "Application",
            ".dll" => "Dynamic Link Library",
            "" => "File",
            _ => $"{extension.TrimStart('.').ToUpperInvariant()} File"
        };
    }
}