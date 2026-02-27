# macOS Support Design — FastFind.NET

## Overview
Add macOS platform support to FastFind.NET by creating a MacOSFileSystemProvider as an independent implementation within the existing FastFind.Unix project.

## Approach
**Independent implementation (no Linux refactoring)** — Copy LinuxFileSystemProvider and modify macOS-specific parts only. Zero risk to existing Linux functionality.

## Decisions
- **File monitoring**: FileSystemWatcher (managed .NET, maps to FSEvents internally)
- **Mount detection**: DriveInfo + `/Volumes` directory enumeration
- **FS type detection**: DriveInfo.DriveFormat fallback
- **No P/Invoke**: All managed .NET APIs

## File Changes

### New Files
| File | Purpose |
|------|---------|
| `src/FastFind.Unix/MacOS/MacOSFileSystemProvider.cs` | macOS file system provider |
| `src/FastFind.Unix.Tests/MacOS/MacOSFileSystemProviderTests.cs` | Provider tests |
| `src/FastFind.Unix.Tests/MacOS/MacOSFactoryRegistrationTests.cs` | Factory registration tests |
| `src/FastFind.Unix.Tests/MacOS/MacOSSearchEngineTests.cs` | Search engine tests |

### Modified Files
| File | Change |
|------|--------|
| `src/FastFind.Unix/UnixSearchEngine.cs` | Add `CreateMacOSSearchEngine()` factory |
| `src/FastFind.Unix/UnixRegistration.cs` | Uncomment macOS registration |
| `src/FastFind.Unix/Common/UnixPathHelper.cs` | Add macOS virtual FS types |
| `.github/workflows/dotnet.yml` | Add test execution to test-macos job |

### Unchanged
- `src/FastFind.Unix/Linux/LinuxFileSystemProvider.cs` — no modifications
- All Windows code — no modifications
- Core interfaces — no modifications

## MacOSFileSystemProvider Design

Based on LinuxFileSystemProvider with these differences:

| Component | Linux | macOS |
|-----------|-------|-------|
| Platform guard | `OperatingSystem.IsLinux()` | `OperatingSystem.IsMacOS()` |
| Mount detection | `/proc/mounts` parsing | `DriveInfo` + `/Volumes` enumeration |
| FS type detection | `/proc/mounts` FS field | `DriveInfo.DriveFormat` |
| File enumeration | Channel BFS (same) | Channel BFS (same) |
| File monitoring | FileSystemWatcher/inotify | FileSystemWatcher/FSEvents |
| Symlink handling | .NET LinkTarget (same) | .NET LinkTarget (same) |
| Hidden files | dotfile check (same) | dotfile check (same) |

## Test Strategy
- `[Trait("OS", "macOS")]` + `if (!OperatingSystem.IsMacOS()) return;` guard pattern
- Reuse `TestFileTreeFixture` (fully portable)
- GitHub Actions: `macos-latest` runner with `workflow_dispatch` trigger

## CI/CD
- test-macos job: add `dotnet test` execution (currently build-only)
- Keep `workflow_dispatch` trigger (cost optimization)
- Push permitted for iterative verification
