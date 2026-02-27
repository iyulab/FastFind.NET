using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Linux;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

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
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Linux search engine can only be used on Linux platforms");
        }

        var provider = new LinuxFileSystemProvider(loggerFactory);
        return new UnixSearchEngineImpl(provider, loggerFactory);
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
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Indexing was cancelled");
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
    /// Unix implementation uses in-memory indexing only. Persistence is not supported.
    /// Use FastFind.SQLite for persistent index storage.
    /// </remarks>
    public Task SaveIndexAsync(string? filePath = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotSupportedException(
            "Unix search engine uses in-memory indexing only. " +
            "Use FastFind.SQLite for persistent index storage.");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Unix implementation uses in-memory indexing only. Persistence is not supported.
    /// Use FastFind.SQLite for persistent index storage.
    /// </remarks>
    public Task LoadIndexAsync(string? filePath = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotSupportedException(
            "Unix search engine uses in-memory indexing only. " +
            "Use FastFind.SQLite for persistent index storage.");
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

    private List<FileItem> SearchIndex(SearchQuery query, CancellationToken cancellationToken)
    {
        IEnumerable<FileItem> source = _index.Values;

        // Text matching
        if (!string.IsNullOrEmpty(query.SearchText))
        {
            Regex? regex = null;
            if (query.UseRegex)
            {
                regex = query.GetCompiledRegex();
            }
            else if (query.SearchText.Contains('*') || query.SearchText.Contains('?'))
            {
                regex = query.GetWildcardRegex();
            }

            if (regex != null)
            {
                source = source.Where(item =>
                {
                    var target = query.SearchFileNameOnly ? item.Name : item.FullPath;
                    return regex.IsMatch(target);
                });
            }
            else
            {
                var comparison = query.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                source = source.Where(item =>
                {
                    var target = query.SearchFileNameOnly ? item.Name : item.FullPath;
                    return target.Contains(query.SearchText, comparison);
                });
            }
        }

        // BasePath filter
        if (!string.IsNullOrEmpty(query.BasePath))
        {
            var basePath = query.BasePath.TrimEnd('/');
            if (query.IncludeSubdirectories)
            {
                source = source.Where(item =>
                    item.FullPath.StartsWith(basePath, StringComparison.Ordinal));
            }
            else
            {
                source = source.Where(item =>
                    item.DirectoryPath.Equals(basePath, StringComparison.Ordinal) ||
                    item.DirectoryPath.Equals(basePath + "/", StringComparison.Ordinal));
            }
        }

        // Extension filter
        if (!string.IsNullOrEmpty(query.ExtensionFilter))
        {
            var ext = query.ExtensionFilter.StartsWith('.')
                ? query.ExtensionFilter
                : "." + query.ExtensionFilter;

            source = source.Where(item =>
                item.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        // Include/exclude files and directories
        if (!query.IncludeFiles)
            source = source.Where(item => item.IsDirectory);
        if (!query.IncludeDirectories)
            source = source.Where(item => !item.IsDirectory);

        // Hidden/system filters
        if (!query.IncludeHidden)
            source = source.Where(item => !item.IsHidden);
        if (!query.IncludeSystem)
            source = source.Where(item => !item.IsSystem);

        // Size filters
        if (query.MinSize.HasValue)
            source = source.Where(item => item.Size >= query.MinSize.Value);
        if (query.MaxSize.HasValue)
            source = source.Where(item => item.Size <= query.MaxSize.Value);

        // Date filters
        if (query.MinCreatedDate.HasValue)
            source = source.Where(item => item.CreatedTime >= query.MinCreatedDate.Value);
        if (query.MaxCreatedDate.HasValue)
            source = source.Where(item => item.CreatedTime <= query.MaxCreatedDate.Value);
        if (query.MinModifiedDate.HasValue)
            source = source.Where(item => item.ModifiedTime >= query.MinModifiedDate.Value);
        if (query.MaxModifiedDate.HasValue)
            source = source.Where(item => item.ModifiedTime <= query.MaxModifiedDate.Value);

        // Required/excluded attributes
        if (query.RequiredAttributes.HasValue)
            source = source.Where(item => item.Attributes.HasFlag(query.RequiredAttributes.Value));
        if (query.ExcludedAttributes.HasValue)
            source = source.Where(item => !item.Attributes.HasFlag(query.ExcludedAttributes.Value));

        // Search locations filter
        if (query.SearchLocations.Count > 0)
        {
            source = source.Where(item =>
                query.SearchLocations.Any(loc =>
                    item.FullPath.StartsWith(loc, StringComparison.Ordinal)));
        }

        // Excluded paths
        if (query.ExcludedPaths.Count > 0)
        {
            source = source.Where(item =>
                !query.ExcludedPaths.Any(ex =>
                    item.FullPath.StartsWith(ex, StringComparison.Ordinal)));
        }

        return source.ToList();
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
