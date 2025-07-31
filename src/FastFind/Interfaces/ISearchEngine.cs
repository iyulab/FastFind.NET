using FastFind.Models;

namespace FastFind.Interfaces;

/// <summary>
/// Main interface for the FastFind search engine
/// </summary>
public interface ISearchEngine : IDisposable
{
    /// <summary>
    /// Event raised when indexing progress changes
    /// </summary>
    event EventHandler<IndexingProgressEventArgs>? IndexingProgressChanged;

    /// <summary>
    /// Event raised when a file system change is detected
    /// </summary>
    event EventHandler<FileChangeEventArgs>? FileChanged;

    /// <summary>
    /// Event raised when search progress updates
    /// </summary>
    event EventHandler<SearchProgressEventArgs>? SearchProgressChanged;

    /// <summary>
    /// Gets whether the search engine is currently indexing
    /// </summary>
    bool IsIndexing { get; }

    /// <summary>
    /// Gets whether the search engine is monitoring for file changes
    /// </summary>
    bool IsMonitoring { get; }

    /// <summary>
    /// Gets the total number of indexed files
    /// </summary>
    long TotalIndexedFiles { get; }

    /// <summary>
    /// Starts indexing with the specified options
    /// </summary>
    /// <param name="options">Indexing configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the indexing operation</returns>
    Task StartIndexingAsync(IndexingOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the indexing process
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the stop operation</returns>
    Task StopIndexingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a search with the specified query
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search results</returns>
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a simple text search
    /// </summary>
    /// <param name="searchText">Text to search for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search results</returns>
    Task<SearchResult> SearchAsync(string searchText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a real-time search that yields results as they are found
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of search results</returns>
    IAsyncEnumerable<SearchResult> SearchRealTimeAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets indexing statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Indexing statistics</returns>
    Task<IndexingStatistics> GetIndexingStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets search performance statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search statistics</returns>
    Task<SearchStatistics> GetSearchStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached data and statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the clear operation</returns>
    Task ClearCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the index to persistent storage
    /// </summary>
    /// <param name="filePath">Path to save the index (null for default location)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the save operation</returns>
    Task SaveIndexAsync(string? filePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the index from persistent storage
    /// </summary>
    /// <param name="filePath">Path to load the index from (null for default location)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the load operation</returns>
    Task LoadIndexAsync(string? filePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimizes the index for better performance
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the optimization operation</returns>
    Task OptimizeIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the index for specific locations
    /// </summary>
    /// <param name="locations">Locations to refresh (null for all locations)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the refresh operation</returns>
    Task RefreshIndexAsync(IEnumerable<string>? locations = null, CancellationToken cancellationToken = default);
}