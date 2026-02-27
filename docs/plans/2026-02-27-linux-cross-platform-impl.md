# FastFind.Unix Linux Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** FastFind.Unix 패키지에 Linux 파일 열거, 변경 추적, 팩토리 등록을 구현하여 WSL 및 Linux에서 동작하는 크로스 플랫폼 파일 검색 라이브러리를 완성한다.

**Architecture:** 기존 Windows 구현(WindowsSearchEngine, WindowsFileSystemProvider)의 패턴을 그대로 따른다. ModuleInitializer 기반 자동 등록, BoundedChannel 기반 병렬 열거, FileSystemWatcher 기반 변경 추적. 단일 FastFind.Unix 패키지에 Linux/MacOS 폴더를 분리하고 런타임에 OS 감지로 분기한다.

**Tech Stack:** .NET 10, xUnit + FluentAssertions, System.Threading.Channels, Microsoft.Extensions.DependencyInjection/Logging

**Design Doc:** `docs/plans/2026-02-27-linux-cross-platform-design.md`

**Development Environment:** WSL(Ubuntu)에서 직접 빌드/테스트, Docker는 다중 배포판 호환성 테스트 전용.

---

## Task 1: Project Scaffolding — FastFind.Unix.csproj 확장

**Files:**
- Modify: `src/FastFind.Unix/FastFind.Unix.csproj`
- Delete: `src/FastFind.Unix/Class1.cs`

**Step 1: csproj를 Windows 프로젝트 수준으로 확장**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <Platforms>AnyCPU;x64</Platforms>
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
    <RuntimeIdentifiers>linux-x64;linux-arm64;osx-x64;osx-arm64</RuntimeIdentifiers>
    <PackageId>FastFind.Unix</PackageId>
    <Title>FastFind.NET Unix</Title>
    <Description>Unix/Linux implementation for FastFind.NET high-performance file search library</Description>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="FastFind.Unix.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FastFind\FastFind.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: placeholder Class1.cs 삭제**

```bash
rm src/FastFind.Unix/Class1.cs
```

**Step 3: Linux/, MacOS/, Common/ 디렉터리 생성**

```bash
mkdir -p src/FastFind.Unix/Linux src/FastFind.Unix/MacOS src/FastFind.Unix/Common
```

**Step 4: WSL에서 빌드 확인**

Run: `dotnet build src/FastFind.Unix/FastFind.Unix.csproj`
Expected: Build succeeded (warning은 OK, error 0)

**Step 5: Commit**

```bash
git add src/FastFind.Unix/
git commit -m "chore: scaffold FastFind.Unix project structure for Linux support"
```

---

## Task 2: UnixRegistration — ModuleInitializer 자동 등록

**Files:**
- Create: `src/FastFind.Unix/UnixRegistration.cs`

**Reference:** `src/FastFind.Windows/WindowsSearchEngine.cs:18-64` (WindowsRegistration 패턴)

**Step 1: 테스트 없이 등록 코드 작성 (테스트는 Task 5에서)**

`src/FastFind.Unix/UnixRegistration.cs`:

```csharp
using FastFind;
using FastFind.Interfaces;
using System.Runtime.CompilerServices;

namespace FastFind.Unix;

/// <summary>
/// Unix search engine registration helper
/// </summary>
public static class UnixRegistration
{
    private static volatile bool _isRegistered = false;
    private static readonly object _lock = new object();

#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        EnsureRegistered();
    }

    public static void EnsureRegistered()
    {
        if (_isRegistered) return;

        lock (_lock)
        {
            if (_isRegistered) return;

            if (OperatingSystem.IsLinux())
            {
                FastFinder.RegisterSearchEngineFactory(
                    PlatformType.Linux,
                    loggerFactory => UnixSearchEngine.CreateLinuxSearchEngine(loggerFactory));
                _isRegistered = true;
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Phase 2: macOS 구현 시 활성화
                // FastFinder.RegisterSearchEngineFactory(
                //     PlatformType.MacOS,
                //     loggerFactory => UnixSearchEngine.CreateMacOSSearchEngine(loggerFactory));
                // _isRegistered = true;
            }
        }
    }
}
```

**Step 2: 빌드 확인 (아직 UnixSearchEngine이 없으므로 실패 예상)**

Run: `dotnet build src/FastFind.Unix/FastFind.Unix.csproj`
Expected: FAIL — `UnixSearchEngine` does not exist

> 다음 Task에서 UnixSearchEngine을 만들어 해결한다.

---

## Task 3: LinuxFileSystemProvider — IFileSystemProvider 구현

**Files:**
- Create: `src/FastFind.Unix/Linux/LinuxFileSystemProvider.cs`
- Create: `src/FastFind.Unix/Common/UnixPathHelper.cs`

**Reference:** `src/FastFind.Windows/Implementation/WindowsFileSystemProvider.cs` (Channel 패턴)

**Step 1: UnixPathHelper 작성**

`src/FastFind.Unix/Common/UnixPathHelper.cs`:

