# Changelog

All notable changes to FastFind.NET are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

**`IFileSystemProvider.GetAttributesAsync(path)`** — a path's attributes and nothing else, `null` when they cannot be read. The default
implementation answers through `GetFileInfoAsync`, so a provider that has nothing cheaper (or a test double) needs no change; the Windows,
change-journal, Linux and macOS providers read the attributes with one call.

### Changed

**A monitored change no longer costs more the deeper it is, on Windows and on Linux/macOS.** A change a monitor reports is checked against every
directory between the monitored location and the item (one attribute read each, so that a file created in `.git` is not indexed). The Windows engine
made each of those reads through `GetFileInfoAsync`, which hops to the thread pool and makes several file-system calls to build a record of which one
attribute was used; it now asks `GetAttributesAsync` (about four times cheaper per read). Both engines also remember what they read of each directory
for a few seconds and forget a directory when the monitor reports a change to it, so a burst reads each directory above its changes once. A directory
whose attributes change without the monitor reporting it is believed for at most five seconds. Measured on a burst of 5,000 changes in 20 directories,
per change, lowest to highest median of three runs: nine levels deep 541-1,764 microseconds before, 4-9 after; three levels 265-820 before, 3-6 after;
twenty levels 2,732-3,241 before, 14-20 after (the change's own file-info call: 89-392). Ranges and method in `docs/BENCHMARKS.md`.

**The Linux and macOS providers bound a followed chain of links, as the Windows walk does.** With `FollowSymlinks` set, a link back to an ancestor
was entered again beneath itself until the operating system refused the path (about forty crossings on Linux): forty times the work and as many
duplicate entries for the same tree. A walk now stops descending after eight links in a row and still lists the link it does not enter; the limit
is one definition shared by every provider, so the same tree gives the same entries on every platform.

**A case-insensitive text query is answered with the runtime's ordinal-ignore-case search.** The query evaluator and
`FastFileItem.MatchesName`, `MatchesPath` and `MatchesExtension` called `SIMDStringMatcher`, which measured 3.9 times slower than
`string.Contains(OrdinalIgnoreCase)` on a long string. They now call the runtime. Measured end to end with the build before and
the build after run alternately three times (ranges in `docs/BENCHMARKS.md`), the median of a Windows-index query that reads the
whole corpus fell to 0.34-0.87 of what it was in every one of nine cells; the Unix engine's index, which runs the same evaluator, shows
overlapping ranges in most cells and no gain that three runs can separate from noise in all but two. The two folds differ on five compatibility characters only (the Kelvin, Angstrom and Ohm signs, capital sharp
s, U+03F4): a name containing U+212A is no longer matched by a query for `k`, as `OrdinalIgnoreCase` has always read it.
`SIMDStringMatcher` itself is unchanged and still public; `MatchesWildcard` still uses it.

**Text that described SIMD as the search path's engine now says what the code does.** The package description, the README, the
documentation comments on `ISearchIndex.SearchAsync`, `IndexConfiguration.UseSIMD` and `WindowsSearchEngineOptions.EnableSIMDAcceleration`,
and the feature list `FastFinder` reports no longer claim SIMD acceleration of search. `UseSIMD` and `EnableSIMDAcceleration` were never
read by the library; they now say so (they remain, so code that sets them still compiles).

## [2.6.0] - 2026-11-14

### Changed

**An elevated Windows process no longer pays the whole-volume read for a small folder.** With administrator rights the Auto
provider always used the change-journal pass, which reads the entire volume whatever the location: 7-13 s on the volumes
measured, for a folder of 6 entries as for one of 2.2 million. The directory walk costs per entry instead (about 25-100
microseconds), so a folder of a few thousand entries takes 0.03 s to walk and 7 s to read from the journal, and a folder of
two million takes about a minute to walk and 9 s from the journal; where the two cross differs by volume (about 130,000 entries
on one, 280,000 on another). An indexing pass now **starts with the walk and, if it has not finished within 10 seconds, continues
with the journal pass** for the locations still unread, giving no entry twice - at most about twice the better choice. Locations
that are a volume root or on a volume the journal cannot read are not timed. The walk is used on its own when `CollectFileMetadata`,
`MaxDepth` or `FollowSymlinks` is set: the journal pass reads none of the last two (an elevated process used to ignore them),
and with the first it reads each entry as the walk does anyway. Entries of a pass that may hand over carry no size or timestamps,
as the journal pass's always did, so an index is the same whichever pass produced an entry. `ProviderMode.Mft` and
`ProviderMode.Standard` are forced as before, and monitoring still uses the change journal.

**Damage met after a store is open is `IndexStoreCorruptedException` too.** Only opening a store mapped a damaged file; a read,
write or enumeration on an open store whose file had been damaged since raised the driver's own exception (`SQLITE_CORRUPT`,
`SQLITE_NOTADB`), which a caller of `IIndexPersistence` could not catch without referencing the driver and whose message said nothing
of what to do. The operations of the SQLite store, the enumerations while they run, and a transaction's commit now raise the same
exception with the store's path and the driver's exception inside; a refused write, a lock or a cancellation stay what they were.

