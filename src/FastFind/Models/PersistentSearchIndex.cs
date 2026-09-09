using FastFind.Interfaces;

namespace FastFind.Models;

/// <summary>
/// An <see cref="ISearchIndex"/> that keeps its data in an <see cref="IIndexPersistence"/> store and
/// answers queries from it, rather than holding a copy in memory.
/// </summary>
/// <remarks>
/// <para>
/// This is the counterpart to an in-memory index, not a cache in front of one. Nothing here retains
/// items, so resident cost does not grow with the corpus — which is the whole point of pairing a
/// disk-backed store with an engine. An in-memory index paired with a store gets the memory of the
/// former plus the I/O of the latter.
/// </para>
/// <para>
/// Queries are evaluated by <see cref="SearchQueryEvaluator"/>, the same predicate every other
/// backend uses, so a search returns the same set here as it would in memory.
/// </para>
/// <para>
/// This index answers only from the store. It does not scan the live file system for items it has
/// not been given, so a query issued before indexing completes returns only what is indexed so far.
/// That is deliberate: a store-backed index exists to bound memory and to survive restarts, and
/// silently mixing in unindexed file-system results would make its results depend on how far
/// indexing had progressed.
/// </para>
/// <para>
/// The store is the single source of truth, so this type holds no lock of its own; concurrency
/// guarantees are the provider's.
/// </para>
/// </remarks>
public sealed class PersistentSearchIndex : ISearchIndex
{
    private readonly IIndexPersistence _persistence;
    private readonly bool _ownsPersistence;
    private bool _disposed;

    /// <summary>
    /// Wraps a persistence provider as a search index.
    /// </summary>
    /// <param name="persistence">Store that holds the index. Must already be initialised.</param>
    /// <param name="ownsPersistence">
    /// Whether disposing this index also disposes <paramref name="persistence"/>. Pass
    /// <c>false</c> when the caller keeps using the store, or shares it with another index.
    /// </param>
    public PersistentSearchIndex(IIndexPersistence persistence, bool ownsPersistence = false)
    {
        ArgumentNullException.ThrowIfNull(persistence);

        _persistence = persistence;
        _ownsPersistence = ownsPersistence;
    }

    /// <inheritdoc/>
    public long Count => _persistence.Count;

    /// <summary>
    /// Bytes this index retains in managed memory: none. The store's own footprint is reported by
    /// <see cref="IIndexPersistence.GetStatisticsAsync"/>.
    /// </summary>
    public long MemoryUsage => 0;

    /// <inheritdoc/>
    public bool IsReady => !_disposed && _persistence.IsReady;

    /// <inheritdoc/>
    public IIndexPersistence? Persistence => _persistence;

    /// <inheritdoc/>
    public Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.AddAsync(item, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.AddBatchAsync(items, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.RemoveAsync(fullPath, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> UpdateAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.UpdateAsync(item, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);

        var validation = query.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage ?? "Invalid search query.", nameof(query));
        }

        return _persistence.SearchAsync(query, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.GetAsync(fullPath, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> ContainsAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.ExistsAsync(fullPath, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.GetByDirectoryAsync(directoryPath, recursive, cancellationToken);
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.ClearAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _persistence.OptimizeAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stats = await _persistence.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);

        return new IndexStatistics
        {
            TotalItems = stats.TotalItems,
            TotalDirectories = stats.TotalDirectories,
            TotalFiles = stats.TotalFiles,
            MemoryUsageBytes = MemoryUsage,
            PersistenceEnabled = true,
            LastUpdated = stats.LastOptimized
        };
    }

    /// <summary>
    /// Returns the number of items already in the store.
    /// </summary>
    /// <remarks>
    /// There is nothing to load: this index reads from the store on every query. The count is
    /// returned so that a caller which uses the result to report progress still gets a truthful
    /// number.
    /// </remarks>
    public Task<int> LoadFromPersistenceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.FromResult((int)Math.Min(_persistence.Count, int.MaxValue));
    }

    /// <summary>
    /// Returns the number of items in the store.
    /// </summary>
    /// <remarks>
    /// There is nothing to save: writes go straight to the store as they are made, so the index is
    /// never ahead of it.
    /// </remarks>
    public Task<int> SaveToPersistenceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.FromResult((int)Math.Min(_persistence.Count, int.MaxValue));
    }

    /// <summary>
    /// Not supported: watching the file system belongs to the engine, which forwards changes to the
    /// index as ordinary writes.
    /// </summary>
    public Task StartMonitoringAsync(IEnumerable<string> locations, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="StartMonitoringAsync"/>
    public Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsPersistence && _persistence is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsPersistence)
        {
            await _persistence.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
