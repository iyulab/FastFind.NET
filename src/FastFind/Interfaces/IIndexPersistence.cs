using FastFind.Models;

namespace FastFind.Interfaces;

/// <summary>
/// Defines the contract for index persistence operations.
/// Implementations can use SQLite, LiteDB, or other storage backends.
/// </summary>
public interface IIndexPersistence : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the total number of indexed items
    /// </summary>
    long Count { get; }

    /// <summary>
    /// Gets whether the persistence layer is initialized and ready
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets the storage path or connection string
    /// </summary>
    string StoragePath { get; }

    /// <summary>
    /// Initializes the persistence layer with the given configuration
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the initialization operation</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a file item to the persistent storage
    /// </summary>
    /// <param name="item">File item to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the add operation</returns>
    Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds multiple file items to the persistent storage in batch
    /// </summary>
    /// <param name="items">File items to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of items added</returns>
    Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a file from the persistent storage by its full path
    /// </summary>
    /// <param name="fullPath">Full path of the file to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the item was removed, false if not found</returns>
    Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes multiple files from the persistent storage
    /// </summary>
    /// <param name="fullPaths">Full paths of files to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of items removed</returns>
    Task<int> RemoveBatchAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing file in the persistent storage
    /// </summary>
    /// <param name="item">Updated file item</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the item was updated, false if not found</returns>
    Task<bool> UpdateAsync(FastFileItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a file item by its full path
    /// </summary>
    /// <param name="fullPath">Full path of the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File item if found, null otherwise</returns>
    Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file exists in the persistent storage
    /// </summary>
    /// <param name="fullPath">Full path of the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the file exists in storage</returns>
    Task<bool> ExistsAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for files matching the given query
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of matching files</returns>
    IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files in a specific directory
    /// </summary>
    /// <param name="directoryPath">Directory path</param>
    /// <param name="recursive">Whether to include subdirectories</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of files in the directory</returns>
    IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files with a specific extension
    /// </summary>
    /// <param name="extension">File extension (e.g., ".txt")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of files with the extension</returns>
    IAsyncEnumerable<FastFileItem> GetByExtensionAsync(string extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all items from the persistent storage
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the clear operation</returns>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimizes the persistent storage for better performance
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the optimization operation</returns>
    Task OptimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets storage statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Persistence statistics</returns>
    Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a transaction for batch operations
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Transaction handle</returns>
    Task<IIndexTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Vacuums the storage to reclaim space
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the vacuum operation</returns>
    Task VacuumAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a transaction for batch index operations
/// </summary>
public interface IIndexTransaction : IAsyncDisposable
{
    /// <summary>
    /// Commits the transaction
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the transaction
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about the persistent storage
/// </summary>
public record PersistenceStatistics
{
    /// <summary>
    /// Total number of indexed items
    /// </summary>
    public required long TotalItems { get; init; }

    /// <summary>
    /// Total number of directories
    /// </summary>
    public required long TotalDirectories { get; init; }

    /// <summary>
    /// Total number of files
    /// </summary>
    public required long TotalFiles { get; init; }

    /// <summary>
    /// Storage size in bytes
    /// </summary>
    public required long StorageSizeBytes { get; init; }

    /// <summary>
    /// Last optimization time
    /// </summary>
    public DateTime? LastOptimized { get; init; }

    /// <summary>
    /// Last vacuum time
    /// </summary>
    public DateTime? LastVacuumed { get; init; }

    /// <summary>
    /// Average query time in milliseconds
    /// </summary>
    public double? AverageQueryTimeMs { get; init; }

    /// <summary>
    /// Number of unique extensions
    /// </summary>
    public int UniqueExtensions { get; init; }
}
