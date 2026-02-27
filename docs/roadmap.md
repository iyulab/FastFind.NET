# FastFind.NET Roadmap

## Current Status (v1.3.0)

### Core (FastFind.Core)
- Cross-Platform SIMD: Vector256/Vector128 auto-dispatch (AVX2, SSE2, NEON)
- Ultra-Optimized FastFileItem: 61-byte struct with string interning
- High-Performance StringPool: 60-80% memory reduction
- IAsyncEnumerable streaming, Channel-based architecture

### Windows (FastFind.Windows) — Production
- MFT Direct Access: 31K+ files/sec NTFS enumeration
- USN Journal: Real-time file change detection
- HybridFileSystemProvider: Auto MFT/Standard selection
- ModuleInitializer auto-registration

### Linux (FastFind.Unix) — Preview
- Channel-based BFS parallel file enumeration
- inotify change tracking via FileSystemWatcher
- /proc/mounts parsing, virtual FS filtering
- ModuleInitializer auto-registration

### macOS (FastFind.Unix) — Preview
- Channel-based BFS parallel file enumeration (shared with Linux)
- FSEvents change tracking via FileSystemWatcher
- DriveInfo + /Volumes mount detection
- ModuleInitializer auto-registration

### SQLite (FastFind.SQLite) — Production
- FTS5 full-text search, WAL mode, bulk insert
- MftSqlitePipeline for MFT → SQLite data flow

### CI/CD
- PR validation (Windows + Linux auto, macOS manual trigger)
- Docker multi-distro testing (SDK container)
- NuGet auto-publish on version change

---

## v1.1.0 — Stability & Quality (Completed)

- [x] IncludeHidden filtering verified + 4 new test cases
- [x] Dead code removal (SearchOptimizedFileItem, -265 lines)
- [x] Unix stub methods: NotSupportedException for Save/Load, real RefreshIndex
- [x] Unix test coverage: 10 new UnixSearchEngine tests
- [x] Sync-over-async fix in WindowsFileSystemProvider.Dispose()
- [x] Lock type standardization (Lock over object)
- [x] Path exclusion: proper segment matching (no false positives)
- [x] Full verification: 231 Windows + 29 Linux tests, 0 failures

---

## v1.2.0 — Cross-Platform MVP (Completed)

- [x] SIMD Vector128/Vector256 cross-platform refactoring
- [x] Linux Channel-based BFS implementation
- [x] FastFind.Unix NuGet package
- [x] CI/CD Linux + macOS jobs

---

## v1.3.0 — macOS Support + BFS Stability (Completed)

- [x] macOS FileSystemProvider (DriveInfo + /Volumes mount detection)
- [x] macOS FileSystemWatcher monitoring (FSEvents)
- [x] macOS factory registration (ModuleInitializer)
- [x] macOS CI test suite (20 tests passing)
- [x] BFS Channel worker race condition fix (pendingWork counter)
- [x] Cross-platform test constructor safety (nullable engine pattern)

---

## v1.4.0 — Platform-Specific Optimization

- [ ] Linux: getdents64 direct syscall, fanotify (5.9+)
- [ ] macOS: searchfs, getattrlistbulk
- [ ] ARM64 AdvSimd optimization
- [ ] Capability discovery pattern (ISearchCapability)

## Future

- [ ] Btrfs/io_uring/eBPF advanced features
- [ ] Trigram posting list (sub-ms substring matching)
- [ ] systemd/LaunchDaemon service packaging
- [ ] Network storage, content search, OpenTelemetry

---

## Platform Support

| Platform | Status | Package |
|----------|--------|---------|
| Windows 10/11, Server 2019+ | Production | FastFind.Windows |
| Linux (Ubuntu, RHEL, Alpine) | Preview | FastFind.Unix |
| macOS (Ventura+) | Preview | FastFind.Unix |

**Last Updated**: February 2026
