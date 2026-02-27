# macOS Support Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add macOS platform support to FastFind.NET via independent MacOSFileSystemProvider within FastFind.Unix.

**Architecture:** Independent implementation — copy LinuxFileSystemProvider and replace Linux-specific code (`/proc/mounts`) with macOS equivalents (DriveInfo + `/Volumes`). All portable code (Channel BFS enumeration, FileSystemWatcher monitoring, symlink/hidden file handling) is reused as-is. Zero changes to existing Linux code.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, GitHub Actions (macos-latest)

---

### Task 1: Add macOS virtual FS types to UnixPathHelper

**Files:**
- Modify: `src/FastFind.Unix/Common/UnixPathHelper.cs:11-36`

**Step 1: Add macOS virtual FS entries**

Add `devfs` and `volfs` to the existing `VirtualFileSystems` HashSet:

```csharp
// After "overlay" in the HashSet, add:
"devfs",
"volfs"
```

**Step 2: Build to verify**

Run: `dotnet build src/FastFind.Unix/FastFind.Unix.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/FastFind.Unix/Common/UnixPathHelper.cs
git commit -m "feat(macos): add macOS virtual filesystem types to UnixPathHelper"
```

---

### Task 2: Create MacOSFileSystemProvider

**Files:**
- Create: `src/FastFind.Unix/MacOS/MacOSFileSystemProvider.cs`

**Step 1: Create MacOS directory**

```bash
mkdir -p src/FastFind.Unix/MacOS
```

**Step 2: Write MacOSFileSystemProvider**

Based on LinuxFileSystemProvider with these differences:
- `SupportedPlatform` → `PlatformType.MacOS`
- `IsAvailable` → `OperatingSystem.IsMacOS()`
- `GetAvailableLocationsAsync()` → DriveInfo + `/Volumes` enumeration (replaces `/proc/mounts`)
- `GetFileSystemTypeAsync()` → DriveInfo.DriveFormat (replaces `/proc/mounts` lookup)
- `GetPerformanceInfo()` → macOS-appropriate estimates (40K files/sec)
- Remove `ParseProcMounts()` and `MountEntry` class entirely

Key implementation for `GetAvailableLocationsAsync()`:
```csharp
public Task<IEnumerable<Interfaces.DriveInfo>> GetAvailableLocationsAsync(
    CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    var drives = new List<Interfaces.DriveInfo>();

    try
    {
        // Primary: use System.IO.DriveInfo for all ready drives
        foreach (var sysDrive in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                if (!sysDrive.IsReady) continue;
                if (UnixPathHelper.IsVirtualFileSystem(sysDrive.DriveFormat)) continue;

                drives.Add(new Interfaces.DriveInfo
                {
                    Name = sysDrive.RootDirectory.FullName,
                    Label = sysDrive.VolumeLabel,
                    FileSystem = sysDrive.DriveFormat,
                    TotalSize = sysDrive.TotalSize,
                    AvailableSpace = sysDrive.AvailableFreeSpace,
                    IsReady = true,
                    DriveType = MapDriveType(sysDrive.DriveType)
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading drive info: {Drive}", sysDrive.Name);
            }
        }

        // Supplement: enumerate /Volumes for additional mount points
        if (Directory.Exists("/Volumes"))
        {
            foreach (var volDir in Directory.GetDirectories("/Volumes"))
            {
                if (drives.Any(d => d.Name == volDir || d.Name == volDir + "/"))
                    continue;

                try
                {
                    var sysDrive = new System.IO.DriveInfo(volDir);
                    if (sysDrive.IsReady)
                    {
                        drives.Add(new Interfaces.DriveInfo
                        {
                            Name = volDir,
                            Label = Path.GetFileName(volDir),
                            FileSystem = sysDrive.DriveFormat,
                            TotalSize = sysDrive.TotalSize,
                            AvailableSpace = sysDrive.AvailableFreeSpace,
                            IsReady = true,
                            DriveType = MapDriveType(sysDrive.DriveType)
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error reading volume: {Path}", volDir);
                }
            }
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error enumerating drives");
    }

    // Fallback: ensure at least root is included
    if (drives.Count == 0)
    {
        try
        {
            var rootDrive = new System.IO.DriveInfo("/");
            drives.Add(new Interfaces.DriveInfo
            {
                Name = "/",
                Label = "Macintosh HD",
                FileSystem = rootDrive.IsReady ? rootDrive.DriveFormat : "apfs",
                TotalSize = rootDrive.IsReady ? rootDrive.TotalSize : 0,
                AvailableSpace = rootDrive.IsReady ? rootDrive.AvailableFreeSpace : 0,
                IsReady = rootDrive.IsReady,
                DriveType = Interfaces.DriveType.Fixed
            });
        }
        catch
        {
            drives.Add(new Interfaces.DriveInfo
            {
                Name = "/",
                Label = "Macintosh HD",
                FileSystem = "apfs",
                TotalSize = 0,
                AvailableSpace = 0,
                IsReady = true,
                DriveType = Interfaces.DriveType.Fixed
            });
        }
    }

    return Task.FromResult<IEnumerable<Interfaces.DriveInfo>>(drives);
}
```

