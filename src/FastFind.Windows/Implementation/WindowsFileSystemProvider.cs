using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace FastFind.Windows.Implementation;

/// <summary>
/// Windows-optimized file system provider with NTFS support
/// </summary>
[SupportedOSPlatform("windows")]
internal class WindowsFileSystemProvider : IFileSystemProvider
{
    private readonly ILogger<WindowsFileSystemProvider> _logger;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _enumerationSemaphore;
    private bool _disposed = false;

    public WindowsFileSystemProvider(ILogger<WindowsFileSystemProvider> logger)
    {
        _logger = logger;
        var maxConcurrentOperations = Environment.ProcessorCount * 2;
        _enumerationSemaphore = new SemaphoreSlim(maxConcurrentOperations, maxConcurrentOperations);
    }

    /// <inheritdoc/>
    public PlatformType SupportedPlatform => PlatformType.Windows;

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsWindows();

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

        // 🚀 완전히 새로운 고성능 아키텍처 - TaskCanceledException 완전 방지
        var channel = Channel.CreateBounded<FileItem>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true // 성능 최적화
        });

        // 생산자 작업을 별도 태스크로 실행
        var producerTask = Task.Run(async () => 
        {
            try
            {
                await ProduceFileItemsUltraFastAsync(locationArray, options, channel.Writer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 정상적인 취소
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Producer error");
            }
            finally
            {
                // 반드시 채널 완료 처리
                try { channel.Writer.Complete(); } catch { }
            }
        }, cancellationToken);
        
        try
        {
            // 소비자: 간단하고 빠른 읽기
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            // 정리 작업 - 타임아웃으로 무한 대기 방지
            try
            {
                await producerTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            catch
            {
                // 타임아웃이나 기타 예외 무시
            }
        }
    }

    // 🚀 초고속 생산자 - 모든 성능 최적화 적용
    private async Task ProduceFileItemsUltraFastAsync(
        string[] locations,
        IndexingOptions options,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            // CPU 코어 × 2배 병렬도 (최대 성능)
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount * 2
            };

            // 병렬로 각 위치 처리
            await Parallel.ForEachAsync(locations, parallelOptions, async (location, ct) =>
            {
                if (ct.IsCancellationRequested || _disposed)
                    return;

                try
                {
                    // 로컬 버퍼로 배치 처리
                    var localBuffer = new List<FileItem>(1000);
                    
                    // 동기 방식으로 직접 열거 (비동기 오버헤드 제거)
                    foreach (var item in EnumerateLocationSync(location, options, ct))
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        
                        localBuffer.Add(item);
                        
                        // 1000개씩 배치로 채널에 쓰기
                        if (localBuffer.Count >= 1000)
                        {
                            await WriteBufferToChannelAsync(localBuffer, writer, ct);
                            localBuffer.Clear();
                        }
                    }
                    
                    // 남은 항목들 처리
                    if (localBuffer.Count > 0)
                    {
                        await WriteBufferToChannelAsync(localBuffer, writer, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 정상적인 취소
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error processing location: {Location}", location);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 정상적인 취소
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Producer error");
        }
    }

    // 🚀 동기 파일 아이템 생성 - 최고 성능, FastFileItem 사용
    private static FastFileItem? CreateFastFileItemSync(string filePath)
    {
        try
        {
            // 한 번의 호출로 파일/디렉토리 구분
            var attributes = File.GetAttributes(filePath);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;

            if (isDirectory)
            {
                var dirInfo = new DirectoryInfo(filePath);
                return new FastFileItem(
                    dirInfo.FullName,
                    dirInfo.Name,
                    dirInfo.Parent?.FullName ?? string.Empty,
                    string.Empty,
                    0,
                    dirInfo.CreationTime,
                    dirInfo.LastWriteTime,
                    dirInfo.LastAccessTime,
                    attributes,
                    dirInfo.FullName.Length > 0 ? dirInfo.FullName[0] : '\0'
                );
            }
            else
            {
                var fileInfo = new FileInfo(filePath);
                return new FastFileItem(
                    fileInfo.FullName,
                    fileInfo.Name,
                    fileInfo.DirectoryName ?? string.Empty,
                    fileInfo.Extension,
                    fileInfo.Length,
                    fileInfo.CreationTime,
                    fileInfo.LastWriteTime,
                    fileInfo.LastAccessTime,
                    attributes,
                    fileInfo.FullName.Length > 0 ? fileInfo.FullName[0] : '\0'
                );
            }
        }
        catch
        {
            return null;
        }
    }

    // 🚀 완전 동기 열거 - 최고 성능, TaskCanceledException 불가능
    private IEnumerable<FastFileItem> EnumerateFastLocationSync(string location, IndexingOptions options, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !Directory.Exists(location))
            yield break;

        var enumerationOptions = new System.IO.EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = !options.MaxDepth.HasValue || options.MaxDepth.Value > 1,
            ReturnSpecialDirectories = false,
            AttributesToSkip = GetAttributesToSkip(options),
            MaxRecursionDepth = options.MaxDepth ?? int.MaxValue
        };

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(location, "*", enumerationOptions);
        }
        catch
        {
            yield break;
        }

        var processedCount = 0;
        foreach (var filePath in entries)
        {
            // 간단한 취소 체크 (10000개마다로 최적화)
            if (++processedCount % 10000 == 0 && cancellationToken.IsCancellationRequested)
                yield break;

            var fileItem = CreateFastFileItemSync(filePath);
            if (fileItem.HasValue && ShouldIncludeFastFile(fileItem.Value, options))
            {
                yield return fileItem.Value;
            }
        }
    }

    // 🚀 FastFileItem용 필터링 (성능 최적화)
    private static bool ShouldIncludeFastFile(FastFileItem file, IndexingOptions options)
    {
        // Check hidden files - 비트 연산으로 최적화
        if (!options.IncludeHidden && file.IsHidden)
            return false;

        // Check system files - 비트 연산으로 최적화
        if (!options.IncludeSystem && file.IsSystem)
            return false;

        // Check file size
        if (options.MaxFileSize.HasValue && file.Size > options.MaxFileSize.Value)
            return false;

        // Check excluded paths - SIMD 최적화 문자열 검색 사용
        var fullPathSpan = file.FullPath.AsSpan();
        foreach (var excludedPath in options.ExcludedPaths)
        {
            if (SIMDStringMatcher.ContainsVectorized(fullPathSpan, excludedPath.AsSpan()))
                return false;
        }

        // Check excluded extensions - 빠른 비교
        if (!string.IsNullOrEmpty(file.Extension))
        {
            var extensionSpan = file.Extension.AsSpan();
            foreach (var excludedExt in options.ExcludedExtensions)
            {
                if (extensionSpan.Equals(excludedExt.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    // 🚀 동기 방식으로 직접 열거 (비동기 오버헤드 제거)
    private IEnumerable<FileItem> EnumerateLocationSync(string location, IndexingOptions options, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !Directory.Exists(location))
            yield break;

        var enumerationOptions = new System.IO.EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = !options.MaxDepth.HasValue || options.MaxDepth.Value > 1,
            ReturnSpecialDirectories = false,
            AttributesToSkip = GetAttributesToSkip(options),
            MaxRecursionDepth = options.MaxDepth ?? int.MaxValue
        };

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(location, "*", enumerationOptions);
        }
        catch
        {
            yield break;
        }

        var processedCount = 0;
        foreach (var filePath in entries)
        {
            // 간단한 취소 체크 (5000개마다)
            if (++processedCount % 5000 == 0 && cancellationToken.IsCancellationRequested)
                yield break;

            var fileItem = CreateFileItemSync(filePath);
            if (fileItem != null && ShouldIncludeFile(fileItem, options))
            {
                yield return fileItem;
            }
        }
    }

    // 🚀 동기 파일 아이템 생성 - 최고 성능
    private static FileItem? CreateFileItemSync(string filePath)
    {
        try
        {
            // 한 번의 호출로 파일/디렉토리 구분
            var attributes = File.GetAttributes(filePath);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;

            if (isDirectory)
            {
                var dirInfo = new DirectoryInfo(filePath);
                return new FileItem
                {
                    FullPath = dirInfo.FullName,
                    Name = dirInfo.Name,
                    DirectoryPath = dirInfo.Parent?.FullName ?? string.Empty,
                    Extension = string.Empty,
                    Size = 0,
                    CreatedTime = dirInfo.CreationTime,
                    ModifiedTime = dirInfo.LastWriteTime,
                    AccessedTime = dirInfo.LastAccessTime,
                    Attributes = attributes,
                    DriveLetter = dirInfo.FullName.Length > 0 ? dirInfo.FullName[0] : '\0'
                };
            }
            else
            {
                var fileInfo = new FileInfo(filePath);
                return new FileItem
                {
                    FullPath = fileInfo.FullName,
                    Name = fileInfo.Name,
                    DirectoryPath = fileInfo.DirectoryName ?? string.Empty,
                    Extension = fileInfo.Extension,
                    Size = fileInfo.Length,
                    CreatedTime = fileInfo.CreationTime,
                    ModifiedTime = fileInfo.LastWriteTime,
                    AccessedTime = fileInfo.LastAccessTime,
                    Attributes = attributes,
                    DriveLetter = fileInfo.FullName.Length > 0 ? fileInfo.FullName[0] : '\0'
                };
            }
        }
        catch
        {
            return null;
        }
    }

    // 🚀 고효율 채널 쓰기
    private static async Task WriteBufferToChannelAsync(List<FileItem> buffer, ChannelWriter<FileItem> writer, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var item in buffer)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                await writer.WriteAsync(item, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상적인 취소
        }
        catch (Exception)
        {
            // 채널이 닫힌 경우 등 - 무시하고 계속
        }
    }

    /// <inheritdoc/>
    public async Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (cancellationToken.IsCancellationRequested)
            return null;

        try
        {
            return await Task.Run(() =>
            {
                // Check cancellation before doing I/O operations
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(filePath) && !Directory.Exists(filePath))
                    return null;

                var info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    var dirInfo = new DirectoryInfo(filePath);
                    if (!dirInfo.Exists)
                        return null;

                    return CreateFileItemFromDirectoryInfo(dirInfo);
                }

                return CreateFileItemFromFileInfo(info);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("GetFileInfo cancelled for: {FilePath}", filePath);
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogDebug(ex, "Error getting file info for: {FilePath}", filePath);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Interfaces.DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await Task.Run(() =>
        {
            var drives = new List<Interfaces.DriveInfo>();

            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                        continue;

                    drives.Add(new Interfaces.DriveInfo
                    {
                        Name = drive.Name,
                        Label = drive.VolumeLabel,
                        FileSystem = drive.DriveFormat,
                        TotalSize = drive.TotalSize,
                        AvailableSpace = drive.AvailableFreeSpace,
                        IsReady = drive.IsReady,
                        DriveType = ConvertDriveType(drive.DriveType)
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error getting info for drive: {DriveName}", drive.Name);
                }
            }

            return drives.AsEnumerable();
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var changeQueue = new ConcurrentQueue<FileChangeEventArgs>();
        var watchers = new List<FileSystemWatcher>();

        try
        {
            // Set up file system watchers
            foreach (var location in locations)
            {
                if (!Directory.Exists(location))
                    continue;

                var watcher = new FileSystemWatcher(location)
                {
                    IncludeSubdirectories = options.IncludeSubdirectories,
                    InternalBufferSize = options.BufferSize,
                    NotifyFilter = GetNotifyFilters(options)
                };

                if (options.MonitorCreation)
                    watcher.Created += (s, e) => EnqueueChange(changeQueue, FileChangeType.Created, e.FullPath);

                if (options.MonitorModification)
                    watcher.Changed += (s, e) => EnqueueChange(changeQueue, FileChangeType.Modified, e.FullPath);

                if (options.MonitorDeletion)
                    watcher.Deleted += (s, e) => EnqueueChange(changeQueue, FileChangeType.Deleted, e.FullPath);

                if (options.MonitorRename)
                    watcher.Renamed += (s, e) => EnqueueChange(changeQueue, FileChangeType.Renamed, e.FullPath);

                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
                _watchers[location] = watcher;

                _logger.LogDebug("Started monitoring: {Location}", location);
            }

            // Process changes
            var lastChangeTime = DateTime.MinValue;
            var debounceInterval = options.DebounceInterval;

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);

                var currentTime = DateTime.Now;
                if (currentTime - lastChangeTime < debounceInterval)
                    continue;

                while (changeQueue.TryDequeue(out var change))
                {
                    if (ShouldIncludeChange(change.NewPath, options))
                    {
                        yield return change;
                        lastChangeTime = currentTime;
                    }
                }
            }
        }
        finally
        {
            // Clean up watchers
            foreach (var watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing file system watcher");
                }
            }

            foreach (var location in locations)
            {
                _watchers.TryRemove(location, out _);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await Task.Run(() => File.Exists(path) || Directory.Exists(path), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await Task.Run(() =>
        {
            try
            {
                var rootPath = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(rootPath))
                    return "Unknown";

                var drive = new System.IO.DriveInfo(rootPath);
                return drive.DriveFormat;
            }
            catch
            {
                return "Unknown";
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public ProviderPerformance GetPerformanceInfo()
    {
        return new ProviderPerformance
        {
            EstimatedFilesPerSecond = CanAccessMasterFileTable() ? 100000 : 50000, // 성능 향상 반영
            SupportsFastEnumeration = true,
            SupportsNativeMonitoring = true,
            MemoryOverheadPerFile = 150, // 최적화로 메모리 사용량 감소
            Priority = CanAccessMasterFileTable() ? 100 : 90
        };
    }

    /// <summary>
    /// Checks if the current process can access the Master File Table
    /// </summary>
    /// <returns>True if MFT access is available</returns>
    public static bool CanAccessMasterFileTable()
    {
        try
        {
            // Try to open a volume handle (requires elevated privileges)
            var volumeHandle = CreateFile(
                @"\\.\C:",
                0x80000000, // GENERIC_READ
                0x01 | 0x02, // FILE_SHARE_READ | FILE_SHARE_WRITE
                IntPtr.Zero,
                3, // OPEN_EXISTING
                0,
                IntPtr.Zero);

            if (volumeHandle != INVALID_HANDLE_VALUE)
            {
                CloseHandle(volumeHandle);
                return true;
            }
        }
        catch
        {
            // Ignore exceptions
        }

        return false;
    }

    private static FileItem CreateFileItemFromFileInfo(FileInfo info)
    {
        return new FileItem
        {
            FullPath = info.FullName,
            Name = info.Name,
            DirectoryPath = info.DirectoryName ?? string.Empty,
            Extension = info.Extension,
            Size = info.Length,
            CreatedTime = info.CreationTime,
            ModifiedTime = info.LastWriteTime,
            AccessedTime = info.LastAccessTime,
            Attributes = info.Attributes,
            DriveLetter = info.FullName.Length > 0 ? info.FullName[0] : '\0'
        };
    }

    private static FileItem CreateFileItemFromDirectoryInfo(DirectoryInfo info)
    {
        return new FileItem
        {
            FullPath = info.FullName,
            Name = info.Name,
            DirectoryPath = info.Parent?.FullName ?? string.Empty,
            Extension = string.Empty,
            Size = 0,
            CreatedTime = info.CreationTime,
            ModifiedTime = info.LastWriteTime,
            AccessedTime = info.LastAccessTime,
            Attributes = info.Attributes,
            DriveLetter = info.FullName.Length > 0 ? info.FullName[0] : '\0'
        };
    }

    private static FastFind.Interfaces.DriveType ConvertDriveType(System.IO.DriveType driveType)
    {
        return driveType switch
        {
            System.IO.DriveType.Fixed => FastFind.Interfaces.DriveType.Fixed,
            System.IO.DriveType.Removable => FastFind.Interfaces.DriveType.Removable,
            System.IO.DriveType.Network => FastFind.Interfaces.DriveType.Network,
            System.IO.DriveType.Ram => FastFind.Interfaces.DriveType.Ram,
            _ => FastFind.Interfaces.DriveType.Unknown
        };
    }

    private static bool ShouldIncludeFile(FileItem file, IndexingOptions options)
    {
        // Check hidden files
        if (!options.IncludeHidden && file.IsHidden)
            return false;

        // Check system files
        if (!options.IncludeSystem && file.IsSystem)
            return false;

        // Check file size
        if (options.MaxFileSize.HasValue && file.Size > options.MaxFileSize.Value)
            return false;

        // Check excluded paths
        foreach (var excludedPath in options.ExcludedPaths)
        {
            if (file.FullPath.Contains(excludedPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Check excluded extensions
        if (!string.IsNullOrEmpty(file.Extension) && 
            options.ExcludedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static FileAttributes GetAttributesToSkip(IndexingOptions options)
    {
        var attributesToSkip = FileAttributes.Normal;

        if (!options.IncludeHidden)
            attributesToSkip |= FileAttributes.Hidden;

        if (!options.IncludeSystem)
            attributesToSkip |= FileAttributes.System;

        return attributesToSkip;
    }

    private static NotifyFilters GetNotifyFilters(MonitoringOptions options)
    {
        var filters = NotifyFilters.Attributes;

        if (options.MonitorCreation || options.MonitorDeletion)
            filters |= NotifyFilters.FileName | NotifyFilters.DirectoryName;

        if (options.MonitorModification)
            filters |= NotifyFilters.LastWrite | NotifyFilters.Size;

        if (options.MonitorRename)
            filters |= NotifyFilters.FileName | NotifyFilters.DirectoryName;

        return filters;
    }

    private static void EnqueueChange(ConcurrentQueue<FileChangeEventArgs> queue, FileChangeType changeType, string path)
    {
        queue.Enqueue(new FileChangeEventArgs(changeType, path));
    }

    private static void EnqueueRename(ConcurrentQueue<FileChangeEventArgs> queue, string oldPath, string newPath)
    {
        queue.Enqueue(new FileChangeEventArgs(FileChangeType.Renamed, newPath, null, oldPath));
    }

    private static bool ShouldIncludeChange(string path, MonitoringOptions options)
    {
        foreach (var excludedPath in options.ExcludedPaths)
        {
            if (path.Contains(excludedPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsFileSystemProvider));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            
            _logger.LogDebug("Disposing WindowsFileSystemProvider");

            // Stop all file system watchers first
            var watcherDisposeErrors = new List<Exception>();
            foreach (var watcher in _watchers.Values)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    watcherDisposeErrors.Add(ex);
                    _logger.LogDebug(ex, "Error disposing file system watcher");
                }
            }

            _watchers.Clear();

            // Dispose the semaphore
            try
            {
                _enumerationSemaphore.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing enumeration semaphore");
            }

            if (watcherDisposeErrors.Count != 0)
            {
                _logger.LogWarning("Encountered {ErrorCount} errors while disposing file system watchers", watcherDisposeErrors.Count);
            }
            
            _logger.LogDebug("WindowsFileSystemProvider disposal completed");
        }
    }

    #region P/Invoke declarations for NTFS access

    private const int INVALID_HANDLE_VALUE = -1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}