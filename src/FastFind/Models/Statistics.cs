namespace FastFind.Models;

/// <summary>
/// Statistics for indexing operations
/// </summary>
public record IndexingStatistics
{
    /// <summary>
    /// Total number of files indexed
    /// </summary>
    public long TotalFiles { get; init; }

    /// <summary>
    /// Total number of directories indexed
    /// </summary>
    public long TotalDirectories { get; init; }

    /// <summary>
    /// Total size of all indexed files in bytes
    /// </summary>
    public long TotalSize { get; init; }

    /// <summary>
    /// Time taken for the last complete indexing operation
    /// </summary>
    public TimeSpan LastIndexingTime { get; init; }

    /// <summary>
    /// Average indexing speed (files per second)
    /// </summary>
    public double AverageIndexingSpeed { get; init; }

    /// <summary>
    /// Memory usage of the index in bytes
    /// </summary>
    public long IndexMemoryUsage { get; init; }

    /// <summary>
    /// Disk usage of the index in bytes (if persisted)
    /// </summary>
    public long IndexDiskUsage { get; init; }

    /// <summary>
    /// Compression ratio of the index (if compression is enabled)
    /// </summary>
    public double CompressionRatio { get; init; }

    /// <summary>
    /// Last time the index was updated
    /// </summary>
    public DateTime LastUpdateTime { get; init; }

    /// <summary>
    /// Number of indexing operations performed
    /// </summary>
    public long IndexingOperations { get; init; }

    /// <summary>
    /// Location-specific statistics
    /// </summary>
    public Dictionary<string, LocationStatistics> LocationStats { get; init; } = new();

    /// <summary>
    /// Index efficiency metrics
    /// </summary>
    public IndexEfficiency Efficiency { get; init; } = new();

    /// <summary>
    /// Formatted total size for display
    /// </summary>
    public string TotalSizeFormatted => FormatBytes(TotalSize);

    /// <summary>
    /// Formatted index memory usage for display
    /// </summary>
    public string IndexMemoryUsageFormatted => FormatBytes(IndexMemoryUsage);

    /// <summary>
    /// Formatted index disk usage for display
    /// </summary>
    public string IndexDiskUsageFormatted => FormatBytes(IndexDiskUsage);

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";

        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:N1} {suffixes[suffixIndex]}";
    }
}

/// <summary>
/// Statistics for a specific location (drive or mount point)
/// </summary>
public record LocationStatistics
{
    /// <summary>
    /// Location identifier (drive letter or mount point)
    /// </summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>
    /// Number of files in this location
    /// </summary>
    public long FileCount { get; init; }

    /// <summary>
    /// Number of directories in this location
    /// </summary>
    public long DirectoryCount { get; init; }

    /// <summary>
    /// Total size of files in this location
    /// </summary>
    public long TotalSize { get; init; }

    /// <summary>
    /// Last time this location was scanned
    /// </summary>
    public DateTime LastScanned { get; init; }

    /// <summary>
    /// Current status of this location
    /// </summary>
    public LocationStatus Status { get; init; }

    /// <summary>
    /// Error message if the location failed to index
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Time taken to index this location
    /// </summary>
    public TimeSpan IndexingTime { get; init; }

    /// <summary>
    /// Average file size in this location
    /// </summary>
    public double AverageFileSize => FileCount > 0 ? (double)TotalSize / FileCount : 0;
}

/// <summary>
/// Index efficiency metrics
/// </summary>
public record IndexEfficiency
{
    /// <summary>
    /// Memory usage per file (bytes)
    /// </summary>
    public double MemoryPerFile { get; init; }

    /// <summary>
    /// Disk usage per file (bytes, if persisted)
    /// </summary>
    public double DiskPerFile { get; init; }

    /// <summary>
    /// Index lookup speed (lookups per second)
    /// </summary>
    public double LookupSpeed { get; init; }

    /// <summary>
    /// Index update speed (updates per second)
    /// </summary>
    public double UpdateSpeed { get; init; }