**A change a monitor reports from inside a hidden directory is no longer indexed (both engines).** An indexing pass does not enter a
hidden directory (or, with system files off, a system one), so it reads nothing beneath it; a monitored creation looked only at the
changed file's own attributes, so a file created in `.git` (which is not hidden itself) was added to the index, and a file renamed into
a hidden directory kept its entry under the new path. A change is now checked against the directories between the monitored location
and the item (one attribute read each); only directories below the location count, as in a pass, so a location that lies under a
hidden `AppData` still works. With `IncludeHidden = true` / `IncludeSystem = true` nothing changes.

**The change-journal pass (an elevated Windows process) now indexes the same entries as the directory walk.** With hidden
files off it used to admit each entry by its own attributes, so everything inside a hidden directory (`.git`, `AppData`,
a hidden build-output folder) was indexed although the walk never enters one — the same folder gave a different index depending
on whether the process was elevated (+1,335 entries in a 1,769-entry repository folder; in a 2.2-million-entry one the net difference was +181,177). A
hidden directory, and with system files off a system directory, now takes everything beneath it out of the pass, as the walk
does; only a directory *below* the location being indexed counts (a location that lies under `AppData` is still indexed), and
the location itself is no longer listed as an entry of its own. An elevated process's index therefore loses what lay under such
directories; ask for it with `IncludeHidden = true` / `IncludeSystem = true`. One difference remains, and is the journal's own: it lists a file once, under one of its names, so the
other names of a hard-linked file (build tools link their outputs; `WinSxS` links system files) are read by the walk and not by
the journal pass. (Entries a monitor reports from inside a hidden
directory are a separate gap, not changed here.)

**These change what you get back:** `SearchResult.TotalMatches` is now the same thing on both engines — the number of
matches found before the search stopped. When `HasMoreResults` is false it is the exact count; when it is true it is a
lower bound, at least `MaxResults + 1`. The Windows engine used to report at most `MaxResults` and say "more" when the
matches equalled the cap; the Unix engine reported every match. `HasMoreResults` is now exact on both: true only when at
least one match lay beyond the cap. Code that read `TotalMatches` as "all matches" on the Unix engine, or compared it
with `MaxResults` on the Windows one, needs the new reading.

- **A query is no longer also answered from the file system because the index is small (Windows).** The index
  scanned the disk whenever it held fewer than ten items or a query found fewer than five matches, so the same query
  returned a different set depending on how many matches there happened to be, from a second mechanism with its own
  rules. The scan now runs only for a location the index does not cover — a `BasePath` or `SearchLocations` entry with
  nothing indexed under it, or an index that is empty. A covered location that is still being indexed returns what the
  index holds so far. `SearchStatistics.IndexHits` and `FileSystemScans` now count what happened (they reported every
  search as an index search and no scans): a search the file system also answered counts as a scan, not an index hit.
- **A capped query on the Unix engine no longer collects every match.** It stops one match past `MaxResults`; a query
  for 100 results over a million entries used to build a list of up to 99,706.

### Fixed

- **The change-journal monitor read the whole journal ahead of a slow consumer.** `UsnJournalMonitor` handed
  records to its readers through an unbounded channel, and a reader that looks up the file system for every record
  (the monitored path of the Windows engine does) fell behind a burst of changes while the monitor kept queueing
  them. The channel now holds at most 100,000 unread records; past that the monitor stops reading the journal until
  the reader catches up — the journal keeps the changes — and logs one Warning per stall. A consumer of
  `UsnJournalMonitor.Changes` must keep reading it while the monitor runs, including one that only uses the
  `ChangeDetected` event: without a reader the monitor now pauses after 100,000 records instead of retaining them.