Key implementation for `GetFileSystemTypeAsync()`:
```csharp
public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();

    try
    {
        var fullPath = Path.GetFullPath(path);

        // Find the drive that best matches this path
        System.IO.DriveInfo? bestMatch = null;
        int bestLength = -1;

        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                if (fullPath.StartsWith(root, StringComparison.Ordinal) && root.Length > bestLength)
                {
                    bestMatch = drive;
                    bestLength = root.Length;
                }
            }
            catch { /* skip inaccessible drives */ }
        }

        if (bestMatch != null)
        {
            return Task.FromResult(bestMatch.DriveFormat);
        }
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "Error determining file system type for: {Path}", path);
    }

    return Task.FromResult("unknown");
}
```

All other methods (EnumerateFilesAsync, MonitorChangesAsync, GetFileInfoAsync, ExistsAsync, Dispose, etc.) are identical to LinuxFileSystemProvider — copy verbatim with class name / namespace changes only.

**Step 3: Build to verify**

Run: `dotnet build src/FastFind.Unix/FastFind.Unix.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/FastFind.Unix/MacOS/MacOSFileSystemProvider.cs
git commit -m "feat(macos): add MacOSFileSystemProvider with DriveInfo + /Volumes mount detection"
```

---

### Task 3: Add CreateMacOSSearchEngine factory method

**Files:**
- Modify: `src/FastFind.Unix/UnixSearchEngine.cs:1-34`

**Step 1: Add using directive and factory method**

Add `using FastFind.Unix.MacOS;` to imports (line 3 area).

Add factory method after `CreateLinuxSearchEngine()`:

```csharp
/// <summary>
/// Creates a macOS-optimized search engine
/// </summary>
/// <param name="loggerFactory">Optional logger factory</param>
/// <returns>macOS search engine instance</returns>
public static ISearchEngine CreateMacOSSearchEngine(ILoggerFactory? loggerFactory = null)
{
    if (!OperatingSystem.IsMacOS())
    {
        throw new PlatformNotSupportedException(
            "macOS search engine can only be used on macOS platforms");
    }

    var provider = new MacOSFileSystemProvider(loggerFactory);
    return new UnixSearchEngineImpl(provider, loggerFactory);
}
```

**Step 2: Build to verify**

Run: `dotnet build src/FastFind.Unix/FastFind.Unix.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/FastFind.Unix/UnixSearchEngine.cs
git commit -m "feat(macos): add CreateMacOSSearchEngine factory method"
```

---

### Task 4: Activate macOS registration

**Files:**
- Modify: `src/FastFind.Unix/UnixRegistration.cs:53-60`

**Step 1: Uncomment macOS registration block**

Replace the commented Phase 2 block (lines 53-60) with active code:

```csharp
if (OperatingSystem.IsMacOS())
{
    FastFinder.RegisterSearchEngineFactory(
        PlatformType.MacOS,
        loggerFactory => UnixSearchEngine.CreateMacOSSearchEngine(loggerFactory));
    _isRegistered = true;
}
```

**Step 2: Build to verify**

Run: `dotnet build src/FastFind.Unix/FastFind.Unix.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/FastFind.Unix/UnixRegistration.cs
git commit -m "feat(macos): activate macOS search engine registration"
```

---

### Task 5: Write macOS tests

**Files:**
- Create: `src/FastFind.Unix.Tests/MacOS/MacOSFactoryRegistrationTests.cs`
- Create: `src/FastFind.Unix.Tests/MacOS/MacOSFileSystemProviderTests.cs`
- Create: `src/FastFind.Unix.Tests/MacOS/MacOSSearchEngineTests.cs`

**Step 1: Create MacOS test directory**

```bash
mkdir -p src/FastFind.Unix.Tests/MacOS
```

**Step 2: Write MacOSFactoryRegistrationTests.cs**

