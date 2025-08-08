using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace FastFind.Windows.Implementation;

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

        // Collect candidates first while holding the lock, then release it
        IEnumerable<FileItem> candidates;

        _indexLock.EnterReadLock();
        try
        {
            // Get search candidates synchronously while holding the lock
            candidates = GetSearchCandidatesSync(query).ToList();
        }
        finally
        {
            _indexLock.ExitReadLock();
        }

        // Now process candidates without holding the lock
        var regex = query.GetCompiledRegex() ?? query.GetWildcardRegex();
        var searchText = query.SearchText?.ToLowerInvariant() ?? string.Empty;
        var matchCount = 0;

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (query.MaxResults.HasValue && matchCount >= query.MaxResults.Value)
                break;

            if (MatchesQuery(candidate, query, regex, searchText))
            {
                matchCount++;
                yield return candidate;
            }

            // Yield control periodically for better responsiveness
            if (matchCount % 100 == 0)
                await Task.Yield();
        }
    }

    private IEnumerable<FileItem> GetSearchCandidatesSync(SearchQuery query)
    {
        // Optimize search by using appropriate index
        if (!string.IsNullOrEmpty(query.ExtensionFilter))
        {
            return GetFilesByExtensionSync(query.ExtensionFilter);
        }

        if (query.SearchLocations.Count > 0)
        {
            return GetFilesByLocationsSync(query.SearchLocations);
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

    private IEnumerable<FileItem> GetFilesByLocationsSync(IList<string> locations)
    {
        foreach (var location in locations)
        {
            var normalizedPath = location.ToLowerInvariant().TrimEnd('\\', '/');
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
        if (!query.IncludeFiles && !file.IsDirectory) return false;
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

            if (regex != null)
            {
                return regex.IsMatch(targetText);
            }
            else
            {
                var comparison = query.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                return targetText.Contains(searchText, comparison);
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