# FastFind.NET Roadmap

Development roadmap and platform support status for FastFind.NET

## Current Status (v1.0.14)

### Completed Features

#### Core Foundation (FastFind.Core)
- [x] Core Interfaces: ISearchEngine, IFileSystemProvider with IAsyncDisposable
- [x] Ultra-Optimized FastFileItem: 61-byte struct with string interning
- [x] Cross-Platform SIMD: Vector256/Vector128 auto-dispatch (AVX2, SSE2, NEON)
- [x] High-Performance StringPool: String interning with 60-80% memory reduction
- [x] Enhanced Async Patterns: IAsyncEnumerable streaming, Channel-based architecture
- [x] SearchQuery fluent builder with validation
- [x] Cancellation support and logging integration

#### Windows Implementation (FastFind.Windows) - Production Ready
- [x] High-Performance Indexing with NTFS optimization
- [x] MFT Direct Access: Ultra-fast NTFS file enumeration
- [x] USN Journal Integration: Real-time file change detection
- [x] HybridFileSystemProvider: Auto-selection of MFT vs Standard mode
- [x] Factory pattern with ModuleInitializer auto-registration
- [x] Platform capability detection (admin, AVX2, NTFS)

#### Linux Implementation (FastFind.Unix) - Preview
- [x] LinuxFileSystemProvider: Channel-based BFS parallel file enumeration
- [x] Depth-aware parallelism: depth ≤ 2 dispatched to queue, deeper inline
- [x] inotify-based change tracking via FileSystemWatcher
- [x] /proc/mounts parsing for mount point discovery
- [x] Virtual filesystem filtering (sysfs, proc, tmpfs, devfs)
- [x] UnixSearchEngine factory with ModuleInitializer auto-registration
- [x] 15 tests passing on WSL (Ubuntu 24.04)

#### SQLite Persistence (FastFind.SQLite) - Production Ready
- [x] FTS5 Full-Text Search
- [x] WAL Mode for concurrent access
- [x] Bulk insert operations
- [x] MftSqlitePipeline for MFT → SQLite data flow

#### CI/CD & Developer Experience
- [x] NuGet packages: FastFind.Core, FastFind.Windows, FastFind.Unix, FastFind.SQLite
- [x] Version-based automatic deployment
- [x] PR build validation (Windows + Linux)
- [x] Linux CI job (ubuntu-latest, automatic on PR + push)
- [x] macOS CI job (workflow_dispatch, manual trigger)
- [x] Docker multi-distro compatibility testing
- [x] Comprehensive test suite (231 Windows + 15 Linux tests)

## Key Research Findings

> Based on [research-01: Cross-platform extension strategies](../dev-docs/research/research-01.md)

### Lessons Learned

1. **MFT 구조적 우위는 재현 불가** — NTFS MFT의 flat metadata table은 Linux/macOS에 동등한 구조가 없음. 하지만 병렬 열거 + 인덱스 전략으로 60-80% 성능 달성 가능
2. **인덱스 레이어 투자가 열거 성능 추격보다 중요** — Everything, plocate, FSearch 모두 동일 교훈: 인덱스 존재 시 검색 성능은 플랫폼 독립적. 열거 격차는 cold-start에만 영향
3. **SIMD 크로스 플랫폼은 성숙** — `Vector128<T>`/`Vector256<T>`가 AVX2/NEON 자동 디스패치, 80-90% 수동 튜닝 성능. 현재 AVX2 전용 코드를 마이그레이션해야 함
4. **플랫폼별 최적 전략이 명확**:
   - Linux: parallel `getdents64` (4-8 threads, 64KB buffer) → SSD에서 4-40× 향상
   - macOS: `searchfs()` — catalog-level 열거 (find 대비 ~2× 빠름), `fts_read()` — 범용 최고속
5. **변경 추적도 강력한 등가물 존재**:
   - Linux `fanotify` (5.9+): 단일 마크로 전체 파일시스템 모니터링, USN Journal과 유사도 높음
   - macOS `FSEvents`: 재부팅 간 지속적 이벤트 재생, USN Journal의 기능적 등가물