```csharp
namespace FastFind.Unix.Common;

internal static class UnixPathHelper
{
    private static readonly HashSet<string> VirtualFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysfs", "proc", "devtmpfs", "devpts", "tmpfs", "securityfs",
        "cgroup", "cgroup2", "pstore", "debugfs", "hugetlbfs", "mqueue",
        "fusectl", "configfs", "binfmt_misc", "tracefs", "bpf", "overlay"
    };

    public static bool IsVirtualFileSystem(string fsType) => VirtualFileSystems.Contains(fsType);

    public static char GetMountPointIdentifier(string path)
    {
        // Unix에선 drive letter 개념이 없으므로 '/' 반환
        return '/';
    }

    public static bool ShouldExcludePath(string path, IEnumerable<string> excludedPaths)
    {
        foreach (var excluded in excludedPaths)
        {
            if (path.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
```

**Step 2: LinuxFileSystemProvider 작성**

`src/FastFind.Unix/Linux/LinuxFileSystemProvider.cs`:

```csharp
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FastFind.Unix.Linux;

internal class LinuxFileSystemProvider : IFileSystemProvider
{
    private readonly ILogger<LinuxFileSystemProvider> _logger;
    private bool _disposed = false;

    public LinuxFileSystemProvider(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<LinuxFileSystemProvider>()
            ?? NullLoggerFactory.Instance.CreateLogger<LinuxFileSystemProvider>();
    }

    public PlatformType SupportedPlatform => PlatformType.Linux;
    public bool IsAvailable => OperatingSystem.IsLinux();

    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations,
        IndexingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) yield break;

        var locationArray = locations.ToArray();
        if (locationArray.Length == 0) yield break;

        var channelOptions = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        var channel = Channel.CreateBounded<FileItem>(channelOptions);

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await ProduceFileItemsAsync(locationArray, options, channel.Writer, cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File enumeration producer error");
            }
            finally
            {
                try { channel.Writer.Complete(); } catch { }
            }
        }, cancellationToken);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            try { await producerTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
            catch { }
        }
    }

    private async Task ProduceFileItemsAsync(
        string[] locations,
        IndexingOptions options,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        var workerCount = Math.Clamp(options.ParallelThreads, 1, Environment.ProcessorCount);
        var directoryQueue = Channel.CreateUnbounded<(string Path, int Depth)>();

        // Seed root directories
        foreach (var location in locations)
        {
            if (Directory.Exists(location))
                await directoryQueue.Writer.WriteAsync((location, 0), cancellationToken);
        }
        directoryQueue.Writer.Complete();

        // BFS parallel traversal
        var pendingDirs = Channel.CreateUnbounded<(string Path, int Depth)>();
        var activeWorkers = 0;
        var allDone = new TaskCompletionSource();

        // Transfer seed directories
        await foreach (var dir in directoryQueue.Reader.ReadAllAsync(cancellationToken))
        {
            await pendingDirs.Writer.WriteAsync(dir, cancellationToken);
        }

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            Interlocked.Increment(ref activeWorkers);
            try
            {
                while (await pendingDirs.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (pendingDirs.Reader.TryRead(out var entry))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await EnumerateDirectoryAsync(
                            entry.Path, entry.Depth, options, writer, pendingDirs.Writer, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Worker error processing directories");
            }
            finally
            {
                if (Interlocked.Decrement(ref activeWorkers) == 0)
                {
                    try { pendingDirs.Writer.TryComplete(); } catch { }
                }
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(workers);
    }

    private async Task EnumerateDirectoryAsync(
        string dirPath,
        int depth,
        IndexingOptions options,
        ChannelWriter<FileItem> fileWriter,
        ChannelWriter<(string Path, int Depth)> dirWriter,
        CancellationToken cancellationToken)
    {
        if (options.MaxDepth.HasValue && depth > options.MaxDepth.Value)
            return;

        try
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = options.IncludeHidden ? 0 : FileAttributes.Hidden,
                ReturnSpecialDirectories = false
            };

            foreach (var entry in new DirectoryInfo(dirPath).EnumerateFileSystemInfos("*", enumOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry is DirectoryInfo subDir)
                {
                    if (!options.FollowSymlinks && subDir.LinkTarget != null)
                        continue;

                    if (UnixPathHelper.ShouldExcludePath(subDir.FullName, options.ExcludedPaths))
                        continue;

                    // Depth-based branching: shallow dirs go to queue, deep dirs processed inline
                    if (depth < 2)
                        await dirWriter.WriteAsync((subDir.FullName, depth + 1), cancellationToken);
                    else
                        await EnumerateDirectoryAsync(
                            subDir.FullName, depth + 1, options, fileWriter, dirWriter, cancellationToken);

                    // Also emit directory as a FileItem
                    await fileWriter.WriteAsync(CreateFileItem(subDir), cancellationToken);
                }
                else if (entry is FileInfo fileInfo)
                {
                    if (!options.IncludeSystem && (fileInfo.Attributes & FileAttributes.System) != 0)
                        continue;

                    if (options.MaxFileSize.HasValue && fileInfo.Length > options.MaxFileSize.Value)
                        continue;

                    var ext = fileInfo.Extension.ToLowerInvariant();
                    if (options.ExcludedExtensions.Contains(ext))
                        continue;

                    await fileWriter.WriteAsync(CreateFileItem(fileInfo), cancellationToken);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "IO error enumerating {Path}", dirPath);
        }
    }

    private static FileItem CreateFileItem(FileSystemInfo info)
    {
        var isDir = info is DirectoryInfo;
        return new FileItem
        {
            FullPath = info.FullName,
            Name = info.Name,
            DirectoryPath = isDir ? info.FullName : Path.GetDirectoryName(info.FullName) ?? string.Empty,
            Extension = isDir ? string.Empty : info.Extension,
            Size = isDir ? 0 : ((FileInfo)info).Length,
            CreatedTime = info.CreationTimeUtc,
            ModifiedTime = info.LastWriteTimeUtc,
            AccessedTime = info.LastAccessTimeUtc,
            Attributes = info.Attributes,
            DriveLetter = '/'
        };
    }

    public Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(filePath))
                return Task.FromResult<FileItem?>(CreateFileItem(new FileInfo(filePath)));
            if (Directory.Exists(filePath))
                return Task.FromResult<FileItem?>(CreateFileItem(new DirectoryInfo(filePath)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting file info for {Path}", filePath);
        }
        return Task.FromResult<FileItem?>(null);
    }

    public Task<IEnumerable<Interfaces.DriveInfo>> GetAvailableLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        var drives = new List<Interfaces.DriveInfo>();

        try
        {
            // Parse /proc/mounts for physical mount points
            if (File.Exists("/proc/mounts"))
            {
                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;

                    var device = parts[0];
                    var mountPoint = parts[1];
                    var fsType = parts[2];

                    if (UnixPathHelper.IsVirtualFileSystem(fsType))
                        continue;

                    if (!device.StartsWith("/dev/"))
                        continue;

                    try
                    {
                        var driveInfo = new System.IO.DriveInfo(mountPoint);
                        drives.Add(new Interfaces.DriveInfo
                        {
                            Name = mountPoint,
                            Label = Path.GetFileName(device),
                            FileSystem = fsType,
                            TotalSize = driveInfo.TotalSize,
                            AvailableSpace = driveInfo.AvailableFreeSpace,
                            IsReady = driveInfo.IsReady,
                            DriveType = Interfaces.DriveType.Fixed
                        });
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading mount points");
        }

        if (drives.Count == 0)
        {
            // Fallback: at least report root
            drives.Add(new Interfaces.DriveInfo
            {
                Name = "/",
                FileSystem = "unknown",
                IsReady = true,
                DriveType = Interfaces.DriveType.Fixed
            });
        }

        return Task.FromResult<IEnumerable<Interfaces.DriveInfo>>(drives);
    }

    public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var changeChannel = Channel.CreateBounded<FileChangeEventArgs>(
            new BoundedChannelOptions(options.BufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        var watchers = new List<FileSystemWatcher>();

        try
        {
            foreach (var location in locations)
            {
                if (!Directory.Exists(location)) continue;

                var watcher = new FileSystemWatcher(location)
                {
                    IncludeSubdirectories = options.IncludeSubdirectories,
                    InternalBufferSize = Math.Max(options.BufferSize, 65536),
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite | NotifyFilters.Size
                };

                void Enqueue(FileChangeType type, string path) =>
                    changeChannel.Writer.TryWrite(new FileChangeEventArgs(type, path));

                if (options.MonitorCreation)
                    watcher.Created += (_, e) => Enqueue(FileChangeType.Created, e.FullPath);
                if (options.MonitorModification)
                    watcher.Changed += (_, e) => Enqueue(FileChangeType.Modified, e.FullPath);
                if (options.MonitorDeletion)
                    watcher.Deleted += (_, e) => Enqueue(FileChangeType.Deleted, e.FullPath);
                if (options.MonitorRename)
                    watcher.Renamed += (_, e) => Enqueue(FileChangeType.Renamed, e.FullPath);
                watcher.Error += (_, e) =>
                    _logger.LogWarning(e.GetException(), "FileSystemWatcher error on {Location}", location);

                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            await foreach (var change in changeChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
        finally
        {
            foreach (var w in watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(path) || Directory.Exists(path));

    public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists("/proc/mounts"))
            {
                var bestMatch = "";
                var bestFsType = "unknown";

                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;

                    var mountPoint = parts[1];
                    if (path.StartsWith(mountPoint) && mountPoint.Length > bestMatch.Length)
                    {
                        bestMatch = mountPoint;
                        bestFsType = parts[2];
                    }
                }

                return Task.FromResult(bestFsType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error detecting filesystem type for {Path}", path);
        }
        return Task.FromResult("unknown");
    }

    public ProviderPerformance GetPerformanceInfo() => new()
    {
        EstimatedFilesPerSecond = 500_000,
        SupportsFastEnumeration = false,  // Phase 2: getdents64로 true
        SupportsNativeMonitoring = true,  // inotify via FileSystemWatcher
        MemoryOverheadPerFile = 200,
        Priority = 50  // Windows MFT보다 낮음
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
```

