using FastFind.Indexing;
using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using System.Security;
using System.Threading.Channels;

namespace FastFind.Windows.Implementation;

/// <summary>
/// Thread-safe hash set for concurrent operations
/// </summary>
internal class ConcurrentHashSet<T> where T : notnull
{
    private readonly HashSet<T> _set = new();
    private readonly object _lock = new();

    public bool Add(T item)
    {
        lock (_lock)
        {
            return _set.Add(item);
        }
    }

    public bool Contains(T item)
    {
        lock (_lock)
        {
            return _set.Contains(item);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _set.Count;
            }
        }
    }
}

/// <summary>
/// High-performance Windows-optimized search index implementation
/// </summary>
[SupportedOSPlatform("windows")]
internal class WindowsSearchIndex : ISearchIndex
{
    private readonly ILogger<WindowsSearchIndex> _logger;

    /// <summary>
    /// Every indexed entry as a <see cref="FastFileItem"/>, grouped by directory. This used to be a
    /// map of <see cref="FileItem"/> objects keyed by a lower-cased copy of each path, beside a
    /// directory map and an extension map holding a second lower-cased copy, and a trie with a node
    /// per file — about 2,760 bytes an entry, the trie alone more than half of it.
    /// </summary>
    private readonly FileEntryTable _entries = new();

    private long _memoryUsage = 0;
    private bool _isReady = false;
    private bool _disposed = false;
    private IIndexPersistence? _persistence;

    public WindowsSearchIndex(ILogger<WindowsSearchIndex> logger, IIndexPersistence? persistence = null)
    {
        _logger = logger;
        _persistence = persistence;
        _isReady = true;
    }

    /// <inheritdoc/>
    public long Count => _entries.Count;

    /// <inheritdoc/>
    public long MemoryUsage => Interlocked.Read(ref _memoryUsage);

    /// <inheritdoc/>
    public bool IsReady => _isReady && !_disposed;

    /// <inheritdoc/>
    public IIndexPersistence? Persistence => _persistence;

