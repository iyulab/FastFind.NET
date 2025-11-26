using FastFind.Models;

namespace FastFind.Interfaces;

/// <summary>
/// Interface for high-performance in-memory search index operations.
/// This interface focuses on search operations and delegates persistence to IIndexPersistence.
/// </summary>
public interface ISearchIndex : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the total number of indexed items
    /// </summary>
    long Count { get; }

    /// <summary>
    /// Gets the memory usage of the index in bytes
    /// </summary>
    long MemoryUsage { get; }

    /// <summary>
    /// Gets whether the index is ready for searching
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets the optional persistence layer for this index
    /// </summary>
    IIndexPersistence? Persistence { get; }

    /// <summary>
    /// Adds a file item to the index
    /// </summary>
    /// <param name="item">File item to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the add operation</returns>
    Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds multiple file items to the index in batch
    /// </summary>
    /// <param name="items">File items to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of items added</returns>
    Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a file from the index
    /// </summary>
    /// <param name="fullPath">Full path of the file to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if item was removed, false if not found</returns>
    Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing file in the index
    /// </summary>
    /// <param name="item">Updated file item</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if item was updated, false if not found</returns>
    Task<bool> UpdateAsync(FastFileItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the index with the specified query using SIMD-accelerated matching
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of matching files</returns>
    IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a file item by its full path
    /// </summary>
    /// <param name="fullPath">Full path of the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File item if found, null otherwise</returns>
    Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file exists in the index
    /// </summary>
    /// <param name="fullPath">Full path of the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the file exists in the index</returns>
    Task<bool> ContainsAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files in a specific directory
    /// </summary>
    /// <param name="directoryPath">Directory path</param>
    /// <param name="recursive">Whether to include subdirectories</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of files in the directory</returns>
    IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all items from the index
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the clear operation</returns>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimizes the index for better performance
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the optimization operation</returns>
    Task OptimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets index statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Index statistics</returns>
    Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads index data from the persistence layer
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of items loaded</returns>
    Task<int> LoadFromPersistenceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves current index data to the persistence layer
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of items saved</returns>
    Task<int> SaveToPersistenceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts monitoring for changes in the specified locations
    /// </summary>
    /// <param name="locations">Locations to monitor</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the monitoring start operation</returns>
    Task StartMonitoringAsync(IEnumerable<string> locations, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops monitoring for file system changes
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the monitoring stop operation</returns>
    Task StopMonitoringAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about the search index
/// </summary>
public record IndexStatistics
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
    /// Memory usage in bytes
    /// </summary>
    public required long MemoryUsageBytes { get; init; }

    /// <summary>
    /// Whether persistence is enabled
    /// </summary>
    public bool PersistenceEnabled { get; init; }

    /// <summary>
    /// Last index update time
    /// </summary>
    public DateTime? LastUpdated { get; init; }

    /// <summary>
    /// Number of unique extensions
    /// </summary>
    public int UniqueExtensions { get; init; }

    /// <summary>
    /// Average search time in milliseconds
    /// </summary>
    public double? AverageSearchTimeMs { get; init; }
}