**Step 3: 빌드 확인 (아직 UnixSearchEngine이 없으므로 실패 예상)**

이 파일은 UnixSearchEngine과 함께 빌드됨.

---

## Task 4: UnixSearchEngine — Factory + ISearchEngine 오케스트레이션

**Files:**
- Create: `src/FastFind.Unix/UnixSearchEngine.cs`

**Reference:** `src/FastFind.Windows/WindowsSearchEngine.cs:69-117` (CreateWindowsSearchEngine 패턴)
**Reference:** `src/FastFind.Windows/Implementation/WindowsSearchEngineImpl.cs` (ISearchEngine 구현)

**Step 1: UnixSearchEngine 팩토리 + UnixSearchEngineImpl 작성**

`src/FastFind.Unix/UnixSearchEngine.cs`:

```csharp
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Linux;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FastFind.Unix;

/// <summary>
/// Unix search engine factory
/// </summary>
public static class UnixSearchEngine
{
    public static ISearchEngine CreateLinuxSearchEngine(ILoggerFactory? loggerFactory = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux search engine can only be used on Linux");

        return new UnixSearchEngineImpl(
            new LinuxFileSystemProvider(loggerFactory),
            loggerFactory?.CreateLogger<UnixSearchEngineImpl>());
    }
}

internal class UnixSearchEngineImpl : ISearchEngine
{
    private readonly IFileSystemProvider _provider;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, FileItem> _index = new();
    private CancellationTokenSource? _indexingCts;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private long _totalIndexedFiles;
    private bool _disposed;

    public event EventHandler<IndexingProgressEventArgs>? IndexingProgressChanged;
    public event EventHandler<FileChangeEventArgs>? FileChanged;
    public event EventHandler<SearchProgressEventArgs>? SearchProgressChanged;

    public bool IsIndexing { get; private set; }
    public bool IsMonitoring { get; private set; }
    public long TotalIndexedFiles => Interlocked.Read(ref _totalIndexedFiles);

    public UnixSearchEngineImpl(IFileSystemProvider provider, ILogger? logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task StartIndexingAsync(IndexingOptions options, CancellationToken cancellationToken = default)
    {
        if (IsIndexing) return;
        IsIndexing = true;
        _indexingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sw = Stopwatch.StartNew();
        var count = 0L;

        try
        {
            var locations = options.GetEffectiveSearchLocations().ToArray();
            _logger?.LogInformation("Starting indexing on {Locations}", string.Join(", ", locations));

            IndexingProgressChanged?.Invoke(this, new IndexingProgressEventArgs(
                string.Join(", ", locations), 0, 0, TimeSpan.Zero, "", IndexingPhase.Scanning));

            await foreach (var item in _provider.EnumerateFilesAsync(locations, options, _indexingCts.Token))
            {
                _index[item.FullPath] = item;
                var current = Interlocked.Increment(ref count);
                Interlocked.Exchange(ref _totalIndexedFiles, current);

                if (current % 10000 == 0)
                {
                    IndexingProgressChanged?.Invoke(this, new IndexingProgressEventArgs(
                        string.Join(", ", locations), current, 0, sw.Elapsed,
                        item.DirectoryPath, IndexingPhase.Scanning));
                }
            }

            IndexingProgressChanged?.Invoke(this, new IndexingProgressEventArgs(
                string.Join(", ", locations), count, count, sw.Elapsed, "", IndexingPhase.Completed));

            _logger?.LogInformation("Indexing completed: {Count} files in {Elapsed}", count, sw.Elapsed);

            if (options.EnableMonitoring)
            {
                _monitoringCts = new CancellationTokenSource();
                _monitoringTask = MonitorInBackgroundAsync(locations, options, _monitoringCts.Token);
            }
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private async Task MonitorInBackgroundAsync(
        string[] locations, IndexingOptions options, CancellationToken ct)
    {
        IsMonitoring = true;
        var monitorOptions = new MonitoringOptions
        {
            IncludeSubdirectories = true,
            BufferSize = 65536
        };

        try
        {
            await foreach (var change in _provider.MonitorChangesAsync(locations, monitorOptions, ct))
            {
                switch (change.ChangeType)
                {
                    case FileChangeType.Created:
                    case FileChangeType.Modified:
                        var info = await _provider.GetFileInfoAsync(change.NewPath, ct);
                        if (info != null)
                        {
                            _index[info.FullPath] = info;
                            Interlocked.Increment(ref _totalIndexedFiles);
                        }
                        break;
                    case FileChangeType.Deleted:
                        if (_index.TryRemove(change.NewPath, out _))
                            Interlocked.Decrement(ref _totalIndexedFiles);
                        break;
                }

                FileChanged?.Invoke(this, change);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsMonitoring = false;
        }
    }

    public Task StopIndexingAsync(CancellationToken cancellationToken = default)
    {
        _indexingCts?.Cancel();
        _monitoringCts?.Cancel();
        IsIndexing = false;
        IsMonitoring = false;
        return Task.CompletedTask;
    }

    public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var searchText = query.SearchText?.ToLowerInvariant() ?? "";

        var matches = _index.Values
            .Where(f => MatchesQuery(f, query))
            .ToList();

        sw.Stop();

        var result = new SearchResult
        {
            Query = query,
            TotalMatches = matches.Count,
            ResultCount = Math.Min(matches.Count, query.MaxResults),
            SearchTime = sw.Elapsed,
            IsComplete = true,
            HasMoreResults = matches.Count > query.MaxResults,
            Files = ToAsyncEnumerable(matches.Take(query.MaxResults))
        };

        return Task.FromResult(result);
    }

    public Task<SearchResult> SearchAsync(string searchText, CancellationToken cancellationToken = default)
    {
        var query = new SearchQuery { SearchText = searchText };
        return SearchAsync(query, cancellationToken);
    }

    private static bool MatchesQuery(FileItem item, SearchQuery query)
    {
        if (!string.IsNullOrEmpty(query.SearchText))
        {
            var searchLower = query.SearchText.ToLowerInvariant();
            if (!item.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(query.ExtensionFilter))
        {
            var ext = query.ExtensionFilter.StartsWith('.') ? query.ExtensionFilter : $".{query.ExtensionFilter}";
            if (!item.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (query.MinSize.HasValue && item.Size < query.MinSize.Value)
            return false;
        if (query.MaxSize.HasValue && item.Size > query.MaxSize.Value)
            return false;

        return true;
    }

    private static async IAsyncEnumerable<FastFileItem> ToAsyncEnumerable(
        IEnumerable<FileItem> items, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item.ToFastFileItem();
        }
    }

    public async IAsyncEnumerable<SearchResult> SearchRealTimeAsync(
        SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await SearchAsync(query, cancellationToken);
    }

    public Task<IndexingStatistics> GetIndexingStatisticsAsync(CancellationToken ct = default)
        => Task.FromResult(new IndexingStatistics
        {
            TotalFiles = TotalIndexedFiles,
            TotalDirectories = _index.Values.Count(f => f.IsDirectory),
            IsIndexing = IsIndexing
        });

    public Task<SearchStatistics> GetSearchStatisticsAsync(CancellationToken ct = default)
        => Task.FromResult(new SearchStatistics());

    public Task ClearCacheAsync(CancellationToken ct = default)
    {
        _index.Clear();
        Interlocked.Exchange(ref _totalIndexedFiles, 0);
        return Task.CompletedTask;
    }

    public Task SaveIndexAsync(string? filePath = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task LoadIndexAsync(string? filePath = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task OptimizeIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshIndexAsync(IEnumerable<string>? locations = null, CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _indexingCts?.Cancel();
        _monitoringCts?.Cancel();
        _provider.Dispose();
    }
}
```

