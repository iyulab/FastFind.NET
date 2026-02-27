using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using System.Buffers;

namespace FastFind.Windows.Implementation;

/// <summary>
/// Windows-optimized search engine implementation with .NET 10 performance enhancements
/// </summary>
[SupportedOSPlatform("windows")]
internal class WindowsSearchEngineImpl : ISearchEngine
{
    private readonly IFileSystemProvider _fileSystemProvider;
    private readonly ISearchIndex _searchIndex;
    private readonly ILogger<WindowsSearchEngineImpl> _logger;
    private readonly WindowsSearchEngineOptions _options;

    // .NET 10: Enhanced concurrent collections and performance monitoring
    private readonly ConcurrentDictionary<string, SearchStatistics> _searchStats = new();
    private readonly object _statisticsLock = new();
    private readonly CancellationTokenSource _lifecycleCancellationSource = new();

    // .NET 10: Channel-based high-performance communication
    private readonly Channel<FileChangeEventArgs> _fileChangeChannel;
    private readonly ChannelWriter<FileChangeEventArgs> _fileChangeWriter;
    private readonly ChannelReader<FileChangeEventArgs> _fileChangeReader;

    // .NET 10: Memory management and buffer pooling
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly SimpleObjectPool<List<FileItem>> _listPool;

    private IndexingOptions? _currentIndexingOptions;
    private Task? _indexingTask;
    private Task? _monitoringTask;
    private Task? _fileChangeProcessingTask;
    private bool _disposed = false;

    // Enhanced performance counters with atomic operations
    private long _totalSearches = 0;
    private long _totalSearchTime = 0;
    private long _totalIndexedFiles = 0;
    private long _averageSearchLatency = 0;

    public WindowsSearchEngineImpl(
        IFileSystemProvider fileSystemProvider,
        ISearchIndex searchIndex,
        WindowsSearchEngineOptions options,
        ILogger<WindowsSearchEngineImpl> logger)
    {
        _fileSystemProvider = fileSystemProvider;
        _searchIndex = searchIndex;
        _options = options;
        _logger = logger;

        // .NET 10: Channel for high-performance file change processing
        var channelOptions = new BoundedChannelOptions(_options.FileWatcherBufferSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _fileChangeChannel = Channel.CreateBounded<FileChangeEventArgs>(channelOptions);
        _fileChangeWriter = _fileChangeChannel.Writer;
        _fileChangeReader = _fileChangeChannel.Reader;

        // .NET 10: Object pooling for better memory management
        _listPool = new SimpleObjectPool<List<FileItem>>(() => new List<FileItem>(), list => list.Clear());

        // Start file change processing task
        _fileChangeProcessingTask = ProcessFileChangesAsync(_lifecycleCancellationSource.Token);

        _logger.LogInformation("WindowsSearchEngineImpl initialized with .NET 10 optimizations");
    }

    /// <inheritdoc/>
    public event EventHandler<IndexingProgressEventArgs>? IndexingProgressChanged;

    /// <inheritdoc/>
    public event EventHandler<FileChangeEventArgs>? FileChanged;

    /// <inheritdoc/>
    public event EventHandler<SearchProgressEventArgs>? SearchProgressChanged;

    /// <inheritdoc/>
    public bool IsIndexing => _indexingTask != null && !_indexingTask.IsCompleted;

    /// <inheritdoc/>
    public bool IsMonitoring => _monitoringTask != null && !_monitoringTask.IsCompleted;

    /// <inheritdoc/>
    public long TotalIndexedFiles => Interlocked.Read(ref _totalIndexedFiles);

    /// <inheritdoc/>
    public async Task StartIndexingAsync(IndexingOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (IsIndexing)
        {
            _logger.LogWarning("Indexing is already in progress, stopping current operation first");
            try
            {
                await StopIndexingAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping existing indexing operation");
            }
        }

        var validation = options.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage, nameof(options));
        }

        _currentIndexingOptions = options;

