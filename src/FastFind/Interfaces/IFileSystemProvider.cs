using FastFind.Models;

namespace FastFind.Interfaces;

/// <summary>
/// Interface for platform-specific file system access
/// </summary>
public interface IFileSystemProvider : IDisposable
{
    /// <summary>
    /// Gets the platform this provider supports
    /// </summary>
    PlatformType SupportedPlatform { get; }

    /// <summary>
    /// Gets whether this provider is available on the current system
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Enumerates files and directories in the specified locations
    /// </summary>
    /// <param name="locations">Locations to enumerate</param>
    /// <param name="options">Indexing options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of file items</returns>
    IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations, 
        IndexingOptions options, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets file information for a specific path
    /// </summary>
    /// <param name="filePath">Path to get information for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File item if successful, null if not found or inaccessible</returns>
    Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets available drives or mount points
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Available drives or mount points</returns>
    Task<IEnumerable<DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts monitoring file system changes
    /// </summary>
    /// <param name="locations">Locations to monitor</param>
    /// <param name="options">Monitoring options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of file change events</returns>
    IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a path exists and is accessible
    /// </summary>
    /// <param name="path">Path to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the path exists and is accessible</returns>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the file system type for a given path
    /// </summary>
    /// <param name="path">Path to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File system type</returns>
    Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets performance characteristics of the provider
    /// </summary>
    /// <returns>Provider performance information</returns>
    ProviderPerformance GetPerformanceInfo();
}

/// <summary>
/// Drive or mount point information
/// </summary>
public record DriveInfo
{
    /// <summary>
    /// Drive name (e.g., "C:" on Windows or "/" on Unix)
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Drive label or description
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// File system type (e.g., "NTFS", "ext4", "APFS")
    /// </summary>
    public required string FileSystem { get; init; }

    /// <summary>
    /// Total size in bytes
    /// </summary>
    public long TotalSize { get; init; }

    /// <summary>
    /// Available free space in bytes
    /// </summary>
    public long AvailableSpace { get; init; }

    /// <summary>
    /// Whether the drive is ready and accessible
    /// </summary>
    public bool IsReady { get; init; }

    /// <summary>
    /// Drive type (Fixed, Removable, Network, etc.)
    /// </summary>
    public DriveType DriveType { get; init; }
}

/// <summary>
/// Options for file system monitoring
/// </summary>
public class MonitoringOptions
{
    /// <summary>
    /// Whether to monitor file creation
    /// </summary>
    public bool MonitorCreation { get; set; } = true;

    /// <summary>
    /// Whether to monitor file modification
    /// </summary>
    public bool MonitorModification { get; set; } = true;

    /// <summary>
    /// Whether to monitor file deletion
    /// </summary>
    public bool MonitorDeletion { get; set; } = true;

    /// <summary>
    /// Whether to monitor file renaming/moving
    /// </summary>
    public bool MonitorRename { get; set; } = true;

    /// <summary>
    /// Whether to monitor subdirectories
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Whether to monitor directory changes
    /// </summary>
    public bool MonitorDirectories { get; set; } = true;

    /// <summary>
    /// Buffer size for change notifications
    /// </summary>
    public int BufferSize { get; set; } = 8192;

    /// <summary>
    /// Debounce interval to avoid duplicate notifications
    /// </summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Paths to exclude from monitoring
    /// </summary>
    public IList<string> ExcludedPaths { get; set; } = new List<string>();
}

/// <summary>
/// Provider performance characteristics
/// </summary>
public record ProviderPerformance
{
    /// <summary>
    /// Estimated files per second for enumeration
    /// </summary>
    public double EstimatedFilesPerSecond { get; init; }

    /// <summary>
    /// Whether the provider supports fast enumeration
    /// </summary>
    public bool SupportsFastEnumeration { get; init; }

    /// <summary>
    /// Whether the provider supports native monitoring
    /// </summary>
    public bool SupportsNativeMonitoring { get; init; }

    /// <summary>
    /// Memory overhead per file (bytes)
    /// </summary>
    public double MemoryOverheadPerFile { get; init; }

    /// <summary>
    /// Provider priority (higher is better)
    /// </summary>
    public int Priority { get; init; }
}

/// <summary>
/// Supported platform types
/// </summary>
public enum PlatformType
{
    /// <summary>
    /// Windows platform
    /// </summary>
    Windows,

    /// <summary>
    /// macOS platform
    /// </summary>
    MacOS,

    /// <summary>
    /// Linux platform
    /// </summary>
    Linux,

    /// <summary>
    /// Generic Unix platform
    /// </summary>
    Unix,

    /// <summary>
    /// Cross-platform fallback
    /// </summary>
    CrossPlatform
}

/// <summary>
/// Drive types
/// </summary>
public enum DriveType
{
    /// <summary>
    /// Fixed drive (hard disk)
    /// </summary>
    Fixed,

    /// <summary>
    /// Removable drive (USB, CD, etc.)
    /// </summary>
    Removable,

    /// <summary>
    /// Network drive
    /// </summary>
    Network,

    /// <summary>
    /// RAM drive
    /// </summary>
    Ram,

    /// <summary>
    /// Unknown drive type
    /// </summary>
    Unknown
}