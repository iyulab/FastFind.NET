using FastFind.Models;

namespace FastFind.Interfaces;

/// <summary>
/// Interface for high-performance search index operations
/// </summary>
public interface ISearchIndex : IDisposable
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
    /// Adds a file item to the index
    /// </summary>
    /// <param name="fileItem">File item to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the add operation</returns>
    Task AddFileAsync(FileItem fileItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds multiple file items to the index in batch
    /// </summary>
    /// <param name="fileItems">File items to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the add operation</returns>
    Task AddFilesAsync(IEnumerable<FileItem> fileItems, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a file from the index
    /// </summary>
    /// <param name="filePath">Full path of the file to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the remove operation</returns>
    Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing file in the index
    /// </summary>
    /// <param name="fileItem">Updated file item</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the update operation</returns>
    Task UpdateFileAsync(FileItem fileItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the index with the specified query
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of matching files</returns>
    IAsyncEnumerable<FileItem> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a file item by its full path
    /// </summary>
    /// <param name="filePath">Full path of the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File item if found, null otherwise</returns>
    Task<FileItem?> GetFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file exists in the index
    /// </summary>
    /// <param name="filePath">Full path of the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the file exists in the index</returns>
    Task<bool> ContainsAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files in a specific directory
    /// </summary>
    /// <param name="directoryPath">Directory path</param>
    /// <param name="recursive">Whether to include subdirectories</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of files in the directory</returns>
    IAsyncEnumerable<FileItem> GetFilesInDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken cancellationToken = default);

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
    Task<IndexingStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the index to a stream
    /// </summary>
    /// <param name="stream">Stream to save to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the save operation</returns>
    Task SaveToStreamAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the index from a stream
    /// </summary>
    /// <param name="stream">Stream to load from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the load operation</returns>
    Task LoadFromStreamAsync(Stream stream, CancellationToken cancellationToken = default);

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