**Step 2: 전체 빌드 확인**

Run: `dotnet build src/FastFind.Unix/FastFind.Unix.csproj`
Expected: Build succeeded

**Step 3: 솔루션 전체 빌드 확인**

Run: `dotnet build src/FastFind.sln --configuration Release`
Expected: Build succeeded (모든 프로젝트)

**Step 4: Commit**

```bash
git add src/FastFind.Unix/
git commit -m "feat: implement Linux file system provider and search engine for FastFind.Unix"
```

---

## Task 5: FastFind.Unix.Tests — 테스트 프로젝트 생성 및 핵심 테스트

**Files:**
- Create: `src/FastFind.Unix.Tests/FastFind.Unix.Tests.csproj`
- Create: `src/FastFind.Unix.Tests/TestFixtures/TestFileTreeFixture.cs`
- Create: `src/FastFind.Unix.Tests/Linux/FactoryRegistrationTests.cs`
- Create: `src/FastFind.Unix.Tests/Linux/LinuxFileSystemProviderTests.cs`
- Create: `src/FastFind.Unix.Tests/Linux/LinuxFileMonitorTests.cs`
- Modify: `src/FastFind.sln` (프로젝트 추가)

**Step 1: 테스트 프로젝트 생성**

```bash
cd src && dotnet new xunit -n FastFind.Unix.Tests --framework net10.0
```

