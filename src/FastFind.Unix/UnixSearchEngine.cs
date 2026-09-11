using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Linux;
using FastFind.Unix.MacOS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FastFind.Unix;

/// <summary>
/// Unix search engine factory — creates platform-specific search engine instances
/// </summary>
public static class UnixSearchEngine
{
    /// <summary>
    /// Creates a Linux-optimized search engine
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory</param>
    /// <returns>Linux search engine instance</returns>
    public static ISearchEngine CreateLinuxSearchEngine(ILoggerFactory? loggerFactory = null)
    {
        return CreateLinuxSearchEngine(SearchEngineOptions.ForLogger(loggerFactory));
    }

    /// <summary>
    /// Creates a Linux-optimized search engine from a full set of composition options.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <see cref="SearchEngineOptions.PersistenceMode"/> is
    /// <see cref="PersistenceMode.MirrorInMemory"/> — see <see cref="RejectUnsupportedComposition"/>.
    /// </exception>
    public static ISearchEngine CreateLinuxSearchEngine(SearchEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RejectUnsupportedComposition(options);

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Linux search engine can only be used on Linux platforms");
        }

        var provider = new LinuxFileSystemProvider(options.LoggerFactory);
        return new UnixSearchEngineImpl(provider, options.LoggerFactory, options.ResolveIndex());
    }

    /// <summary>
    /// Creates a macOS-optimized search engine
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory</param>
    /// <returns>macOS search engine instance</returns>
    public static ISearchEngine CreateMacOSSearchEngine(ILoggerFactory? loggerFactory = null)
    {
        return CreateMacOSSearchEngine(SearchEngineOptions.ForLogger(loggerFactory));
    }

    /// <summary>
    /// Creates a macOS-optimized search engine from a full set of composition options.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <see cref="SearchEngineOptions.PersistenceMode"/> is
    /// <see cref="PersistenceMode.MirrorInMemory"/> — see <see cref="RejectUnsupportedComposition"/>.
    /// </exception>
    public static ISearchEngine CreateMacOSSearchEngine(SearchEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RejectUnsupportedComposition(options);

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "macOS search engine can only be used on macOS platforms");
        }

        var provider = new MacOSFileSystemProvider(options.LoggerFactory);
        return new UnixSearchEngineImpl(provider, options.LoggerFactory, options.ResolveIndex());
    }

    /// <summary>
    /// Rejects the one composition this engine still cannot honour.
    /// </summary>
    /// <remarks>
    /// A store or a custom index is now supported: <see cref="SearchEngineOptions.ResolveIndex"/>
    /// produces the <see cref="ISearchIndex"/> and the engine routes through it.
    /// <see cref="PersistenceMode.MirrorInMemory"/> is not, because it needs a shared in-memory
    /// <see cref="ISearchIndex"/> to mirror into and this engine's in-memory path is a plain
    /// dictionary rather than one. Failing loudly is deliberate: accepting the option and ignoring
    /// it would leave a caller believing their index was mirrored when it was not.
    /// </remarks>
    private static void RejectUnsupportedComposition(SearchEngineOptions options)
    {
        if (options.Persistence is null) return;
        if (options.PersistenceMode != PersistenceMode.MirrorInMemory) return;

        throw new NotSupportedException(
            "The Unix search engine cannot honour PersistenceMode.MirrorInMemory: it has no " +
            "in-memory ISearchIndex to mirror into. Use PersistenceMode.QueryFromStore, which is " +
            "supported, or supply your own ISearchIndex.");
    }
}

/// <summary>
/// Internal search engine implementation for Unix platforms.
/// Uses ConcurrentDictionary as an in-memory index and LINQ-based search.
/// </summary>
internal class UnixSearchEngineImpl : ISearchEngine
{
    private readonly IFileSystemProvider _provider;
    private readonly ILogger<UnixSearchEngineImpl> _logger;
    private readonly ConcurrentDictionary<string, FileItem> _index = new(StringComparer.Ordinal);

    /// <summary>
    /// The index this engine reads and writes when it was composed with one, otherwise
    /// <c>null</c> and <see cref="_index"/> is used instead.
    /// </summary>
    private readonly ISearchIndex? _searchIndex;

    /// <summary>
    /// How many items are accumulated before being written to a supplied index.
    /// </summary>
    private const int IndexWriteBatchSize = 10_000;