    /// <summary>
    /// Cache hit rate (0-1)
    /// </summary>
    public double CacheHitRate { get; init; }

    /// <summary>
    /// Index fragmentation level (0-1, lower is better)
    /// </summary>
    public double FragmentationLevel { get; init; }

    /// <summary>
    /// Overall efficiency score (0-100)
    /// </summary>
    public double OverallScore { get; init; }
}

/// <summary>
/// Search performance statistics
/// </summary>
public record SearchStatistics
{
    /// <summary>
    /// Total number of searches performed
    /// </summary>
    public long TotalSearches { get; init; }

    /// <summary>
    /// Average search time across all searches
    /// </summary>
    public TimeSpan AverageSearchTime { get; init; }

    /// <summary>
    /// Fastest search time recorded
    /// </summary>
    public TimeSpan FastestSearchTime { get; init; }

    /// <summary>
    /// Slowest search time recorded
    /// </summary>
    public TimeSpan SlowestSearchTime { get; init; }

    /// <summary>
    /// Total time spent searching
    /// </summary>
    public TimeSpan TotalSearchTime { get; init; }

    /// <summary>
    /// Total number of files matched across all searches
    /// </summary>
    public long TotalMatches { get; init; }

    /// <summary>
    /// Average number of matches per search
    /// </summary>
    public double AverageMatches => TotalSearches > 0 ? (double)TotalMatches / TotalSearches : 0;

    /// <summary>
    /// Number of cache hits
    /// </summary>
    public long CacheHits { get; init; }

    /// <summary>
    /// Number of cache misses
    /// </summary>
    public long CacheMisses { get; init; }

    /// <summary>
    /// Cache hit rate as a percentage
    /// </summary>
    public double CacheHitRate => (CacheHits + CacheMisses) > 0 ? 
        (double)CacheHits / (CacheHits + CacheMisses) * 100 : 0;

    /// <summary>
    /// Number of index hits (searches that used the index)
    /// </summary>
    public long IndexHits { get; init; }

    /// <summary>
    /// Number of file system scans (searches that bypassed the index)
    /// </summary>
    public long FileSystemScans { get; init; }

    /// <summary>
    /// Index utilization rate as a percentage
    /// </summary>
    public double IndexUtilizationRate => (IndexHits + FileSystemScans) > 0 ? 
        (double)IndexHits / (IndexHits + FileSystemScans) * 100 : 0;

    /// <summary>
    /// Last search timestamp
    /// </summary>
    public DateTime LastSearch { get; init; }

    /// <summary>
    /// Most common search patterns
    /// </summary>
    public Dictionary<string, long> CommonSearchPatterns { get; init; } = new();

    /// <summary>
    /// Search performance by query complexity
    /// </summary>
    public Dictionary<SearchComplexity, TimeSpan> PerformanceByComplexity { get; init; } = new();
}

/// <summary>
/// Status of a specific location
/// </summary>
public enum LocationStatus
{
    /// <summary>
    /// Location is not yet indexed
    /// </summary>
    NotIndexed,

    /// <summary>
    /// Location is currently being indexed
    /// </summary>
    Indexing,

    /// <summary>
    /// Location is indexed and up to date
    /// </summary>
    Indexed,

    /// <summary>
    /// Location is being monitored for changes
    /// </summary>
    Monitoring,

    /// <summary>
    /// Location index is outdated and needs refresh
    /// </summary>
    Outdated,

    /// <summary>
    /// Location is inaccessible or encountered an error
    /// </summary>
    Error,

    /// <summary>
    /// Location no longer exists
    /// </summary>
    NotFound
}

/// <summary>
/// Search query complexity levels
/// </summary>
public enum SearchComplexity
{
    /// <summary>
    /// Simple text search
    /// </summary>
    Simple,

    /// <summary>
    /// Wildcard search
    /// </summary>
    Wildcard,

    /// <summary>
    /// Regular expression search
    /// </summary>
    Regex,

    /// <summary>
    /// Complex search with multiple filters
    /// </summary>
    Complex
}