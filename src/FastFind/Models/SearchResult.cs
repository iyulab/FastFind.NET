namespace FastFind.Models;

/// <summary>
/// Represents the result of a search operation with comprehensive metadata
/// </summary>
public record SearchResult
{
    /// <summary>
    /// The original search query
    /// </summary>
    public required SearchQuery Query { get; init; }

    /// <summary>
    /// Total number of files that match the search criteria
    /// </summary>
    public long TotalMatches { get; init; }

    /// <summary>
    /// Number of results actually returned (may be less than TotalMatches due to limits)
    /// </summary>
    public long ResultCount { get; init; }

    /// <summary>
    /// Time taken to perform the search
    /// </summary>
    public TimeSpan SearchTime { get; init; }

    /// <summary>
    /// Whether the search completed successfully
    /// </summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>
    /// Whether there are more results available beyond the returned set
    /// </summary>
    public bool HasMoreResults { get; init; }

    /// <summary>
    /// Async enumerable of matching files
    /// </summary>
    public IAsyncEnumerable<FastFileItem> Files { get; init; } = AsyncEnumerable.Empty<FastFileItem>();

    /// <summary>
    /// Performance metrics for the search operation
    /// </summary>
    public SearchMetrics? Metrics { get; init; }

    /// <summary>
    /// Error message if the search failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the search encountered an error
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Creates a successful search result
    /// </summary>
    public static SearchResult Success(
        SearchQuery query,
        long totalMatches,
        long resultCount,
        TimeSpan searchTime,
        IAsyncEnumerable<FastFileItem> files,
        SearchMetrics? metrics = null,
        bool hasMoreResults = false)
    {
        return new SearchResult
        {
            Query = query,
            TotalMatches = totalMatches,
            ResultCount = resultCount,
            SearchTime = searchTime,
            IsComplete = true,
            HasMoreResults = hasMoreResults,
            Files = files,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Creates a partial search result (for real-time updates)
    /// </summary>
    public static SearchResult Partial(
        SearchQuery query,
        long totalMatches,
        long resultCount,
        TimeSpan searchTime,
        IAsyncEnumerable<FastFileItem> files,
        SearchMetrics? metrics = null)
    {
        return new SearchResult
        {
            Query = query,
            TotalMatches = totalMatches,
            ResultCount = resultCount,
            SearchTime = searchTime,
            IsComplete = false,
            HasMoreResults = true,
            Files = files,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Creates an empty search result
    /// </summary>
    public static SearchResult Empty(SearchQuery query, TimeSpan searchTime, string? errorMessage = null)
    {
        return new SearchResult
        {
            Query = query,
            TotalMatches = 0,
            ResultCount = 0,
            SearchTime = searchTime,
            IsComplete = true,
            HasMoreResults = false,
            Files = AsyncEnumerable.Empty<FastFileItem>(),
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Creates a failed search result
    /// </summary>
    public static SearchResult Failed(SearchQuery query, TimeSpan searchTime, string errorMessage)
    {
        return new SearchResult
        {
            Query = query,
            TotalMatches = 0,
            ResultCount = 0,
            SearchTime = searchTime,
            IsComplete = true,
            HasMoreResults = false,
            Files = AsyncEnumerable.Empty<FastFileItem>(),
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Performance metrics for search operations
/// </summary>
public record SearchMetrics
{
    /// <summary>
    /// Number of files processed during the search
    /// </summary>
    public long FilesProcessed { get; init; }

    /// <summary>
    /// Number of files that passed initial filters
    /// </summary>
    public long FilesFiltered { get; init; }

    /// <summary>
    /// Time spent on text matching
    /// </summary>
    public TimeSpan TextMatchingTime { get; init; }

    /// <summary>
    /// Time spent on applying filters
    /// </summary>
    public TimeSpan FilteringTime { get; init; }

    /// <summary>
    /// Time spent on I/O operations
    /// </summary>
    public TimeSpan IoTime { get; init; }

    /// <summary>
    /// Memory usage during search (bytes)
    /// </summary>
    public long MemoryUsage { get; init; }

    /// <summary>
    /// Whether the search index was used
    /// </summary>
    public bool UsedIndex { get; init; } = true;

    /// <summary>
    /// Index hit rate (percentage of files found in index vs. file system scan)
    /// </summary>
    public double IndexHitRate { get; init; }

    /// <summary>
    /// Number of cache hits during the search
    /// </summary>
    public long CacheHits { get; init; }

    /// <summary>
    /// Number of cache misses during the search
    /// </summary>
    public long CacheMisses { get; init; }

    /// <summary>
    /// Files processed per second
    /// </summary>
    public double FilesPerSecond => FilesProcessed / Math.Max(TextMatchingTime.TotalSeconds, 0.001);

    /// <summary>
    /// Cache hit rate as a percentage
    /// </summary>
    public double CacheHitRate => (CacheHits + CacheMisses) > 0 ? 
        (double)CacheHits / (CacheHits + CacheMisses) * 100 : 0;

    /// <summary>
    /// Overall efficiency score (0-100)
    /// </summary>
    public double EfficiencyScore => Math.Min(100, 
        (IndexHitRate * 0.4) + (CacheHitRate * 0.3) + (Math.Min(FilesPerSecond / 1000, 1) * 100 * 0.3));
}