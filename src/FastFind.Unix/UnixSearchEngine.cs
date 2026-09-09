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
    /// A store or a custom index was supplied. The Unix engine keeps its index internally and has no
    /// persistence path yet — see the remarks on <see cref="RejectUnsupportedComposition"/>.
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
        return new UnixSearchEngineImpl(provider, options.LoggerFactory);
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
    /// A store or a custom index was supplied — see <see cref="RejectUnsupportedComposition"/>.
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
        return new UnixSearchEngineImpl(provider, options.LoggerFactory);
    }

    /// <summary>
    /// Rejects composition options this engine cannot honour.
    /// </summary>
    /// <remarks>
    /// <see cref="UnixSearchEngineImpl"/> holds its index internally rather than taking an
    /// <see cref="ISearchIndex"/>, so it cannot be given a store or a custom index. Failing loudly is
    /// deliberate: accepting the option and ignoring it would leave a caller believing their index
    /// was persisted when it was not.
    /// </remarks>
    private static void RejectUnsupportedComposition(SearchEngineOptions options)
    {
        if (options.Persistence is null && options.Index is null) return;

        throw new NotSupportedException(
            "The Unix search engine keeps its index internally and cannot be given an " +
            "IIndexPersistence or a custom ISearchIndex. Use the in-memory engine, or a " +
            "persistence provider directly.");
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
    /// Always <c>null</c>: this engine keeps its index in an internal dictionary rather than behind
    /// an <see cref="ISearchIndex"/>, which is also why it rejects a supplied index or store.
    /// </remarks>
    public ISearchIndex? Index => null;

    public UnixSearchEngineImpl(IFileSystemProvider provider, ILoggerFactory? loggerFactory = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<UnixSearchEngineImpl>();
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

            _index.Clear();
            Interlocked.Exchange(ref _totalIndexedFiles, 0);
            long processedFiles = 0;
            bool capReached = false;

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

                _index[item.FullPath] = item;
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
    public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stopwatch = Stopwatch.StartNew();

        OnSearchProgressChanged(query, 0, 0, stopwatch.Elapsed, false, SearchPhase.Initializing);

        try
        {
            var results = SearchIndex(query, cancellationToken);
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

            var asyncFiles = ConvertToFastFileItemAsync(limitedResults);

            OnSearchProgressChanged(query, totalMatches, _index.Count, stopwatch.Elapsed, true, SearchPhase.Completed);

            return Task.FromResult(SearchResult.Success(
                query,
                totalMatches,
                limitedResults.Count,
                stopwatch.Elapsed,
                asyncFiles,
                hasMoreResults: hasMore));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            OnSearchProgressChanged(query, 0, 0, stopwatch.Elapsed, true, SearchPhase.Cancelled);
            return Task.FromResult(SearchResult.Empty(query, stopwatch.Elapsed));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Search error");
            OnSearchProgressChanged(query, 0, 0, stopwatch.Elapsed, true, SearchPhase.Failed);
            return Task.FromResult(SearchResult.Failed(query, stopwatch.Elapsed, ex.Message));
        }
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
    public Task<IndexingStatistics> GetIndexingStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var totalFiles = 0L;
        var totalDirs = 0L;
        var totalSize = 0L;

        foreach (var item in _index.Values)
        {
            if (item.Attributes.HasFlag(System.IO.FileAttributes.Directory))
                totalDirs++;
            else
                totalFiles++;

            totalSize += item.Size;
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

        return Task.FromResult(stats);
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
    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _index.Clear();
        Interlocked.Exchange(ref _totalIndexedFiles, 0);
        Interlocked.Exchange(ref _totalSearches, 0);
        Interlocked.Exchange(ref _totalMatches, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This engine keeps its index in memory and exposes no <see cref="ISearchIndex"/>, so it cannot
    /// persist one whatever it is composed with — which is also why it rejects a supplied store at
    /// construction. Use a persistence provider directly for a durable index on this platform.
    /// </remarks>
    public Task<int> SaveIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotSupportedException(
            "The Unix search engine uses in-memory indexing only and cannot save an index. " +
            "Use a FastFind.SQLite persistence provider directly for durable storage.");
    }

    /// <inheritdoc cref="SaveIndexAsync"/>
    public Task<int> LoadIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotSupportedException(
            "The Unix search engine uses in-memory indexing only and cannot load an index. " +
            "Use a FastFind.SQLite persistence provider directly for durable storage.");
    }

    /// <inheritdoc/>
    public Task OptimizeIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // ConcurrentDictionary in-memory index does not require optimization
        return Task.CompletedTask;
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
        var keysToRemove = _index.Keys
            .Where(k => paths.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        foreach (var key in keysToRemove)
            _index.TryRemove(key, out _);

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
            _index[item.FullPath] = item;
            refreshed++;
        }

        Interlocked.Exchange(ref _totalIndexedFiles, _index.Count);
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

    private static async IAsyncEnumerable<FastFileItem> ConvertToFastFileItemAsync(
        IList<FileItem> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item.ToFastFileItem();
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
                    HandleFileChange(change);
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

    private void HandleFileChange(FileChangeEventArgs change)
    {
        try
        {
            switch (change.ChangeType)
            {
                case FileChangeType.Created:
                case FileChangeType.Modified:
                    UpdateIndexEntry(change.NewPath);
                    break;

                case FileChangeType.Deleted:
                    _index.TryRemove(change.NewPath, out _);
                    Interlocked.Exchange(ref _totalIndexedFiles, _index.Count);
                    break;

                case FileChangeType.Renamed:
                    if (change.OldPath != null)
                        _index.TryRemove(change.OldPath, out _);
                    UpdateIndexEntry(change.NewPath);
                    break;
            }

            FileChanged?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error handling file change: {Path}", change.NewPath);
        }
    }

    private void UpdateIndexEntry(string path)
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
                _index[path] = item;
                Interlocked.Exchange(ref _totalIndexedFiles, _index.Count);
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
