using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Windows.Mft;

/// <summary>
/// High-performance MFT reader using direct NTFS access.
/// Provides Everything-level performance for file enumeration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftReader : IDisposable
{
    private readonly ILogger<MftReader>? _logger;
    private readonly MftReaderOptions _options;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly ConcurrentDictionary<char, SafeFileHandle> _volumeHandles;
    private readonly ConcurrentDictionary<ulong, string> _directoryPathCache;
    private bool _disposed;

    // Root directory file reference number
    private const ulong ROOT_DIRECTORY_FRN = 0x0005000000000005;

    /// <summary>
    /// Creates a new MFT reader with default options.
    /// </summary>
    public MftReader(ILogger<MftReader>? logger = null)
        : this(MftReaderOptions.Default, logger)
    {
    }

    /// <summary>
    /// Creates a new MFT reader with specified options.
    /// </summary>
    public MftReader(MftReaderOptions options, ILogger<MftReader>? logger = null)
    {
        _options = options.Validate();
        _logger = logger;
        _bufferPool = ArrayPool<byte>.Shared;
        _volumeHandles = new ConcurrentDictionary<char, SafeFileHandle>();
        _directoryPathCache = new ConcurrentDictionary<ulong, string>();
    }

    /// <summary>
    /// Gets the current options.
    /// </summary>
    public MftReaderOptions Options => _options;

    /// <summary>
    /// Checks if MFT access is available (requires admin rights and NTFS)
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets NTFS volume information for a drive
    /// </summary>
    public NtfsVolumeInfo? GetVolumeInfo(char driveLetter)
    {
        ThrowIfDisposed();

        try
        {
            using var handle = OpenVolume(driveLetter);
            if (handle == null || handle.IsInvalid)
                return null;

            var volumeData = new NTFS_VOLUME_DATA_BUFFER();
            var volumeDataSize = (uint)Marshal.SizeOf<NTFS_VOLUME_DATA_BUFFER>();
            var volumeDataPtr = Marshal.AllocHGlobal((int)volumeDataSize);

            try
            {
                if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FSCTL_GET_NTFS_VOLUME_DATA,
                    IntPtr.Zero,
                    0,
                    volumeDataPtr,
                    volumeDataSize,
                    out _,
                    IntPtr.Zero))
                {
                    _logger?.LogWarning("Failed to get NTFS volume data for drive {Drive}", driveLetter);
                    return null;
                }

                volumeData = Marshal.PtrToStructure<NTFS_VOLUME_DATA_BUFFER>(volumeDataPtr);

                return new NtfsVolumeInfo(
                    driveLetter,
                    volumeData.VolumeSerialNumber,
                    volumeData.TotalClusters,
                    volumeData.FreeClusters,
                    volumeData.BytesPerSector,
                    volumeData.BytesPerCluster,
                    volumeData.BytesPerFileRecordSegment,
                    volumeData.MftStartLcn,
                    volumeData.MftValidDataLength);
            }
            finally
            {
                Marshal.FreeHGlobal(volumeDataPtr);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting volume info for drive {Drive}", driveLetter);
            return null;
        }
    }

    /// <summary>
    /// Enumerates all files on a drive using MFT direct access.
    /// This is the high-performance path that achieves Everything-level speed.
    /// </summary>
    public async IAsyncEnumerable<MftFileRecord> EnumerateFilesAsync(
        char driveLetter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var handle = GetOrCreateVolumeHandle(driveLetter);
        if (handle == null || handle.IsInvalid)
        {
            _logger?.LogWarning("Cannot open volume {Drive} for MFT access", driveLetter);
            yield break;
        }

        // Initialize directory path cache with root
        var rootPath = $"{driveLetter}:\\";
        _directoryPathCache[ROOT_DIRECTORY_FRN & 0x0000FFFFFFFFFFFF] = rootPath;

        var buffer = _bufferPool.Rent(_options.BufferSize);
        var directoryRecords = new ConcurrentDictionary<ulong, MftFileRecord>();

        try
        {
            var enumData = new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue
            };

            var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var bufferPtr = bufferHandle.AddrOfPinnedObject();

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.FSCTL_ENUM_USN_DATA,
                        ref enumData,
                        (uint)Marshal.SizeOf<MFT_ENUM_DATA_V0>(),
                        bufferPtr,
                        (uint)buffer.Length,
                        out var bytesReturned,
                        IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 38) // ERROR_HANDLE_EOF
                            break;

                        _logger?.LogWarning("DeviceIoControl failed with error {Error}", error);
                        break;
                    }

                    if (bytesReturned <= 8)
                        break;

                    // First 8 bytes contain the next file reference number
                    enumData.StartFileReferenceNumber = BitConverter.ToUInt64(buffer, 0);

                    // Process USN records in the buffer
                    var offset = 8;
                    while (offset < bytesReturned)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var record = ParseUsnRecord(buffer, offset, out var recordLength);
                        if (recordLength == 0)
                            break;

                        offset += (int)recordLength;

                        if (record.HasValue)
                        {
                            var mftRecord = record.Value;

                            // Cache directory records for path building
                            if (mftRecord.IsDirectory)
                            {
                                directoryRecords[mftRecord.GetRecordNumber()] = mftRecord;
                            }

                            yield return mftRecord;
                        }
                    }

                    // Yield periodically for better async behavior
                    await Task.Yield();
                }
            }
            finally
            {
                bufferHandle.Free();
            }
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    /// <summary>
    /// Enumerates files from multiple drives in parallel for maximum throughput
    /// </summary>
    public async IAsyncEnumerable<MftFileRecord> EnumerateAllDrivesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var ntfsDrives = GetNtfsDrives();
        if (ntfsDrives.Length == 0)
        {
            _logger?.LogWarning("No NTFS drives found");
            yield break;
        }

        // Create channels for each drive
        var channel = System.Threading.Channels.Channel.CreateUnbounded<MftFileRecord>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        // Start enumeration tasks for each drive
        var tasks = ntfsDrives.Select(async drive =>
        {
            try
            {
                await foreach (var record in EnumerateFilesAsync(drive, cancellationToken))
                {
                    await channel.Writer.WriteAsync(record, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error enumerating drive {Drive}", drive);
            }
        }).ToArray();

        // Complete the channel when all tasks are done
        _ = Task.WhenAll(tasks).ContinueWith(_ => channel.Writer.Complete(), cancellationToken);

        // Yield records from all drives
        await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return record;
        }
    }

    /// <summary>
    /// Gets the full path for a file reference number
    /// </summary>
    public string? GetFullPath(char driveLetter, ulong fileReferenceNumber, ulong parentFileReferenceNumber, string fileName)
    {
        var parentRecordNumber = MftFileRecord.ExtractRecordNumber(parentFileReferenceNumber);

        if (_directoryPathCache.TryGetValue(parentRecordNumber, out var parentPath))
        {
            return Path.Combine(parentPath, fileName);
        }

        // Fallback: just return drive:\filename
        return $"{driveLetter}:\\{fileName}";
    }

    /// <summary>
    /// Builds full paths for all records using parent-child relationships
    /// </summary>
    public void BuildPathCache(char driveLetter, IEnumerable<MftFileRecord> directoryRecords)
    {
        var rootPath = $"{driveLetter}:\\";
        var directories = directoryRecords
            .Where(r => r.IsDirectory)
            .ToDictionary(r => r.GetRecordNumber());

        // BFS to build paths from root
        var queue = new Queue<(ulong RecordNumber, string Path)>();
        queue.Enqueue((5, rootPath)); // Root directory is always record 5

        _directoryPathCache[5] = rootPath;

        while (queue.Count > 0)
        {
            var (parentRecord, parentPath) = queue.Dequeue();

            foreach (var dir in directories.Values.Where(d =>
                MftFileRecord.ExtractRecordNumber(d.ParentFileReferenceNumber) == parentRecord))
            {
                var dirPath = Path.Combine(parentPath, dir.FileName);
                var recordNumber = dir.GetRecordNumber();

                _directoryPathCache[recordNumber] = dirPath;
                queue.Enqueue((recordNumber, dirPath));
            }
        }
    }

    /// <summary>
    /// Gets all NTFS drives on the system
    /// </summary>
    public static char[] GetNtfsDrives()
    {
        var drives = new List<char>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady &&
                    drive.DriveType == DriveType.Fixed &&
                    string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    drives.Add(drive.Name[0]);
                }
            }
            catch
            {
                // Skip drives that can't be accessed
            }
        }

        return drives.ToArray();
    }

    /// <summary>
    /// Performs a benchmark of MFT enumeration speed
    /// </summary>
    public async Task<MftEnumerationResult> BenchmarkAsync(char driveLetter, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stopwatch = Stopwatch.StartNew();
        long totalRecords = 0;
        long fileCount = 0;
        long directoryCount = 0;

        try
        {
            await foreach (var record in EnumerateFilesAsync(driveLetter, cancellationToken))
            {
                totalRecords++;
                if (record.IsDirectory)
                    directoryCount++;
                else
                    fileCount++;
            }

            stopwatch.Stop();

            var result = MftEnumerationResult.Success(
                driveLetter,
                totalRecords,
                fileCount,
                directoryCount,
                stopwatch.Elapsed);

            _logger?.LogInformation(
                "MFT enumeration completed: {Records} records ({Files} files, {Dirs} directories) in {Time:F2}s ({Rate:N0} records/sec)",
                totalRecords, fileCount, directoryCount,
                stopwatch.Elapsed.TotalSeconds,
                result.RecordsPerSecond);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "MFT enumeration failed for drive {Drive}", driveLetter);
            return MftEnumerationResult.Failure(driveLetter, ex.Message);
        }
    }

    #region Private Methods

    private SafeFileHandle? OpenVolume(char driveLetter)
    {
        var volumePath = $"\\\\.\\{driveLetter}:";

        var handle = NativeMethods.CreateFileW(
            volumePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _logger?.LogWarning("Failed to open volume {Volume}, error: {Error}", volumePath, error);
            return null;
        }

        return handle;
    }

    private SafeFileHandle? GetOrCreateVolumeHandle(char driveLetter)
    {
        return _volumeHandles.GetOrAdd(driveLetter, d =>
        {
            var handle = OpenVolume(d);
            return handle ?? new SafeFileHandle(IntPtr.Zero, false);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MftFileRecord? ParseUsnRecord(byte[] buffer, int offset, out uint recordLength)
    {
        if (offset + 4 > buffer.Length)
        {
            recordLength = 0;
            return null;
        }

        recordLength = BitConverter.ToUInt32(buffer, offset);
        if (recordLength == 0 || offset + recordLength > buffer.Length)
        {
            recordLength = 0;
            return null;
        }

        // Parse USN_RECORD_V2 structure
        var majorVersion = BitConverter.ToUInt16(buffer, offset + 4);
        if (majorVersion != 2 && majorVersion != 3)
        {
            // Unsupported version, skip
            return null;
        }

        var fileReferenceNumber = BitConverter.ToUInt64(buffer, offset + 8);
        var parentFileReferenceNumber = BitConverter.ToUInt64(buffer, offset + 16);
        var timeStamp = BitConverter.ToInt64(buffer, offset + 32);
        var fileAttributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 52);
        var fileNameLength = BitConverter.ToUInt16(buffer, offset + 56);
        var fileNameOffset = BitConverter.ToUInt16(buffer, offset + 58);

        if (fileNameLength == 0 || offset + fileNameOffset + fileNameLength > buffer.Length)
        {
            return null;
        }

        var fileName = Encoding.Unicode.GetString(buffer, offset + fileNameOffset, fileNameLength);

        // Skip system files and metadata
        if (fileName.StartsWith("$") || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        // Convert FILETIME to DateTime
        var dateTime = DateTime.FromFileTimeUtc(timeStamp);

        return new MftFileRecord(
            fileReferenceNumber,
            parentFileReferenceNumber,
            fileAttributes,
            0, // Size not available in USN record
            fileName,
            dateTime,
            dateTime,
            dateTime);
    }

    /// <summary>
    /// Parse all USN records in a buffer batch using optimized Span-based parsing.
    /// Returns a list of records that can be safely yielded across async boundaries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<MftFileRecord> ParseBufferBatch(
        byte[] buffer,
        int bytesReturned,
        ConcurrentDictionary<ulong, MftFileRecord> directoryRecords)
    {
        var records = new List<MftFileRecord>(256); // Pre-allocate for typical batch size
        var bufferSpan = buffer.AsSpan(0, bytesReturned);
        var offset = 8; // Skip first 8 bytes (next file reference number)

        while (offset < bytesReturned)
        {
            if (MftParserV2.TryParseUsnRecord(bufferSpan, ref offset, out var mftRecord))
            {
                // Cache directory records for path building
                if (mftRecord.IsDirectory)
                {
                    directoryRecords[mftRecord.GetRecordNumber()] = mftRecord;
                }

                records.Add(mftRecord);
            }
            else
            {
                // If parsing failed and offset didn't advance, break to avoid infinite loop
                var recordLength = MftParserV2.GetRecordLength(bufferSpan, offset);
                if (recordLength == 0)
                    break;
            }
        }

        return records;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MftReader));
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var handle in _volumeHandles.Values)
        {
            try
            {
                if (!handle.IsInvalid && !handle.IsClosed)
                    handle.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _volumeHandles.Clear();
        _directoryPathCache.Clear();
    }

    #endregion
}