**Step 2: csproj 수정**

`src/FastFind.Unix.Tests/FastFind.Unix.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FastFind\FastFind.csproj" />
    <ProjectReference Include="..\FastFind.Unix\FastFind.Unix.csproj" />
  </ItemGroup>

</Project>
```

**Step 3: 솔루션에 추가**

```bash
cd src && dotnet sln add FastFind.Unix.Tests/FastFind.Unix.Tests.csproj
```

**Step 4: TestFileTreeFixture 작성**

`src/FastFind.Unix.Tests/TestFixtures/TestFileTreeFixture.cs`:

```csharp
namespace FastFind.Unix.Tests.TestFixtures;

public class TestFileTreeFixture : IDisposable
{
    public string RootPath { get; }

    public TestFileTreeFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"fastfind-test-{Guid.NewGuid():N}");
        CreateTestTree();
    }

    private void CreateTestTree()
    {
        // Structure:
        // root/
        //   file1.txt (100 bytes)
        //   file2.cs  (200 bytes)
        //   .hidden   (50 bytes)
        //   sub1/
        //     file3.txt (150 bytes)
        //     sub1a/
        //       file4.log (300 bytes)
        //   sub2/
        //     file5.pdf (500 bytes)

        Directory.CreateDirectory(RootPath);
        File.WriteAllBytes(Path.Combine(RootPath, "file1.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(RootPath, "file2.cs"), new byte[200]);
        File.WriteAllBytes(Path.Combine(RootPath, ".hidden"), new byte[50]);

        var sub1 = Path.Combine(RootPath, "sub1");
        Directory.CreateDirectory(sub1);
        File.WriteAllBytes(Path.Combine(sub1, "file3.txt"), new byte[150]);

        var sub1a = Path.Combine(sub1, "sub1a");
        Directory.CreateDirectory(sub1a);
        File.WriteAllBytes(Path.Combine(sub1a, "file4.log"), new byte[300]);

        var sub2 = Path.Combine(RootPath, "sub2");
        Directory.CreateDirectory(sub2);
        File.WriteAllBytes(Path.Combine(sub2, "file5.pdf"), new byte[500]);
    }

    public void Dispose()
    {
        try { Directory.Delete(RootPath, true); } catch { }
    }
}
```

