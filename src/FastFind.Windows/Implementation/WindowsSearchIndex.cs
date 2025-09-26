using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private readonly ConcurrentDictionary<string, FileItem> _fileIndex = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _directoryIndex = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _extensionIndex = new();
    private readonly ReaderWriterLockSlim _indexLock = new();
    private readonly object _statsLock = new();

    private long _memoryUsage = 0;
    private bool _isReady = false;
    private bool _disposed = false;

    public WindowsSearchIndex(ILogger<WindowsSearchIndex> logger)
    {
        _logger = logger;
        _isReady = true;
    }

    /// <inheritdoc/>
    public long Count => _fileIndex.Count;

    /// <inheritdoc/>
    public long MemoryUsage => Interlocked.Read(ref _memoryUsage);

    /// <inheritdoc/>
    public bool IsReady => _isReady && !_disposed;

    /// <inheritdoc/>
    public async Task AddFileAsync(FileItem fileItem, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _indexLock.EnterWriteLock();
            try
            {
                var key = fileItem.FullPath.ToLowerInvariant();
                var wasAdded = _fileIndex.TryAdd(key, fileItem);

                if (wasAdded)
                {
                    UpdateIndices(fileItem, IndexOperation.Add);
                    UpdateMemoryUsage(fileItem, IndexOperation.Add);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task AddFilesAsync(IEnumerable<FileItem> fileItems, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (cancellationToken.IsCancellationRequested)
            return;

        var items = fileItems.ToArray();
        if (items.Length == 0)
            return;

        // 성능 최우선 고속 배치 처리 - OperationCanceledException 방지
        await Task.Run(() =>
        {
            try
            {
                // 단일 락으로 모든 작업을 한 번에 처리 (최고 성능)
                _indexLock.EnterWriteLock();
                try
                {
                    var addedCount = 0;
                    var batchProcessed = 0;

                    foreach (var fileItem in items)
                    {
                        // 간소화된 취소 체크 (5000개마다만)
                        if (++batchProcessed % 5000 == 0 && cancellationToken.IsCancellationRequested)
                            break;

                        var key = fileItem.FullPath.ToLowerInvariant();
                        var wasAdded = _fileIndex.TryAdd(key, fileItem);

                        if (wasAdded)
                        {
                            UpdateIndices(fileItem, IndexOperation.Add);
                            UpdateMemoryUsage(fileItem, IndexOperation.Add);
                            addedCount++;
                        }
                    }

                    _logger.LogDebug("Added {AddedCount} files to index in single batch", addedCount);
                }
                finally
                {
                    _indexLock.ExitWriteLock();
                }
            }
            catch (OperationCanceledException)
            {
                // 정상적인 취소 - 로깅만
                _logger.LogDebug("Batch add operation cancelled");
            }
        }, CancellationToken.None); // 내부에서 취소 처리하므로 외부 토큰 사용 안함
    }

    /// <inheritdoc/>
    public async Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _indexLock.EnterWriteLock();
            try
            {
                var key = filePath.ToLowerInvariant();
                if (_fileIndex.TryRemove(key, out var removedFile))
                {
                    UpdateIndices(removedFile, IndexOperation.Remove);
                    UpdateMemoryUsage(removedFile, IndexOperation.Remove);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateFileAsync(FileItem fileItem, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _indexLock.EnterWriteLock();
            try
            {
                var key = fileItem.FullPath.ToLowerInvariant();
                if (_fileIndex.TryGetValue(key, out var existingFile))
                {
                    // Remove old indices and add new ones
                    UpdateIndices(existingFile, IndexOperation.Remove);
                    UpdateMemoryUsage(existingFile, IndexOperation.Remove);
                }

                _fileIndex[key] = fileItem;
                UpdateIndices(fileItem, IndexOperation.Add);
                UpdateMemoryUsage(fileItem, IndexOperation.Add);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var validation = query.Validate();
        if (!validation.IsValid)
        {
            _logger.LogWarning("Invalid search query: {Error}", validation.ErrorMessage);
            yield break;
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
    private async IAsyncEnumerable<FileItem> SearchHybridAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Only use regex if explicitly requested OR if wildcards are present
        var regex = query.GetCompiledRegex() ??
                   (query.SearchText?.Contains('*') == true || query.SearchText?.Contains('?') == true
                       ? query.GetWildcardRegex()
                       : null);
        var searchText = query.SearchText?.ToLowerInvariant() ?? string.Empty;
        var matchCount = 0;
        var returnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Search indexed results (fast path)
        _indexLock.EnterReadLock();
        try
        {
            var indexedCandidates = GetSearchCandidatesSync(query).ToList();
            _logger.LogDebug("Hybrid search: Found {Count} indexed candidates", indexedCandidates.Count);

            foreach (var candidate in indexedCandidates)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                if (query.MaxResults.HasValue && matchCount >= query.MaxResults.Value)
                    yield break;

                if (MatchesQuery(candidate, query, regex, searchText))
                {
                    returnedPaths.Add(candidate.FullPath);
                    matchCount++;
                    yield return candidate;

                    // Yield control periodically for better responsiveness
                    if (matchCount % 50 == 0)
                        await Task.Yield();
                }
            }
        }
        finally
        {
            _indexLock.ExitReadLock();
        }

        // Phase 2: Fill gaps with live filesystem search (for incomplete indexing)
        if (ShouldPerformFilesystemFallback(query, matchCount))
        {
            _logger.LogDebug("Hybrid search: Performing filesystem fallback, current matches: {Count}", matchCount);
            Console.WriteLine($"[DEBUG] Starting filesystem fallback - searchText: '{searchText}', BasePath: '{query.BasePath}'");

            var fsResultCount = 0;
            await foreach (var fsResult in SearchFilesystemAsync(query, returnedPaths, regex, searchText, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                if (query.MaxResults.HasValue && matchCount >= query.MaxResults.Value)
                    yield break;

                fsResultCount++;
                matchCount++;
                Console.WriteLine($"[DEBUG] Filesystem found file #{fsResultCount}: {fsResult.Name} at {fsResult.DirectoryPath}");
                yield return fsResult;

                // Yield control periodically for better responsiveness
                if (matchCount % 25 == 0)
                    await Task.Yield();
            }
            Console.WriteLine($"[DEBUG] Filesystem fallback completed: {fsResultCount} additional files found");
        }
        else
        {
            Console.WriteLine($"[DEBUG] Skipping filesystem fallback - Count: {Count}, matchCount: {matchCount}");
        }

        Console.WriteLine($"[DEBUG] Hybrid search completed: {matchCount} total matches");
        _logger.LogDebug("Hybrid search completed: {TotalMatches} matches", matchCount);
    }

    /// <summary>
    /// Determines if filesystem fallback search should be performed
    /// </summary>
    private bool ShouldPerformFilesystemFallback(SearchQuery query, int indexedMatches)
    {
        // Always perform fallback if index is very small (suggests no or incomplete indexing)
        if (Count < 10)
            return true;

        // Always perform fallback if we have specific search paths (BasePath or SearchLocations)
        // This ensures we always find files in user-specified directories
        if (query.BasePath != null || query.SearchLocations.Count > 0)
            return true;

        // Perform fallback if we found very few results from index
        if (indexedMatches < 5)
            return true;

        // For broad searches with good index coverage, trust the index
        return false;
    }

    /// <summary>
    /// Performs high-performance parallel filesystem search to complement indexed results
    /// </summary>
    private async IAsyncEnumerable<FileItem> SearchFilesystemAsync(
        SearchQuery query,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        string searchText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var searchPaths = GetFilesystemSearchPaths(query);
        Console.WriteLine($"[DEBUG] SearchFilesystemAsync - SearchPaths: [{string.Join(", ", searchPaths)}], regex: {(regex != null ? "YES" : "NO")}");

        if (query.IncludeSubdirectories)
        {
            // Use parallel scanning for subdirectory search (maximum performance)
            await foreach (var fileItem in ScanDirectoriesParallelAsync(searchPaths, query, excludePaths, regex, searchText, cancellationToken))
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

                await foreach (var fileItem in ScanDirectoryAsync(searchPath, query, searchOptions, excludePaths, regex, searchText, cancellationToken))
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
        string searchText,
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
                await ScanPathsInParallel(searchPaths, query, excludePaths, regex, searchText,
                    processedPaths, writer, semaphore, cancellationToken);
            }
            finally
            {
                writer.Complete();
                semaphore.Dispose();
            }
        }, cancellationToken);

        // Stream results as they become available
        Console.WriteLine($"[DEBUG] Starting to read from result channel...");
        var channelResultCount = 0;
        await foreach (var result in resultChannel.Reader.ReadAllAsync(cancellationToken))
        {
            channelResultCount++;
            Console.WriteLine($"[DEBUG] Yielding result #{channelResultCount}: {result.Name}");
            yield return result;
        }
        Console.WriteLine($"[DEBUG] Channel reading completed, yielded {channelResultCount} results");

        // Wait for scanning to complete
        Console.WriteLine($"[DEBUG] Waiting for scanning task to complete...");
        await scanningTask;
        Console.WriteLine($"[DEBUG] Scanning task completed");
    }

    /// <summary>
    /// Parallel path scanning with dynamic work distribution
    /// </summary>
    private async Task ScanPathsInParallel(
        List<string> searchPaths,
        SearchQuery query,
        HashSet<string> excludePaths,
        System.Text.RegularExpressions.Regex? regex,
        string searchText,
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
            workers[i] = ProcessDirectoriesWorker(directoryQueue, query, excludePaths, regex, searchText,
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
        string searchText,
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
                    regex, searchText, writer, cancellationToken);

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
        string searchText,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine($"[DEBUG] Processing directory: {directoryPath}");

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false, // We handle recursion manually for parallel processing
                IgnoreInaccessible = true,
                BufferSize = 8192,
                AttributesToSkip = query.IncludeHidden ? FileAttributes.None : FileAttributes.Hidden
            };

            // Get all entries in this directory
            var entries = Directory.EnumerateFileSystemEntries(directoryPath, "*", options);
            Console.WriteLine($"[DEBUG] Found {entries.Count()} entries in {directoryPath}");

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

                    // Skip excluded paths
                    if (query.ExcludedPaths.Any(excluded =>
                        fullPath.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
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
                    Console.WriteLine($"[DEBUG] Checking file: {fileItem.Name} (FullPath: {fileItem.FullPath})");
                    if (MatchesQuery(fileItem, query, regex, searchText))
                    {
                        Console.WriteLine($"[DEBUG] File MATCHES query: {fileItem.Name}");
                        lock (excludePaths)
                        {
                            if (!excludePaths.Contains(fullPath))
                            {
                                excludePaths.Add(fullPath);
                                Console.WriteLine($"[DEBUG] Adding match to results: {fileItem.Name}");

                                // Write to channel (non-blocking)
                                if (!writer.TryWrite(fileItem))
                                {
                                    // Channel is full, wait and retry
                                    writer.WriteAsync(fileItem, cancellationToken).AsTask().Wait(cancellationToken);
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[DEBUG] File does NOT match query: {fileItem.Name}");
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
            _indexLock.EnterReadLock();
            try
            {
                paths.AddRange(_directoryIndex.Keys.Take(5)); // Limit to avoid excessive scanning
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
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
        string searchText,
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

            // Skip excluded paths
            if (query.ExcludedPaths.Any(excluded =>
                fullPath.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
                continue;

            FileItem? fileItem = GetFileItemSafely(fullPath);

            if (fileItem != null && MatchesQuery(fileItem, query, regex, searchText))
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

    private IEnumerable<FileItem> GetSearchCandidatesSync(SearchQuery query)
    {
        // BasePath takes precedence over SearchLocations
        if (!string.IsNullOrEmpty(query.BasePath))
        {
            var basePathCandidates = GetFilesByLocationsSync(new[] { query.BasePath }, query.IncludeSubdirectories);

            // If extension filter is also specified, apply it to base path results
            if (!string.IsNullOrEmpty(query.ExtensionFilter))
            {
                return ApplyExtensionFilter(basePathCandidates, query.ExtensionFilter);
            }

            return basePathCandidates;
        }

        // Optimize search by using appropriate index
        if (!string.IsNullOrEmpty(query.ExtensionFilter))
        {
            return GetFilesByExtensionSync(query.ExtensionFilter);
        }

        if (query.SearchLocations.Count > 0)
        {
            return GetFilesByLocationsSync(query.SearchLocations, query.IncludeSubdirectories);
        }

        // Return all files
        return _fileIndex.Values;
    }

    private IEnumerable<FileItem> GetFilesByExtensionSync(string extension)
    {
        var normalizedExtension = extension.ToLowerInvariant();
        if (_extensionIndex.TryGetValue(normalizedExtension, out var filePaths))
        {
            foreach (var filePath in filePaths)
            {
                if (_fileIndex.TryGetValue(filePath, out var fileItem))
                {
                    yield return fileItem;
                }
            }
        }
    }

    private IEnumerable<FileItem> GetFilesByLocationsSync(IList<string> locations, bool includeSubdirectories = true)
    {
        foreach (var location in locations)
        {
            var normalizedPath = Path.GetFullPath(location).ToLowerInvariant().TrimEnd('\\', '/');

            if (includeSubdirectories)
            {
                // Search in the specified directory and all subdirectories
                foreach (var fileItem in _fileIndex.Values)
                {
                    var itemDirectory = fileItem.DirectoryPath?.ToLowerInvariant().TrimEnd('\\', '/');
                    if (itemDirectory != null)
                    {
                        // More robust path matching
                        if (itemDirectory == normalizedPath ||
                            itemDirectory.StartsWith(normalizedPath + "\\") ||
                            itemDirectory.StartsWith(normalizedPath + "/"))
                        {
                            yield return fileItem;
                        }
                    }
                }
            }
            else
            {
                // Search only in the specified directory (exact match)
                if (_directoryIndex.TryGetValue(normalizedPath, out var filePaths))
                {
                    foreach (var filePath in filePaths)
                    {
                        if (_fileIndex.TryGetValue(filePath, out var fileItem))
                        {
                            yield return fileItem;
                        }
                    }
                }
            }
        }
    }

    private IEnumerable<FileItem> ApplyExtensionFilter(IEnumerable<FileItem> candidates, string extension)
    {
        var normalizedExtension = extension.ToLowerInvariant();
        foreach (var candidate in candidates)
        {
            if (candidate.Extension.ToLowerInvariant() == normalizedExtension)
            {
                yield return candidate;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<FileItem?> GetFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            _indexLock.EnterReadLock();
            try
            {
                var key = filePath.ToLowerInvariant();
                return _fileIndex.TryGetValue(key, out var fileItem) ? fileItem : null;
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            _indexLock.EnterReadLock();
            try
            {
                var key = filePath.ToLowerInvariant();
                return _fileIndex.ContainsKey(key);
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> GetFilesInDirectoryAsync(
        string directoryPath,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var normalizedPath = directoryPath.ToLowerInvariant().TrimEnd('\\', '/');

        // Collect files while holding the lock, then release it
        List<FileItem> files;

        _indexLock.EnterReadLock();
        try
        {
            files = new List<FileItem>();
            if (_directoryIndex.TryGetValue(normalizedPath, out var filePaths))
            {
                foreach (var filePath in filePaths)
                {
                    if (_fileIndex.TryGetValue(filePath, out var fileItem))
                    {
                        if (recursive || fileItem.DirectoryPath.Equals(directoryPath, StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(fileItem);
                        }
                    }
                }
            }
        }
        finally
        {
            _indexLock.ExitReadLock();
        }

        // Yield files without holding the lock
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }

        await Task.Yield();
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await Task.Run(() =>
        {
            _indexLock.EnterWriteLock();
            try
            {
                _fileIndex.Clear();
                _directoryIndex.Clear();
                _extensionIndex.Clear();
                Interlocked.Exchange(ref _memoryUsage, 0);

                _logger.LogInformation("Search index cleared");
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await Task.Run(() =>
        {
            _indexLock.EnterWriteLock();
            try
            {
                // Force garbage collection to reclaim memory
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // Recalculate memory usage
                RecalculateMemoryUsage();

                _logger.LogInformation("Search index optimized. Memory usage: {MemoryMB} MB",
                    MemoryUsage / (1024.0 * 1024.0));
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IndexingStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await Task.Run(() =>
        {
            _indexLock.EnterReadLock();
            try
            {
                var files = _fileIndex.Values.Where(f => !f.IsDirectory).ToArray();
                var directories = _fileIndex.Values.Where(f => f.IsDirectory).ToArray();

                return new IndexingStatistics
                {
                    TotalFiles = files.Length,
                    TotalDirectories = directories.Length,
                    TotalSize = files.Sum(f => f.Size),
                    IndexMemoryUsage = MemoryUsage,
                    LastUpdateTime = DateTime.Now,
                    Efficiency = new IndexEfficiency
                    {
                        MemoryPerFile = files.Length > 0 ? (double)MemoryUsage / files.Length : 0,
                        LookupSpeed = 1000000, // Estimated lookups per second
                        CacheHitRate = 0.95 // Estimated cache hit rate
                    }
                };
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task SaveToStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotImplementedException("Stream persistence not yet implemented");
    }

    /// <inheritdoc/>
    public Task LoadFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        throw new NotImplementedException("Stream persistence not yet implemented");
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

    private static bool MatchesQuery(FileItem file, SearchQuery query, System.Text.RegularExpressions.Regex? regex, string searchText)
    {
        // Type filters
        Console.WriteLine($"[DEBUG] MatchesQuery - IncludeFiles: {query.IncludeFiles}, IsDirectory: {file.IsDirectory}");
        if (!query.IncludeFiles && !file.IsDirectory)
        {
            Console.WriteLine($"[DEBUG] MatchesQuery - REJECTED: IncludeFiles=false and IsDirectory=false");
            return false;
        }
        if (!query.IncludeDirectories && file.IsDirectory) return false;
        if (!query.IncludeHidden && file.IsHidden) return false;
        if (!query.IncludeSystem && file.IsSystem) return false;

        // Size filters
        if (query.MinSize.HasValue && file.Size < query.MinSize.Value) return false;
        if (query.MaxSize.HasValue && file.Size > query.MaxSize.Value) return false;

        // Date filters
        if (query.MinCreatedDate.HasValue && file.CreatedTime < query.MinCreatedDate.Value) return false;
        if (query.MaxCreatedDate.HasValue && file.CreatedTime > query.MaxCreatedDate.Value) return false;
        if (query.MinModifiedDate.HasValue && file.ModifiedTime < query.MinModifiedDate.Value) return false;
        if (query.MaxModifiedDate.HasValue && file.ModifiedTime > query.MaxModifiedDate.Value) return false;

        // Extension filter
        if (!string.IsNullOrEmpty(query.ExtensionFilter))
        {
            if (!file.Extension.Equals(query.ExtensionFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Text search
        if (!string.IsNullOrEmpty(searchText))
        {
            var targetText = query.SearchFileNameOnly
                ? file.Name.ToLowerInvariant()
                : file.FullPath.ToLowerInvariant();

            Console.WriteLine($"[DEBUG] MatchesQuery - SearchText: '{searchText}', TargetText: '{targetText}', SearchFileNameOnly: {query.SearchFileNameOnly}");

            if (regex != null)
            {
                var regexMatch = regex.IsMatch(targetText);
                Console.WriteLine($"[DEBUG] MatchesQuery - Regex match result: {regexMatch}");
                return regexMatch;
            }
            else
            {
                var comparison = query.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                var containsResult = targetText.Contains(searchText, comparison);
                Console.WriteLine($"[DEBUG] MatchesQuery - Contains result: {containsResult} (CaseSensitive: {query.CaseSensitive})");
                return containsResult;
            }
        }

        return true;
    }

    private void UpdateIndices(FileItem fileItem, IndexOperation operation)
    {
        var filePath = fileItem.FullPath.ToLowerInvariant();
        var directoryPath = fileItem.DirectoryPath.ToLowerInvariant();
        var extension = fileItem.Extension.ToLowerInvariant();

        if (operation == IndexOperation.Add)
        {
            // Update directory index
            _directoryIndex.AddOrUpdate(directoryPath,
                new HashSet<string> { filePath },
                (key, existing) =>
                {
                    existing.Add(filePath);
                    return existing;
                });

            // Update extension index
            if (!string.IsNullOrEmpty(extension))
            {
                _extensionIndex.AddOrUpdate(extension,
                    new HashSet<string> { filePath },
                    (key, existing) =>
                    {
                        existing.Add(filePath);
                        return existing;
                    });
            }
        }
        else if (operation == IndexOperation.Remove)
        {
            // Update directory index
            if (_directoryIndex.TryGetValue(directoryPath, out var directoryFiles))
            {
                directoryFiles.Remove(filePath);
                if (directoryFiles.Count == 0)
                {
                    _directoryIndex.TryRemove(directoryPath, out _);
                }
            }

            // Update extension index
            if (!string.IsNullOrEmpty(extension) && _extensionIndex.TryGetValue(extension, out var extensionFiles))
            {
                extensionFiles.Remove(filePath);
                if (extensionFiles.Count == 0)
                {
                    _extensionIndex.TryRemove(extension, out _);
                }
            }
        }
    }

    private void UpdateMemoryUsage(FileItem fileItem, IndexOperation operation)
    {
        // Rough estimation of memory usage
        var estimatedSize = fileItem.FullPath.Length * 2 +
                           fileItem.Name.Length * 2 +
                           fileItem.DirectoryPath.Length * 2 +
                           100; // Base object overhead

        if (operation == IndexOperation.Add)
        {
            Interlocked.Add(ref _memoryUsage, estimatedSize);
        }
        else if (operation == IndexOperation.Remove)
        {
            Interlocked.Add(ref _memoryUsage, -estimatedSize);
        }
    }

    private void RecalculateMemoryUsage()
    {
        var totalSize = _fileIndex.Values.Sum(f =>
            f.FullPath.Length * 2 + f.Name.Length * 2 + f.DirectoryPath.Length * 2 + 100);

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
            _indexLock.Dispose();
            _disposed = true;
        }
    }

    private enum IndexOperation
    {
        Add,
        Remove
    }
}