    private CancellationTokenSource? _indexingCts;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;

    private volatile bool _isIndexing;
    private volatile bool _isMonitoring;
    private long _totalIndexedFiles;
    private long _totalSearches;
    private long _totalMatches;
    private TimeSpan _lastIndexingTime;
    private DateTime _lastSearchTime;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<IndexingProgressEventArgs>? IndexingProgressChanged;

    /// <inheritdoc/>
    public event EventHandler<FileChangeEventArgs>? FileChanged;

    /// <inheritdoc/>
    public event EventHandler<SearchProgressEventArgs>? SearchProgressChanged;

    /// <inheritdoc/>
    public bool IsIndexing => _isIndexing;

    /// <inheritdoc/>
    public bool IsMonitoring => _isMonitoring;

    /// <inheritdoc/>
    public long TotalIndexedFiles => Interlocked.Read(ref _totalIndexedFiles);

    /// <inheritdoc/>
    /// <remarks>
    /// The supplied index when this engine was composed with one — including the
    /// <c>PersistentSearchIndex</c> that <see cref="SearchEngineOptions.ResolveIndex"/> builds from a
    /// store. <c>null</c> when it was not, in which case the engine keeps its index in an internal
    /// dictionary that is not reachable as an <see cref="ISearchIndex"/>.
    /// </remarks>
    public ISearchIndex? Index => _searchIndex;

    /// <summary>
    /// Whether this engine answers from a supplied <see cref="ISearchIndex"/> rather than from its
    /// own dictionary.
    /// </summary>
    private bool UsesSuppliedIndex => _searchIndex is not null;

    public UnixSearchEngineImpl(
        IFileSystemProvider provider,
        ILoggerFactory? loggerFactory = null,
        ISearchIndex? searchIndex = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<UnixSearchEngineImpl>();
        _searchIndex = searchIndex;
    }

    /// <inheritdoc/>
    public async Task StartIndexingAsync(IndexingOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isIndexing)
        {
            _logger.LogWarning("Indexing is already in progress");
            return;
        }

        _isIndexing = true;
        _indexingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = _indexingCts.Token;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var locations = options.GetEffectiveSearchLocations().ToArray();

            OnIndexingProgressChanged(new IndexingProgressEventArgs(
                string.Join(", ", locations),
                0, 0,
                TimeSpan.Zero,
                string.Empty,
                IndexingPhase.Initializing));

            if (UsesSuppliedIndex)
            {
                await _searchIndex!.ClearAsync(linkedToken).ConfigureAwait(false);
            }
            else
            {
                _index.Clear();
            }

            Interlocked.Exchange(ref _totalIndexedFiles, 0);
            long processedFiles = 0;
            bool capReached = false;

            // Writes to a supplied index are batched: a store-backed index pays a round trip per
            // call, so adding one item at a time would dominate enumeration.
            var pending = UsesSuppliedIndex ? new List<FastFileItem>(IndexWriteBatchSize) : null;

            OnIndexingProgressChanged(new IndexingProgressEventArgs(
                string.Join(", ", locations),
                0, 0,
                stopwatch.Elapsed,
                string.Empty,
                IndexingPhase.Scanning));

            await foreach (var item in _provider.EnumerateFilesAsync(locations, options, linkedToken)
                               .ConfigureAwait(false))
            {
                linkedToken.ThrowIfCancellationRequested();

                if (pending is not null)
                {
                    pending.Add(item.ToFastFileItem());
                    if (pending.Count >= IndexWriteBatchSize)
                    {
                        await _searchIndex!.AddBatchAsync(pending, linkedToken).ConfigureAwait(false);
                        pending.Clear();
                    }
                }
                else
                {
                    _index[item.FullPath] = item;
                }

                var count = Interlocked.Increment(ref processedFiles);
                Interlocked.Exchange(ref _totalIndexedFiles, count);

                if (options.MaxFileCount.HasValue && count >= options.MaxFileCount.Value)
                {
                    capReached = true;
                    break;
                }

                // Fire progress event periodically
                if (count % 10_000 == 0)
                {
                    OnIndexingProgressChanged(new IndexingProgressEventArgs(
                        string.Join(", ", locations),
                        count, 0,
                        stopwatch.Elapsed,
                        item.FullPath,
                        IndexingPhase.Scanning));
                }
            }

