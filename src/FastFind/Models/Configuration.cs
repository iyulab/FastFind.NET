namespace FastFind.Models;

/// <summary>
/// Configuration options for the search index
/// </summary>
public record IndexConfiguration
{
    /// <summary>
    /// Whether to use SIMD-accelerated string matching
    /// </summary>
    public bool UseSIMD { get; init; } = true;

    /// <summary>
    /// Whether to use string pooling for memory optimization
    /// </summary>
    public bool UseStringPooling { get; init; } = true;

    /// <summary>
    /// Maximum number of items to cache in memory
    /// </summary>
    public int MaxCacheSize { get; init; } = 100_000;

    /// <summary>
    /// Whether to enable file system monitoring
    /// </summary>
    public bool EnableMonitoring { get; init; } = true;

    /// <summary>
    /// Persistence configuration (null for in-memory only)
    /// </summary>
    public PersistenceConfiguration? Persistence { get; init; }

    /// <summary>
    /// Maximum concurrent operations for indexing
    /// </summary>
    public int MaxConcurrency { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Batch size for bulk operations
    /// </summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// Extensions to exclude from indexing
    /// </summary>
    public HashSet<string> ExcludedExtensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Directories to exclude from indexing
    /// </summary>
    public HashSet<string> ExcludedDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether to index hidden files
    /// </summary>
    public bool IncludeHiddenFiles { get; init; } = false;

    /// <summary>
    /// Whether to index system files
    /// </summary>
    public bool IncludeSystemFiles { get; init; } = false;

    /// <summary>
    /// Default configuration for most use cases
    /// </summary>
    public static IndexConfiguration Default => new();

    /// <summary>
    /// High-performance configuration optimized for speed
    /// </summary>
    public static IndexConfiguration HighPerformance => new()
    {
        UseSIMD = true,
        UseStringPooling = true,
        MaxConcurrency = Environment.ProcessorCount * 2,
        BatchSize = 5000,
        MaxCacheSize = 500_000
    };

    /// <summary>
    /// Low-memory configuration for constrained environments
    /// </summary>
    public static IndexConfiguration LowMemory => new()
    {
        UseSIMD = true,
        UseStringPooling = true,
        MaxConcurrency = 2,
        BatchSize = 100,
        MaxCacheSize = 10_000
    };
}

/// <summary>
/// Configuration options for persistence layer
/// </summary>
public record PersistenceConfiguration
{
    /// <summary>
    /// Type of persistence provider
    /// </summary>
    public PersistenceType Type { get; init; } = PersistenceType.SQLite;

    /// <summary>
    /// Path to the database file or connection string
    /// </summary>
    public required string StoragePath { get; init; }

    /// <summary>
    /// Whether to use WAL (Write-Ahead Logging) mode for SQLite
    /// </summary>
    public bool UseWAL { get; init; } = true;

    /// <summary>
    /// Whether to enable full-text search (FTS5 for SQLite)
    /// </summary>
    public bool EnableFullTextSearch { get; init; } = true;

    /// <summary>
    /// Cache size in pages (for SQLite)
    /// </summary>
    public int CacheSize { get; init; } = 10_000;

    /// <summary>
    /// Whether to automatically sync changes to persistence
    /// </summary>
    public bool AutoSync { get; init; } = true;

    /// <summary>
    /// Interval for auto-sync operations (if enabled)
    /// </summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Page size for the database (4096 is optimal for most SSDs)
    /// </summary>
    public int PageSize { get; init; } = 4096;

    /// <summary>
    /// Whether to use mmap for faster reads
    /// </summary>
    public bool UseMmap { get; init; } = true;

    /// <summary>
    /// Maximum mmap size in bytes (0 for default)
    /// </summary>
    public long MmapSize { get; init; } = 0;

    /// <summary>
    /// Creates a default SQLite configuration
    /// </summary>
    public static PersistenceConfiguration CreateSQLite(string databasePath) => new()
    {
        Type = PersistenceType.SQLite,
        StoragePath = databasePath,
        UseWAL = true,
        EnableFullTextSearch = true
    };

    /// <summary>
    /// Creates a high-performance SQLite configuration
    /// </summary>
    public static PersistenceConfiguration CreateSQLiteHighPerformance(string databasePath) => new()
    {
        Type = PersistenceType.SQLite,
        StoragePath = databasePath,
        UseWAL = true,
        EnableFullTextSearch = true,
        CacheSize = 50_000,
        UseMmap = true,
        MmapSize = 1024 * 1024 * 512 // 512MB
    };
}

/// <summary>
/// Type of persistence provider
/// </summary>
public enum PersistenceType
{
    /// <summary>
    /// SQLite database (recommended)
    /// </summary>
    SQLite,

    /// <summary>
    /// Custom persistence provider
    /// </summary>
    Custom
}