- **A rename reported with a new path beneath its old one grew every indexed path under it, once per report.**
  `MoveTreeAsync` re-rooted each descendant under the new path, so applying the same pair again made every
  path one block longer; the grown paths were new strings that the string pool never frees, and a long-running
  process with monitoring on ran out of memory. A directory cannot be moved beneath itself, so such a pair is
  not a move. `SearchIndexTreeExtensions.IsMove` says so, `MoveTreeAsync` now throws `ArgumentException` for
  one, and the new `ApplyRenameAsync` applies a monitor's rename the way both engines now do: a real move is
  moved, anything else leaves the index as it is — unless the old path is gone from the file system, in which
  case its subtree is removed — and the engine logs a warning. A rename that changes only the case of a path
  is still a move.
- **A change the monitor reported skipped the indexing options (Windows).** The pass applied
  `ExcludedExtensions`, `IncludeHidden`, `IncludeSystem`, `MaxFileSize` and `MaxFileCount`; a created, modified
  or renamed item reached the index with none of them, so files the options refuse were added and an index
  capped to bound memory kept growing after the pass. A monitored change now goes through the same admission
  as a pass item, an entry that stops being admissible (it became hidden, was renamed to an excluded
  extension) leaves the index, and `MaxFileCount` is a ceiling the index keeps: a new item is added only below
  it, an item the index already holds may still change, and a deletion makes room. `TotalIndexedFiles`
  follows the index after the pass instead of stopping at the pass's count.
- **The watcher's queue of waiting events had no bound (Windows).** The engine's own channel is bounded and
  makes the monitoring loop wait when processing falls behind, but the queue ahead of it grew without limit.
  It holds at most 100,000 events; past that an event is dropped and counted, and a warning says how many.
  A watcher error (for example an internal buffer overflow) is now logged as a warning too, since the index
  has missed changes it will not hear about again.
- **`IncludeHidden = false` skipped only dot-names on macOS (Unix).** A folder carrying the BSD hidden flag
  (`~/Library`) is hidden to .NET (`FileAttributes.Hidden`) but has no leading dot, so the walk entered it and
  reached other applications' data, which macOS guards with a permission prompt naming the walking process.
  The Linux and macOS enumerators now ask the one definition of what an index admits (the same one the
  Windows providers use) about the item itself, so the Hidden attribute decides on both, together with
  excluded paths, excluded extensions and `MaxFileSize`. On Linux nothing changes: .NET reports a dot-name as
  hidden.
- **A change the monitor reported skipped the indexing options (Unix).** The Linux and macOS engine indexed a
  created or modified file with no check, so excluded extensions, hidden files and `MaxFileCount` held only
  during the pass. It now goes through the same admission and cap as the Windows engine, and an entry that
  stops being admissible leaves the index.
- **On macOS a search scoped to a folder spelled in the other Unicode normalization form found nothing.** APFS
  treats a name in form C and the same name in form D as one name, and a Finder-created folder is decomposed
  where a shell-created one is not, so a `BasePath`, `SearchLocations` entry or `ExcludedPaths` entry spelled one
  way matched nothing indexed the other way, and a name query typed in one form missed a name stored in the
  other, with no error. Path and name comparison now reads both sides in form C **on macOS only** (on Linux
  and Windows the two spellings are two names and stay so); stored paths are never rewritten, and a wildcard
  pattern is built in form C. A regular expression you write is matched as written, against a composed name.
- **A change the engine could not apply was logged at `Debug`.** It is now a `Warning` naming the change type
  and path, so a consumer at its default log level can see that the index may disagree with the file system.

## [2.5.0] - 2026-11-06

**When upgrading, these change what you get back:** a `MaxDepth` of 1 on Windows lists one more level; the Windows
directory walk no longer enters junctions and symbolic links (it entered all of them); the Linux and macOS providers now
*list* symbolic links (they left them out); a search issued while the index is still filling no longer returns what
`ExcludedPaths` and the other indexing options exclude; `SearchStatistics.LastSearch` is UTC on both engines; the shipped
dependencies' lower bounds rise to 10.0.14. Each is described below.