            // Flush whatever the last batch left, including on the cap-reached break above:
            // dropping the tail would silently lose up to a batch of files.
            if (pending is { Count: > 0 })
            {
                await _searchIndex!.AddBatchAsync(pending, linkedToken).ConfigureAwait(false);
                pending.Clear();
            }

            stopwatch.Stop();
            _lastIndexingTime = stopwatch.Elapsed;

            if (capReached)
            {
                _logger.LogWarning(
                    "Indexing cap reached: {FileCount} files indexed (MaxFileCount={MaxFileCount}). Files beyond the cap are not indexed.",
                    processedFiles, options.MaxFileCount);

                OnIndexingProgressChanged(new IndexingProgressEventArgs(
                    string.Join(", ", locations),
                    processedFiles, processedFiles,
                    stopwatch.Elapsed,
                    string.Empty,
                    IndexingPhase.CapReached));
            }
            else
            {
                OnIndexingProgressChanged(new IndexingProgressEventArgs(
                    string.Join(", ", locations),
                    processedFiles, processedFiles,
                    stopwatch.Elapsed,
                    string.Empty,
                    IndexingPhase.Completed));

                _logger.LogInformation(
                    "Indexing completed: {FileCount} items indexed in {Duration}",
                    processedFiles, stopwatch.Elapsed);

                // Start monitoring if requested
                if (options.EnableMonitoring)
                {
                    await StartMonitoringAsync(locations, linkedToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Indexing stopped (cancellation requested)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during indexing");
            throw;
        }
        finally
        {
            _isIndexing = false;
        }
    }

    /// <inheritdoc/>
    public Task StopIndexingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            _indexingCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        StopMonitoring();

        _isIndexing = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stopwatch = Stopwatch.StartNew();

        OnSearchProgressChanged(query, 0, 0, stopwatch.Elapsed, false, SearchPhase.Initializing);

        try
        {
            // Both paths evaluate through SearchQueryEvaluator — the dictionary path directly, the
            // supplied index inside its own implementation — so the same query returns the same set
            // whichever one answers it.
            var results = UsesSuppliedIndex
                ? await CollectFromIndexAsync(query, cancellationToken).ConfigureAwait(false)
                : SearchIndex(query, cancellationToken).Select(item => item.ToFastFileItem()).ToList();

            stopwatch.Stop();

            var totalMatches = results.Count;
            Interlocked.Increment(ref _totalSearches);
            Interlocked.Add(ref _totalMatches, totalMatches);
            _lastSearchTime = DateTime.UtcNow;

            // Apply MaxResults limit
            var limitedResults = query.MaxResults.HasValue
                ? results.Take(query.MaxResults.Value).ToList()
                : results;

            var hasMore = query.MaxResults.HasValue && totalMatches > query.MaxResults.Value;

            var asyncFiles = ToAsyncEnumerable(limitedResults);

            OnSearchProgressChanged(query, totalMatches, TotalIndexedFiles, stopwatch.Elapsed, true, SearchPhase.Completed);

            return SearchResult.Success(
                query,
                totalMatches,
                limitedResults.Count,
                stopwatch.Elapsed,
                asyncFiles,
                hasMoreResults: hasMore);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            OnSearchProgressChanged(query, 0, 0, stopwatch.Elapsed, true, SearchPhase.Cancelled);
            return SearchResult.Empty(query, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Search error");
            OnSearchProgressChanged(query, 0, 0, stopwatch.Elapsed, true, SearchPhase.Failed);
            return SearchResult.Failed(query, stopwatch.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// Drains the supplied index's results for a query.
    /// </summary>
    private async Task<List<FastFileItem>> CollectFromIndexAsync(
        SearchQuery query, CancellationToken cancellationToken)
    {
        var results = new List<FastFileItem>();

        await foreach (var item in _searchIndex!.SearchAsync(query, cancellationToken).ConfigureAwait(false))
        {
            results.Add(item);
        }

        return results;
    }

    /// <inheritdoc/>
    public Task<SearchResult> SearchAsync(string searchText, CancellationToken cancellationToken = default)
    {
        var query = new SearchQuery { SearchText = searchText };
        return SearchAsync(query, cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SearchResult> SearchRealTimeAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await SearchAsync(query, cancellationToken).ConfigureAwait(false);
        yield return result;
    }

    /// <inheritdoc/>
    public async Task<IndexingStatistics> GetIndexingStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var totalFiles = 0L;
        var totalDirs = 0L;
        var totalSize = 0L;

        if (UsesSuppliedIndex)
        {
            // A store-backed index counts without materialising the corpus; enumerating it here to
            // sum sizes would defeat the point of not holding one.
            var indexStats = await _searchIndex!.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
            totalFiles = indexStats.TotalFiles;
            totalDirs = indexStats.TotalDirectories;
            totalSize = 0;
        }
        else
        {
            foreach (var item in _index.Values)
            {
                if (item.Attributes.HasFlag(System.IO.FileAttributes.Directory))
                    totalDirs++;
                else
                    totalFiles++;

                totalSize += item.Size;
            }
        }

        var stats = new IndexingStatistics
        {
            TotalFiles = totalFiles,
            TotalDirectories = totalDirs,
            TotalSize = totalSize,
            LastIndexingTime = _lastIndexingTime,
            AverageIndexingSpeed = _lastIndexingTime.TotalSeconds > 0
                ? (totalFiles + totalDirs) / _lastIndexingTime.TotalSeconds
                : 0,
            IndexMemoryUsage = GC.GetTotalMemory(false),
            IndexDiskUsage = 0,
            CompressionRatio = 0,
            LastUpdateTime = DateTime.UtcNow,
            IndexingOperations = 1
        };

        return stats;
    }

    /// <inheritdoc/>
    public Task<SearchStatistics> GetSearchStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stats = new SearchStatistics
        {
            TotalSearches = Interlocked.Read(ref _totalSearches),
            TotalMatches = Interlocked.Read(ref _totalMatches),
            AverageSearchTime = TimeSpan.FromMilliseconds(10),
            FastestSearchTime = TimeSpan.FromMilliseconds(1),
            SlowestSearchTime = TimeSpan.FromMilliseconds(100),
            TotalSearchTime = TimeSpan.Zero,
            CacheHits = 0,
            CacheMisses = 0,
            IndexHits = Interlocked.Read(ref _totalSearches),
            FileSystemScans = 0,
            LastSearch = _lastSearchTime
        };

        return Task.FromResult(stats);
    }

    /// <inheritdoc/>
    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (UsesSuppliedIndex)
        {
            await _searchIndex!.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _index.Clear();
        }

        Interlocked.Exchange(ref _totalIndexedFiles, 0);
        Interlocked.Exchange(ref _totalSearches, 0);
        Interlocked.Exchange(ref _totalMatches, 0);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// This engine has no store — it was created without one, so there is nothing to save to. Same
    /// behaviour as the Windows engine.
    /// </exception>
    public async Task<int> SaveIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var persistence = RequirePersistence();

        var savedCount = await _searchIndex!.SaveToPersistenceAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Index saved to {Store}: {SavedCount} items", persistence.StoragePath, savedCount);

        return savedCount;
    }

    /// <inheritdoc cref="SaveIndexAsync"/>
    public async Task<int> LoadIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var persistence = RequirePersistence();

        var loadedCount = await _searchIndex!.LoadFromPersistenceAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _totalIndexedFiles, _searchIndex.Count);
        _logger.LogInformation("Index loaded from {Store}: {LoadedCount} items", persistence.StoragePath, loadedCount);

        return loadedCount;
    }

