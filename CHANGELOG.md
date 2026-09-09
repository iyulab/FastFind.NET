# Changelog

All notable changes to FastFind.NET are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

This release changes behaviour that consumers may be relying on. Read **Breaking changes** before
upgrading. The headline is that a search engine can now keep its index in a persistence store instead
of in memory, and that several APIs which accepted input and silently discarded it now either work or
fail loudly.

### Added

- **A composition point for persistence.** `FastFinder.CreateSearchEngine(IIndexPersistence, …)`
  creates an engine whose index lives in the given store. `SearchEngineOptions` carries the full set
  of composition inputs — logger factory, store, index, and disposal ownership — so future options
  do not change factory signatures again.
- **`PersistenceMode`**, choosing what a supplied store is used for:
  - `QueryFromStore` (default) — queries are answered from the store and the engine holds no copy,
    so its footprint does not grow with the number of indexed files.
  - `MirrorInMemory` — an in-memory index answers queries and every write also reaches the store.
    Keeps in-memory speed and adds durability, at the cost of memory for the index *plus* the store.
- **`PersistentSearchIndex`** (`FastFind.Core`) — an `ISearchIndex` that answers from an
  `IIndexPersistence` and retains nothing itself. Measured over 500,000 files: `MemoryUsage` reports
  0 throughout, and a search issued after indexing returns results without growing the managed heap.
- **`SearchQueryEvaluator`** (`FastFind.Core`) — the single definition of what satisfies a
  `SearchQuery`. Every index and persistence provider evaluates through it, so a query returns the
  same set whichever backend answers it. A backend may narrow candidates first, but only in ways
  that cannot exclude a match the evaluator accepts.
- **`ISearchEngine.Index`** — the index an engine searches, or `null` for an engine that keeps its
  index internally. Without it, an engine composed with a store had no reachable index.
- Schema versioning for the SQLite store. A database written by a different version is rebuilt on
  open; the store is a cache of the file system, so a rebuild costs one re-index.

### Fixed

- **Paths were corrupted on Linux and macOS.** `FastFileItem.FullPath` and `DirectoryPath` rewrote
  `/` to `\`, so `/home/user/docs/file.txt` came back as `\home\user\docs\file.txt` — unusable for
  I/O. Separator folding now happens only on Windows, where both are valid.
- **Paths differing only in case were merged.** Interning lower-cased the deduplication key, so
  `Alpha.txt` and `ALPHA.TXT` resolved to whichever was seen first. On a case-sensitive file system
  the second file's reported path was simply wrong. Deduplication is now exact and casing is
  preserved; the SQLite store likewise keeps original casing, with case-insensitive path identity on
  Windows provided by the column collation.
- **`SqlitePersistence.SearchAsync` threw on wildcard patterns.** A search for `*.cs` raised
  `SqliteException: fts5: syntax error`.
- **`SqlitePersistence.SearchAsync` could not match inside a token.** Searching `port` returned
  nothing for `report.txt`, because full-text tokenisation matches whole tokens and prefixes.
  Substring matching now behaves as it does in memory.
- **`SqlitePersistence.SearchAsync` silently ignored six `SearchQuery` fields**: `UseRegex`,
  `CaseSensitive`, `MinCreatedDate`, `MaxCreatedDate`, `BasePath`, and `SearchLocations` /
  `IncludeSubdirectories`. Results came back unfiltered with no error.
- **`MaxResults` limited candidates rather than matches**, so any query with a filter returned fewer
  results than requested while more matches remained in the store.
- **`ExcludedPaths` was ignored** by every backend. It is now honoured.
- **`PersistenceConfiguration.PageSize` had no effect** in the default configuration. The page-size
  pragma was emitted after the journal-mode pragma, and SQLite ignores a page-size change once a
  database is in WAL mode — which is the default.
- **`SaveIndexAsync` never saved anything.** It ignored its `filePath` argument and returned
  successfully when no store was configured; since no store *could* be configured before this
  release, it was an unconditional no-op for every consumer.
- **`StringPool.Cleanup()` corrupted live results.** It evicted interned strings whose ids the index
  still held, so affected files reported empty paths and names afterwards. It was called by
  `OptimizeIndexAsync` under `OptimizationScenario.LowMemory`.
- `SQLitePCLRaw` is pinned past [CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)
  (high severity, memory corruption in SQLite before 3.50.2). `FastFind.SQLite` now ships SQLite
  3.53.4.

### Changed

- **Memory.** Interning holds one mapping per unique string instead of three dictionary entries and
  two copies of every path, and no longer allocates on a cache hit. Measured over 500,000 files with
  99-character paths: **484 MB → 288 MB** of managed heap (1,016 → 605 bytes per file). Against the
  released 1.4.0 the figure is 378 MB.
- `StringPoolStats.MemoryUsageBytes` now includes per-entry object, dictionary and array overhead. It
  previously counted only character data and under-reported the pool's real cost by roughly half.
- `PersistenceConfiguration` documentation corrected against measured behaviour: `CacheSize` is in
  **KiB, not pages** (the default is ~9.8 MiB, not ~39 MiB); `MmapSize = 0` with `UseMmap` enabled
  means a **256 MiB** window, not none; `PageSize` applies only when the database file is created.
- All package dependencies brought to current, including two test-tooling majors.

### Breaking changes

- `ISearchEngine.SaveIndexAsync` / `LoadIndexAsync` no longer take a `filePath`, and return
  `Task<int>` (the item count). No implementation ever read the parameter, and where an index is
  stored is now decided when the engine is composed. **Callers passing a path will not compile.**
- Both now **throw** instead of succeeding quietly: `InvalidOperationException` when the engine has
  no store (the message names the fix), `NotSupportedException` where an engine cannot persist an
  index at all. **Code that appeared to work will now throw** — it was never saving anything.
- `ISearchIndex.SearchAsync` throws `ArgumentException` for a query `SearchQuery.Validate()` rejects,
  where it previously returned an empty result indistinguishable from a query that matched nothing.
- `FastFinder.RegisterSearchEngineFactory` takes `Func<SearchEngineOptions, ISearchEngine>` instead
  of `Func<ILoggerFactory?, ISearchEngine>`. Migrate a registration to
  `options => Build(options.LoggerFactory)`. A compatibility overload was considered and rejected:
  two overloads differing only in delegate type make every lambda registration ambiguous.
- `ISearchEngine` gained an `Index` property — a breaking change for any external implementation of
  the interface.
- `PersistenceConfiguration.AutoSync` and `SyncInterval` are marked `[Obsolete]`. No provider has
  ever read them; setting them changes nothing.
- `StringPool.Cleanup()` is marked `[Obsolete]` and does nothing. Eviction cannot be made safe while
  callers hold interned ids. Use `Reset()` when no interned value is still in use.
- Searching by path is now case-sensitive on Linux and macOS, matching those file systems. It was
  effectively case-insensitive because casing was being destroyed at interning time.

### Known limitations

- The Unix engine cannot be given a persistence store. `UnixSearchEngineImpl` keeps its index in an
  internal dictionary rather than behind an `ISearchIndex`, so composing it with a store or a custom
  index throws `NotSupportedException` rather than silently ignoring the request.
- `SearchQuery.RequiredAttributes` and `ExcludedAttributes` are not honoured by any backend.
- `FastFind.SQLite` has no automated coverage on Linux.

## [1.4.0]

- `IndexingOptions.MaxFileCount` and `IndexingPhase.CapReached`.