6. **LibraryImport이 네이티브 코드 기본 전략** — ~2-5ns 오버헤드로 모든 syscall 바인딩 충분. Rust shim은 eBPF 등 극소수 경우만 정당화
7. **권한 계층화 필수** — fanotify는 CAP_SYS_ADMIN 필요, macOS FDA 수동 허용 필요. 비특권/특권 경로를 모두 제공하는 tiered 설계가 핵심

## Next Phase

### v1.1.0 - Stability & Quality
- [ ] Hidden file filtering (IncludeHidden/IncludeSystem enforcement)
- [ ] Real-time file system monitoring improvements
- [ ] SQLite bulk optimization fix (MftSqliteIntegration)
- [ ] Test coverage expansion

### v1.2.0 - Cross-Platform MVP (Phase 1)

> Goal: 3개 플랫폼 기능적 동등성 달성. 최적 성능보다 동작하는 크로스 플랫폼이 우선.

#### SIMD 크로스 플랫폼화
- [x] `SIMDStringMatcher`를 `Vector128<T>`/`Vector256<T>` 기반으로 리팩터링
- [x] AVX2 전용 코드 → 크로스 플랫폼 정적 메서드로 마이그레이션
- [x] 3-tier 폴백: Vector256 → Vector128 → Scalar (JIT dead-branch elimination)

#### 플랫폼 추상화
- [ ] Capability discovery 패턴 도입 (`ISearchCapability`, `Supports<T>()`, `GetCapability<T>()`)
- [ ] `IChangeTracking` 인터페이스 (WatchChangesAsync, SupportsPersistentJournal, LastSyncPoint)
- [ ] `IFastEnumeration` 인터페이스 (Strategy, EstimateFileCountAsync)
- [ ] `PlatformProfile` record (EstimatedFullScanTime, RecommendedParallelism, RequiresElevation, SimdLevel)
- [ ] Volume 추상화: Drive letter / Mount point / Volume → `IVolumeInfo { MountPoint, Label }`

#### Linux 기본 구현 (FastFind.Unix)
- [x] .NET API 기반 파일 열거 (Directory.EnumerateFileSystemEntries)
- [x] Channel 기반 병렬 디렉터리 순회 (BoundedChannel BFS)
- [x] Depth 기반 분기: depth 0-2는 큐 디스패치, 이후는 inline 처리
- [x] `inotify` 변경 추적 (FileSystemWatcher, DropOldest 백프레셔)
- [x] `/proc/mounts` 파싱으로 파일시스템 타입 감지
- [ ] `getdents64` 직접 호출 (LibraryImport, 64KB 버퍼, d_type 최적화) — Phase 2 최적화

#### macOS 기본 구현 (FastFind.Unix)
- [ ] `fts_open()`/`fts_read()` 기반 트리 순회
- [ ] `FSEvents` 변경 추적 (`sinceWhen` 지속적 이벤트 ID 추적, 재부팅 간 복구)
- [ ] `kFSEventStreamCreateFlagFileEvents` 파일 수준 이벤트 (macOS 10.7+)

#### 빌드 & 패키징
- [x] FastFind.Unix NuGet 패키지 (linux-x64, linux-arm64, osx-x64, osx-arm64)
- [x] CI/CD Linux job (ubuntu-latest, automatic)
- [x] CI/CD macOS job (workflow_dispatch, manual trigger)
- [x] Docker 멀티 배포판 호환성 테스트 인프라
- [ ] `NativeLibrary.SetDllImportResolver` 커스텀 프로빙

### v1.3.0 - Platform-Specific Optimization (Phase 2)

> Goal: 플랫폼별 고유 API 활용으로 성능 최적화. 특권/비특권 경로 계층화.

#### Linux 고급 열거 & 추적
- [ ] `fanotify` (Linux 5.9+): FAN_MARK_FILESYSTEM + FAN_REPORT_DFID_NAME, 특권 경로
- [ ] `statfs()` 기반 파일시스템별 최적 열거 경로 자동 선택
- [ ] XFS `XFS_IOC_BULKSTAT`: 특권 환경에서 inode 순서 대량 메타데이터 접근
- [ ] 변경 추적 계층화: fanotify (특권) → inotify (비특권) → 주기적 전체 재스캔 (폴백)

