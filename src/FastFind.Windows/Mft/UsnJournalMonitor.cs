using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Windows.Mft;

/// <summary>
/// Real-time file system change monitor using USN Journal.
/// Provides Everything-level real-time updates for file changes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsnJournalMonitor : IAsyncDisposable, IDisposable
{
    private readonly ILogger<UsnJournalMonitor>? _logger;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly Dictionary<char, SafeFileHandle> _volumeHandles;
    private readonly Dictionary<char, USN_JOURNAL_DATA_V0> _journalData;
    private readonly Channel<UsnChangeRecord> _changeChannel;
    private readonly CancellationTokenSource _monitoringCts;
    private readonly List<Task> _monitoringTasks;
    private bool _disposed;
    private bool _isMonitoring;

    // Buffer size for USN Journal reading (64KB for optimal performance)
    private const int USN_BUFFER_SIZE = 64 * 1024;

    // Polling interval for USN Journal updates (100ms for near real-time)
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Event raised when file system changes are detected
    /// </summary>
    public event EventHandler<UsnChangeEventArgs>? ChangeDetected;

    /// <summary>
    /// Gets whether the monitor is currently active
    /// </summary>
    public bool IsMonitoring => _isMonitoring && !_disposed;

    /// <summary>
    /// Gets the async enumerable of file changes for streaming consumption
    /// </summary>
    public ChannelReader<UsnChangeRecord> Changes => _changeChannel.Reader;

    public UsnJournalMonitor(ILogger<UsnJournalMonitor>? logger = null)
    {
        _logger = logger;
        _bufferPool = ArrayPool<byte>.Shared;
        _volumeHandles = new Dictionary<char, SafeFileHandle>();
        _journalData = new Dictionary<char, USN_JOURNAL_DATA_V0>();
        _monitoringCts = new CancellationTokenSource();
        _monitoringTasks = new List<Task>();

        _changeChannel = Channel.CreateUnbounded<UsnChangeRecord>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });
    }

    /// <summary>
    /// Checks if USN Journal monitoring is available for a drive
    /// </summary>
    public bool IsAvailableForDrive(char driveLetter)
    {
        if (!MftReader.IsAvailable())
            return false;

        try
        {
            var handle = OpenVolume(driveLetter);
            if (handle == null || handle.IsInvalid)
                return false;

            var journalData = QueryJournalData(handle);
            handle.Dispose();

            return journalData.HasValue;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts monitoring file system changes on specified drives
    /// </summary>
    public async Task StartMonitoringAsync(
        IEnumerable<char>? driveLetters = null,
        TimeSpan? pollingInterval = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isMonitoring)
        {
            _logger?.LogWarning("USN Journal monitoring is already active");
            return;
        }

        var drives = driveLetters?.ToArray() ?? MftReader.GetNtfsDrives();
        var interval = pollingInterval ?? DefaultPollingInterval;

        _logger?.LogInformation("Starting USN Journal monitoring for drives: {Drives}", string.Join(", ", drives));

        foreach (var drive in drives)
        {
            try
            {
                var handle = OpenVolume(drive);
                if (handle == null || handle.IsInvalid)
                {
                    _logger?.LogWarning("Cannot open volume {Drive} for USN monitoring", drive);
                    continue;
                }

                // Create or query USN Journal
                var journalData = await EnsureJournalExistsAsync(handle, cancellationToken);
                if (!journalData.HasValue)
                {
                    _logger?.LogWarning("Cannot access USN Journal for drive {Drive}", drive);
                    handle.Dispose();
                    continue;
                }

                _volumeHandles[drive] = handle;
                _journalData[drive] = journalData.Value;

                // Start monitoring task for this drive
                var monitorTask = MonitorDriveAsync(drive, handle, journalData.Value, interval, _monitoringCts.Token);
                _monitoringTasks.Add(monitorTask);

                _logger?.LogInformation("Started USN monitoring for drive {Drive}, Journal ID: {JournalId}",
                    drive, journalData.Value.UsnJournalID);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start monitoring for drive {Drive}", drive);
            }
        }

        _isMonitoring = _monitoringTasks.Count > 0;

        if (!_isMonitoring)
        {
            _logger?.LogWarning("No drives could be monitored");
        }
    }

    /// <summary>
    /// Stops all monitoring activities
    /// </summary>
    public async Task StopMonitoringAsync()
    {
        if (!_isMonitoring)
            return;

        _logger?.LogInformation("Stopping USN Journal monitoring");

        _monitoringCts.Cancel();

        try
        {
            await Task.WhenAll(_monitoringTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        _monitoringTasks.Clear();
        _isMonitoring = false;

        // Close volume handles
        foreach (var handle in _volumeHandles.Values)
        {
            try
            {
                if (!handle.IsInvalid && !handle.IsClosed)
                    handle.Dispose();
            }
            catch { }
        }
        _volumeHandles.Clear();
        _journalData.Clear();

        _changeChannel.Writer.Complete();

        _logger?.LogInformation("USN Journal monitoring stopped");
    }

    /// <summary>
    /// Gets the current USN for a drive (for synchronization purposes)
    /// </summary>
    public long? GetCurrentUsn(char driveLetter)
    {
        if (_journalData.TryGetValue(driveLetter, out var journalData))
        {
            return journalData.NextUsn;
        }
        return null;
    }

    /// <summary>
    /// Reads historical changes from the USN Journal starting from a specific USN
    /// </summary>
    public async IAsyncEnumerable<UsnChangeRecord> ReadHistoricalChangesAsync(
        char driveLetter,
        long startUsn,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var handle = OpenVolume(driveLetter);
        if (handle == null || handle.IsInvalid)
        {
            _logger?.LogWarning("Cannot open volume {Drive} for reading history", driveLetter);
            yield break;
        }

        try
        {
            var journalData = QueryJournalData(handle);
            if (!journalData.HasValue)
            {
                _logger?.LogWarning("Cannot query USN Journal for drive {Drive}", driveLetter);
                yield break;
            }

            await foreach (var record in ReadUsnRecordsAsync(handle, journalData.Value, startUsn, cancellationToken))
            {
                yield return record;
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    #region Private Methods

    private async Task MonitorDriveAsync(
        char driveLetter,
        SafeFileHandle handle,
        USN_JOURNAL_DATA_V0 journalData,
        TimeSpan pollingInterval,
        CancellationToken cancellationToken)
    {
        var currentUsn = journalData.NextUsn;

        _logger?.LogDebug("Starting monitor loop for drive {Drive}, starting USN: {Usn}", driveLetter, currentUsn);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var recordsFound = 0;

                    await foreach (var record in ReadUsnRecordsAsync(handle, journalData, currentUsn, cancellationToken))
                    {
                        // Update current USN for next iteration
                        if (record.Usn > currentUsn)
                            currentUsn = record.Usn;

                        recordsFound++;

                        // Write to channel for streaming consumers
                        await _changeChannel.Writer.WriteAsync(record, cancellationToken);

                        // Raise event for event-based consumers
                        OnChangeDetected(new UsnChangeEventArgs(driveLetter, record));
                    }

                    if (recordsFound > 0)
                    {
                        _logger?.LogDebug("Processed {Count} USN records for drive {Drive}", recordsFound, driveLetter);
                    }

                    // Wait before next poll
                    await Task.Delay(pollingInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error reading USN records for drive {Drive}", driveLetter);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Monitor loop cancelled for drive {Drive}", driveLetter);
        }
    }

    private async IAsyncEnumerable<UsnChangeRecord> ReadUsnRecordsAsync(
        SafeFileHandle handle,
        USN_JOURNAL_DATA_V0 journalData,
        long startUsn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = _bufferPool.Rent(USN_BUFFER_SIZE);

        try
        {
            var readData = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = startUsn,
                ReasonMask = 0xFFFFFFFF, // All reasons
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalID = journalData.UsnJournalID
            };

            var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var bufferPtr = bufferHandle.AddrOfPinnedObject();

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.FSCTL_READ_USN_JOURNAL,
                        ref readData,
                        (uint)Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(),
                        bufferPtr,
                        (uint)buffer.Length,
                        out var bytesReturned,
                        IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 1181) // ERROR_JOURNAL_ENTRY_DELETED
                        {
                            _logger?.LogWarning("USN Journal entries deleted, resetting to lowest valid USN");
                            readData.StartUsn = journalData.LowestValidUsn;
                            continue;
                        }
                        break;
                    }

                    if (bytesReturned <= 8)
                        break;

                    // First 8 bytes contain the next USN
                    var nextUsn = BitConverter.ToInt64(buffer, 0);
                    readData.StartUsn = nextUsn;

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
                            yield return record.Value;
                        }
                    }

                    // If we got very few bytes, we've caught up - yield control
                    if (bytesReturned < 1000)
                    {
                        await Task.Yield();
                        break;
                    }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UsnChangeRecord? ParseUsnRecord(byte[] buffer, int offset, out uint recordLength)
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
        var usn = BitConverter.ToInt64(buffer, offset + 24);
        var timeStamp = BitConverter.ToInt64(buffer, offset + 32);
        var reason = (UsnReason)BitConverter.ToUInt32(buffer, offset + 40);
        var fileAttributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 52);
        var fileNameLength = BitConverter.ToUInt16(buffer, offset + 56);
        var fileNameOffset = BitConverter.ToUInt16(buffer, offset + 58);

        if (fileNameLength == 0 || offset + fileNameOffset + fileNameLength > buffer.Length)
        {
            return null;
        }

        var fileName = System.Text.Encoding.Unicode.GetString(buffer, offset + fileNameOffset, fileNameLength);

        // Skip system files and metadata
        if (fileName.StartsWith("$") || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        // Convert FILETIME to DateTime
        var dateTime = DateTime.FromFileTimeUtc(timeStamp);

        return new UsnChangeRecord(
            usn,
            fileReferenceNumber,
            parentFileReferenceNumber,
            reason,
            fileAttributes,
            fileName,
            dateTime);
    }

    private SafeFileHandle? OpenVolume(char driveLetter)
    {
        var volumePath = $"\\\\.\\{driveLetter}:";

        var handle = NativeMethods.CreateFileW(
            volumePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
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

    private USN_JOURNAL_DATA_V0? QueryJournalData(SafeFileHandle handle)
    {
        var journalData = new USN_JOURNAL_DATA_V0();
        var journalDataSize = (uint)Marshal.SizeOf<USN_JOURNAL_DATA_V0>();
        var journalDataPtr = Marshal.AllocHGlobal((int)journalDataSize);

        try
        {
            if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero,
                0,
                journalDataPtr,
                journalDataSize,
                out _,
                IntPtr.Zero))
            {
                return null;
            }

            journalData = Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(journalDataPtr);
            return journalData;
        }
        finally
        {
            Marshal.FreeHGlobal(journalDataPtr);
        }
    }

    private async Task<USN_JOURNAL_DATA_V0?> EnsureJournalExistsAsync(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        // First, try to query existing journal
        var journalData = QueryJournalData(handle);
        if (journalData.HasValue)
            return journalData;

        // Journal doesn't exist, try to create it
        _logger?.LogInformation("Creating USN Journal on volume");

        var createData = new CREATE_USN_JOURNAL_DATA
        {
            MaximumSize = 32 * 1024 * 1024, // 32 MB
            AllocationDelta = 8 * 1024 * 1024 // 8 MB
        };

        var createDataSize = (uint)Marshal.SizeOf<CREATE_USN_JOURNAL_DATA>();
        var createDataPtr = Marshal.AllocHGlobal((int)createDataSize);

        try
        {
            Marshal.StructureToPtr(createData, createDataPtr, false);

            if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.FSCTL_CREATE_USN_JOURNAL,
                createDataPtr,
                createDataSize,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
            {
                _logger?.LogWarning("Failed to create USN Journal, error: {Error}", Marshal.GetLastWin32Error());
                return null;
            }

            // Wait a bit for journal creation
            await Task.Delay(100, cancellationToken);

            // Query the newly created journal
            return QueryJournalData(handle);
        }
        finally
        {
            Marshal.FreeHGlobal(createDataPtr);
        }
    }

    private void OnChangeDetected(UsnChangeEventArgs e)
    {
        ChangeDetected?.Invoke(this, e);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UsnJournalMonitor));
    }

    #endregion

    #region IDisposable / IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await StopMonitoringAsync();

        _monitoringCts.Dispose();
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _monitoringCts.Cancel();

        try
        {
            Task.WaitAll(_monitoringTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch { }

        foreach (var handle in _volumeHandles.Values)
        {
            try
            {
                if (!handle.IsInvalid && !handle.IsClosed)
                    handle.Dispose();
            }
            catch { }
        }

        _monitoringCts.Dispose();
        _changeChannel.Writer.TryComplete();
        _disposed = true;
    }

    #endregion
}

/// <summary>
/// Event args for USN change events
/// </summary>
[SupportedOSPlatform("windows")]
public class UsnChangeEventArgs : EventArgs
{
    /// <summary>
    /// Drive letter where the change occurred
    /// </summary>
    public char DriveLetter { get; }

    /// <summary>
    /// The change record
    /// </summary>
    public UsnChangeRecord Record { get; }

    public UsnChangeEventArgs(char driveLetter, UsnChangeRecord record)
    {
        DriveLetter = driveLetter;
        Record = record;
    }
}
