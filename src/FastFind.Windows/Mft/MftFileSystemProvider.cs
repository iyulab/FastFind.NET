using FastFind.Interfaces;
using FastFind.Indexing;
using FastFind.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace FastFind.Windows.Mft;

/// <summary>
/// NTFS file system provider that enumerates a volume through its change journal
/// (<c>FSCTL_ENUM_USN_DATA</c>). Requires administrator privileges on Windows.
/// </summary>
/// <remarks>
/// It does not parse the Master File Table itself. Journal enumeration returns each entry's
/// name, parent and attributes, but no size and no timestamps — the record's timestamp field is
/// specified to be zero for this call. Size and times are therefore read per entry, and only when
/// <see cref="IndexingOptions.CollectFileMetadata"/> is set; see <see cref="ProvidesFileMetadata"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MftFileSystemProvider : IFileSystemProvider, IAsyncDisposable
{
    private readonly ILogger<MftFileSystemProvider>? _logger;
    private readonly MftReader _mftReader;
    private bool _disposed;

    private const int CHANNEL_CAPACITY = 100000;

    /// <summary>
    /// Gets whether MFT access is available (requires admin + NTFS)
    /// </summary>
    public static bool IsMftAccessAvailable => MftReader.IsAvailable();

    public MftFileSystemProvider(ILogger<MftFileSystemProvider>? logger = null)
    {
        _logger = logger;
        _mftReader = new MftReader(logger as ILogger<MftReader>);
    }

    /// <inheritdoc/>
    public PlatformType SupportedPlatform => PlatformType.Windows;

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsWindows() && MftReader.IsAvailable();

    private readonly System.Collections.Concurrent.ConcurrentBag<string> _enumerationFailures = new();

    /// <inheritdoc/>
    /// <remarks>
    /// A location that does not exist, one on a drive this provider cannot read (not mounted, not NTFS), or a
    /// drive whose enumeration raised an error — reported as the drive root, <c>C:\</c>.
    /// </remarks>
    public IReadOnlyCollection<string> EnumerationFailures => _enumerationFailures.Distinct().ToArray();

    /// <inheritdoc/>
    /// <remarks>
    /// Only with <see cref="IndexingOptions.CollectFileMetadata"/>: the journal enumeration this
    /// provider reads carries no sizes or times of its own.
    /// </remarks>
    public bool ProvidesFileMetadata(IndexingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.CollectFileMetadata;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations,
        IndexingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // The list is of this enumeration's failures, not an accumulation across passes.
        _enumerationFailures.Clear();
        var requested = locations.ToArray();

        if (!IsAvailable)
        {
            _logger?.LogWarning("MFT access not available, falling back to empty enumeration");
            foreach (var location in requested) _enumerationFailures.Add(location);
            yield break;
        }

        // Determine which drives to scan based on locations
        var driveLetters = GetDriveLettersFromLocations(requested);

        // A location that is not there, or sits on a drive this provider is not going to read, is a gap in
        // what was asked for: the volume scan below would simply never produce anything for it.
        foreach (var location in requested)
        {
            var root = Path.GetPathRoot(location);
            var drive = string.IsNullOrEmpty(root) ? '\0' : char.ToUpperInvariant(root[0]);
            if (!Directory.Exists(location) || !driveLetters.Contains(drive))
                _enumerationFailures.Add(location);
        }

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

        // The producers belong to this pass, not to the caller's token: a consumer that stops reading
        // (a file cap, a stop request, disposal) must be able to end them, or each parks on the full
        // channel holding its volume handle and its read buffers until the process exits.
        using var passCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var passToken = passCancellation.Token;

        // Start producer tasks for each drive in parallel
        var producerTasks = driveLetters.Select(async driveLetter =>
        {
            Interlocked.Increment(ref _activeProducers);
            try
            {
                await EnumerateDriveToChannelAsync(driveLetter, locations, options, channel.Writer, passToken);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error enumerating drive {Drive}", driveLetter);
                _enumerationFailures.Add($"{driveLetter}:\\");
            }
            finally
            {
                Interlocked.Decrement(ref _activeProducers);
            }
        }).ToArray();

        // Complete the channel when all producers are done. Not tied to a token: it must always run.
        var producersDone = Task.WhenAll(producerTasks).ContinueWith(_ =>
        {
            try { channel.Writer.Complete(); } catch { }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        try
        {
            // Yield results from channel
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            // The pass is over, however the consumer left it: end the producers and wait for them, so
            // that nothing of this pass is still reading the volume when the call returns.
            passCancellation.Cancel();
            try
            {
                await producersDone.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "A producer ended with an error while the pass was closing");
            }
        }
    }

    private int _activeProducers;

    /// <summary>
    /// The number of drive producers still running. Zero once every pass has been disposed or has
    /// run to the end — the property a stopped pass is held to.
    /// </summary>
    internal int ActiveProducers => Volatile.Read(ref _activeProducers);

    private async Task EnumerateDriveToChannelAsync(
        char driveLetter,
        IEnumerable<string> locations,
        IndexingOptions options,
        ChannelWriter<FileItem> writer,
        CancellationToken cancellationToken)
    {
        var driveRoot = $"{driveLetter}:\\";
        var locationFilters = NormalizeLocations(locations)
            .Where(l => l.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var unresolved = new StrongBox<int>();
        var records = _mftReader.EnumerateFilesAsync(driveLetter, cancellationToken);

        // What the walk does not read, this pass does not yield: everything beneath a hidden or system
        // directory that lies below the location (not only an entry that is itself hidden), and the location itself.
        var skipDirectories = IndexingAdmission.AttributesToSkip(options);
        var locationDepths = locationFilters.Select(l => DirectoryDepth(l, driveRoot)).ToArray();

        await foreach (var (record, fullPath, skippedDepth) in MftPathResolver.ResolveAsync(records, driveRoot, unresolved, skipDirectories, cancellationToken))
        {
            var locationDepth = DeepestLocationDepth(fullPath, locationFilters, locationDepths);
            if (locationDepth < 0 || IsLocationItself(fullPath, locationFilters))
                continue;

            if (skippedDepth > locationDepth)
                continue;

            var fileItem = ConvertToFileItem(record, fullPath, options.CollectFileMetadata);
            if (IndexingAdmission.Admits(fileItem, options))
            {
                await writer.WriteAsync(fileItem, cancellationToken);
            }
        }

        if (unresolved.Value > 0)
        {
            _logger?.LogDebug(
                "Drive {Drive}: {Count} entries skipped because an ancestor directory was not enumerated",
                driveLetter, unresolved.Value);
        }
    }

    /// <summary>The number of segments a path has beneath a volume root: <c>D:\</c> is 0, <c>D:\a\b</c> is 2.</summary>
    internal static int DirectoryDepth(string path, string driveRoot) =>
        path.Length <= driveRoot.Length
            ? 0
            : path[driveRoot.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// The depth of the deepest location that <paramref name="path"/> is at or beneath, or -1 when it is in none. With no
    /// location at all the whole volume is wanted: depth 0.
    /// </summary>
    internal static int DeepestLocationDepth(string path, string[] locationFilters, int[] locationDepths)
    {
        if (locationFilters.Length == 0)
            return 0;

        var deepest = -1;
        for (var i = 0; i < locationFilters.Length; i++)
        {
            if (locationDepths[i] > deepest
                && SearchQueryEvaluator.IsUnder(path, locationFilters[i], includeSubdirectories: true))
                deepest = locationDepths[i];
        }

        return deepest;
    }

    /// <summary>
    /// Whether a path is one of the locations being indexed, not something beneath one. A pass lists what a location
    /// <em>contains</em>: the directory walk never yields the folder it starts in, and this pass is read as the same
    /// answer.
    /// </summary>
    internal static bool IsLocationItself(string path, string[] locationFilters)
    {
        var trimmed = path.TrimEnd('\\', '/');
        foreach (var location in locationFilters)
        {
            if (string.Equals(trimmed, location.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a path is one of the locations being indexed or lies beneath one.
    /// </summary>
    /// <remarks>
    /// A location must match at a separator: <c>C:\src\App</c> covers <c>C:\src\App\x.cs</c> but
    /// not <c>C:\src\App.Tests\x.cs</c>, which a bare prefix test admitted.
    /// </remarks>
    internal static bool IsPathInLocations(string path, string[] locationFilters)
    {
        if (locationFilters.Length == 0)
            return true;

        foreach (var location in locationFilters)
        {
            if (SearchQueryEvaluator.IsUnder(path, location, includeSubdirectories: true))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds an item from a journal record, reading the entry's size and timestamps from the file
    /// system when <paramref name="collectMetadata"/> is set.
    /// </summary>
    /// <remarks>
    /// Without it, the record's own values pass through. For a journal-enumerated record those are
    /// a size of 0 and <see cref="DateTime.MinValue"/> times — "not collected". An entry whose
    /// metadata cannot be read (gone since enumeration, or denied) keeps those values too rather
    /// than acquiring invented ones.
    /// </remarks>
    internal static FileItem ConvertToFileItem(MftFileRecord record, string fullPath, bool collectMetadata)
    {
        var directoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var extension = record.IsDirectory ? string.Empty : Path.GetExtension(record.FileName);

        var size = record.FileSize;
        var created = record.CreationTime;
        var modified = record.ModificationTime;
        var accessed = record.AccessTime;

        if (collectMetadata && TryReadMetadata(fullPath, record.IsDirectory, out var metadata))
        {
            (size, created, modified, accessed) = metadata;
        }

        return new FileItem
        {
            FullPath = fullPath,
            Name = record.FileName,
            DirectoryPath = directoryPath,
            Extension = extension,
            Size = size,
            CreatedTime = created,
            ModifiedTime = modified,
            AccessedTime = accessed,
            Attributes = record.Attributes,
            DriveLetter = fullPath.Length > 0 ? fullPath[0] : '\0'
        };
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
    /// <remarks>
    /// Each call reads the change journal with its own monitor, so monitoring can be stopped and
    /// started again. Every change is placed on disk by asking Windows where its parent directory
    /// is now (<see cref="FileIdDirectoryPaths"/>); a change whose parent no longer exists is not
    /// reported. A rename is reported once, with both paths; one that crosses the edge of the
    /// monitored locations — or of what <see cref="MonitoringOptions.ExcludedPaths"/> leaves out —
    /// is reported as the deletion or creation it is from inside that boundary.
    /// </remarks>
    public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var locationFilters = NormalizeLocations(locations);
        var driveLetters = GetDriveLettersFromLocations(locationFilters);

        await using var monitor = new UsnJournalMonitor(_logger as ILogger<UsnJournalMonitor>);
        using var directories = new FileIdDirectoryPaths();
        var translator = new UsnChangeTranslator(directories);

        await monitor.StartMonitoringAsync(driveLetters, null, cancellationToken);

        await foreach (var record in monitor.Changes.ReadAllAsync(cancellationToken))
        {
            if (record.IsDirectory && !options.MonitorDirectories)
                continue;

            var change = FileChangeScope.RestrictToIncluded(
                ScopeToLocations(translator.Translate(record), locationFilters),
                options.ExcludedPaths);

            if (change is not null && IsMonitored(change.ChangeType, options))
                yield return change;
        }
    }

    /// <summary>
    /// Restricts a change to the monitored locations. A rename out of them is, from inside, a
    /// deletion; a rename into them is a creation — the fold itself lives in
    /// <see cref="FileChangeScope"/>, which the exclusion list uses too.
    /// </summary>
    internal static FileChangeEventArgs? ScopeToLocations(FileChangeEventArgs? change, string[] locationFilters) =>
        FileChangeScope.Restrict(change, path => IsPathInLocations(path, locationFilters));

    private static bool IsMonitored(FileChangeType type, MonitoringOptions options) => type switch
    {
        FileChangeType.Created => options.MonitorCreation,
        FileChangeType.Deleted => options.MonitorDeletion,
        FileChangeType.Renamed => options.MonitorRename,
        _ => options.MonitorModification,
    };

    /// <summary>
    /// Locations as absolute paths with the platform separator. <c>C:/data</c> is a valid Windows
    /// path, and compared as written it matched nothing — which, for a drive, meant no filter.
    /// </summary>
    private static string[] NormalizeLocations(IEnumerable<string> locations) =>
        locations.Where(l => !string.IsNullOrWhiteSpace(l)).Select(Path.GetFullPath).ToArray();

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


    #region Metadata collection

    /// <summary>
    /// Reads an entry's size and UTC timestamps with a single file system query.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the entry no longer exists or cannot be queried. The caller must then not
    /// use <paramref name="metadata"/>: <see cref="FileSystemInfo"/> reports 1601-01-01 for the
    /// times of an entry it could not read, which is exactly the kind of value that must not reach
    /// the index.
    /// </returns>
    internal static bool TryReadMetadata(
        string path,
        bool isDirectory,
        out (long Size, DateTime Created, DateTime Modified, DateTime Accessed) metadata)
    {
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists)
            {
                metadata = (
                    info is FileInfo file ? file.Length : 0,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc,
                    info.LastAccessTimeUtc);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        metadata = default;
        return false;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _mftReader.Dispose();
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _mftReader.Dispose();
            _disposed = true;
        }
    }
}
