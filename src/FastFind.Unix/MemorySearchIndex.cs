using System.Runtime.CompilerServices;
using FastFind.Indexing;
using FastFind.Interfaces;
using FastFind.Models;

namespace FastFind.Unix;

/// <summary>
/// The Unix engine's own index when it is not composed with one: every entry as a
/// <see cref="FastFileItem"/> in a <see cref="FileEntryTable"/>.
/// </summary>
/// <remarks>
/// <para>
/// Being an <see cref="ISearchIndex"/> is what lets the engine have one code path. It used to keep a
/// dictionary of <see cref="FileItem"/> objects beside the supplied-index path, and every operation
/// was written twice — including subtree removal, which the dictionary side did by bare prefix on
/// refresh, so refreshing <c>/src/app</c> also dropped <c>/src/app-tests</c>.
/// </para>
/// <para>
/// Adding an entry that exists replaces it, as the SQLite store does. Nothing is persisted.
/// </para>
/// </remarks>
internal sealed class MemorySearchIndex : ISearchIndex
{
    private readonly FileEntryTable _entries = new();
    private long _memoryUsage;
    private bool _disposed;

    /// <summary>Every entry, for callers that need more than a query can express.</summary>
    internal IEnumerable<FastFileItem> Entries => _entries.All();

    public long Count => _entries.Count;

    /// <summary>
    /// Rough estimate: the item, its name, and its table slot. Directory strings are shared by every
    /// entry beneath them and are not counted per entry.
    /// </summary>
    public long MemoryUsage => Interlocked.Read(ref _memoryUsage);

    public bool IsReady => !_disposed;

    public IIndexPersistence? Persistence => null;

    public Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Put(item);
        return Task.CompletedTask;
    }

    public Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var written = 0;
        foreach (var item in items)
        {
            if ((written & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            Put(item);
            written++;
        }

        return Task.FromResult(written);
    }

    public Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryRemove(fullPath, out var removed)) return Task.FromResult(false);

        Interlocked.Add(ref _memoryUsage, -EstimateSize(removed));
        return Task.FromResult(true);
    }

    public Task<bool> UpdateAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Put(item));
    }

    public async IAsyncEnumerable<FastFileItem> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var validation = query.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage ?? "Invalid search query.", nameof(query));
        }

        var textMatcher = SearchQueryEvaluator.CreateTextMatcher(query);
        var yielded = 0;

        foreach (var item in Candidates(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SearchQueryEvaluator.Matches(item, query, textMatcher)) continue;

            yield return item;
            if (++yielded % 1000 == 0) await Task.Yield();
        }
    }

    public Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.FromResult(_entries.TryGet(fullPath, out var item) ? item : (FastFileItem?)null);
    }

    public Task<bool> ContainsAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.FromResult(_entries.Contains(fullPath));
    }

    public async IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(
        string directoryPath,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        foreach (var item in recursive ? _entries.Under(directoryPath) : _entries.InDirectory(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _entries.Clear();
        Interlocked.Exchange(ref _memoryUsage, 0);
        return Task.CompletedTask;
    }

    public Task OptimizeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        long total = 0, directories = 0;
        var extensions = new HashSet<int>();
        foreach (var item in _entries.All())
        {
            total++;
            if (item.IsDirectory) directories++;
            else if (!string.IsNullOrEmpty(item.Extension)) extensions.Add(item.ExtensionId);
        }

        return Task.FromResult(new IndexStatistics
        {
            TotalItems = total,
            TotalFiles = total - directories,
            TotalDirectories = directories,
            MemoryUsageBytes = MemoryUsage,
            PersistenceEnabled = false,
            LastUpdated = DateTime.UtcNow,
            UniqueExtensions = extensions.Count,
        });
    }

    public Task<int> LoadFromPersistenceAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("This index has no persistence store.");

    public Task<int> SaveToPersistenceAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("This index has no persistence store.");

    public Task StartMonitoringAsync(IEnumerable<string> locations, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _entries.Clear();
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The entries a query could match, narrowed by its scope; the evaluator decides the rest.
    /// </summary>
    private IEnumerable<FastFileItem> Candidates(SearchQuery query)
    {
        // One scope narrows. Several may overlap — /a and /a/b — and narrowing each would return an
        // entry once per scope holding it, so they take one pass and the evaluator decides.
        var scope = !string.IsNullOrEmpty(query.BasePath) ? query.BasePath
            : query.SearchLocations.Count == 1 ? query.SearchLocations[0]
            : null;

        if (scope is null) return _entries.All();
        return query.IncludeSubdirectories ? _entries.Under(scope) : _entries.InDirectory(scope);
    }

    /// <returns><see langword="true"/> if an entry was replaced.</returns>
    private bool Put(in FastFileItem item)
    {
        var replaced = _entries.Set(item, out var previous);
        if (replaced) Interlocked.Add(ref _memoryUsage, -EstimateSize(previous));
        Interlocked.Add(ref _memoryUsage, EstimateSize(item));
        return replaced;
    }

    private static long EstimateSize(in FastFileItem item) => item.Name.Length * 2L + 128;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
