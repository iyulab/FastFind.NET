# FastFind.NET Roadmap

## Current Status (v1.0.14)

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
- 29 tests passing (WSL Ubuntu 24.04)

### SQLite (FastFind.SQLite) — Production
- FTS5 full-text search, WAL mode, bulk insert
- MftSqlitePipeline for MFT → SQLite data flow

### CI/CD
- PR validation (Windows + Linux), macOS (manual trigger)
- Docker multi-distro testing
- NuGet auto-publish on version tags

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

## v1.2.0 — Cross-Platform MVP

> 3개 플랫폼 기능적 동등성. 최적 성능보다 동작하는 크로스 플랫폼 우선.

- [x] SIMD Vector128/Vector256 크로스 플랫폼 리팩터링
- [x] Linux Channel-based BFS 구현
- [x] FastFind.Unix NuGet 패키지
- [x] CI/CD Linux + macOS jobs
- [ ] Capability discovery 패턴 (ISearchCapability)
- [ ] IChangeTracking / IFastEnumeration 인터페이스
- [ ] PlatformProfile record
- [ ] macOS fts_read + FSEvents 구현

## v1.3.0 — Platform-Specific Optimization

- [ ] Linux: getdents64 직접 호출, fanotify (5.9+)
- [ ] macOS: searchfs, getattrlistbulk
- [ ] ARM64 AdvSimd 최적화

## Future

- [ ] Btrfs/io_uring/eBPF 고급 기능
- [ ] Trigram posting list (sub-ms substring matching)
- [ ] systemd/LaunchDaemon 서비스 패키징
- [ ] Network storage, content search, OpenTelemetry

---

## Platform Support

| Platform | Status | Package |
|----------|--------|---------|
| Windows 10/11, Server 2019+ | Production | FastFind.Windows |
| Linux (Ubuntu, RHEL, Alpine) | Preview | FastFind.Unix |
| macOS | Planned (v1.2.0) | FastFind.Unix |

**Last Updated**: February 2026