        // .NET 10: Enhanced cancellation token linking with timeout
        var indexingCts = new CancellationTokenSource(_options.FileOperationTimeout);
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifecycleCancellationSource.Token, indexingCts.Token);

        _indexingTask = Task.Run(async () =>
        {
            try
            {
                await IndexingWorkerAsync(options, combinedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Indexing operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in indexing worker");
            }
            finally
            {
                indexingCts.Dispose();
                combinedCts.Dispose();
            }
        }, combinedCts.Token);

        if (_options.EnableRealtimeMonitoring)
        {
            _monitoringTask = Task.Run(async () =>
            {
                try
                {
                    await MonitoringWorkerAsync(options, combinedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Monitoring operation was cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in monitoring worker");
                }
            }, combinedCts.Token);
        }

        _logger.LogInformation("Indexing started successfully with enhanced .NET 10 optimizations");
    }

    /// <inheritdoc/>
    public async Task StopIndexingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!IsIndexing && !IsMonitoring)
        {
            _logger.LogDebug("No indexing or monitoring operation is currently running");
            return;
        }

        _logger.LogInformation("Stopping indexing and monitoring operations...");

        try
        {
            _lifecycleCancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, which is fine
        }

        // .NET 10: Enhanced task completion with timeout and graceful shutdown
        var stopTasks = new List<Task>();

        if (_indexingTask != null)
        {
            stopTasks.Add(WaitForTaskWithTimeoutAsync(_indexingTask, "indexing", TimeSpan.FromSeconds(30)));
        }

        if (_monitoringTask != null)
        {
            stopTasks.Add(WaitForTaskWithTimeoutAsync(_monitoringTask, "monitoring", TimeSpan.FromSeconds(10)));
        }

        try
        {
            await Task.WhenAll(stopTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Some tasks did not stop cleanly");
        }

        _indexingTask = null;
        _monitoringTask = null;

        _logger.LogInformation("Indexing and monitoring stopped successfully");
    }

    /// <summary>
    /// .NET 10: Enhanced task timeout handling with better cancellation support
    /// </summary>
    private async Task WaitForTaskWithTimeoutAsync(Task task, string taskName, TimeSpan timeout)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            await task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            _logger.LogDebug("{TaskName} task completed successfully", taskName);
        }
        catch (OperationCanceledException) when (task.IsCompletedSuccessfully)
        {
            _logger.LogDebug("{TaskName} task completed successfully (was already done)", taskName);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{TaskName} task did not complete within {Timeout} seconds", taskName, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for {TaskName} task to complete", taskName);
        }
    }

    /// <inheritdoc/>
    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var validation = query.Validate();
        if (!validation.IsValid)
        {
            return SearchResult.Failed(query, TimeSpan.Zero, validation.ErrorMessage!);
        }

        var searchId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();

        _logger.LogDebug("🔍 Starting search {SearchId}: {SearchText}", searchId, query.SearchText);

        try
        {
            // Notify search started
            SearchProgressChanged?.Invoke(this, new SearchProgressEventArgs(
                query, 0, 0, TimeSpan.Zero, false, SearchPhase.Initializing));

            var totalMatches = 0L;

            // .NET 10: Use object pooling for better memory management
            var resultList = _listPool.Get();
            try
            {
                // .NET 10: Enhanced async enumeration with ConfigureAwait
                // SearchAsync now returns FastFileItem, convert to FileItem for internal storage
                await foreach (var fastFileItem in _searchIndex.SearchAsync(query, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    resultList.Add(fastFileItem.ToFileItem());
                    totalMatches++;

                    // Update progress with adaptive frequency
                    if (totalMatches % GetProgressUpdateFrequency(totalMatches) == 0)
                    {
                        SearchProgressChanged?.Invoke(this, new SearchProgressEventArgs(
                            query, totalMatches, _searchIndex.Count, stopwatch.Elapsed, false, SearchPhase.SearchingIndex));
                    }

                    // Apply result limit
                    if (query.MaxResults.HasValue && totalMatches >= query.MaxResults.Value)
                        break;
                }

                stopwatch.Stop();

                // Update statistics with atomic operations
                Interlocked.Increment(ref _totalSearches);
                Interlocked.Add(ref _totalSearchTime, stopwatch.ElapsedMilliseconds);
                Interlocked.Exchange(ref _averageSearchLatency,
                    Interlocked.Read(ref _totalSearchTime) / Math.Max(1, Interlocked.Read(ref _totalSearches)));

                var searchTime = stopwatch.Elapsed;
                var hasMoreResults = query.MaxResults.HasValue && totalMatches >= query.MaxResults.Value;

                // .NET 10: Enhanced metrics collection
                var metrics = new SearchMetrics
                {
                    FilesProcessed = _searchIndex.Count,
                    FilesFiltered = totalMatches,
                    TextMatchingTime = searchTime,
                    MemoryUsage = _searchIndex.MemoryUsage,
                    UsedIndex = true,
                    IndexHitRate = 100.0
                };

                // .NET 10: Create a copy of resultList before returning to pool
                // This is necessary because ConvertToFastFileItemAsyncEnumerable uses lazy evaluation
                // and the enumeration may happen after the finally block clears the list
                var resultListCopy = resultList.ToList();

                var result = SearchResult.Success(
                    query, totalMatches, resultListCopy.Count, searchTime,
                    ConvertToFastFileItemAsyncEnumerable(resultListCopy), metrics, hasMoreResults);

                // Notify search completed
                SearchProgressChanged?.Invoke(this, new SearchProgressEventArgs(
                    query, totalMatches, _searchIndex.Count, searchTime, true, SearchPhase.Completed));

                _logger.LogDebug("🔍 Search {SearchId} completed: {Matches} matches in {ElapsedMs}ms",
                    searchId, totalMatches, searchTime.TotalMilliseconds);

                return result;
            }
            finally
            {
                // Return the list to the pool
                resultList.Clear();
                _listPool.Return(resultList);
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();

            SearchProgressChanged?.Invoke(this, new SearchProgressEventArgs(
                query, 0, 0, stopwatch.Elapsed, true, SearchPhase.Cancelled));

            _logger.LogDebug("🔍 Search {SearchId} cancelled after {ElapsedMs}ms", searchId, stopwatch.ElapsedMilliseconds);

            return SearchResult.Empty(query, stopwatch.Elapsed, "Search was cancelled");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            SearchProgressChanged?.Invoke(this, new SearchProgressEventArgs(
                query, 0, 0, stopwatch.Elapsed, true, SearchPhase.Failed));

            _logger.LogError(ex, "🔍 Search {SearchId} failed: {Error}", searchId, ex.Message);

            return SearchResult.Failed(query, stopwatch.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// .NET 10: Adaptive progress update frequency based on result count
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetProgressUpdateFrequency(long totalMatches) => totalMatches switch
    {
        < 1000 => 50,      // Update every 50 matches for small results
        < 10000 => 100,    // Update every 100 matches for medium results  
        < 100000 => 500,   // Update every 500 matches for large results
        _ => 1000          // Update every 1000 matches for very large results
    };

    /// <inheritdoc/>
    public async Task<SearchResult> SearchAsync(string searchText, CancellationToken cancellationToken = default)
    {
        var query = new SearchQuery { SearchText = searchText };
        return await SearchAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SearchResult> SearchRealTimeAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var lastQuery = string.Empty;
        var debounceDelay = TimeSpan.FromMilliseconds(_options.UseAdvancedStringOptimizations ? 150 : 200);
        var lastSearchTime = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            var currentTime = DateTime.Now;
            var timeSinceLastSearch = currentTime - lastSearchTime;

            // .NET 10: Enhanced debouncing with string optimization awareness
            if (query.SearchText != lastQuery && timeSinceLastSearch >= debounceDelay)
            {
                lastQuery = query.SearchText;
                lastSearchTime = currentTime;

                var result = await SearchAsync(query, cancellationToken).ConfigureAwait(false);
                yield return result;
            }

            // .NET 10: Adaptive delay based on system performance
            var delayMs = 50; // Simplified without complex CPU monitoring
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<IndexingStatistics> GetIndexingStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var indexStats = await _searchIndex.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);

        // Convert IndexStatistics to IndexingStatistics
        return new IndexingStatistics
        {
            TotalFiles = indexStats.TotalFiles,
            TotalDirectories = indexStats.TotalDirectories,
            IndexMemoryUsage = indexStats.MemoryUsageBytes,
            LastUpdateTime = indexStats.LastUpdated ?? DateTime.MinValue
        };
    }

    /// <inheritdoc/>
    public async Task<SearchStatistics> GetSearchStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await Task.Run(() =>
        {
            var totalSearches = Interlocked.Read(ref _totalSearches);
            var totalTime = Interlocked.Read(ref _totalSearchTime);
            var averageTime = totalSearches > 0 ? TimeSpan.FromMilliseconds(totalTime / (double)totalSearches) : TimeSpan.Zero;

            // .NET 10: Enhanced statistics with performance metrics
            return new SearchStatistics
            {
                TotalSearches = totalSearches,
                AverageSearchTime = averageTime,
                TotalSearchTime = TimeSpan.FromMilliseconds(totalTime),
                IndexHits = totalSearches,
                FileSystemScans = 0,
                LastSearch = DateTime.Now
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _searchIndex.ClearAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _totalSearches, 0);
        Interlocked.Exchange(ref _totalSearchTime, 0);
        Interlocked.Exchange(ref _averageSearchLatency, 0);
        Interlocked.Exchange(ref _totalIndexedFiles, 0);

        _searchStats.Clear();

        // .NET 10: Force garbage collection for better memory management
        if (_options.UseAggressiveMemoryOptimization)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        }

        _logger.LogInformation("🧹 Cache and statistics cleared");
    }

    /// <inheritdoc/>
    public async Task SaveIndexAsync(string? filePath = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Save to persistence layer if available
        if (_searchIndex.Persistence != null)
        {
            var savedCount = await _searchIndex.SaveToPersistenceAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Index saved to persistence layer: {SavedCount} items", savedCount);
        }
        else
        {
            _logger.LogWarning("No persistence layer configured, index not saved");
        }
    }

    /// <inheritdoc/>
    public async Task LoadIndexAsync(string? filePath = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Load from persistence layer if available
        if (_searchIndex.Persistence != null)
        {
            var loadedCount = await _searchIndex.LoadFromPersistenceAsync(cancellationToken).ConfigureAwait(false);

            // Update indexed files count
            var stats = await _searchIndex.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _totalIndexedFiles, stats.TotalFiles);

            _logger.LogInformation("Index loaded from persistence layer: {LoadedCount} items", loadedCount);
        }
        else
        {
            _logger.LogWarning("No persistence layer configured, index not loaded");
        }
    }

    /// <inheritdoc/>
    public async Task OptimizeIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _searchIndex.OptimizeAsync(cancellationToken).ConfigureAwait(false);

        // .NET 10: Memory optimization after index optimization
        if (_options.UseAggressiveMemoryOptimization)
        {
            StringPool.Cleanup();
            LazyFormatCache.Cleanup();
            GC.Collect();
        }

        _logger.LogInformation("⚡ Index optimization completed");
    }

    /// <inheritdoc/>
    public async Task RefreshIndexAsync(IEnumerable<string>? locations = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_currentIndexingOptions == null)
        {
            _logger.LogWarning("Cannot refresh index: no previous indexing options available");
            return;
        }

        var refreshOptions = _currentIndexingOptions;

        if (locations != null)
        {
            refreshOptions = new IndexingOptions
            {
                SpecificDirectories = locations.ToList(),
                ExcludedPaths = _currentIndexingOptions.ExcludedPaths,
                ExcludedExtensions = _currentIndexingOptions.ExcludedExtensions,
                IncludeHidden = _currentIndexingOptions.IncludeHidden,
                IncludeSystem = _currentIndexingOptions.IncludeSystem,
                MaxFileSize = _currentIndexingOptions.MaxFileSize,
                ParallelThreads = _currentIndexingOptions.ParallelThreads,
                BatchSize = _currentIndexingOptions.BatchSize
            };
        }

        await StartIndexingAsync(refreshOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// .NET 10: Enhanced indexing worker with advanced parallel processing
    /// </summary>
    private async Task IndexingWorkerAsync(IndexingOptions options, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var processedFiles = 0L;
        var batchSize = Math.Min(options.BatchSize, _options.MaxConcurrentOperations * 50);

        // .NET 10: Use object pooling for batch processing
        var batch = _listPool.Get();

        try
        {
            _logger.LogInformation("Starting enhanced indexing with {Threads} threads", options.ParallelThreads);

            var locations = options.GetEffectiveSearchLocations();

            IndexingProgressChanged?.Invoke(this, new IndexingProgressEventArgs(
                string.Join(", ", locations), 0, 0, TimeSpan.Zero, string.Empty, IndexingPhase.Initializing));

            // .NET 10: Enhanced async enumeration with ConfigureAwait
            await foreach (var fileItem in _fileSystemProvider.EnumerateFilesAsync(locations, options, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                batch.Add(fileItem);
                processedFiles++;

                // Process in adaptive batches for better performance
                if (batch.Count >= batchSize)
                {
                    // Convert FileItem to FastFileItem for the new interface
                    await _searchIndex.AddBatchAsync(batch.Select(f => f.ToFastFileItem()), cancellationToken).ConfigureAwait(false);
                    batch.Clear();

                    // Update total indexed files atomically
                    Interlocked.Exchange(ref _totalIndexedFiles, processedFiles);

                    // Report progress with adaptive frequency
                    if (processedFiles % GetProgressReportFrequency(processedFiles) == 0)
                    {
                        IndexingProgressChanged?.Invoke(this, new IndexingProgressEventArgs(
                            string.Join(", ", locations), processedFiles, -1, stopwatch.Elapsed,
                            fileItem.FullPath, IndexingPhase.Indexing));
                    }
                }

                // .NET 10: Adaptive yielding based on performance
                if (processedFiles % 100 == 0)
                    await Task.Yield();
            }

            // Process remaining files
            if (batch.Count > 0)
            {
                // Convert FileItem to FastFileItem for the new interface
                await _searchIndex.AddBatchAsync(batch.Select(f => f.ToFastFileItem()), cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _totalIndexedFiles, processedFiles);
            }

            stopwatch.Stop();

            _logger.LogInformation("Enhanced indexing completed: {ProcessedFiles} files in {ElapsedMs}ms",
                processedFiles, stopwatch.ElapsedMilliseconds);

            IndexingProgressChanged?.Invoke(this, new IndexingProgressEventArgs(
                string.Join(", ", locations), processedFiles, processedFiles, stopwatch.Elapsed,
                "Completed", IndexingPhase.Completed));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Indexing cancelled after processing {ProcessedFiles} files", processedFiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing failed after processing {ProcessedFiles} files", processedFiles);

            IndexingProgressChanged?.Invoke(this, new IndexingProgressEventArgs(
                "Error", processedFiles, processedFiles, stopwatch.Elapsed,
                ex.Message, IndexingPhase.Failed));
        }
        finally
        {
            // Return the batch list to the pool
            batch.Clear();
            _listPool.Return(batch);
        }
    }

    /// <summary>
    /// .NET 10: Adaptive progress reporting frequency
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetProgressReportFrequency(long processedFiles) => processedFiles switch
    {
        < 10000 => 100,    // Every 100 files for small operations
        < 100000 => 500,   // Every 500 files for medium operations
        < 1000000 => 1000, // Every 1000 files for large operations
        _ => 5000          // Every 5000 files for very large operations
    };

    /// <summary>
    /// .NET 10: Channel-based file change processing for better performance
    /// </summary>
    private async Task ProcessFileChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var change in _fileChangeReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessFileChangeAsync(change, cancellationToken).ConfigureAwait(false);
                FileChanged?.Invoke(this, change);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error processing file change: {Path}", change.NewPath);
            }
        }
    }

    /// <summary>
    /// .NET 10: Enhanced monitoring worker with channel-based communication
    /// </summary>
    private async Task MonitoringWorkerAsync(IndexingOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var monitoringOptions = new MonitoringOptions
            {
                IncludeSubdirectories = true,
                BufferSize = _options.FileWatcherBufferSize,
                DebounceInterval = TimeSpan.FromMilliseconds(500),
                ExcludedPaths = options.ExcludedPaths.ToList()
            };

            var locations = options.GetEffectiveSearchLocations();

            _logger.LogInformation("Starting enhanced file system monitoring for: {Locations}", string.Join(", ", locations));

            // .NET 10: Channel-based change processing for better throughput
            await foreach (var change in _fileSystemProvider.MonitorChangesAsync(locations, monitoringOptions, cancellationToken).ConfigureAwait(false))
            {
                // Write to channel for asynchronous processing
                if (!_fileChangeWriter.TryWrite(change))
                {
                    // Channel is full, wait for space
                    await _fileChangeWriter.WriteAsync(change, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("File system monitoring cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enhanced file system monitoring failed");
        }
        finally
        {
            // Complete the channel when monitoring stops
            _fileChangeWriter.TryComplete();
        }
    }

    private async Task ProcessFileChangeAsync(FileChangeEventArgs change, CancellationToken cancellationToken)
    {
        switch (change.ChangeType)
        {
            case FileChangeType.Created:
            case FileChangeType.Modified:
                var fileItem = await _fileSystemProvider.GetFileInfoAsync(change.NewPath, cancellationToken).ConfigureAwait(false);
                if (fileItem != null)
                {
                    await _searchIndex.UpdateAsync(fileItem.ToFastFileItem(), cancellationToken).ConfigureAwait(false);
                }
                break;

            case FileChangeType.Deleted:
                await _searchIndex.RemoveAsync(change.NewPath, cancellationToken).ConfigureAwait(false);
                break;

            case FileChangeType.Renamed:
                if (!string.IsNullOrEmpty(change.OldPath))
                {
                    await _searchIndex.RemoveAsync(change.OldPath, cancellationToken).ConfigureAwait(false);
                }
                var renamedFileItem = await _fileSystemProvider.GetFileInfoAsync(change.NewPath, cancellationToken).ConfigureAwait(false);
                if (renamedFileItem != null)
                {
                    await _searchIndex.AddAsync(renamedFileItem.ToFastFileItem(), cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    /// <summary>
    /// .NET 10: Enhanced async enumerable conversion with object pooling
    /// </summary>
    private static async IAsyncEnumerable<FileItem> ConvertToAsyncEnumerable(IList<FileItem> items)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            yield return item;
        }
    }

    /// <summary>
    /// Convert FileItem list to FastFileItem async enumerable
    /// </summary>
    private static async IAsyncEnumerable<FastFileItem> ConvertToFastFileItemAsyncEnumerable(IList<FileItem> items)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            yield return item.ToFastFileItem();
        }
    }

    private static string GetDefaultIndexPath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fastFindPath = Path.Combine(appDataPath, "FastFind.NET");
        Directory.CreateDirectory(fastFindPath);
        return Path.Combine(fastFindPath, "index.dat");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsSearchEngineImpl));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            // Complete the file change channel
            _fileChangeWriter.TryComplete();

            // Cancel all operations first
            _lifecycleCancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, continue with cleanup
        }

        // .NET 10: Enhanced disposal with better timeout handling
        var waitTasks = new List<Task>();

        if (_indexingTask != null && !_indexingTask.IsCompleted)
            waitTasks.Add(_indexingTask);

        if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            waitTasks.Add(_monitoringTask);

        if (_fileChangeProcessingTask != null && !_fileChangeProcessingTask.IsCompleted)
            waitTasks.Add(_fileChangeProcessingTask);

        if (waitTasks.Count > 0)
        {
            try
            {
                Task.WaitAll(waitTasks.ToArray(), TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex)
            {
                foreach (var innerEx in ex.InnerExceptions)
                {
                    if (innerEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(innerEx, "Exception during disposal cleanup");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Exception during disposal cleanup");
            }
        }

        // Dispose resources
        try
        {
            _lifecycleCancellationSource.Dispose();
            _fileSystemProvider.Dispose();
            _searchIndex.Dispose();
            _listPool.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing resources");
        }
    }
}

/// <summary>
/// .NET 10: Simple object pool implementation for high performance
/// </summary>
internal class SimpleObjectPool<T> : IDisposable where T : class
{
    private readonly Func<T> _factory;
    private readonly Action<T> _reset;
    private readonly ConcurrentQueue<T> _objects = new();
    private readonly int _maxObjects = Environment.ProcessorCount * 4;

    public SimpleObjectPool(Func<T> factory, Action<T> reset)
    {
        _factory = factory;
        _reset = reset;
    }

    public T Get()
    {
        return _objects.TryDequeue(out var obj) ? obj : _factory();
    }

    public void Return(T obj)
    {
        if (_objects.Count < _maxObjects)
        {
            _reset(obj);
            _objects.Enqueue(obj);
        }
    }

    public void Dispose()
    {
        while (_objects.TryDequeue(out var obj))
        {
            if (obj is IDisposable disposable)
                disposable.Dispose();
        }
    }
}