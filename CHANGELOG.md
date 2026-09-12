# Changelog

All notable changes to FastFind.NET are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed

- **An exclusion written with forward slashes was ignored while indexing on Windows.** Six places
  compared `ExcludedPaths`, in three different ways, and none of them treated `/` and `\` as the same
  separator — so `C:/proj/bin` excluded nothing, while the same spelling in a *search* query worked.
  One predicate, `PathExclusion`, now answers for all of them: the standard Windows provider
  (indexing, its second filter, and change monitoring), the change-journal provider, the in-memory
  index's file-system fallback scan, both Unix providers, and `SearchQuery.ExcludedPaths` through
  `SearchQueryEvaluator`.
- **The asynchronous Windows enumerator never applied the exclusion list at all** — it checked
  hidden, system and size only, so an excluded path reached the index whenever the provider
  dispatched through it.
- **On Linux and macOS an exclusion matched a directory that merely started with it**: `/data/logs`
  excluded `/data/logsx`. Exclusions are also tested against files now, not only directories.
- **Change monitoring ignored `MonitoringOptions.ExcludedPaths` on three of the four monitors.** The
  change-journal provider and both Unix providers never read the list, so a tree left out of the
  index was pulled back into it the moment anything under it changed. All four now read it through
  `PathExclusion`.
- **A file renamed into an excluded directory left its old path in the index.** A monitor that
  filters on a change's new path drops that event entirely, and the entry for the path the file no
  longer has stays. A rename across the edge of the monitored locations — or of the exclusion list —
  is now reported as what it is from the index's side: leaving is a deletion of the old path,
  arriving is a creation. The fold existed only in the change-journal provider, only for locations;
  it is now `FileChangeScope` in the core package and every monitor uses it.
- **A path stored on Windows read back with separators it was never given.**
  `StringPool.InternPath` rewrote `/` to `\` before interning, so an item built from
  `C:/data/proj` reported `C:\data\proj`. It interns verbatim now, on every platform; identity that
  ignores the difference still comes from the comparers and from `PathExclusion`. The rewrite was
  inert for indexing — every Windows enumerator builds its items through `FileInfo`/`DirectoryInfo`,
  which canonicalise separators before a path reaches the pool — so this changes what a directly
  constructed `FastFileItem` reports, and nothing about an indexed one. Per-entry index retention is
  unchanged at both corpus sizes (387.7 B at 100k, 385.0 B at 300k). A path held in a
  `FastFind.SQLite` store is still folded on Windows, where it is the store's only source of
  separator-insensitive identity.
- **The Unix engine could not pass an exclusion list to its monitor**, because
  `StartMonitoringAsync` never filled the field. It now carries the list the index was built with.
- **A Unix refresh re-indexed exactly what the index had excluded.** `RefreshIndexAsync` built its
  own indexing options with an empty exclusion list and hidden files forced on, because the engine
  kept none of the options it had indexed with; it now retains and reuses them, as the Windows
  engine already did.

### Changed

- **An exclusion matches whole path segments, never a substring.** On Windows the comparison was a
  case-insensitive substring of the full path, so `temp` excluded `C:\attempts` and
  `template.docx`, and `.git` excluded `.github`. An entry is now either a fully qualified path,
  which excludes what sits at or under it, or a name or relative path (`bin`, `src/bin`), which
  excludes any run of whole segments that spells it. Files that a substring match used to drop are
  indexed again.
- **`SearchQuery.ExcludedPaths` accepts a bare name and rejects the excluded directory itself.** It
  was tested against the item's directory with at-or-under semantics only, so `bin` excluded nothing
  and the excluded directory's own entry was still returned.
- **`IndexingOptions.ExcludedPaths` is empty by default.** It held glob patterns — `**/temp/**`,
  `**/bin/**`, `**/node_modules/**` and five more — and nothing in the library has ever interpreted
  `**`, on any platform, so they excluded nothing. They are removed rather than made to work:
  activating them would have silently dropped `bin`, `obj`, `temp`, `cache` and `packages` from
  every index that kept the defaults. No result changes. `ExcludedExtensions` is unaffected — those
  defaults have always been honoured.
- `PathExclusion` is public: a provider outside this library reads an exclusion list the same way
  the built-in ones do.

## [2.3.0] - 2026-09-11

### Changed

- **`FastFileItem` no longer keeps its full path; it rebuilds it from the directory and the name.**
  The full path is unique per file, so interning it deduplicated nothing, and it was the largest
  string an item held. When the path is exactly directory, separator and name — as every provider
  in this library produces it — only those two are kept and `FullPath` is composed on read; any
  other path is still interned, so `FullPath` reads back as it was stored in every case. Measured,
  one process per measurement, on top of the index changes below: the Windows in-memory index
  **676 → 388 bytes an entry at 100,000 and 683 → 385 at 300,000**; the Unix engine, end to end on
  a real tree, **626 → 341 at 101,010 and 642 → 348 at 303,030**. Consequences:
  - Each read of `FullPath` allocates. Read it once into a local where a loop uses it repeatedly.
    Matching a query's text against the full path does not allocate — the path is composed on the
    stack — and costs about 15% more on a full scan that matches text against every full path;
    a name-only scan is unaffected.
  - `FastFileItem.FullPathId` is `[Obsolete]`. It still returns an id `StringPool.Get` resolves to
    the full path, interning the path on demand to do so.
  - Equality and the hash code still follow the full path, ordinally.
- **The Windows in-memory index holds a quarter of the memory per entry, and scans about four times
  faster.** It stored each entry as a `FileItem` object keyed by a lower-cased copy of its path, kept
  a second lower-cased copy in its directory and extension maps, and built a path trie with a node
  for every file — about 2,760 bytes an entry, more than half of it the trie. It now stores the
  compact `FastFileItem` grouped by directory, keyed by the entry's own directory and name, with
  case-insensitive identity coming from the comparer rather than from copies. Measured with 99-character
  paths, 100 files to a directory, one process per measurement: **2,758 → 676 bytes an entry at
  100,000 entries, 2,770 → 683 at 300,000**; a query that scans every entry, best of ten runs,
  **135–141 → 24–45 ms at 100,000 and 560–573 → 125–132 ms at 300,000**, allocating an eighth as
  much. Where every file has a
  directory of its own, an entry costs about 1,070 bytes. A path spelled with forward slashes
  (`C:/data/a.txt`) now finds, updates and removes its entry; it used to miss it.
- **The Unix engine's own index holds a fifth less memory per entry and scans three to six times
  faster.** Without a supplied index it kept `FileItem` objects in a dictionary and converted each
  one back to a `FastFileItem` — re-interning four strings — for every candidate of every search.
  It now keeps `FastFileItem` values in the same directory-grouped table as the Windows index, as
  an `ISearchIndex`, so the engine has one code path whether or not it was given an index. Measured
  end to end on Linux, indexing a real tree of 99-character paths, one process per measurement:
  **796 → 626 bytes an entry at 101,010 entries and 817 → 642 at 303,030**; a full scan, best of
  ten, **91–138 → 62–64 ms and 483–899 → 145–150 ms**, allocating a sixth as much.

### Fixed

- `RefreshIndexAsync` on the Unix engine without a supplied index removed entries by bare path
  prefix, so refreshing `/src/app` also dropped everything under `/src/app-tests` and did not put it
  back.
- A query with overlapping `SearchLocations` — `C:\a` and `C:\a\b` — returned an entry once for
  each location holding it, on the Windows in-memory index.
- The Windows in-memory index, when given a store, persisted the first *n* items of a batch where
  *n* was the number it added — not the items it added, whenever the batch held an entry the index
  already had.

## [2.2.0] - 2026-09-11

### Fixed

- **The Windows MFT provider gave many files a wrong path, or dropped them.** It placed files in
  batches while it was still discovering directories, and a file whose parent directory is
  enumerated later — journal enumeration runs in file-reference order, which does not put a
  directory before its contents — had its path cut short and rooted at the drive:
  `D:\src\App\Models\Item.cs` was indexed as `D:\Item.cs`. Files sharing a name then collided on
  that one path. On a whole-drive index of 1.6 million entries, **36% of a sample pointed at paths
  that do not exist**; an index of one folder silently lost the same files, since their wrong paths
  fell outside it. Paths are now built only from a parent chain that reaches the volume root; an
  entry whose chain is not yet complete waits until enumeration ends. The same sample now finds
  0.04% missing — files deleted while the index was built. An entry beneath a directory the
  enumeration never returns, such as the `$`-prefixed metadata directories, is left out rather
  than given a guessed path.

  The directory-path cache was also shared, unsynchronised, across drives enumerated in parallel,
  and every NTFS volume's root is record 5, so a multi-drive index could place one drive's
  directories under another. Each volume now resolves with its own state.
- **Real-time monitoring on the Windows MFT path reported no changes.** It placed each change by
  passing the first letter of the *file name* as the drive letter to a directory cache nothing
  filled, so every change got a path like `r:\readme.md` and the location filter discarded it.
  Measured on an elevated engine: creating, renaming and deleting files under a monitored folder
  produced **0 events**, and the index kept the deleted file and never gained the new ones. Each
  change is now placed by asking Windows where its parent directory is (`OpenFileById` with
  `GetFinalPathNameByHandle`), which needs no copy of the volume's directory tree. The same
  operations now produce one event each and the index follows them. Also in the monitor:
  - A rename is one `Renamed` event carrying both paths, so the index drops the old entry; it
    previously kept it. A rename across the edge of the monitored locations is reported as the
    deletion or creation it is from inside them.
  - Creations, modifications and deletions are reported once, from the journal's closing record,
    instead of once per intermediate record.
  - The newest record was re-read on every poll until a newer one arrived — a deletion was reported
    seven times in four seconds — because each read restarted at that record's own sequence number.
    Reads now resume where the journal says the next record is.
  - Monitoring can be stopped and started again. The monitor was shared by the provider and could
    not restart: its cancellation source stayed cancelled and its channel stayed completed.
  - Version 3 journal records are skipped rather than read at version 2 offsets.
- **Renaming or deleting a directory left everything beneath it in the index.** A file system
  reports the change once, for the directory, and the engines applied it to that one entry: after
  a rename every descendant kept a path that no longer exists and the contents never appeared at
  the new location; after a deletion the descendants stayed searchable. Both engines now carry the
  change down the tree, on every index — in-memory, store-backed, and the Unix engine's own. Also:
  - The standard Windows provider reported a rename without its old path — an `EnqueueRename`
    helper that carries it existed and was never called — so even a renamed *file* left its old
    entry behind.
  - `GetByDirectoryAsync(recursive: true)` on the in-memory index returned only direct children.
  - The SQLite store's recursive directory query matched by bare prefix, so `C:\src\App` also
    returned `C:\src\App.Tests`, and an `_` in a directory name matched any character.

  Verified live on both Windows providers and on Linux: after renaming `proj` to `renamed` and
  deleting `junk`, `leaf.cs` is found only at `renamed\src\deep\leaf.cs` and nothing under `junk`
  remains.
- **The Windows engine cut every indexing run short at 30 seconds, and said nothing.** It bounded
  the whole run with `WindowsSearchEngineOptions.FileOperationTimeout`, whose optimised default was
  30 or 60 seconds depending on memory — read from `GC.GetTotalMemory`, the managed heap in use,
  so 30 on every machine. A run that hit it raised neither `Completed` nor `Failed`. Measured on a
  whole-drive MFT index: **726,000 of 1,618,541 entries at 30.2 s**, then silence; the index looked
  finished. Indexing now has no time limit unless `IndexingTimeout` sets one, and a run cut short
  by it raises `Failed` naming the option, keeping what it indexed. The same whole-drive run now
  completes (67.6 s). The memory reading behind the other optimised defaults (concurrency, cache
  sizes) is the machine's available memory now, as intended.
- **A location or scope written with forward slashes did not match on Windows.** `C:/data` is a
  valid Windows path, but it was compared as written against stored backslash paths. On the MFT
  path a location that matched nothing left its drive unfiltered, so indexing `C:/data` indexed all
  of C: (4.5 million entries, measured); and a query `BasePath` or `SearchLocations` entry written
  that way matched nothing. Locations are normalised, and scopes are compared with separators folded
  on Windows only — stored paths are not rewritten, and elsewhere a backslash stays an ordinary
  file name character.
- **On the Windows MFT path, every item's timestamps were 1601-01-01, so date filters matched
  nothing — silently.** That provider enumerates the volume with `FSCTL_ENUM_USN_DATA` and read the
  record's `TimeStamp` as the file's creation, modification and access time. The specification
  requires that field to be zero for this call, and in any case it records when a journal entry was
  logged, not when a file was written. A `MinModifiedDate` of 2020 therefore excluded every file,
  and the search reported success with no results. The standard provider, used when the process is
  not elevated, was unaffected — so the same query answered correctly or not depending on the
  process's privileges. Present in 1.4.0, 2.0.0 and 2.1.0.
- **Date bounds are compared in UTC.** Item timestamps are UTC, but a `SearchQuery` date bound was
  compared by raw ticks whatever its `Kind`, so a bound built from local time was off by the UTC
  offset. `MinCreatedDate`, `MaxCreatedDate`, `MinModifiedDate` and `MaxModifiedDate` now convert
  to UTC when set — an `Unspecified` value is taken as local time, as `DateTime.ToUniversalTime`
  does — and read back as UTC.
- `MftCompactRecord.ToMftFileRecord` returned the modification time as the creation and access
  times. It now returns them unset: the compact form does not keep them.
- `ISearchEngine.RefreshIndexAsync` with explicit locations dropped `MaxDepth`, `FollowSymlinks` and
  the metadata setting from the options it re-indexed with.
- **The MFT provider's location scoping matched by bare string prefix.** Indexing `C:\src\App`
  also indexed `C:\src\App.Tests` and `C:\src\Application`, and a location given with a trailing
  separator excluded its own directory entry. Locations now match at a path separator, through the
  same check the query evaluator uses.
- The USN record parser accepted version 3 records but read them at version 2 offsets, which do not
  apply to them. It now rejects them. Enumeration requests version 2 records, so none were seen in
  practice.

### Added

- `SearchIndexTreeExtensions.RemoveTreeAsync` and `MoveTreeAsync` apply a directory's deletion or
  move to everything an `ISearchIndex` holds beneath it, keeping sizes, times and attributes. They
  are what the engines use, and work on any index, including a custom one.
- **`IndexingOptions.CollectFileMetadata`** — reads each entry's size and all three timestamps with
  one file system query while indexing, on the one provider whose enumeration lacks them (Windows
  MFT). It replaces `CollectFileSize`, which already paid for that query and kept only the size.
  Directories are covered too; they previously never had a size or time read.
- **A size or date filter the index cannot answer is refused, not answered with nothing.** When the
  engine's last indexing run did not collect metadata, `SearchAsync` returns a result with
  `HasError` set and a message naming `CollectFileMetadata`, where it returned an empty result that
  read as "no such files". `SearchQueryEvaluator.RequiresFileMetadata(query)` tells whether a query
  depends on metadata, and `IFileSystemProvider.ProvidesFileMetadata(options)` whether a provider
  supplies it — a default interface member, so existing implementations need no change.

### Changed

- **`MftSqlitePipeline` and `UsnSqliteSyncService` are `[Obsolete]`.** Both wrote wrong paths into
  the store: the pipeline stored every entry as `<drive>:\<name>`, never resolving its parent
  directories, and the sync service stored a change's bare file name as its full path with an empty
  directory. A search engine composed with the store does both jobs correctly —
  `FastFinder.CreateSearchEngine(store)`, then `StartIndexingAsync`, with `EnableMonitoring` for
  what the sync service did. They remain until the next major version.
- `WindowsSearchEngineOptions.FileOperationTimeout` is `[Obsolete]` in favour of
  `IndexingTimeout`, which says what it bounds and defaults to no limit. Setting the old name still
  sets the limit.
- `UsnChangeRecord` carries the `DriveLetter` of the volume that logged it. A file reference means
  nothing without its volume. The existing constructor remains and leaves it `'\0'`.
- `MftReader.GetFullPath` and `MftReader.BuildPathCache` are `[Obsolete]`. `GetFullPath` returns the
  name at the drive root for any parent it has not cached, and nothing in the library calls either
  any more.
- A timestamp that was not collected is `DateTime.MinValue`, not an invented value, and satisfies
  no date bound — an unknown time is neither before nor after anything. `FastFileItem` keeps an
  unspecified or local `MinValue` as unset rather than shifting it by the UTC offset.
- `IndexingOptions.CollectFileSize` is `[Obsolete]` and forwards to `CollectFileMetadata`.
  `IndexingOptions.FileSizeCollectionBatchSize` is `[Obsolete]`: it was documented as the batch size
  of a parallel collection pass that has never existed, and it has no effect.

### Known limitations

- A size that was not collected is still 0, which a real empty file also has, so an individual
  entry whose metadata could not be read still passes a `MaxSize` bound. The engine-level refusal
  covers the systematic case — an index built without metadata — but not isolated unreadable
  entries.
- The refusal depends on the engine knowing how its index was built. An engine that loads a store
  without indexing in the same process has no such knowledge, and answers date bounds from whatever
  the store holds. Items stored by earlier versions from the MFT path carry 1601-01-01 until they
  are re-indexed.
- `IndexingOptions.MaxFileSize` cannot be applied on the MFT path without `CollectFileMetadata`,
  since the size is unknown when the limit would be checked.

## [2.1.0] - 2026-09-10

### Added

- **The Linux and macOS engines can be composed with a persistence store.** They previously threw
  `NotSupportedException` for any store or custom index, so `PersistenceMode.QueryFromStore` and
  `ISearchEngine.Index` were Windows-only. `UnixSearchEngineImpl` now takes an `ISearchIndex` and
  routes its reads, writes, monitoring updates, refresh, statistics and optimize through it when one
  is supplied, keeping its in-memory dictionary as the default path.
  `PersistenceMode.MirrorInMemory` is still rejected, and still loudly: it needs a shared in-memory
  `ISearchIndex` to mirror into, which this engine does not have.
- `FastFind.SQLite` now has automated coverage on Linux. It was exercised on Windows only.

### Changed

- **`ISearchEngine.SaveIndexAsync` / `LoadIndexAsync` on Linux and macOS throw
  `InvalidOperationException` instead of `NotSupportedException` when the engine has no store.** The
  operation is supported on those platforms now; an engine without a store simply has nothing to save
  to, and the message names the fix. This matches the Windows engine. Code catching
  `NotSupportedException` to detect "this platform cannot persist" needs updating.

### Known limitations

- `PersistenceMode.MirrorInMemory` is still not supported on Linux or macOS. It needs a shared
  in-memory `ISearchIndex` to mirror into, and those engines keep their default in-memory index in a
  plain dictionary. Composing with it throws rather than silently not mirroring; `QueryFromStore`,
  the default, is supported.
- A store-backed engine reports `IndexingStatistics.TotalSize` as 0. Summing sizes would mean reading
  every row, which is what a store-backed index exists to avoid.

### Fixed

- **`SqlitePersistence` could corrupt its own connection under concurrent use.** It held a single
  `SqliteConnection` and created every command on it. `SqliteConnection` is not thread-safe, so
  concurrent calls raced on its internal command list and threw `ArgumentOutOfRangeException` from
  inside `Microsoft.Data.Sqlite` — intermittently, at roughly one run in six of a concurrent
  read/write workload. Present since at least 1.4.0 and shipped in 2.0.0. Each operation now opens
  its own pooled connection, the shape `Microsoft.Data.Sqlite` documents for concurrent use, so
  searching while indexing no longer needs the serialisation that release advised.

  Connection-scoped settings — cache size, temp store, mmap window, busy timeout — are applied to
  every connection rather than once at initialisation, and shared-cache mode was dropped: it changes
  transaction and table locking, and nothing here used the in-memory databases it exists for.

  A transaction opened with `BeginTransactionAsync` is **ambient to the asynchronous flow that
  opened it** — operations issued on that flow before it completes still run inside it, as before.
  Work fanned out concurrently from within an open transaction inherits its connection and is
  therefore not supported, for the same reason a `SqliteConnection` cannot be shared across threads.

  Bulk ingest (`AddBulkOptimizedAsync`, `AddFromStreamAsync`) holds one connection for its whole
  window, and the two are serialised against each other so they queue rather than contend for
  SQLite's single writer. Inside a caller's transaction, bulk batches join it instead of opening a
  nested transaction, which SQLite does not support — previously that combination failed.
- **`SqlitePersistence.Count` could exceed the number of stored rows.** `AddAsync` decides whether
  to increment the count by checking whether the row is already there, and the check and the upsert
  were separate steps: concurrent adds of the same path all read "absent" and all incremented. The
  two now run in one `IMMEDIATE` transaction, so the check happens under the write lock.
- **File-system change notifications blocked a thread pool thread on Linux and macOS.** When the
  engine is backed by a store, each monitored create, modify, delete or rename wrote to it, and the
  write was awaited synchronously from a callback. The whole notification pipeline is already async,
  so the writes are now awaited properly and changes no longer serialise behind store I/O.
- **The Unix engine answered some queries differently from every other backend.** It filtered its
  in-memory index with its own copy of the query predicate rather than through
  `SearchQueryEvaluator`, and the copy had drifted: it compared `BasePath` with an ordinal comparison
  on every platform, and applied its own rules for the extension and location filters. It now
  evaluates through the same predicate as the Windows index and the SQLite store, so a query returns
  the same set on Linux and macOS as it does anywhere else.
- **`SearchQuery.RequiredAttributes` and `ExcludedAttributes` are now honoured.** No backend applied
  them. `RequiredAttributes` demands every requested flag; `ExcludedAttributes` rejects an item
  carrying **any** of them, which is what "attributes that must not be present" means — a filter
  excluding `ReadOnly | Hidden` now excludes an item that is merely read-only.

## [2.0.0] - 2026-09-09

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
- **The SQLite store no longer maintains a full-text index.** It created an FTS5 virtual table and
  kept it current with three triggers, so every write paid for it, while no query ever read it — the
  candidate query narrows with `LIKE`, never `MATCH`. The bulk paths also carried a three-phase
  drop-triggers / insert / rebuild sequence and a recovery routine, all of which existed solely to
  keep that index from corrupting. Measured against 1.4.0 on the same machine, corpus and call
  (`AddBulkOptimizedAsync`, 99-character paths): **bytes per stored entry fall by about 10%** —
  887 → 796 at 50,000 items and 899 → 811 at 200,000. **Ingest throughput is not claimed to change**:
  a 50,000-item run improved (897 → 1,176 items/s) but a 200,000-item run did not reproduce it
  (472 → 442 items/s), and these are single runs, so the honest reading is that the write path does
  less work and stores less, while end-to-end throughput is dominated by something else.
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
- `PersistenceConfiguration.EnableFullTextSearch` is marked `[Obsolete]`. The store maintains no
  full-text index, so setting it changes nothing; previously its only possible effect was to slow
  writes. An index that could serve these queries would need the `trigram` tokenizer — `unicode61`
  cannot match inside a token — which would be a new capability, not a restoration of this one.
- The SQLite schema version is now 3. A version 2 database still carries the full-text table and its
  triggers and is rebuilt without them on open, at the cost of one re-index.

### Known limitations

- The Unix engine cannot be given a persistence store. `UnixSearchEngineImpl` keeps its index in an
  internal dictionary rather than behind an `ISearchIndex`, so composing it with a store or a custom
  index throws `NotSupportedException` rather than silently ignoring the request.
- `SearchQuery.RequiredAttributes` and `ExcludedAttributes` are not honoured by any backend.
- `FastFind.SQLite` has no automated coverage on Linux.

### Dependency floors

- The packages require `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging`
  and `Microsoft.Extensions.Logging.Abstractions` **10.0.12 or later**, and `FastFind.SQLite`
  requires `Microsoft.Data.Sqlite` **10.0.12 or later** — the servicing band these releases were
  built and tested against. A project pinning an earlier 10.0.x of any of them gets NuGet `NU1109`
  downgrade errors on upgrade rather than a resolution, so move those pins to 10.0.12 in the same
  change. The same floors apply to 2.1.0.

## [1.4.0]

- `IndexingOptions.MaxFileCount` and `IndexingPhase.CapReached`.
