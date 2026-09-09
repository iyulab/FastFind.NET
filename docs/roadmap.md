# FastFind.NET Roadmap

Direction, not a release checklist. **What shipped in each version is in
[`CHANGELOG.md`](../CHANGELOG.md)** — this page went stale for three releases by trying to be both.

## Where the project is

| Platform | Status | Package |
|----------|--------|---------|
| Windows 10/11, Server 2019+ | Production | `FastFind.Windows` |
| Linux (Ubuntu, RHEL, Alpine) | Preview | `FastFind.Unix` |
| macOS (Ventura+) | Preview | `FastFind.Unix` |

The core is `FastFind.Core`: a 61-byte `FastFileItem`, SIMD string matching that dispatches across
Vector256/Vector128/scalar, and `SearchQueryEvaluator` as the single definition of what a query
matches — so every backend returns the same set. `FastFind.SQLite` is an optional store that answers
queries from disk, so an engine's memory does not grow with the corpus.

Windows reads the NTFS Master File Table directly and tracks changes through the USN journal. Linux
and macOS enumerate through a Channel-based parallel BFS and watch for changes through
`FileSystemWatcher` (inotify and FSEvents respectively). Both are marked preview because they have
had far less production exposure than the Windows path, not because of a known gap.

## Where it is going

Nothing here is scheduled. These are the directions worth taking, roughly in the order they would
pay off.

**Closing the gap between the platforms.** The Unix providers go through the general file APIs where
the Windows one goes to the metadata directly. `getdents64` on Linux and `getattrlistbulk` on macOS
are the equivalents, and `fanotify` (Linux 5.9+) is a better change feed than inotify for whole-tree
watching. A capability-discovery seam would let a caller ask what the current platform can actually
do rather than infer it.

**Search that scales past substring matching.** Substring queries scan candidates. A trigram posting
list would make them sub-millisecond on a large corpus. Full-text search was removed in 2.0.0 — the
FTS5 index it maintained was written on every change and read by no query — so this would be a
deliberate re-introduction with a tokenizer chosen for substring matching, not a restoration.

**Running as a service.** systemd and LaunchDaemon packaging, so an index can be kept warm across
sessions rather than rebuilt per process.

**Further out.** Content search, network storage, OpenTelemetry instrumentation, and filesystem-
specific paths such as Btrfs and io_uring.

## Performance claims

Figures live in [`BENCHMARKS.md`](BENCHMARKS.md). A performance or memory claim in this project needs
measurement at **at least two corpus sizes** before it is written down: a 2.0.0-era ingest figure
improved 31% at 50,000 items and reversed at 200,000, and the interning saving this page once
advertised as 60–80% measured at 2–5% when it was finally checked.