```csharp
using FastFind.Interfaces;
using FluentAssertions;

namespace FastFind.Unix.Tests.MacOS;

[Trait("Category", "Integration")]
[Trait("OS", "macOS")]
public class MacOSFactoryRegistrationTests
{
    [Fact]
    public void EnsureRegistered_OnMacOS_ShouldRegisterFactory()
    {
        if (!OperatingSystem.IsMacOS()) return;

        UnixRegistration.EnsureRegistered();
        var platforms = FastFinder.GetAvailablePlatforms();
        platforms.Should().Contain(PlatformType.MacOS);
    }

    [Fact]
    public void CreateSearchEngine_OnMacOS_ShouldReturnEngine()
    {
        if (!OperatingSystem.IsMacOS()) return;

        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();
        engine.Should().NotBeNull();
    }

    [Fact]
    public void CreateMacOSSearchEngine_DirectCall_ShouldNotThrow()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var act = () => { using var e = UnixSearchEngine.CreateMacOSSearchEngine(); };
        act.Should().NotThrow();
    }
}
```

**Step 3: Write MacOSFileSystemProviderTests.cs**

```csharp
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using FluentAssertions;

namespace FastFind.Unix.Tests.MacOS;

[Trait("Category", "Functional")]
[Trait("OS", "macOS")]
public class MacOSFileSystemProviderTests : IClassFixture<TestFileTreeFixture>
{
    private readonly TestFileTreeFixture _fixture;

    public MacOSFileSystemProviderTests(TestFileTreeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldReturnAllFiles()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().HaveCount(6);
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldRespectExcludedExtensions()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.ExcludedExtensions = new List<string> { ".log" };

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        items.Where(i => !i.IsDirectory).Should().NotContain(i => i.Extension == ".log");
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldPopulateFields()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();

        FileItem? found = null;
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            if (item.Name == "file1.txt") { found = item; break; }
        }

        found.Should().NotBeNull();
        found!.Size.Should().Be(100);
        found.Extension.Should().Be(".txt");
        found.FullPath.Should().EndWith("file1.txt");
        found.DriveLetter.Should().Be('/');
        found.IsDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task GetFileInfoAsync_ExistingFile_ShouldReturnInfo()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var filePath = Path.Combine(_fixture.RootPath, "file1.txt");

        var result = await provider.GetFileInfoAsync(filePath);
        result.Should().NotBeNull();
        result!.Name.Should().Be("file1.txt");
    }

    [Fact]
    public async Task GetFileInfoAsync_NonExistent_ShouldReturnNull()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var result = await provider.GetFileInfoAsync("/nonexistent/path/file.txt");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAvailableLocationsAsync_ShouldReturnAtLeastRoot()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var locations = await provider.GetAvailableLocationsAsync();
        locations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetFileSystemTypeAsync_Root_ShouldReturnType()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var fsType = await provider.GetFileSystemTypeAsync("/");
        fsType.Should().NotBe("unknown");
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldExcludeHiddenWhenNotRequested()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.IncludeHidden = false;

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().HaveCount(5);
        files.Should().NotContain(i => i.Name.StartsWith('.'));
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldReturnDirectories()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        var dirs = items.Where(i => i.IsDirectory).ToList();
        dirs.Should().HaveCount(3);
        dirs.Select(d => d.Name).Should().Contain(new[] { "sub1", "sub1a", "sub2" });
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldRespectExcludedPaths()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.ExcludedPaths = new List<string> { Path.Combine(_fixture.RootPath, "sub1") };

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().NotContain(i => i.Name == "file3.txt");
        files.Should().NotContain(i => i.Name == "file4.log");
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldRespectMaxFileSize()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.MaxFileSize = 200;

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().NotContain(i => i.Name == "file4.log");
        files.Should().NotContain(i => i.Name == "file5.pdf");
    }

    [Fact]
    public void IsAvailable_OnMacOS_ShouldBeTrue()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var provider = CreateProvider();
        provider.IsAvailable.Should().BeTrue();
        provider.SupportedPlatform.Should().Be(PlatformType.MacOS);
    }

    private static IFileSystemProvider CreateProvider()
        => new FastFind.Unix.MacOS.MacOSFileSystemProvider();

    private IndexingOptions CreateOptions() => new()
    {
        SpecificDirectories = { _fixture.RootPath },
        IncludeHidden = true,
        ExcludedPaths = new List<string>(),
        ExcludedExtensions = new List<string>()
    };
}
```

**Step 4: Write MacOSSearchEngineTests.cs**