#### macOS 고급 열거
- [ ] `searchfs()`: APFS/HFS+ catalog-level 전체 볼륨 열거 (초기 인덱싱)
- [ ] `getattrlistbulk`: 다중 속성이 필요한 열거에 활용
- [ ] CNID → path 해결 전략 구현

#### SIMD 플랫폼 특화 핫패스
- [ ] ARM64 `AdvSimd` 최적화 string matching (Apple Silicon에서 10-20% 추가 성능)
- [ ] `movemask` 대안 NEON 패턴 구현

### Future Considerations (Phase 3+)

#### Linux 고급 기능
- [ ] Btrfs `BTRFS_IOC_TREE_SEARCH`: B-tree 직접 메타데이터 열거
- [ ] Btrfs snapshot diff (`send --no-data`): 주기적 zero-event-loss 검증 안전망
- [ ] `io_uring` `IORING_OP_GETDENTS` 통합 (mainline 머지 후)
- [ ] eBPF VFS hooks: 고보안 환경용 프로세스 귀속 + 컨테이너 인식 (별도 네이티브 데몬)

#### 인덱스 고도화
- [ ] plocate 영감 trigram posting list: SQLite FTS5 위에 sub-millisecond substring 매칭 레이어
- [ ] TurboPFor 정수 압축 + ZSTD 블록 압축으로 인덱스 크기 최적화

#### 서비스 패키징
- [ ] systemd service: CPUWeight=20, IOWeight=20, MemoryHigh=512M, CAP_SYS_ADMIN
- [ ] macOS LaunchDaemon: ProcessType=Background, LowPriorityIO=true, root 권한

#### 기타
- Network storage (UNC/SMB) optimization
- Content-based search (document indexing)
- OpenTelemetry integration
- NativeAOT 배포 옵션 (`DirectPInvoke`, ISA 타겟팅)

## Platform Support

### Current

| Platform | Status | Package |
|----------|--------|---------|
| Windows 10/11 | Production Ready | FastFind.Windows |
| Windows Server 2019+ | Production Ready | FastFind.Windows |
| Linux (Ubuntu, RHEL, Alpine) | Preview | FastFind.Unix |
| SQLite Persistence | Production Ready | FastFind.SQLite |

### Planned

| Platform | Target | Package | Key Strategy |
|----------|--------|---------|--------------|
| macOS 11+ | v1.2.0 | FastFind.Unix | fts_read + FSEvents |
| Linux (getdents64 optimized) | v1.3.0 | FastFind.Unix | getdents64 + fanotify |
| Linux (privileged) | v1.3.0 | FastFind.Unix | fanotify + XFS BULKSTAT |
| macOS (optimized) | v1.3.0 | FastFind.Unix | searchfs + getattrlistbulk |

### .NET Runtime

| Version | Status |
|---------|--------|
| .NET 10 | Primary Target |

## Performance Benchmarks

### Current (Windows)

| Metric | Result |
|--------|--------|
| SIMD String Matching | 1,877,459 ops/sec |
| MFT File Enumeration | 31,073 files/sec |
| File Indexing | 243,856 files/sec |
| Search Operations | 1,680,631 ops/sec |
| StringPool Interning | 6,437 paths/sec |
| Memory per Operation | 439 bytes |

> Benchmarks measured on Windows 11, .NET 10. Results vary by hardware.

### Cross-Platform Target Metrics

| Metric | Windows (baseline) | Linux target | macOS target |
|--------|-------------------|-------------|-------------|
| 1M file enum (SSD, warm) | ~1s (MFT) | <3s (parallel getdents64) | <5s (searchfs + fts) |
| 1M file enum (SSD, cold) | ~3s (MFT) | <10s | <15s |
| Change event latency | <1s (USN poll) | <1s (fanotify) | <2s (FSEvents) |
| Substring search (5M files) | <1ms | <1ms | <1ms |
| SIMD throughput (GB/s) | ~15 (AVX2) | ~15 (AVX2 on x86) | ~9 (NEON on M-series) |

---

**Last Updated**: February 2026