**Step 5: Factory 등록 테스트**

`src/FastFind.Unix.Tests/Linux/FactoryRegistrationTests.cs`:

```csharp
using FastFind.Interfaces;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Integration")]
[Trait("OS", "Linux")]
public class FactoryRegistrationTests
{
    [Fact]
    public void EnsureRegistered_OnLinux_ShouldRegisterFactory()
    {
        if (!OperatingSystem.IsLinux())
            return; // Skip on non-Linux

        UnixRegistration.EnsureRegistered();
        var platforms = FastFinder.GetAvailablePlatforms();
        platforms.Should().Contain(PlatformType.Linux);
    }

    [Fact]
    public void CreateSearchEngine_OnLinux_ShouldReturnEngine()
    {
        if (!OperatingSystem.IsLinux())
            return;

        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();
        engine.Should().NotBeNull();
    }

    [Fact]
    public void CreateSearchEngine_ShouldNotThrowOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var act = () => UnixSearchEngine.CreateLinuxSearchEngine();
        act.Should().NotThrow();
    }
}
```

**Step 6: LinuxFileSystemProvider 테스트**

`src/FastFind.Unix.Tests/Linux/LinuxFileSystemProviderTests.cs`:

```csharp
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class LinuxFileSystemProviderTests : IClassFixture<TestFileTreeFixture>
{
    private readonly TestFileTreeFixture _fixture;

    public LinuxFileSystemProviderTests(TestFileTreeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldReturnAllFiles()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = new IndexingOptions
        {
            SpecificDirectories = { _fixture.RootPath },
            IncludeHidden = true,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(
            new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        // 5 files + 3 directories (sub1, sub1a, sub2)
        items.Should().HaveCountGreaterOrEqualTo(5);
        items.Where(i => !i.IsDirectory).Should().HaveCount(5);
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldRespectExcludedExtensions()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = new IndexingOptions
        {
            SpecificDirectories = { _fixture.RootPath },
            IncludeHidden = true,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string> { ".log" }
        };

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(
            new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        items.Where(i => !i.IsDirectory).Should().NotContain(i => i.Extension == ".log");
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldPopulateFileItemFields()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = new IndexingOptions
        {
            SpecificDirectories = { _fixture.RootPath },
            IncludeHidden = true,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        FileItem? found = null;
        await foreach (var item in provider.EnumerateFilesAsync(
            new[] { _fixture.RootPath }, options))
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
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var filePath = Path.Combine(_fixture.RootPath, "file1.txt");

        var result = await provider.GetFileInfoAsync(filePath);
        result.Should().NotBeNull();
        result!.Name.Should().Be("file1.txt");
    }

    [Fact]
    public async Task GetFileInfoAsync_NonExistent_ShouldReturnNull()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var result = await provider.GetFileInfoAsync("/nonexistent/path/file.txt");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAvailableLocationsAsync_ShouldReturnAtLeastRoot()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var locations = await provider.GetAvailableLocationsAsync();
        locations.Should().NotBeEmpty();
        locations.Should().Contain(d => d.Name == "/" || d.Name.StartsWith("/"));
    }

    [Fact]
    public async Task GetFileSystemTypeAsync_Root_ShouldReturnType()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var fsType = await provider.GetFileSystemTypeAsync("/");
        fsType.Should().NotBe("unknown");
    }

    [Fact]
    public void IsAvailable_OnLinux_ShouldBeTrue()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        provider.IsAvailable.Should().BeTrue();
        provider.SupportedPlatform.Should().Be(PlatformType.Linux);
    }

    private static IFileSystemProvider CreateProvider()
        => new FastFind.Unix.Linux.LinuxFileSystemProvider();
}
```

**Step 7: 파일 모니터 테스트**

`src/FastFind.Unix.Tests/Linux/LinuxFileMonitorTests.cs`:

```csharp
using FastFind.Interfaces;
using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class LinuxFileMonitorTests
{
    [Fact]
    public async Task MonitorChangesAsync_ShouldDetectCreatedFile()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), $"fastfind-monitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);

        try
        {
            using var provider = new FastFind.Unix.Linux.LinuxFileSystemProvider();
            var options = new MonitoringOptions { IncludeSubdirectories = false };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            FileChangeEventArgs? detected = null;

            var monitorTask = Task.Run(async () =>
            {
                await foreach (var change in provider.MonitorChangesAsync(
                    new[] { testDir }, options, cts.Token))
                {
                    detected = change;
                    break;
                }
            }, cts.Token);

            // Give watcher time to start
            await Task.Delay(500);

            // Create a file
            var newFile = Path.Combine(testDir, "new-file.txt");
            await File.WriteAllTextAsync(newFile, "test content");

            // Wait for detection
            try { await monitorTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }

            detected.Should().NotBeNull();
            detected!.ChangeType.Should().Be(FileChangeType.Created);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}
```