    /// <summary>
    /// The store backing this engine's index, or a failure explaining how to give it one.
    /// </summary>
    /// <remarks>
    /// Throwing rather than returning zero is deliberate and matches the Windows engine: a save that
    /// reports success without a store is how a caller ends up believing their index was persisted.
    /// </remarks>
    private IIndexPersistence RequirePersistence()
    {
        return _searchIndex?.Persistence ?? throw new InvalidOperationException(
            "This engine has no persistence store, so there is nothing to save to or load from. " +
            "Create it with FastFinder.CreateSearchEngine(persistence) to give it one.");
    }

    /// <inheritdoc/>
    public async Task OptimizeIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (UsesSuppliedIndex)
        {
            await _searchIndex!.OptimizeAsync(cancellationToken).ConfigureAwait(false);
        }

        // The ConcurrentDictionary in-memory index requires no optimization.
    }

    /// <inheritdoc/>
    public async Task RefreshIndexAsync(IEnumerable<string>? locations = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var paths = locations?.ToArray();
        if (paths == null || paths.Length == 0)
        {
            _logger.LogWarning("RefreshIndexAsync: no locations specified, nothing to refresh");
            return;
        }

        _logger.LogInformation("Refreshing index for {Count} locations", paths.Length);

        // Remove stale entries for the specified locations
        if (UsesSuppliedIndex)
        {
            foreach (var path in paths)
            {
                await foreach (var stale in _searchIndex!
                                   .GetByDirectoryAsync(path, recursive: true, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await _searchIndex.RemoveAsync(stale.FullPath, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            var keysToRemove = _index.Keys
                .Where(k => paths.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
                .ToList();

            foreach (var key in keysToRemove)
                _index.TryRemove(key, out _);
        }

        // Re-enumerate the locations
        var options = new IndexingOptions
        {
            IncludeHidden = true,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        long refreshed = 0;
        await foreach (var item in _provider.EnumerateFilesAsync(paths, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (UsesSuppliedIndex)
            {
                await _searchIndex!.AddAsync(item.ToFastFileItem(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _index[item.FullPath] = item;
            }

            refreshed++;
        }

        Interlocked.Exchange(
            ref _totalIndexedFiles,
            UsesSuppliedIndex ? _searchIndex!.Count : _index.Count);
        _logger.LogInformation("Refresh completed: {Refreshed} items re-indexed", refreshed);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _indexingCts?.Cancel(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error cancelling indexing CTS during dispose"); }

        try { _indexingCts?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error disposing indexing CTS"); }

        StopMonitoring();
        _provider.Dispose();
        _index.Clear();
    }

    // ────────────────────────────── Private Helpers ──────────────────────────────

    /// <summary>
    /// Filters the in-memory index for a query.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="SearchQueryEvaluator"/>, the single definition of what satisfies a
    /// <see cref="SearchQuery"/>. This method previously re-implemented the whole predicate — around
    /// a hundred lines of <c>Where</c> clauses — which is exactly the duplication that let the
    /// backends disagree with one another before Phase B. Any filtering rule belongs in the
    /// evaluator, not here.
    /// </remarks>
    private List<FileItem> SearchIndex(SearchQuery query, CancellationToken cancellationToken)
    {
        var textMatcher = SearchQueryEvaluator.CreateTextMatcher(query);
        var results = new List<FileItem>();

        foreach (var item in _index.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SearchQueryEvaluator.Matches(item.ToFastFileItem(), query, textMatcher))
            {
                results.Add(item);
            }
        }

        return results;
    }

    private static async IAsyncEnumerable<FastFileItem> ToAsyncEnumerable(
        IList<FastFileItem> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private async Task StartMonitoringAsync(string[] locations, CancellationToken cancellationToken)
    {
        _monitoringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isMonitoring = true;

        var options = new MonitoringOptions
        {
            IncludeSubdirectories = true,
            MonitorCreation = true,
            MonitorModification = true,
            MonitorDeletion = true,
            MonitorRename = true
        };

        _monitoringTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in _provider.MonitorChangesAsync(
                                   locations, options, _monitoringCts.Token).ConfigureAwait(false))
                {
                    await HandleFileChangeAsync(change).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring error");
            }
            finally
            {
                _isMonitoring = false;
            }
        }, _monitoringCts.Token);
    }

    private async Task HandleFileChangeAsync(FileChangeEventArgs change)
    {
        try
        {
            switch (change.ChangeType)
            {
                case FileChangeType.Created:
                case FileChangeType.Modified:
                    await UpdateIndexEntryAsync(change.NewPath).ConfigureAwait(false);
                    break;

                case FileChangeType.Deleted:
                    await RemoveIndexEntryAsync(change.NewPath).ConfigureAwait(false);
                    break;

                case FileChangeType.Renamed:
                    if (change.OldPath != null)
                        await MoveIndexEntryAsync(change.OldPath, change.NewPath).ConfigureAwait(false);
                    await UpdateIndexEntryAsync(change.NewPath).ConfigureAwait(false);
                    break;
            }

            FileChanged?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error handling file change: {Path}", change.NewPath);
        }
    }

    /// <summary>
    /// Drops a path — and, for a directory, everything beneath it — from whichever index this
    /// engine is using. A directory's deletion is reported once, for the directory.
    /// </summary>
    private async Task RemoveIndexEntryAsync(string path)
    {
        if (UsesSuppliedIndex)
        {
            var index = _searchIndex!;
            await index.RemoveTreeAsync(path).ConfigureAwait(false);
            Interlocked.Exchange(ref _totalIndexedFiles, index.Count);
        }
        else
        {
            if (!_index.TryRemove(path, out var removed) || removed.IsDirectory)
            {
                foreach (var key in _index.Keys.Where(k => SearchQueryEvaluator.IsUnder(k, path, includeSubdirectories: true)).ToList())
                    _index.TryRemove(key, out _);
            }
            Interlocked.Exchange(ref _totalIndexedFiles, _index.Count);
        }
    }

    /// <summary>
    /// Moves a path — and, for a directory, everything beneath it — to its new location in
    /// whichever index this engine is using.
    /// </summary>
    private async Task MoveIndexEntryAsync(string oldPath, string newPath)
    {
        if (UsesSuppliedIndex)
        {
            var index = _searchIndex!;
            await index.MoveTreeAsync(oldPath, newPath).ConfigureAwait(false);
            Interlocked.Exchange(ref _totalIndexedFiles, index.Count);
            return;
        }

        var oldRoot = oldPath.TrimEnd('/');
        var newRoot = newPath.TrimEnd('/');
        foreach (var key in _index.Keys.Where(k => SearchQueryEvaluator.IsUnder(k, oldRoot, includeSubdirectories: true)).ToList())
        {
            if (!_index.TryRemove(key, out var item)) continue;

            var fullPath = newRoot + key[oldRoot.Length..];
            _index[fullPath] = item with
            {
                FullPath = fullPath,
                Name = Path.GetFileName(fullPath),
                DirectoryPath = Path.GetDirectoryName(fullPath) ?? item.DirectoryPath,
            };
        }
        Interlocked.Exchange(ref _totalIndexedFiles, _index.Count);
    }

    private async Task UpdateIndexEntryAsync(string path)
    {
        try
        {
            FileItem? item = null;

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                item = new FileItem
                {
                    FullPath = info.FullName,
                    Name = info.Name,
                    DirectoryPath = info.DirectoryName ?? "/",
                    Extension = info.Extension,
                    Size = info.Length,
                    CreatedTime = info.CreationTimeUtc,
                    ModifiedTime = info.LastWriteTimeUtc,
                    AccessedTime = info.LastAccessTimeUtc,
                    Attributes = info.Attributes,
                    DriveLetter = '/',
                    FileRecordNumber = null
                };
            }
            else if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                item = new FileItem
                {
                    FullPath = info.FullName,
                    Name = info.Name,
                    DirectoryPath = info.Parent?.FullName ?? "/",
                    Extension = string.Empty,
                    Size = 0,
                    CreatedTime = info.CreationTimeUtc,
                    ModifiedTime = info.LastWriteTimeUtc,
                    AccessedTime = info.LastAccessTimeUtc,
                    Attributes = info.Attributes,
                    DriveLetter = '/',
                    FileRecordNumber = null
                };
            }

            if (item != null)
            {
                if (UsesSuppliedIndex)
                {
                    await _searchIndex!.AddAsync(item.ToFastFileItem()).ConfigureAwait(false);
                    Interlocked.Exchange(ref _totalIndexedFiles, _searchIndex.Count);
                }
                else
                {
                    _index[path] = item;
                    Interlocked.Exchange(ref _totalIndexedFiles, _index.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error updating index entry: {Path}", path);
        }
    }

    private void StopMonitoring()
    {
        try { _monitoringCts?.Cancel(); } catch { }
        try { _monitoringCts?.Dispose(); } catch { }
        _monitoringCts = null;
        _isMonitoring = false;
    }

    private void OnIndexingProgressChanged(IndexingProgressEventArgs args)
    {
        IndexingProgressChanged?.Invoke(this, args);
    }

    private void OnSearchProgressChanged(
        SearchQuery query, long matches, long processed,
        TimeSpan elapsed, bool isComplete, SearchPhase phase)
    {
        SearchProgressChanged?.Invoke(this, new SearchProgressEventArgs(
            query, matches, processed, elapsed, isComplete, phase));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnixSearchEngineImpl));
    }
}
