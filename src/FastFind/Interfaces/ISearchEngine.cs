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
    /// The index this engine searches, or <c>null</c> when the engine holds its index internally
    /// and does not expose one.
    /// </summary>
    /// <remarks>
    /// Exposed so that a caller who composed the engine with an index — or with an
    /// <see cref="IIndexPersistence"/> that one was built over — can reach it, and so that
    /// <see cref="ISearchIndex.Persistence"/> is discoverable at all. Prefer the engine's own
    /// search and indexing methods for ordinary use.
    /// </remarks>
    ISearchIndex? Index { get; }

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
    /// Writes the current index to the store this engine was composed with.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of items written.</returns>
    /// <remarks>
    /// Where the index is stored is decided when the engine is created — see
    /// <c>FastFinder.CreateSearchEngine(IIndexPersistence, …)</c>. This method took a
    /// <c>filePath</c> until composition existed; the parameter was never read by any
    /// implementation, and a per-call destination would now contradict the one the engine was built
    /// with.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The engine has no persistence store. Compose it with one.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// This engine cannot persist an index at all, whatever it is composed with.
    /// </exception>
    Task<int> SaveIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the index from the store this engine was composed with.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of items available after loading.</returns>
    /// <remarks>
    /// For an index that answers from the store there is nothing to transfer, and the stored count
    /// is returned unchanged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The engine has no persistence store. Compose it with one.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// This engine cannot persist an index at all, whatever it is composed with.
    /// </exception>
    Task<int> LoadIndexAsync(CancellationToken cancellationToken = default);

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