**Step 8: 빌드 확인**

Run: `dotnet build src/FastFind.sln`
Expected: Build succeeded

**Step 9: WSL에서 테스트 실행**

Run: `dotnet test src/FastFind.Unix.Tests/ --filter "Category!=Performance" -v normal`
Expected: All tests pass (Linux 환경에서)

**Step 10: Commit**

```bash
git add src/FastFind.Unix.Tests/ src/FastFind.sln
git commit -m "test: add FastFind.Unix.Tests with Linux provider and monitor tests"
```

---

## Task 6: CI/CD — Linux 테스트 job + macOS 수동 검증

**Files:**
- Modify: `.github/workflows/dotnet.yml`

**Step 1: CI workflow에 test-linux, test-macos, test-linux-compat job 추가**

기존 `validate` job 뒤에 추가:

```yaml
  # 🐧 Linux 기능 테스트
  test-linux:
    runs-on: ubuntu-latest
    if: >
      github.event_name == 'pull_request' ||
      (github.event_name == 'push' && github.ref == 'refs/heads/main')
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

    - name: Restore & Build
      run: dotnet build src/FastFind.sln --configuration Release

    - name: Run Linux Tests
      run: dotnet test src/FastFind.Unix.Tests/ --configuration Release --filter "Category!=Performance" --logger "trx;LogFileName=test-results.trx" --results-directory ./test-results

    - name: Upload test results
      uses: actions/upload-artifact@v4
      if: always()
      with:
        name: linux-test-results
        path: ./test-results/

  # 🍎 macOS 검증 (수동 트리거만 — 비용 절감)
  test-macos:
    runs-on: macos-latest
    if: github.event_name == 'workflow_dispatch'
    steps:
    - uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: ${{ env.DOTNET_VERSION }}

    - name: Restore & Build
      run: dotnet build src/FastFind.sln --configuration Release

    - name: Run macOS Tests
      run: dotnet test src/FastFind.Unix.Tests/ --configuration Release --filter "OS=MacOS&Category!=Performance" --logger "trx;LogFileName=test-results.trx" --results-directory ./test-results
      continue-on-error: true  # macOS 구현 전이므로 실패 허용

    - name: Upload test results
      uses: actions/upload-artifact@v4
      if: always()
      with:
        name: macos-test-results
        path: ./test-results/

  # 🐳 다중 배포판 호환성 테스트 (수동 트리거)
  test-linux-compat:
    runs-on: ubuntu-latest
    if: github.event_name == 'workflow_dispatch'
    strategy:
      matrix:
        container: ['mcr.microsoft.com/dotnet/sdk:10.0']
    container:
      image: ${{ matrix.container }}
    steps:
    - uses: actions/checkout@v4

    - name: Build & Test
      run: |
        dotnet build src/FastFind.sln --configuration Release
        dotnet test src/FastFind.Unix.Tests/ --configuration Release --filter "Category!=Performance"
```

**Step 2: pack-and-publish matrix에 FastFind.Unix 주석 해제**

```yaml
          # Unix 구현 활성화
          - path: 'src/FastFind.Unix/FastFind.Unix.csproj'
            name: 'FastFind.Unix'
```

**Step 3: Commit**

```bash
git add .github/workflows/dotnet.yml
git commit -m "ci: add Linux test job, macOS manual trigger, and enable FastFind.Unix packaging"
```

---

## Task 7: SIMD Cross-Platform Migration

**Files:**
- Modify: `src/FastFind/Models/SIMDStringMatcher.cs`

**Note:** 이 태스크는 기존 Windows 테스트가 모두 통과하는 상태에서 진행해야 한다.

**Step 1: 현재 SIMDStringMatcher 읽기 및 분석**

`src/FastFind/Models/SIMDStringMatcher.cs`를 읽고 모든 AVX2 전용 코드를 식별한다.

**Step 2: Vector128/Vector256 기반으로 마이그레이션**

핵심 변경:
- `Avx2.IsSupported` → `Vector256.IsHardwareAccelerated`
- `Avx2.CompareEqual()` → `Vector256.Equals()`
- `Avx2.MoveMask()` → `Vector256.ExtractMostSignificantBits()`
- 3-tier fallback: Vector256 → Vector128 → Scalar

```csharp
// Before (AVX2 only):
if (Avx2.IsSupported) { /* AVX2 code */ }
else { /* scalar fallback */ }

// After (cross-platform):
if (Vector256.IsHardwareAccelerated && haystack.Length >= Vector256<short>.Count)
{
    return ContainsVector256(haystack, needle);
}
else if (Vector128.IsHardwareAccelerated && haystack.Length >= Vector128<short>.Count)
{
    return ContainsVector128(haystack, needle);
}
else
{
    return ContainsScalar(haystack, needle);
}
```

