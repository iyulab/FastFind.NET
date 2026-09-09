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
    /// Whether to use WAL (Write-Ahead Logging) mode for SQLite.
    /// </summary>
    /// <remarks>
    /// WAL is a persistent property of the database file. It also fixes the page size: once a
    /// database is in WAL mode, <see cref="PageSize"/> can no longer be changed.
    /// </remarks>
    public bool UseWAL { get; init; } = true;

    /// <summary>
    /// Whether to enable full-text search (FTS5 for SQLite)
    /// </summary>
    public bool EnableFullTextSearch { get; init; } = true;

    /// <summary>
    /// Page-cache size in <b>KiB</b> (for SQLite). The default 10,000 is ~9.8 MiB.
    /// </summary>
    /// <remarks>
    /// Emitted as <c>PRAGMA cache_size = -{value}</c>; SQLite reads the negative form as a size in
    /// KiB rather than a page count, so this is not multiplied by <see cref="PageSize"/>. The cache
    /// is native memory held per connection, so it does not bound managed allocations.
    /// </remarks>
    public int CacheSize { get; init; } = 10_000;

    /// <summary>
    /// Reserved. Not currently honoured by any provider.
    /// </summary>
    [Obsolete("AutoSync is not implemented: no provider reads it. Writes reach the store when the calling method completes.")]
    public bool AutoSync { get; init; } = true;

    /// <summary>
    /// Reserved. Not currently honoured by any provider.
    /// </summary>
    [Obsolete("SyncInterval is not implemented: no provider reads it.")]
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Database page size in bytes (4096 is optimal for most SSDs).
    /// </summary>
    /// <remarks>
    /// A persistent property of the database file, applied only when the file is created. It has no
    /// effect on an existing database — changing it there requires a <c>VACUUM</c>.
    /// </remarks>
    public int PageSize { get; init; } = 4096;

    /// <summary>
    /// Whether to memory-map the database for faster reads.
    /// </summary>
    /// <remarks>
    /// Mapped pages are file-backed and count towards the process working set as they are touched,
    /// but they are not managed heap. A large working set with mmap enabled is expected.
    /// </remarks>
    public bool UseMmap { get; init; } = true;

    /// <summary>
    /// Maximum mmap window in bytes. 0 means the provider default, which is <b>256 MiB</b> — not
    /// "no mapping"; set <see cref="UseMmap"/> to <c>false</c> for that.
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