### Fixed

- **An indexing pass on the directory-walk provider held one open handle per pending directory, and
  a pass that stopped early left its producer running.** The walk handed one recursive
  `Directory.EnumerateFileSystemEntries` to a producer task; that enumerator opens every
  sub-directory it discovers and keeps the handle until the directory is listed, so open handles
  followed the width of the tree (54,650 above baseline walking a Windows folder of 372,000 entries).
  The producer was started with the caller's token alone, so a pass that stopped at `MaxFileCount`
  or on a stop request waited one second for it and left it parked on its full channel with every
  handle still open until a garbage collection happened to finalise the enumerator; a scheduled
  re-index then began a second walk beside it. The walk now lists one directory at a time, breadth-first
  as before, and does not enter a directory that `ExcludedPaths` excludes. The producer runs under a
  token linked to the pass, and ending the pass cancels it and waits for it. The same pass-bound
  lifetime applies to the change-journal provider's per-drive producers. Walking that same folder
  went from about 210–267 s to about 19–22 s; small trees are unchanged.
- **A search that arrived while the index was still small returned items that `IndexingOptions` excludes.**
  While an index holds fewer than ten items — which on the change-journal path is the whole enumeration of
  the volume — a search is answered by walking the disk, and that walk read only the query's own
  exclusions. So `ExcludedPaths` (and `ExcludedExtensions`, `IncludeHidden`, `IncludeSystem`,
  `MaxFileSize`) appeared to do nothing for as long as indexing took, and indexing a small folder as an
  administrator took tens of seconds. The walk now admits what the index would admit, through the same
  definition the providers use. It also no longer enters a junction or symbolic link, which could send
  it round a tree for ever.
- **A query that found fewer than five matches paid about 130 ms for a disk scan that had nothing to scan.**
  When an index finds few matches it walks the disk to check that it is not incomplete. That walk's workers
  decided it was finished by polling — an empty queue was checked ten times, 10 ms apart — so it could not take
  less than about 100 ms even with no directory to look at, and it could be abandoned while another worker was still
  listing a directory. It now ends when the last directory it was handed has been listed. A query for a word nothing
  has went from 154 ms to 7–11 ms over a 27,000-entry folder; the cost removed is about 130 ms at every size, which is
  inside the machine's own run-to-run spread over a million-entry index (see `docs/BENCHMARKS.md`).
- **`IndexingStatistics.LastIndexingTime`, `AverageIndexingSpeed` and `IndexingOperations` were never filled by the
  Windows engine** (always zero), and the Unix engine reported `IndexingOperations = 1` before anything had been indexed.
  Both now report the passes that have run and how long the last one took.
- **A pass stopped before it had begun left no trace.** It was started on a token that was already cancelled, so the
  Windows engine never ran it and never released what it held. It now runs, ends as `Cancelled`, and cleans up.
- **Search statistics were not measurements.** The Unix engine reported a 10 ms average, a 1 ms fastest and a
  100 ms slowest search as constants - before any search had run, and whatever the searches had taken. The Windows
  engine summed whole milliseconds, so a search faster than a millisecond (the usual case when a query finds its answer
  early) added nothing, never counted matches, and returned the time of the question as the time of the last search.
  Both engines now keep the same tally - count, matches, total, fastest, slowest, in ticks - report zero when nothing has
  been measured, and `SearchStatistics.LastSearch` is the time of the last search in UTC on both (the Windows engine
  reported local time).
- **Files whose only attribute is `Normal` were never indexed by the directory walk.** `Normal` is the
  one attribute a file carries when it has no other, which is what a file with its archive bit cleared
  reports; the walk built its list of attributes to skip starting from it.
- **`IndexingOptions.MaxDepth` did not mean the same on every provider, or for every value.** A depth of 1 on the Windows directory walk listed only the
  location's own entries, because a depth of 1 switched recursion off; every larger depth, and every depth
  on the Linux and macOS providers, listed the location's entries and that many levels beneath them. A
  depth of *n* now means that everywhere. Only a depth of exactly 1 on Windows changes: it gains the second
  level it was missing.