    /// <inheritdoc/>
    public async Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_entries.TryAdd(item))
        {
            UpdateMemoryUsage(item, IndexOperation.Add);
        }

        // Persist if enabled
        if (_persistence != null)
        {
            await _persistence.AddAsync(item, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (cancellationToken.IsCancellationRequested)
            return 0;

        var added = await Task.Run(() =>
        {
            var accepted = new List<FastFileItem>();
            foreach (var item in items)
            {
                if ((accepted.Count & 0xFFF) == 0 && cancellationToken.IsCancellationRequested)
                    break;

                if (_entries.TryAdd(item))
                {
                    UpdateMemoryUsage(item, IndexOperation.Add);
                    accepted.Add(item);
                }
            }

            _logger.LogDebug("Added {AddedCount} files to index in single batch", accepted.Count);
            return accepted;
        }, CancellationToken.None);

        // Persist what was added. This used to persist the batch's first `addedCount` items, which
        // are not the added ones whenever the batch held an entry the index already had.
        if (_persistence != null && added.Count > 0)
        {
            await _persistence.AddBatchAsync(added, cancellationToken);
        }

        return added.Count;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var removed = _entries.TryRemove(fullPath, out var removedItem);
        if (removed)
        {
            UpdateMemoryUsage(removedItem, IndexOperation.Remove);
        }

        // Persist if enabled
        if (_persistence != null && removed)
        {
            await _persistence.RemoveAsync(fullPath, cancellationToken);
        }

        return removed;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var updated = _entries.Set(item, out var previous);
        if (updated)
        {
            UpdateMemoryUsage(previous, IndexOperation.Remove);
        }
        UpdateMemoryUsage(item, IndexOperation.Add);

        // Persist if enabled
        if (_persistence != null)
        {
            await _persistence.UpdateAsync(item, cancellationToken);
        }

        return updated;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FastFileItem> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var validation = query.Validate();
        if (!validation.IsValid)
        {
            // Was a warning and an empty result, which let a malformed query look like a query that
            // simply matched nothing.
            throw new ArgumentException(validation.ErrorMessage ?? "Invalid search query.", nameof(query));
        }

        // Use hybrid search approach for optimal performance and completeness
        await foreach (var result in SearchHybridAsync(query, cancellationToken))
        {
            yield return result;
        }
    }

    /// <summary>
    /// Hybrid search combining indexed results with live filesystem scanning
    /// for optimal performance and complete results regardless of indexing state
    /// </summary>
    private async IAsyncEnumerable<FastFileItem> SearchHybridAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Compiled once per search and passed down; the evaluator decides regex vs. substring.
        var regex = SearchQueryEvaluator.CreateTextMatcher(query);
        var matchCount = 0;
        var returnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Search indexed results (fast path)
        // Phase 3.1: Lock-free reads - ConcurrentDictionary provides thread-safe enumeration
        // Phase 3.2: Stream results immediately instead of batching
        var indexedCandidates = GetSearchCandidatesSync(query);
        _logger.LogDebug("Hybrid search: Streaming indexed candidates");

        foreach (var candidate in indexedCandidates)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            if (query.MaxResults.HasValue && matchCount >= query.MaxResults.Value)
                break;

            if (MatchesQuery(candidate, query, regex))
            {
                returnedPaths.Add(candidate.FullPath);
                matchCount++;
                
                // Phase 3.2: Yield immediately for first-result latency reduction
                yield return candidate;

                // Yield control periodically for better responsiveness
                if (matchCount % 50 == 0)
                    await Task.Yield();
            }
        }

        // Phase 2: Fill gaps with live filesystem search (for incomplete indexing)
        if (ShouldPerformFilesystemFallback(query, matchCount))
        {
            _logger.LogDebug("Hybrid search: Performing filesystem fallback, current matches: {Count}", matchCount);

            await foreach (var fsResult in SearchFilesystemAsync(query, returnedPaths, regex, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                if (query.MaxResults.HasValue && matchCount >= query.MaxResults.Value)
                    yield break;

                matchCount++;
                yield return fsResult.ToFastFileItem();

                // Yield control periodically for better responsiveness
                if (matchCount % 25 == 0)
                    await Task.Yield();
            }
        }

        _logger.LogDebug("Hybrid search completed: {TotalMatches} matches", matchCount);
    }

    /// <summary>
    /// Determines if filesystem fallback search should be performed.
    /// Optimized to avoid unnecessary filesystem scans when index has adequate coverage.
    /// </summary>
    private bool ShouldPerformFilesystemFallback(SearchQuery query, int indexedMatches)
    {
        // Always perform fallback if index is very small (suggests no or incomplete indexing)
        if (Count < 10)
        {
            if (Count == 0)
            {
                _logger.LogWarning(
                    "Search index is empty. Did you call StartIndexingAsync() before searching? " +
                    "Falling back to filesystem scanning which may be slower.");
            }
            else
            {
                _logger.LogDebug(
                    "Search index contains only {Count} items. Consider running StartIndexingAsync() for better performance.",
                    Count);
            }
            return true;
        }

        // For BasePath queries: check if the path is covered by the index
        if (!string.IsNullOrEmpty(query.BasePath))
        {
            if (IsPathCoveredByIndex(query.BasePath))
            {
                _logger.LogDebug("BasePath {Path} is covered by index, skipping filesystem fallback", query.BasePath);
                return false;  // Index has this path - no need for filesystem scan
            }
            // Path not in index, need filesystem fallback
            return true;
        }

        // For SearchLocations queries: check if all locations are covered
        if (query.SearchLocations.Count > 0)
        {
            var allCovered = query.SearchLocations.All(loc => IsPathCoveredByIndex(loc));
            if (allCovered)
            {
                _logger.LogDebug("All SearchLocations are covered by index, skipping filesystem fallback");
                return false;  // All locations indexed - no need for filesystem scan
            }
            // Some locations not in index, need filesystem fallback
            return true;
        }

        // Perform fallback if we found very few results from index
        if (indexedMatches < 5)
            return true;

        // For broad searches with good index coverage, trust the index
        return false;
    }

    /// <summary>
    /// Checks if a path is covered by the index (has indexed files under it).
    /// </summary>
    private bool IsPathCoveredByIndex(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        return _entries.Covers(path);
    }

    /// <summary>
    /// Performs high-performance parallel filesystem search to complement indexed results
    /// </summary>
    private async IAsyncEnumerable<FileItem> SearchFilesystemAsync(
        SearchQuery query,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var searchPaths = GetFilesystemSearchPaths(query);

        if (query.IncludeSubdirectories)
        {
            // Use parallel scanning for subdirectory search (maximum performance)
            await foreach (var fileItem in ScanDirectoriesParallelAsync(searchPaths, query, excludePaths, regex, cancellationToken))
            {
                yield return fileItem;
            }
        }
        else
        {
            // Use sequential scanning for single-directory search (simpler)
            var searchOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                BufferSize = 8192,
                AttributesToSkip = query.IncludeHidden ? FileAttributes.None : FileAttributes.Hidden
            };

            foreach (var searchPath in searchPaths)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                if (!Directory.Exists(searchPath))
                    continue;

                await foreach (var fileItem in ScanDirectoryAsync(searchPath, query, searchOptions, excludePaths, regex, cancellationToken))
                {
                    yield return fileItem;
                }
            }
        }
    }

    /// <summary>
    /// High-performance parallel directory scanning with optimal thread utilization
    /// </summary>
    private async IAsyncEnumerable<FileItem> ScanDirectoriesParallelAsync(
        List<string> searchPaths,
        SearchQuery query,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Concurrent collections for thread-safe operations
        var processedPaths = new ConcurrentHashSet<string>();
        var resultChannel = Channel.CreateUnbounded<FileItem>();
        var writer = resultChannel.Writer;

        // Calculate optimal concurrency based on system resources
        var maxConcurrency = CalculateOptimalConcurrency();
        var semaphore = new SemaphoreSlim(maxConcurrency);

        _logger.LogDebug("Starting parallel filesystem search with {Concurrency} threads", maxConcurrency);

        // Start parallel scanning tasks
        var scanningTask = Task.Run(async () =>
        {
            try
            {
                await ScanPathsInParallel(searchPaths, query, excludePaths, regex,
                    processedPaths, writer, semaphore, cancellationToken);
            }
            finally
            {
                writer.Complete();
                semaphore.Dispose();
            }
        }, cancellationToken);

        // Stream results as they become available
        await foreach (var result in resultChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return result;
        }

        // Wait for scanning to complete
        await scanningTask;
    }

    /// <summary>
    /// Parallel path scanning with dynamic work distribution
    /// </summary>
    private async Task ScanPathsInParallel(
        List<string> searchPaths,
        SearchQuery query,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        ConcurrentHashSet<string> processedPaths,
        ChannelWriter<FileItem> writer,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var directoryQueue = new ConcurrentQueue<string>();

        // Initialize with root paths
        foreach (var path in searchPaths)
        {
            if (Directory.Exists(path))
            {
                directoryQueue.Enqueue(path);
            }
        }

        // Parallel worker tasks
        var workers = new Task[CalculateOptimalConcurrency()];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = ProcessDirectoriesWorker(directoryQueue, query, excludePaths, regex,
                processedPaths, writer, semaphore, cancellationToken);
        }

        await Task.WhenAll(workers);
    }

    /// <summary>
    /// Individual worker thread for processing directories
    /// </summary>
    private async Task ProcessDirectoriesWorker(
        ConcurrentQueue<string> directoryQueue,
        SearchQuery query,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        ConcurrentHashSet<string> processedPaths,
        ChannelWriter<FileItem> writer,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;
        var emptyQueueCount = 0;
        const int maxEmptyQueueChecks = 10;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!directoryQueue.TryDequeue(out var currentDirectory))
            {
                emptyQueueCount++;

                // If we've found no work for a while, exit to avoid hanging
                if (emptyQueueCount >= maxEmptyQueueChecks)
                {
                    _logger.LogDebug("Worker exiting after {EmptyChecks} empty queue checks, processed {Count} directories",
                        emptyQueueCount, processedCount);
                    break;
                }

                await Task.Delay(10, cancellationToken);
                continue;
            }

            emptyQueueCount = 0; // Reset counter when we find work

            // Skip if already processed
            if (!processedPaths.Add(currentDirectory))
                continue;

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await ProcessSingleDirectoryParallel(currentDirectory, directoryQueue, query, excludePaths,
                    regex, writer, cancellationToken);

                processedCount++;

                // Yield control periodically
                if (processedCount % 10 == 0)
                    await Task.Yield();
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>
    /// Process a single directory and add subdirectories to queue
    /// </summary>
    private async Task ProcessSingleDirectoryParallel(
        string directoryPath,
        ConcurrentQueue<string> directoryQueue,
        SearchQuery query,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false, // We handle recursion manually for parallel processing
                IgnoreInaccessible = true,
                BufferSize = 8192,
                AttributesToSkip = query.IncludeHidden ? FileAttributes.None : FileAttributes.Hidden
            };

            // Get all entries in this directory
            var entries = Directory.EnumerateFileSystemEntries(directoryPath, "*", options);

            await Task.Run(() =>
            {
                foreach (var fullPath in entries)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    // Skip if already returned from index
                    lock (excludePaths)
                    {
                        if (excludePaths.Contains(fullPath))
                            continue;
                    }

                    // Skip excluded paths, by the same definition the evaluator applies
                    if (PathExclusion.IsExcluded(fullPath, query.ExcludedPaths))
                        continue;

                    var fileItem = GetFileItemSafely(fullPath);
                    if (fileItem == null)
                        continue;

                    // If it's a directory, add to queue for processing
                    if (fileItem.IsDirectory && query.IncludeSubdirectories)
                    {
                        directoryQueue.Enqueue(fullPath);
                    }

                    // Check if it matches our search criteria
                    if (MatchesQuery(fileItem, query, regex))
                    {
                        lock (excludePaths)
                        {
                            if (!excludePaths.Contains(fullPath))
                            {
                                excludePaths.Add(fullPath);

                                // Write to channel — TryWrite is non-blocking; fallback uses blocking
                                // wait which is safe here (inside Task.Run, no SynchronizationContext)
                                if (!writer.TryWrite(fileItem))
                                {
                                    writer.WriteAsync(fileItem, cancellationToken).AsTask().Wait(cancellationToken);
                                }
                            }
                        }
                    }
                }
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _logger.LogDebug("Cannot access directory for parallel search: {Path}", directoryPath);
        }
    }

    /// <summary>
    /// Calculate optimal concurrency based on system resources
    /// </summary>
    private static int CalculateOptimalConcurrency()
    {
        var logicalCores = Environment.ProcessorCount;

        // For I/O bound operations like file system scanning, we can use more threads than CPU cores
        // Optimal range: 2-4x logical cores, but cap at reasonable limits
        var optimalConcurrency = Math.Min(logicalCores * 3, 32); // Cap at 32 threads

        // Ensure minimum of 4 threads for responsiveness
        return Math.Max(optimalConcurrency, 4);
    }

    /// <summary>
    /// Gets the filesystem paths to search based on query
    /// </summary>
    private List<string> GetFilesystemSearchPaths(SearchQuery query)
    {
        var paths = new List<string>();

        if (!string.IsNullOrEmpty(query.BasePath))
        {
            paths.Add(query.BasePath);
        }
        else if (query.SearchLocations.Count > 0)
        {
            paths.AddRange(query.SearchLocations);
        }
        else
        {
            // Fallback to indexed directories if no specific paths
            paths.AddRange(_entries.Directories.Take(5)); // Limit to avoid excessive scanning
        }

        return paths;
    }

    /// <summary>
    /// Scans a directory for files matching the query
    /// </summary>
    private async IAsyncEnumerable<FileItem> ScanDirectoryAsync(
        string directoryPath,
        SearchQuery query,
        EnumerationOptions options,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var processedCount = 0;

        IEnumerable<string> enumerable;

        try
        {
            enumerable = Directory.EnumerateFileSystemEntries(directoryPath, "*", options);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _logger.LogDebug("Cannot access directory for filesystem search: {Path}", directoryPath);
            yield break;
        }

        foreach (var fullPath in enumerable)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            // Skip if already returned from index
            if (excludePaths.Contains(fullPath))
                continue;

            // Skip excluded paths, by the same definition the evaluator applies
            if (PathExclusion.IsExcluded(fullPath, query.ExcludedPaths))
                continue;

            FileItem? fileItem = GetFileItemSafely(fullPath);

            if (fileItem != null && MatchesQuery(fileItem, query, regex))
            {
                excludePaths.Add(fullPath); // Prevent future duplicates
                yield return fileItem;
            }

            // Yield control periodically for better responsiveness
            if (++processedCount % 100 == 0)
                await Task.Yield();
        }
    }

    /// <summary>
    /// Safely creates a FileItem from a file path, handling exceptions
    /// </summary>
    private static FileItem? GetFileItemSafely(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Exists)
            {
                return CreateFileItemFromInfo(info);
            }

            var dirInfo = new DirectoryInfo(fullPath);
            if (dirInfo.Exists)
            {
                return CreateFileItemFromInfo(dirInfo);
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Skip inaccessible files/directories
            return null;
        }
    }

    /// <summary>
    /// Creates a FileItem from FileSystemInfo
    /// </summary>
    private static FileItem CreateFileItemFromInfo(FileSystemInfo info)
    {
        var isDirectory = info is DirectoryInfo;
        var size = isDirectory ? 0L : ((FileInfo)info).Length;
        var directoryPath = isDirectory ?
            Path.GetDirectoryName(info.FullName) :
            (info as FileInfo)?.DirectoryName ?? Path.GetDirectoryName(info.FullName);

        return new FileItem
        {
            FullPath = info.FullName,
            Name = info.Name,
            DirectoryPath = directoryPath ?? "",
            Extension = isDirectory ? "" : info.Extension,
            Size = size,
            CreatedTime = info.CreationTime,
            ModifiedTime = info.LastWriteTime,
            AccessedTime = info.LastAccessTime,
            Attributes = info.Attributes,
            DriveLetter = info.FullName.Length > 0 ? info.FullName[0] : 'C'
        };
    }

    /// <summary>
    /// The entries a query could match, narrowed by its scope. Everything else — extension, text,
    /// size, dates — is decided by <see cref="SearchQueryEvaluator"/> on each candidate.
    /// </summary>
    private IEnumerable<FastFileItem> GetSearchCandidatesSync(SearchQuery query)
    {
        // BasePath takes precedence over SearchLocations. One scope narrows; several may overlap —
        // C:\a and C:\a\b — and narrowing each would return an entry once per scope holding it, so
        // they take one pass and the evaluator decides.
        var scope = !string.IsNullOrEmpty(query.BasePath) ? query.BasePath
            : query.SearchLocations.Count == 1 ? query.SearchLocations[0]
            : null;

        if (scope is null) return _entries.All();
        return query.IncludeSubdirectories ? _entries.Under(scope) : _entries.InDirectory(scope);
    }

    /// <inheritdoc/>
    public Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_entries.TryGet(fullPath, out var item) ? item : (FastFileItem?)null);
    }

    /// <inheritdoc/>
    public Task<bool> ContainsAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_entries.Contains(fullPath));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(
        string directoryPath,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var entries = recursive ? _entries.Under(directoryPath) : _entries.InDirectory(directoryPath);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }

        await Task.Yield();
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        _entries.Clear();
        Interlocked.Exchange(ref _memoryUsage, 0);
        _logger.LogInformation("Search index cleared");

        // Clear persistence if enabled
        if (_persistence != null)
        {
            await _persistence.ClearAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await Task.Run(() =>
        {
            // Force garbage collection to reclaim memory
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            RecalculateMemoryUsage();

            _logger.LogInformation("Search index optimized. Memory usage: {MemoryMB} MB",
                MemoryUsage / (1024.0 * 1024.0));
        }, cancellationToken);

        // Optimize persistence if enabled
        if (_persistence != null)
        {
            await _persistence.OptimizeAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await Task.Run(() =>
        {
            long total = 0, directories = 0;
            var extensions = new HashSet<int>();
            foreach (var item in _entries.All())
            {
                total++;
                if (item.IsDirectory) directories++;
                else if (!string.IsNullOrEmpty(item.Extension)) extensions.Add(item.ExtensionId);
            }

            return new IndexStatistics
            {
                TotalItems = total,
                TotalFiles = total - directories,
                TotalDirectories = directories,
                MemoryUsageBytes = MemoryUsage,
                PersistenceEnabled = _persistence != null,
                LastUpdated = DateTime.UtcNow,
                UniqueExtensions = extensions.Count
            };
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> LoadFromPersistenceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_persistence == null)
        {
            _logger.LogWarning("No persistence layer configured");
            return 0;
        }

        var loadedCount = 0;
        var query = new SearchQuery(); // Empty query to get all items

        await foreach (var item in _persistence.SearchAsync(query, cancellationToken))
        {
            if (_entries.TryAdd(item))
            {
                UpdateMemoryUsage(item, IndexOperation.Add);
                loadedCount++;
            }
        }

        _logger.LogInformation("Loaded {Count} items from persistence", loadedCount);
        return loadedCount;
    }

    /// <inheritdoc/>
    public async Task<int> SaveToPersistenceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_persistence == null)
        {
            _logger.LogWarning("No persistence layer configured");
            return 0;
        }

        var items = _entries.All().ToList();

        var savedCount = await _persistence.AddBatchAsync(items, cancellationToken);
        _logger.LogInformation("Saved {Count} items to persistence", savedCount);
        return savedCount;
    }

    /// <inheritdoc/>
    public Task StartMonitoringAsync(IEnumerable<string> locations, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Monitoring will be handled by the file system provider
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Monitoring will be handled by the file system provider
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether an indexed item satisfies a query.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="SearchQueryEvaluator"/> so that this index and every persistence
    /// provider answer the same question the same way. This method used to carry its own copy of
    /// the predicate, and the copy in the SQLite provider had silently drifted from it.
    /// </remarks>
    private static bool MatchesQuery(in FastFileItem item, SearchQuery query, System.Text.RegularExpressions.Regex? regex)
    {
        return SearchQueryEvaluator.Matches(item, query, regex);
    }

    /// <summary>A filesystem-fallback hit, which arrives as a <see cref="FileItem"/>.</summary>
    private static bool MatchesQuery(FileItem file, SearchQuery query, System.Text.RegularExpressions.Regex? regex)
    {
        return SearchQueryEvaluator.Matches(file.ToFastFileItem(), query, regex);
    }

    /// <summary>
    /// Rough estimate of what an entry retains: the item, its name, and its table slot. Directory
    /// strings are shared by every entry beneath them and are not counted per entry.
    /// </summary>
    private void UpdateMemoryUsage(in FastFileItem item, IndexOperation operation)
    {
        var estimatedSize = EstimateSize(item);

        if (operation == IndexOperation.Add)
        {
            Interlocked.Add(ref _memoryUsage, estimatedSize);
        }
        else if (operation == IndexOperation.Remove)
        {
            Interlocked.Add(ref _memoryUsage, -estimatedSize);
        }
    }

    private static long EstimateSize(in FastFileItem item) => item.Name.Length * 2L + 128;

    private void RecalculateMemoryUsage()
    {
        long totalSize = 0;
        foreach (var item in _entries.All()) totalSize += EstimateSize(item);

        Interlocked.Exchange(ref _memoryUsage, totalSize);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsSearchIndex));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_persistence != null)
            {
                await _persistence.DisposeAsync();
            }

            _disposed = true;
        }
    }

    private enum IndexOperation
    {
        Add,
        Remove
    }
}
