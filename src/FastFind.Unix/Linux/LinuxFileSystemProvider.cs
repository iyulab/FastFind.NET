using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FastFind.Unix.Linux;

/// <summary>
/// Linux-optimized file system provider using standard .NET APIs
/// with Channel-based architecture for high-throughput async processing.
/// </summary>
internal class LinuxFileSystemProvider : IFileSystemProvider
{
    private readonly ILogger<LinuxFileSystemProvider> _logger;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private bool _disposed;

    public LinuxFileSystemProvider(ILoggerFactory? loggerFactory = null)
    {
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<LinuxFileSystemProvider>();
    }

    /// <inheritdoc/>
    public PlatformType SupportedPlatform => PlatformType.Linux;

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsLinux();

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations,
        IndexingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (cancellationToken.IsCancellationRequested)
            yield break;

        var locationArray = locations.ToArray();
        if (locationArray.Length == 0)
            yield break;

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
                await ProduceFileItemsAsync(locationArray, options, channel.Writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during file enumeration");
            }
            finally
            {
                try { channel.Writer.Complete(); } catch { }
            }
        }, cancellationToken);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            try
            {
                await producerTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Timeout or other exception — ignore during cleanup
            }
        }
    }

    /// <summary>
    /// Producer that enumerates files using parallel BFS traversal with Channel-based work-stealing.
    /// Directories at depth &lt;= 2 are dispatched to a work queue for parallel processing;
    /// deeper directories are traversed inline.
    /// </summary>
    private async Task ProduceFileItemsAsync(
        string[] locations,
        IndexingOptions options,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        var maxParallelism = Math.Max(1, options.ParallelThreads);
        var workChannel = Channel.CreateUnbounded<(string Path, int Depth)>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        // Seed the work channel with root locations
        foreach (var location in locations)
        {
            if (Directory.Exists(location))
            {
                await workChannel.Writer.WriteAsync((location, 0), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var activeWorkers = 0;
        var workerLock = new object();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workers = new Task[maxParallelism];
        for (int i = 0; i < maxParallelism; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        (string dirPath, int depth) item;
                        try
                        {
                            // Try to read from work channel with a timeout to detect completion
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(100));
                            item = await workChannel.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Timeout — check if all workers are idle and no work remains
                            lock (workerLock)
                            {
                                if (activeWorkers == 0 && workChannel.Reader.Count == 0)
                                {
                                    allDone.TrySetResult();
                                    return;
                                }
                            }
                            continue;
                        }

                        lock (workerLock) { activeWorkers++; }

                        try
                        {
                            await ProcessDirectoryAsync(
                                item.dirPath, item.depth, options, writer, workChannel.Writer, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Normal cancellation
                        }
                        catch (UnauthorizedAccessException)
                        {
                            _logger.LogDebug("Access denied: {Path}", item.dirPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error processing directory: {Path}", item.dirPath);
                        }
                        finally
                        {
                            lock (workerLock)
                            {
                                activeWorkers--;
                                if (activeWorkers == 0 && workChannel.Reader.Count == 0)
                                {
                                    allDone.TrySetResult();
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Worker cancelled
                }
                catch (ChannelClosedException)
                {
                    // Channel closed
                }
            }, cancellationToken);
        }

        // Wait for all work to complete or cancellation
        await Task.WhenAny(allDone.Task, Task.Delay(Timeout.Infinite, cancellationToken))
            .ConfigureAwait(false);

        workChannel.Writer.TryComplete();

        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Timeout or cancellation during shutdown
        }
    }

    /// <summary>
    /// Processes a single directory: enumerates its entries,
    /// writes FileItems to the output channel, and dispatches subdirectories.
    /// Directories at depth &lt;= 2 are dispatched to the work queue;
    /// deeper directories are traversed inline.
    /// </summary>
    private async Task ProcessDirectoryAsync(
        string dirPath,
        int depth,
        IndexingOptions options,
        ChannelWriter<FileItem> output,
        ChannelWriter<(string Path, int Depth)> workQueue,
        CancellationToken cancellationToken)
    {
        if (options.MaxDepth.HasValue && depth > options.MaxDepth.Value)
            return;

        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(dirPath);
            if (!dirInfo.Exists)
                return;
        }
        catch
        {
            return;
        }

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = dirInfo.EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Access denied enumerating: {Path}", dirPath);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "IO error enumerating: {Path}", dirPath);
            return;
        }

        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                // Skip hidden entries unless requested
                if (!options.IncludeHidden && entry.Name.StartsWith('.'))
                    continue;

                // Skip symlinks unless following
                if (!options.FollowSymlinks && entry.LinkTarget != null)
                    continue;

                bool isDirectory = entry is DirectoryInfo;

                if (isDirectory)
                {
                    var subDir = (DirectoryInfo)entry;

                    // Check excluded paths
                    if (IsExcludedPath(subDir.FullName, options.ExcludedPaths))
                        continue;

                    // Write directory item
                    var dirItem = CreateFileItem(subDir);
                    await output.WriteAsync(dirItem, cancellationToken).ConfigureAwait(false);

                    // Dispatch or inline traversal based on depth
                    if (depth <= 2)
                    {
                        // Dispatch to work queue for parallel processing
                        await workQueue.WriteAsync((subDir.FullName, depth + 1), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // Inline traversal for deeper directories
                        await ProcessDirectoryAsync(
                            subDir.FullName, depth + 1, options, output, workQueue, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else if (entry is FileInfo fileInfo)
                {
                    // Check extension exclusions
                    if (options.ExcludedExtensions.Count > 0 &&
                        !string.IsNullOrEmpty(fileInfo.Extension) &&
                        options.ExcludedExtensions.Contains(fileInfo.Extension, StringComparer.OrdinalIgnoreCase))
                        continue;

                    // Check max file size
                    if (options.MaxFileSize.HasValue)
                    {
                        try
                        {
                            if (fileInfo.Length > options.MaxFileSize.Value)
                                continue;
                        }
                        catch
                        {
                            // Cannot read length; skip check
                        }
                    }

                    var item = CreateFileItem(fileInfo);
                    await output.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible entries
            }
            catch (IOException)
            {
                // Skip entries with IO errors
            }
        }
    }

    /// <inheritdoc/>
    public Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            if (File.Exists(filePath))
            {
                var info = new FileInfo(filePath);
                return Task.FromResult<FileItem?>(CreateFileItem(info));
            }

            if (Directory.Exists(filePath))
            {
                var info = new DirectoryInfo(filePath);
                return Task.FromResult<FileItem?>(CreateFileItem(info));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting file info: {Path}", filePath);
        }

        return Task.FromResult<FileItem?>(null);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<Interfaces.DriveInfo>> GetAvailableLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var drives = new List<Interfaces.DriveInfo>();

        try
        {
            var mounts = ParseProcMounts();

            foreach (var mount in mounts)
            {
                if (UnixPathHelper.IsVirtualFileSystem(mount.FsType))
                    continue;

                try
                {
                    var sysDrive = new System.IO.DriveInfo(mount.MountPoint);
                    if (sysDrive.IsReady)
                    {
                        drives.Add(new Interfaces.DriveInfo
                        {
                            Name = mount.MountPoint,
                            Label = mount.Device,
                            FileSystem = mount.FsType,
                            TotalSize = sysDrive.TotalSize,
                            AvailableSpace = sysDrive.AvailableFreeSpace,
                            IsReady = true,
                            DriveType = MapDriveType(sysDrive.DriveType)
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error reading drive info for mount: {Mount}", mount.MountPoint);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing /proc/mounts");
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
                    Label = "Root",
                    FileSystem = "unknown",
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
                    Label = "Root",
                    FileSystem = "unknown",
                    TotalSize = 0,
                    AvailableSpace = 0,
                    IsReady = true,
                    DriveType = Interfaces.DriveType.Fixed
                });
            }
        }

        return Task.FromResult<IEnumerable<Interfaces.DriveInfo>>(drives);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var channelOptions = new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        var channel = Channel.CreateBounded<FileChangeEventArgs>(channelOptions);

        var watchers = new List<FileSystemWatcher>();

        try
        {
            foreach (var location in locations)
            {
                if (!Directory.Exists(location))
                    continue;

                var watcher = new FileSystemWatcher(location)
                {
                    IncludeSubdirectories = options.IncludeSubdirectories,
                    InternalBufferSize = Math.Max(4096, options.BufferSize),
                    EnableRaisingEvents = false
                };

                var notifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                if (options.MonitorModification)
                    notifyFilter |= NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.NotifyFilter = notifyFilter;

                if (options.MonitorCreation || options.MonitorModification)
                {
                    watcher.Created += (_, e) => TryWriteChange(channel.Writer, FileChangeType.Created, e.FullPath);
                    watcher.Changed += (_, e) => TryWriteChange(channel.Writer, FileChangeType.Modified, e.FullPath);
                }

                if (options.MonitorDeletion)
                {
                    watcher.Deleted += (_, e) => TryWriteChange(channel.Writer, FileChangeType.Deleted, e.FullPath);
                }

                if (options.MonitorRename)
                {
                    watcher.Renamed += (_, e) =>
                    {
                        var args = new FileChangeEventArgs(FileChangeType.Renamed, e.FullPath, oldPath: e.OldFullPath);
                        channel.Writer.TryWrite(args);
                    };
                }

                watcher.Error += (_, e) =>
                {
                    _logger.LogWarning(e.GetException(), "FileSystemWatcher error for: {Path}", location);
                };

                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
                _watchers.TryAdd(location, watcher);
            }

            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    _watchers.TryRemove(watcher.Path, out _);
                    watcher.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var exists = File.Exists(path) || Directory.Exists(path);
        return Task.FromResult(exists);
    }

    /// <inheritdoc/>
    public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            var mounts = ParseProcMounts();
            var fullPath = Path.GetFullPath(path);

            // Find the longest matching mount point (physical FS first, then any FS)
            MountEntry? bestMatch = null;
            MountEntry? fallbackMatch = null;
            int bestLength = -1;
            int fallbackLength = -1;

            foreach (var mount in mounts)
            {
                if (!fullPath.StartsWith(mount.MountPoint, StringComparison.Ordinal))
                    continue;

                if (UnixPathHelper.IsVirtualFileSystem(mount.FsType))
                {
                    // Track as fallback (e.g., overlay in Docker containers)
                    if (mount.MountPoint.Length > fallbackLength)
                    {
                        fallbackMatch = mount;
                        fallbackLength = mount.MountPoint.Length;
                    }
                    continue;
                }

                if (mount.MountPoint.Length > bestLength)
                {
                    bestMatch = mount;
                    bestLength = mount.MountPoint.Length;
                }
            }

            if (bestMatch != null)
            {
                return Task.FromResult(bestMatch.FsType);
            }

            // Fallback to virtual FS type (e.g., overlay in containers)
            if (fallbackMatch != null)
            {
                return Task.FromResult(fallbackMatch.FsType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error determining file system type for: {Path}", path);
        }

        return Task.FromResult("unknown");
    }

    /// <inheritdoc/>
    public ProviderPerformance GetPerformanceInfo()
    {
        return new ProviderPerformance
        {
            EstimatedFilesPerSecond = 50_000,
            SupportsFastEnumeration = false,
            SupportsNativeMonitoring = true,
            MemoryOverheadPerFile = 200,
            Priority = 50
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _watchers)
        {
            try
            {
                kvp.Value.EnableRaisingEvents = false;
                kvp.Value.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        _watchers.Clear();
    }

    // ────────────────────────────── Private Helpers ──────────────────────────────

    private static FileItem CreateFileItem(FileInfo fileInfo)
    {
        return new FileItem
        {
            FullPath = fileInfo.FullName,
            Name = fileInfo.Name,
            DirectoryPath = fileInfo.DirectoryName ?? "/",
            Extension = fileInfo.Extension,
            Size = TryGetFileSize(fileInfo),
            CreatedTime = fileInfo.CreationTimeUtc,
            ModifiedTime = fileInfo.LastWriteTimeUtc,
            AccessedTime = fileInfo.LastAccessTimeUtc,
            Attributes = fileInfo.Attributes,
            DriveLetter = '/',
            FileRecordNumber = null
        };
    }

    private static FileItem CreateFileItem(DirectoryInfo dirInfo)
    {
        return new FileItem
        {
            FullPath = dirInfo.FullName,
            Name = dirInfo.Name,
            DirectoryPath = dirInfo.Parent?.FullName ?? "/",
            Extension = string.Empty,
            Size = 0,
            CreatedTime = dirInfo.CreationTimeUtc,
            ModifiedTime = dirInfo.LastWriteTimeUtc,
            AccessedTime = dirInfo.LastAccessTimeUtc,
            Attributes = dirInfo.Attributes,
            DriveLetter = '/',
            FileRecordNumber = null
        };
    }

    private static long TryGetFileSize(FileInfo fileInfo)
    {
        try
        {
            return fileInfo.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsExcludedPath(string path, IList<string> excludedPaths)
    {
        foreach (var excluded in excludedPaths)
        {
            // Simple substring match for common patterns
            var pattern = excluded.Replace("**", "").Replace("*", "").Trim('/');
            if (!string.IsNullOrEmpty(pattern) &&
                path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void TryWriteChange(ChannelWriter<FileChangeEventArgs> writer, FileChangeType changeType, string path)
    {
        var args = new FileChangeEventArgs(changeType, path);
        writer.TryWrite(args);
    }

    /// <summary>
    /// Parses /proc/mounts to get mount point information.
    /// </summary>
    private static List<MountEntry> ParseProcMounts()
    {
        var mounts = new List<MountEntry>();
        const string procMountsPath = "/proc/mounts";

        if (!File.Exists(procMountsPath))
            return mounts;

        try
        {
            foreach (var line in File.ReadLines(procMountsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Format: device mountpoint fstype options dump pass
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    mounts.Add(new MountEntry
                    {
                        Device = parts[0],
                        MountPoint = parts[1],
                        FsType = parts[2]
                    });
                }
            }
        }
        catch
        {
            // Cannot read /proc/mounts — return empty
        }

        return mounts;
    }

    private static Interfaces.DriveType MapDriveType(System.IO.DriveType driveType)
    {
        return driveType switch
        {
            System.IO.DriveType.Fixed => Interfaces.DriveType.Fixed,
            System.IO.DriveType.Removable => Interfaces.DriveType.Removable,
            System.IO.DriveType.Network => Interfaces.DriveType.Network,
            System.IO.DriveType.Ram => Interfaces.DriveType.Ram,
            _ => Interfaces.DriveType.Unknown
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LinuxFileSystemProvider));
    }

    /// <summary>
    /// Represents a parsed mount entry from /proc/mounts
    /// </summary>
    private class MountEntry
    {
        public string Device { get; set; } = string.Empty;
        public string MountPoint { get; set; } = string.Empty;
        public string FsType { get; set; } = string.Empty;
    }
}