```csharp
using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using FluentAssertions;

namespace FastFind.Unix.Tests.MacOS;

[Trait("Category", "Functional")]
[Trait("OS", "macOS")]
public class MacOSSearchEngineTests : IClassFixture<TestFileTreeFixture>, IDisposable
{
    private readonly TestFileTreeFixture _fixture;
    private readonly Interfaces.ISearchEngine _engine;

    public MacOSSearchEngineTests(TestFileTreeFixture fixture)
    {
        _fixture = fixture;
        _engine = UnixSearchEngine.CreateMacOSSearchEngine();
    }

    private async Task EnsureIndexedAsync()
    {
        if (_engine.TotalIndexedFiles > 0) return;

        var options = new IndexingOptions
        {
            SpecificDirectories = { _fixture.RootPath },
            IncludeHidden = true,
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };
        await _engine.StartIndexingAsync(options);
    }

    [Fact]
    public async Task SearchAsync_ByText_ShouldFindMatches()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var result = await _engine.SearchAsync("file1");
        result.TotalMatches.Should().BeGreaterThan(0);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
            items.Add(item);

        items.Should().Contain(i => i.Name == "file1.txt");
    }

    [Fact]
    public async Task SearchAsync_NoMatch_ShouldReturnEmpty()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var result = await _engine.SearchAsync("nonexistent_xyz_12345");
        result.TotalMatches.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_ByExtension_ShouldFilter()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var query = new SearchQuery { ExtensionFilter = ".txt" };
        var result = await _engine.SearchAsync(query);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
            items.Add(item);

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.Extension == ".txt");
    }

    [Fact]
    public async Task GetIndexingStatisticsAsync_AfterIndex_ShouldReturnStats()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var stats = await _engine.GetIndexingStatisticsAsync();
        stats.TotalFiles.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StopIndexingAsync_ShouldNotThrow()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var act = () => _engine.StopIndexingAsync();
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        _engine.Dispose();
    }
}
```

**Step 5: Build tests to verify compilation**

Run: `dotnet build src/FastFind.Unix.Tests/FastFind.Unix.Tests.csproj`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add src/FastFind.Unix.Tests/MacOS/
git commit -m "test(macos): add macOS provider, factory, and search engine tests"
```

---

### Task 6: Update GitHub Actions workflow for macOS testing

**Files:**
- Modify: `.github/workflows/dotnet.yml:90-113`

**Step 1: Update test-macos job to run tests**

Replace the test-macos job (lines 90-113) with:

```yaml
  # 🍎 macOS 테스트 (수동 트리거 전용 — 비용 최소화)
  test-macos:
    runs-on: macos-latest
    if: github.event_name == 'workflow_dispatch'
    steps:
    - uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: ${{ env.DOTNET_VERSION }}

    - name: Cache NuGet packages
      uses: actions/cache@v4
      with:
        path: ~/.nuget/packages
        key: ${{ runner.os }}-nuget-${{ hashFiles('src/Directory.Packages.props', '**/*.csproj') }}
        restore-keys: |
          ${{ runner.os }}-nuget-

    - name: Build Unix Tests
      run: dotnet build src/FastFind.Unix.Tests/FastFind.Unix.Tests.csproj --configuration Release

    - name: Run macOS Tests
      run: dotnet test src/FastFind.Unix.Tests/FastFind.Unix.Tests.csproj --configuration Release --no-build --filter "Category!=Performance" --logger "trx;LogFileName=test-results.trx" --results-directory ./test-results

    - name: Upload test results
      uses: actions/upload-artifact@v4
      if: always()
      with:
        name: macos-test-results
        path: ./test-results/
```

**Step 2: Commit**

```bash
git add .github/workflows/dotnet.yml
git commit -m "ci(macos): add test execution to macOS workflow job"
```

---

### Task 7: Build verification and push

**Step 1: Full solution build**

Run: `dotnet build src/FastFind.sln --configuration Release`
Expected: Build succeeded (all projects)

**Step 2: Run tests locally (Windows — macOS tests will be skipped by guard)**

Run: `dotnet test src/FastFind.Unix.Tests/FastFind.Unix.Tests.csproj --filter "Category!=Performance"`
Expected: All tests pass (macOS tests skipped due to platform guard)

**Step 3: Push and trigger workflow**

```bash
git push origin main
```

Then manually trigger workflow_dispatch from GitHub Actions to run macOS tests.

**Step 4: Update CLAUDE.md platform status**

Update the Platform Support section in CLAUDE.md:
- macOS: 🚧 Planned (Phase 2) → ✅ Preview — DriveInfo + /Volumes mount detection, FileSystemWatcher/FSEvents
