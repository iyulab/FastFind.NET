namespace FastFind.Models;

/// <summary>
/// Configuration options for indexing operations
/// </summary>
public class IndexingOptions
{
    /// <summary>
    /// Drive letters to index (Windows specific, e.g., ['C', 'D'])
    /// </summary>
    public IList<char> DriveLetters { get; set; } = new List<char>();

    /// <summary>
    /// Mount points to index (Unix specific, e.g., ["/", "/home"])
    /// </summary>
    public IList<string> MountPoints { get; set; } = new List<string>();

    /// <summary>
    /// Specific directories to index (overrides drive/mount point settings if specified)
    /// </summary>
    public IList<string> SpecificDirectories { get; set; } = new List<string>();

    /// <summary>
    /// Paths to exclude from indexing (supports wildcards)
    /// </summary>
    public IList<string> ExcludedPaths { get; set; } = new List<string>
    {
        "**/temp/**", "**/cache/**", "**/.git/**", "**/node_modules/**",
        "**/bin/**", "**/obj/**", "**/.vs/**", "**/packages/**"
    };

    /// <summary>
    /// File extensions to exclude from indexing
    /// </summary>
    public IList<string> ExcludedExtensions { get; set; } = new List<string>
    {
        ".tmp", ".temp", ".cache", ".log"
    };

    /// <summary>
    /// Whether to include hidden files and directories
    /// </summary>
    public bool IncludeHidden { get; set; } = false;

    /// <summary>
    /// Whether to include system files and directories
    /// </summary>
    public bool IncludeSystem { get; set; } = false;

    /// <summary>
    /// Maximum file size to index (in bytes, null for no limit)
    /// </summary>
    public long? MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB default

    /// <summary>
    /// Maximum directory depth to traverse (null for no limit)
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Whether to follow symbolic links and junctions
    /// </summary>
    public bool FollowSymlinks { get; set; } = false;

    /// <summary>
    /// Number of parallel indexing threads
    /// </summary>
    public int ParallelThreads { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Batch size for processing files
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Whether to enable real-time file system monitoring after initial indexing
    /// </summary>
    public bool EnableMonitoring { get; set; } = true;

    /// <summary>
    /// Interval for saving index to disk (null to disable auto-save)
    /// </summary>
    public TimeSpan? AutoSaveInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to use memory-mapped files for large indexes
    /// </summary>
    public bool UseMemoryMapping { get; set; } = true;

    /// <summary>
    /// Whether to compress the index data
    /// </summary>
    public bool CompressIndex { get; set; } = true;

    /// <summary>
    /// Priority level for indexing threads
    /// </summary>
    public ThreadPriority IndexingPriority { get; set; } = ThreadPriority.BelowNormal;

    /// <summary>
    /// Whether to collect file sizes during indexing.
    /// When false (default), file sizes will be 0 for maximum indexing performance.
    /// When true, file sizes are collected in a parallel batch after MFT enumeration.
    /// Note: This adds ~10-30% overhead to indexing time depending on file count.
    /// </summary>
    public bool CollectFileSize { get; set; } = false;

    /// <summary>
    /// Batch size for parallel file size collection (only used when CollectFileSize is true).
    /// Higher values improve throughput but use more memory.
    /// </summary>
    public int FileSizeCollectionBatchSize { get; set; } = 5000;

    /// <summary>
    /// Validates the indexing options
    /// </summary>
    public (bool IsValid, string? ErrorMessage) Validate()
    {
        if (DriveLetters.Count == 0 && MountPoints.Count == 0 && SpecificDirectories.Count == 0)
        {
            return (false, "At least one drive letter, mount point, or specific directory must be specified");
        }

        if (ParallelThreads <= 0)
        {
            return (false, "Parallel threads must be a positive number");
        }

        if (BatchSize <= 0)
        {
            return (false, "Batch size must be a positive number");
        }

        if (MaxFileSize.HasValue && MaxFileSize.Value <= 0)
        {
            return (false, "Maximum file size must be a positive number");
        }

        if (MaxDepth.HasValue && MaxDepth.Value <= 0)
        {
            return (false, "Maximum depth must be a positive number");
        }

        return (true, null);
    }

    /// <summary>
    /// Gets the effective search locations based on the current platform
    /// </summary>
    public IEnumerable<string> GetEffectiveSearchLocations()
    {
        if (SpecificDirectories.Count > 0)
        {
            return SpecificDirectories;
        }

        if (OperatingSystem.IsWindows())
        {
            return DriveLetters.Select(d => $"{d}:\\");
        }
        else
        {
            return MountPoints.Count > 0 ? MountPoints : new[] { "/" };
        }
    }

    /// <summary>
    /// Creates default indexing options for the current platform
    /// </summary>
    public static IndexingOptions CreateDefault()
    {
        var options = new IndexingOptions();

        if (OperatingSystem.IsWindows())
        {
            // Default to C: drive on Windows
            options.DriveLetters.Add('C');
        }
        else
        {
            // Default to root and home on Unix systems
            options.MountPoints.Add("/");
            if (Directory.Exists("/home"))
                options.MountPoints.Add("/home");
        }

        return options;
    }
}