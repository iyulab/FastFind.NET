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
    /// <remarks>
    /// Applied only where a file's size is known at enumeration time. The MFT provider learns
    /// sizes only when <see cref="CollectFileMetadata"/> is set; without it every file passes this
    /// limit.
    /// </remarks>
    public long? MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB default

    /// <summary>
    /// Maximum directory depth to traverse (null for no limit)
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Maximum number of files to index (null for no limit).
    /// When the limit is reached, indexing stops and an IndexingPhase.CapReached event is raised.
    /// Files beyond the cap will not appear in search results.
    /// </summary>
    public int? MaxFileCount { get; set; }

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
    /// Whether to read each item's size and timestamps while indexing, on providers whose
    /// enumeration does not already report them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the Windows MFT provider is affected. It enumerates the volume through the change
    /// journal, which reports names, parents and attributes but no sizes or times, so with this
    /// off its items carry a size of 0 and <see cref="DateTime.MinValue"/> for every timestamp —
    /// "not collected", never a real value. With it on, each item costs one metadata read of the
    /// file system entry, which is noticeably slower on a large volume. The standard Windows,
    /// Linux and macOS providers always report metadata and ignore this setting.
    /// </para>
    /// <para>
    /// A search engine refuses a query with a size or date bound over an index built without
    /// metadata — it returns a result with <see cref="SearchResult.HasError"/> set rather than an
    /// empty result that would look like "no such files".
    /// </para>
    /// </remarks>
    public bool CollectFileMetadata { get; set; } = false;

    /// <summary>
    /// Former name of <see cref="CollectFileMetadata"/>, from when only the size was read.
    /// </summary>
    /// <remarks>
    /// Setting it sets <see cref="CollectFileMetadata"/>, which now also fills timestamps from the
    /// same read.
    /// </remarks>
    [Obsolete("Use CollectFileMetadata. The same read now fills timestamps as well as the size.")]
    public bool CollectFileSize
    {
        get => CollectFileMetadata;
        set => CollectFileMetadata = value;
    }

    /// <summary>
    /// Has no effect.
    /// </summary>
    /// <remarks>
    /// It was documented as the batch size of a parallel size-collection pass. No such pass
    /// exists: metadata is read inline, one entry at a time, as items are enumerated.
    /// </remarks>
    [Obsolete("Has no effect. Metadata is read inline during enumeration, not in batches.")]
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

        if (MaxFileCount.HasValue && MaxFileCount.Value <= 0)
        {
            return (false, "MaxFileCount must be a positive number");
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