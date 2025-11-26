using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace FastFind.Windows.Mft;

/// <summary>
/// Ultra-high performance MFT-based file system provider.
/// Achieves Everything-level speed by reading directly from NTFS Master File Table.
/// Requires administrator privileges on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftFileSystemProvider : IFileSystemProvider, IAsyncDisposable
{
    private readonly ILogger<MftFileSystemProvider>? _logger;
    private readonly MftReader _mftReader;
    private readonly UsnJournalMonitor _usnMonitor;
    private readonly Dictionary<ulong, string> _directoryPathCache;
    private bool _disposed;

    // Performance constants
    private const int BATCH_SIZE = 10000;
    private const int CHANNEL_CAPACITY = 100000;

    /// <summary>
    /// Gets whether MFT access is available (requires admin + NTFS)
    /// </summary>
    public static bool IsMftAccessAvailable => MftReader.IsAvailable();

    public MftFileSystemProvider(ILogger<MftFileSystemProvider>? logger = null)
    {
        _logger = logger;
        _mftReader = new MftReader(logger as ILogger<MftReader>);
        _usnMonitor = new UsnJournalMonitor(logger as ILogger<UsnJournalMonitor>);
        _directoryPathCache = new Dictionary<ulong, string>();
    }

    /// <inheritdoc/>
    public PlatformType SupportedPlatform => PlatformType.Windows;

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsWindows() && MftReader.IsAvailable();

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations,
        IndexingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!IsAvailable)
        {
            _logger?.LogWarning("MFT access not available, falling back to empty enumeration");
            yield break;
        }

        // Determine which drives to scan based on locations
        var driveLetters = GetDriveLettersFromLocations(locations);
        if (driveLetters.Length == 0)
        {
            _logger?.LogWarning("No valid NTFS drives found in specified locations");
            yield break;
        }

        _logger?.LogInformation("Starting MFT enumeration for drives: {Drives}", string.Join(", ", driveLetters));

        // High-performance channel for streaming results
        var channel = Channel.CreateBounded<FileItem>(new BoundedChannelOptions(CHANNEL_CAPACITY)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        // Start producer tasks for each drive in parallel
        var producerTasks = driveLetters.Select(async driveLetter =>
        {
            try
            {
                await EnumerateDriveToChannelAsync(driveLetter, locations, options, channel.Writer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error enumerating drive {Drive}", driveLetter);
            }
        }).ToArray();

        // Complete the channel when all producers are done
        _ = Task.WhenAll(producerTasks).ContinueWith(_ =>
        {
            try { channel.Writer.Complete(); } catch { }
        }, cancellationToken);

        // Yield results from channel
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    private async Task EnumerateDriveToChannelAsync(
        char driveLetter,
        IEnumerable<string> locations,
        IndexingOptions options,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        var driveRoot = $"{driveLetter}:\\";
        var locationFilters = locations
            .Where(l => l.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // First pass: collect directory records for path building
        var directoryRecords = new Dictionary<ulong, MftFileRecord>();
        var fileRecords = new List<MftFileRecord>(100000);

        // Initialize root directory
        _directoryPathCache[5] = driveRoot; // Root directory is always record 5

        await foreach (var record in _mftReader.EnumerateFilesAsync(driveLetter, cancellationToken))
        {
            if (record.IsDirectory)
            {
                directoryRecords[record.GetRecordNumber()] = record;
            }
            else
            {
                fileRecords.Add(record);
            }

            // Process in batches for memory efficiency
            if (fileRecords.Count >= BATCH_SIZE)
            {
                await ProcessAndWriteRecordsAsync(
                    driveLetter, fileRecords, directoryRecords,
                    locationFilters, options, writer, cancellationToken);
                fileRecords.Clear();
            }
        }

        // Process remaining records
        if (fileRecords.Count > 0)
        {
            await ProcessAndWriteRecordsAsync(
                driveLetter, fileRecords, directoryRecords,
                locationFilters, options, writer, cancellationToken);
        }

        // Also yield directories (always included for complete enumeration)
        await ProcessAndWriteDirectoriesAsync(
            driveLetter, directoryRecords.Values,
            locationFilters, options, writer, cancellationToken);
    }

    private async Task ProcessAndWriteRecordsAsync(
        char driveLetter,
        IList<MftFileRecord> records,
        Dictionary<ulong, MftFileRecord> directories,
        string[] locationFilters,
        IndexingOptions options,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var fullPath = BuildFullPath(driveLetter, record, directories);
            if (fullPath == null)
                continue;

            // Check if path matches any of the location filters
            if (!IsPathInLocations(fullPath, locationFilters))
                continue;

            var fileItem = ConvertToFileItem(record, fullPath);
            if (ShouldIncludeFile(fileItem, options))
            {
                await writer.WriteAsync(fileItem, cancellationToken);
            }
        }
    }

    private async Task ProcessAndWriteDirectoriesAsync(
        char driveLetter,
        IEnumerable<MftFileRecord> directories,
        string[] locationFilters,
        IndexingOptions options,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        var allDirs = directories.ToDictionary(d => d.GetRecordNumber());

        foreach (var record in directories)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var fullPath = BuildFullPath(driveLetter, record, allDirs);
            if (fullPath == null)
                continue;

            // Check if path matches any of the location filters
            if (!IsPathInLocations(fullPath, locationFilters))
                continue;

            var fileItem = ConvertToFileItem(record, fullPath);
            if (ShouldIncludeFile(fileItem, options))
            {
                await writer.WriteAsync(fileItem, cancellationToken);
            }
        }
    }

    private string? BuildFullPath(char driveLetter, MftFileRecord record, Dictionary<ulong, MftFileRecord> directories)
    {
        var recordNumber = record.GetRecordNumber();

        // Check cache first
        if (_directoryPathCache.TryGetValue(recordNumber, out var cachedPath))
            return cachedPath;

        // Build path from parent chain
        var pathParts = new Stack<string>();
        pathParts.Push(record.FileName);

        var currentParent = MftFileRecord.ExtractRecordNumber(record.ParentFileReferenceNumber);
        var visited = new HashSet<ulong> { recordNumber };

        while (currentParent != 0 && !_directoryPathCache.ContainsKey(currentParent))
        {
            if (!directories.TryGetValue(currentParent, out var parentRecord))
                break;

            if (visited.Contains(currentParent))
                break; // Circular reference protection

            visited.Add(currentParent);
            pathParts.Push(parentRecord.FileName);
            currentParent = MftFileRecord.ExtractRecordNumber(parentRecord.ParentFileReferenceNumber);
        }

        // Build the path
        string basePath;
        if (_directoryPathCache.TryGetValue(currentParent, out var parentPath))
        {
            basePath = parentPath;
        }
        else
        {
            basePath = $"{driveLetter}:\\";
        }

        var fullPath = basePath;
        while (pathParts.Count > 0)
        {
            fullPath = Path.Combine(fullPath, pathParts.Pop());
        }

        // Cache if directory
        if (record.IsDirectory)
        {
            _directoryPathCache[recordNumber] = fullPath;
        }

        return fullPath;
    }

    private static bool IsPathInLocations(string path, string[] locationFilters)
    {
        if (locationFilters.Length == 0)
            return true;

        foreach (var location in locationFilters)
        {
            if (path.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static FileItem ConvertToFileItem(MftFileRecord record, string fullPath)
    {
        var directoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var extension = record.IsDirectory ? string.Empty : Path.GetExtension(record.FileName);

        return new FileItem
        {
            FullPath = fullPath,
            Name = record.FileName,
            DirectoryPath = directoryPath,
            Extension = extension,
            Size = record.FileSize,
            CreatedTime = record.CreationTime,
            ModifiedTime = record.ModificationTime,
            AccessedTime = record.AccessTime,
            Attributes = record.Attributes,
            DriveLetter = fullPath.Length > 0 ? fullPath[0] : '\0'
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

    /// <inheritdoc/>
    public async Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // For single file lookup, use standard .NET APIs (MFT lookup is overkill)
        return await Task.Run(() =>
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
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

                if (Directory.Exists(filePath))
                {
                    var info = new DirectoryInfo(filePath);
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

                return null;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<FastFind.Interfaces.DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await Task.Run(() =>
        {
            var drives = new List<FastFind.Interfaces.DriveInfo>();

            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                        continue;

                    var volumeInfo = drive.DriveFormat == "NTFS"
                        ? _mftReader.GetVolumeInfo(drive.Name[0])
                        : null;

                    drives.Add(new FastFind.Interfaces.DriveInfo
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
                    _logger?.LogDebug(ex, "Error getting info for drive: {DriveName}", drive.Name);
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

        var driveLetters = GetDriveLettersFromLocations(locations);

        // Start USN Journal monitoring
        await _usnMonitor.StartMonitoringAsync(driveLetters, null, cancellationToken);

        try
        {
            await foreach (var change in _usnMonitor.Changes.ReadAllAsync(cancellationToken))
            {
                // Filter based on monitoring options
                if (!ShouldIncludeChange(change, options))
                    continue;

                // Build full path for the change
                var fullPath = _mftReader.GetFullPath(
                    change.FileName[0], // Drive letter approximation
                    MftFileRecord.ExtractRecordNumber(change.FileReferenceNumber),
                    change.ParentFileReferenceNumber,
                    change.FileName);

                if (fullPath == null)
                    continue;

                // Check location filters
                var locationFilters = locations.ToArray();
                if (!IsPathInLocations(fullPath, locationFilters))
                    continue;

                yield return ConvertToChangeEventArgs(change, fullPath);
            }
        }
        finally
        {
            await _usnMonitor.StopMonitoringAsync();
        }
    }

    private static FileChangeEventArgs ConvertToChangeEventArgs(UsnChangeRecord record, string fullPath)
    {
        var changeType = DetermineChangeType(record.Reason);
        return new FileChangeEventArgs(changeType, fullPath);
    }

    private static FileChangeType DetermineChangeType(UsnReason reason)
    {
        if ((reason & UsnReason.FileCreate) != 0)
            return FileChangeType.Created;

        if ((reason & UsnReason.FileDelete) != 0)
            return FileChangeType.Deleted;

        if ((reason & (UsnReason.RenameNewName | UsnReason.RenameOldName)) != 0)
            return FileChangeType.Renamed;

        if ((reason & (UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.DataTruncation)) != 0)
            return FileChangeType.Modified;

        return FileChangeType.Modified;
    }

    private static bool ShouldIncludeChange(UsnChangeRecord change, MonitoringOptions options)
    {
        if (change.IsCreated && !options.MonitorCreation)
            return false;

        if (change.IsDeleted && !options.MonitorDeletion)
            return false;

        if ((change.IsRenamedFrom || change.IsRenamedTo) && !options.MonitorRename)
            return false;

        if (change.IsDataModified && !options.MonitorModification)
            return false;

        if (change.IsDirectory && !options.MonitorDirectories)
            return false;

        return true;
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
            EstimatedFilesPerSecond = 500000, // 500K+ files/sec with MFT
            SupportsFastEnumeration = true,
            SupportsNativeMonitoring = true,
            MemoryOverheadPerFile = 100, // Minimal overhead with MFT
            Priority = 100 // Highest priority for MFT provider
        };
    }

    private static char[] GetDriveLettersFromLocations(IEnumerable<string> locations)
    {
        var driveLetters = new HashSet<char>();
        var ntfsDrives = new HashSet<char>(MftReader.GetNtfsDrives());

        foreach (var location in locations)
        {
            if (location.Length >= 2 && location[1] == ':')
            {
                var driveLetter = char.ToUpperInvariant(location[0]);
                if (ntfsDrives.Contains(driveLetter))
                {
                    driveLetters.Add(driveLetter);
                }
            }
        }

        // If no specific locations provided, use all NTFS drives
        if (driveLetters.Count == 0)
        {
            return ntfsDrives.ToArray();
        }

        return driveLetters.ToArray();
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MftFileSystemProvider));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mftReader.Dispose();
            _usnMonitor.Dispose();
            _directoryPathCache.Clear();
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _mftReader.Dispose();
            await _usnMonitor.DisposeAsync();
            _directoryPathCache.Clear();
            _disposed = true;
        }
    }
}
