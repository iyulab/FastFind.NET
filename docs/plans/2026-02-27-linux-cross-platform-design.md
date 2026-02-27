# Linux/Cross-Platform Extension Design

**Date**: 2026-02-27
**Status**: Approved
**Approach**: Incremental Layer (기존 Windows 패턴 재사용)

## Decisions

| 항목 | 결정 | 근거 |
|------|------|------|
| 우선순위 | Linux 먼저, macOS 후속 | WSL에서 즉시 개발/테스트 가능 |
| 패키지 구조 | 단일 FastFind.Unix | 내부 Linux/, MacOS/ 폴더 분리, 런타임 OS 감지로 분기 |
| 파일 열거 | .NET API + Channel 병렬화 | getdents64 직접 호출은 Phase 2 최적화 |
| Docker 활용 | 테스트 전용 컨테이너 | WSL 직접 개발, Docker는 다중 배포판 호환성 검증 |
| 접근 방식 | Incremental Layer | 기존 패턴 재사용, Windows 변경 최소화, 빠른 피드백 루프 |

## 1. Project Structure

```
src/FastFind.Unix/
├── FastFind.Unix.csproj
├── UnixSearchEngine.cs              # Factory + ISearchEngine orchestration
├── UnixRegistration.cs              # ModuleInitializer auto-registration
├── Linux/
│   ├── LinuxFileSystemProvider.cs   # IFileSystemProvider 구현
│   ├── LinuxFileEnumerator.cs       # Channel 기반 병렬 열거
│   ├── LinuxFileMonitor.cs          # inotify 기반 변경 추적 (FileSystemWatcher)
│   └── InotifyInterop.cs           # LibraryImport P/Invoke (Phase 2)
├── MacOS/                           # Phase 2 placeholder
│   └── MacOSFileSystemProvider.cs
└── Common/
    └── UnixPathHelper.cs            # 경로 처리 유틸리티
```

**NuGet**: 단일 `FastFind.Unix` 패키지
**RuntimeIdentifiers**: linux-x64;linux-arm64;osx-x64;osx-arm64
**런타임 분기**: `OperatingSystem.IsLinux()` / `OperatingSystem.IsMacOS()`

## 2. LinuxFileSystemProvider

Windows의 `WindowsFileSystemProvider`와 동일한 `IFileSystemProvider` 계약 구현.

### EnumerateFilesAsync
- `Directory.EnumerateFileSystemEntries()` 기반 (내부적으로 getdents 사용)
- `Channel<FileItem>` 기반 BFS 병렬 순회 (4-8 worker tasks)
- Depth 기반 분기: depth ≤ 2는 큐 디스패치, 이후 inline 처리
- `IAsyncEnumerable<FileItem>` 출력

### MonitorChangesAsync
- `FileSystemWatcher` 기반 (.NET 내장, 내부적으로 inotify 사용)
- IN_Q_OVERFLOW 대응: 큐 오버플로 시 전체 재스캔 트리거
- Phase 2에서 직접 inotify P/Invoke 교체 옵션

### GetAvailableLocationsAsync
- `/proc/mounts` 파싱으로 마운트 포인트 열거
- 필터: 물리적 디스크만 (tmpfs, proc, sysfs 등 제외)

### GetFileSystemTypeAsync
- `statfs()` LibraryImport 호출로 파일시스템 타입 감지

## 3. Development Environment

```
Windows IDE ──▶ WSL (Ubuntu) ──▶ Docker (호환성 테스트)
                 │                  │
                 ├─ dotnet build    ├─ Ubuntu 22.04/24.04
                 ├─ dotnet test     ├─ RHEL
                 └─ 직접 디버그      └─ Alpine
```

- **WSL**: 직접 개발, 빌드, 테스트 (ext4 파일시스템)
- **Docker**: 다중 배포판 호환성 테스트 전용
- **Windows IDE**: 소스 편집 (WSL 경로 마운트)

### Test Structure
```
src/FastFind.Unix.Tests/
├── Linux/
│   ├── LinuxFileSystemProviderTests.cs
│   ├── LinuxFileEnumeratorTests.cs
│   └── LinuxFileMonitorTests.cs
├── TestFixtures/
│   └── TestFileTreeFixture.cs
└── docker-compose.test.yml
```

## 4. CI/CD Strategy

| Job | Trigger | Runner | 용도 |
|-----|---------|--------|------|
| test-linux | PR + push (자동) | ubuntu-latest | Linux 기능 테스트 |
| test-macos | workflow_dispatch (수동) | macos-latest | macOS 검증 (비용 절감) |
| test-linux-compat | workflow_dispatch (수동) | ubuntu-latest + Docker matrix | 다중 배포판 호환성 |

macOS runner는 시간 소비가 크므로 릴리스 전 수동 트리거로만 실행.

## 5. Implementation Milestones

| 단계 | 내용 | 검증 |
|------|------|------|
| S1 | 프로젝트 셋업 (csproj, Registration, Factory) | WSL 빌드 통과 |
| S2 | 기본 파일 열거 (단일 스레드) | WSL에서 파일 목록 반환 |
| S3 | 병렬 열거 (Channel BFS) | 1M 파일 성능 벤치마크 |
| S4 | 변경 추적 (FileSystemWatcher) | WSL에서 이벤트 수신 |
| S5 | 통합 테스트 + Docker 호환성 | 테스트 suite 통과 |
| S6 | CI/CD 확장 | GitHub Actions Linux job |
| S7 | SIMD 크로스 플랫폼 | Vector128/256 마이그레이션 |

## 6. Out of Scope (Phase 2+)

- getdents64 직접 P/Invoke
- fanotify (CAP_SYS_ADMIN)
- macOS searchfs(), fts_read(), FSEvents
- ARM64 AdvSimd 핫패스
- XFS BULKSTAT, Btrfs TREE_SEARCH
- Capability discovery 패턴 (ISearchCapability)