- **`IndexingOptions.FollowSymlinks` was ignored by the Windows directory walk**, which followed every
  junction and symbolic link and, given a link back to an ancestor, descended until the path was too
  long to continue. A link is now reported like any other entry and not entered unless `FollowSymlinks`
  is set; when it is, a chain of links is crossed only so far.
- **The Linux and macOS providers left a symbolic link out of the index altogether** unless
  `FollowSymlinks` was set, so a file reachable under a second name (`python3` pointing at
  `python3.12`) could not be found by the name it is used under. A link is now listed, as on Windows, and
  not entered unless links are followed.

### Added

- **The packages carry their XML documentation and a symbol package.** Until now a package held the
  assembly and nothing else, so the remarks that describe what each provider does and does not do (what
  the change-journal provider reads, how an exclusion is matched) never reached IntelliSense, and there
  was no way to step into the library's source. Each package now includes its documentation file, and a
  `.snupkg` with Source Link pointing at the commit it was built from is published beside it, so a
  debugger can fetch the matching source.
- **Query latency is measured and published.** `dotnet run -c Release --project src/FastFind.Benchmarks --
  --query-latency` builds a deterministic corpus at 100,000, 300,000 and 1,000,000 entries and times seven query shapes
  against both in-memory indexes; with `--tree <folder>` it indexes a real folder with the engine and times the same
  operation. The figures, and what they say, are in `docs/BENCHMARKS.md` (an early-exit query: about a millisecond or less
  at every size; a query that must read the whole corpus: time proportional to it, with a wide run-to-run spread).
- **`IndexingStatistics` says how the last indexing pass ended and which locations it could not read.**
  `LastRunOutcome` (`IndexingRunOutcome`: `None`, `Completed`, `CompletedWithGaps`, `CapReached`, `TimedOut`,
  `Cancelled`, `Failed`) and `FailedLocations`. A pass that lost a drive, or was given a directory that is not there,
  used to end exactly like one that read everything - the progress event said "completed", the statistics said nothing, and
  the index was simply short. Providers report what they could not read through the new
  `IFileSystemProvider.EnumerationFailures` (a default interface member, so existing providers compile unchanged and
  report nothing). Entries a provider skips because the account may not see them are not failures.
- **`IndexStoreCorruptedException`.** Opening a store whose file is not a database, or is cut short or damaged,
  now raises this exception (an `IOException`, carrying `StorePath`, with the driver's exception inside) instead of
  the driver's own `SqliteException` - error 26 "file is not a database" or error 11 "database disk image is
  malformed" - which a caller could only catch by referencing `Microsoft.Data.Sqlite`, and whose message said
  nothing of what to do next. The message names the file and says to delete it and index again, since an index is
  derived from the file system. The library does not delete it for you. A zero-byte file still opens as an empty
  store, and a failure that says nothing about the file's contents (an unopenable path, a lock) is left as the driver
  reports it. Damage found *after* the store is open is not covered yet.

### Changed

- **Lower bounds of the shipped dependencies rise to the current patch release**
  (`Microsoft.Extensions.*`, `Microsoft.Data.Sqlite`, `System.Management`: 10.0.12 → 10.0.14).

## [2.4.0] - 2026-09-12

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

- **The published performance figures are now sourced, and the ones that were not are withdrawn.**
  `docs/BENCHMARKS.md` and the README carried numbers with no command in the repository that
  reproduces them — SIMD at 1,877,459 ops/sec, enumeration at 31,073 files/sec and "30–60× faster
  than the standard API", search at 1,680,631 ops/sec, 439 bytes per operation, and an industry
  comparison resting on the enumeration figure. Two of them had already been measured and
  contradicted: the SIMD matcher is **3.9× slower** than `string.Contains` compared like for like,
  and string interning saves a few per cent rather than the 60–80% claimed for years. The README
  also published one set of figures for all three platforms, though only Windows was ever measured.
  Every figure that remains names the command and the date that produced it, the withdrawn ones are
  listed with the reason, and the four failing enumeration assertions are published as failing
  rather than having their targets moved.

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
- **The NuGet package description said "MFT direct access".** The Windows provider enumerates the
  NTFS change journal (`FSCTL_ENUM_USN_DATA`); it does not read the Master File Table, which is why
  sizes and timestamps are opt-in through `CollectFileMetadata`. The description now says what the
  packages do.

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