**Step 3: Windows에서 기존 테스트 실행하여 성능 회귀 없음 확인**

Run: `dotnet test src/FastFind.Windows.Tests/ --filter "Category!=Performance"`
Expected: All tests pass

**Step 4: WSL에서 SIMD 테스트 실행**

Run: `dotnet test src/FastFind.Unix.Tests/ --filter "Category!=Performance"`
Expected: All tests pass (Vector256/128이 SSE2/AVX2로 동작)

**Step 5: Commit**

```bash
git add src/FastFind/Models/SIMDStringMatcher.cs
git commit -m "feat: migrate SIMDStringMatcher to cross-platform Vector128/Vector256 abstractions"
```

---

## Task 8: Docker 호환성 테스트 설정

**Files:**
- Create: `docker/test-linux.dockerfile`
- Create: `docker/docker-compose.test.yml`
- Create: `docker/create-test-corpus.sh`

**Step 1: Dockerfile 작성**

`docker/test-linux.dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ .
RUN dotnet restore FastFind.sln
RUN dotnet build FastFind.sln --configuration Release --no-restore

FROM build AS test
# Create test file corpus
RUN mkdir -p /test-data && \
    for i in $(seq 1 1000); do \
        dir="/test-data/dir_$((i % 100))"; \
        mkdir -p "$dir"; \
        dd if=/dev/zero of="$dir/file_$i.txt" bs=1 count=$((RANDOM % 1000 + 1)) 2>/dev/null; \
    done
RUN dotnet test FastFind.Unix.Tests/ --configuration Release --filter "Category!=Performance" --logger "trx;LogFileName=results.trx" --results-directory /results

FROM scratch AS results
COPY --from=test /results/ /
```

**Step 2: docker-compose 작성**

`docker/docker-compose.test.yml`:

```yaml
services:
  test-dotnet-sdk:
    build:
      context: ..
      dockerfile: docker/test-linux.dockerfile
      target: test
    volumes:
      - ./results:/results
```

**Step 3: Commit**

```bash
git add docker/
git commit -m "chore: add Docker test infrastructure for Linux compatibility testing"
```

---

## Task 9: Integration Smoke Test — 전체 파이프라인 검증

**Files:**
- Create: `src/FastFind.Unix.Tests/Integration/EndToEndTests.cs`

**Step 1: End-to-End 테스트 작성**

`src/FastFind.Unix.Tests/Integration/EndToEndTests.cs`:

```csharp
using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("OS", "Linux")]
public class EndToEndTests : IDisposable
{
    private readonly string _testDir;

    public EndToEndTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"fastfind-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        // Create test files
        for (int i = 0; i < 100; i++)
        {
            var subDir = Path.Combine(_testDir, $"dir{i % 10}");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, $"test{i}.txt"), $"content {i}");
            File.WriteAllText(Path.Combine(subDir, $"data{i}.cs"), $"class C{i} {{}}");
        }
    }

    [Fact]
    public async Task FullPipeline_Index_Search_ShouldWork()
    {
        if (!OperatingSystem.IsLinux()) return;

        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();

        var options = new IndexingOptions
        {
            SpecificDirectories = { _testDir },
            IncludeHidden = true,
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        // Index
        await engine.StartIndexingAsync(options);
        engine.TotalIndexedFiles.Should().BeGreaterThan(0);

        // Search
        var result = await engine.SearchAsync("test");
        result.TotalMatches.Should().BeGreaterThan(0);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
        {
            items.Add(item);
        }
        items.Should().NotBeEmpty();
        items.Should().AllSatisfy(i => i.Name.Should().Contain("test"));
    }

    [Fact]
    public async Task Search_ByExtension_ShouldFilter()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        var options = new IndexingOptions
        {
            SpecificDirectories = { _testDir },
            IncludeHidden = true,
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        await engine.StartIndexingAsync(options);

        var query = new SearchQuery { ExtensionFilter = ".cs" };
        var result = await engine.SearchAsync(query);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
        {
            items.Add(item);
        }
        items.Should().NotBeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
```

**Step 2: WSL에서 전체 테스트 실행**

Run: `dotnet test src/FastFind.Unix.Tests/ --filter "Category!=Performance" -v normal`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/FastFind.Unix.Tests/Integration/
git commit -m "test: add end-to-end integration tests for Linux pipeline"
```

---

## Task Summary

| # | Task | Depends On | Est. |
|---|------|-----------|------|
| 1 | Project scaffolding | — | 5 min |
| 2 | UnixRegistration | 1 | 5 min |
| 3 | LinuxFileSystemProvider | 1 | 15 min |
| 4 | UnixSearchEngine + build | 2, 3 | 15 min |
| 5 | Test project + core tests | 4 | 20 min |
| 6 | CI/CD extension | 5 | 10 min |
| 7 | SIMD cross-platform | 5 | 20 min |
| 8 | Docker test infra | 5 | 10 min |
| 9 | E2E integration tests | 5 | 10 min |

**Critical path:** 1 → 2+3 (parallel) → 4 → 5 → 6+7+8+9 (parallel)
