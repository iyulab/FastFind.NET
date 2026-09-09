using FastFind.Interfaces;
using Microsoft.Extensions.Logging;

namespace FastFind.Models;

/// <summary>
/// How a supplied <see cref="IIndexPersistence"/> is used by the engine's index.
/// </summary>
public enum PersistenceMode
{
    /// <summary>
    /// Queries are answered from the store. The engine holds no in-memory copy, so its footprint
    /// does not grow with the number of indexed files.
    /// </summary>
    /// <remarks>
    /// The trade-off is per-query I/O instead of in-memory lookups. Choose this to bound memory, to
    /// keep an index across restarts, or for a corpus too large to hold.
    /// </remarks>
    QueryFromStore = 0,

    /// <summary>
    /// Queries are answered from an in-memory index, and every write is also sent to the store.
    /// </summary>
    /// <remarks>
    /// This keeps in-memory query speed and adds durability, but it costs the memory of the
    /// in-memory index <b>plus</b> the store — it does not reduce footprint. Choose it only when the
    /// corpus comfortably fits in memory and the store is wanted for persistence alone.
    /// </remarks>
    MirrorInMemory = 1
}

/// <summary>
/// Everything a search engine needs to be composed.
/// </summary>
/// <remarks>
/// Passed to the factory registered for a platform, so a new composition point can be added here
/// without changing every factory signature again.
/// </remarks>
public sealed record SearchEngineOptions
{
    /// <summary>
    /// Logger factory for the engine and its components. Optional.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Store to keep the index in. When set, <see cref="PersistenceMode"/> decides whether queries
    /// are answered from it or from an in-memory copy that mirrors into it.
    /// </summary>
    /// <remarks>
    /// The store must already be initialised — the engine does not call
    /// <see cref="IIndexPersistence.InitializeAsync"/> on a provider it did not create, because a
    /// caller may be sharing it.
    /// </remarks>
    public IIndexPersistence? Persistence { get; init; }

    /// <summary>
    /// How <see cref="Persistence"/> is used. Ignored when no store is supplied.
    /// </summary>
    public PersistenceMode PersistenceMode { get; init; } = PersistenceMode.QueryFromStore;

    /// <summary>
    /// A fully-formed index to use instead of the platform default. Takes precedence over
    /// <see cref="Persistence"/>.
    /// </summary>
    /// <remarks>
    /// For a caller with an index implementation of their own. Most callers want
    /// <see cref="Persistence"/> instead, which composes one.
    /// </remarks>
    public ISearchIndex? Index { get; init; }

    /// <summary>
    /// Whether disposing the engine also disposes a supplied <see cref="Persistence"/> or
    /// <see cref="Index"/>. Defaults to <c>false</c>: what the caller constructed, the caller owns.
    /// </summary>
    public bool DisposeSuppliedComponents { get; init; }

    /// <summary>
    /// Options carrying nothing but a logger factory — the shape of the original factory signature.
    /// </summary>
    public static SearchEngineOptions ForLogger(ILoggerFactory? loggerFactory) =>
        new() { LoggerFactory = loggerFactory };

    /// <summary>
    /// Resolves the index these options ask for, or <c>null</c> to let the platform build its
    /// default.
    /// </summary>
    /// <remarks>
    /// Shared by the platform factories so that "index, else store, else default" is decided in one
    /// place rather than once per platform.
    /// </remarks>
    public ISearchIndex? ResolveIndex()
    {
        if (Index is not null) return Index;

        if (Persistence is not null && PersistenceMode == Models.PersistenceMode.QueryFromStore)
        {
            return new PersistentSearchIndex(Persistence, DisposeSuppliedComponents);
        }

        // MirrorInMemory: the platform builds its in-memory index and is given the store to write
        // through to.
        return null;
    }
}
