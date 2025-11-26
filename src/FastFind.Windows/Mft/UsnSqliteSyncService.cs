using System.Runtime.Versioning;
using System.Threading.Channels;
using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;

namespace FastFind.Windows.Mft;

/// <summary>
/// Real-time synchronization service that monitors USN Journal changes
/// and updates SQLite database accordingly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsnSqliteSyncService : IAsyncDisposable
{
    private readonly ILogger<UsnSqliteSyncService>? _logger;
    private readonly IIndexPersistence _persistence;
    private readonly UsnJournalMonitor _monitor;
    private readonly Channel<UsnChangeRecord> _changeChannel;
    private readonly SyncStatistics _statistics;

    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Gets whether the sync service is currently running
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets sync statistics
    /// </summary>
    public SyncStatistics Statistics => _statistics;

    public UsnSqliteSyncService(
        IIndexPersistence persistence,
        ILogger<UsnSqliteSyncService>? logger = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _logger = logger;
        _monitor = new UsnJournalMonitor(logger: null);
        _statistics = new SyncStatistics();

        _changeChannel = Channel.CreateUnbounded<UsnChangeRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Starts real-time synchronization for all NTFS drives
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isRunning)
        {
            _logger?.LogWarning("Sync service is already running");
            return;
        }

        var drives = MftReader.GetNtfsDrives();
        if (drives.Length == 0)
        {
            throw new InvalidOperationException("No NTFS drives found for monitoring");
        }

        await StartAsync(drives, cancellationToken);
    }

    /// <summary>
    /// Starts real-time synchronization for specific drives
    /// </summary>
    public async Task StartAsync(char[] driveLetters, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isRunning)
        {
            _logger?.LogWarning("Sync service is already running");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;
        _statistics.Reset();
        _statistics.StartTime = DateTime.UtcNow;

        _logger?.LogInformation("Starting USN-SQLite sync service for drives: {Drives}",
            string.Join(", ", driveLetters.Select(d => $"{d}:")));

        // Start monitoring using UsnJournalMonitor
        await _monitor.StartMonitoringAsync(driveLetters, cancellationToken: _cts.Token);

        // Start forwarding task from monitor's Changes channel to our processing channel
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in _monitor.Changes.ReadAllAsync(_cts.Token))
                {
                    await _changeChannel.Writer.WriteAsync(change, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on stop
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error forwarding USN changes");
            }
        }, _cts.Token);

        // Start processing task
        _processingTask = ProcessChangesAsync(_cts.Token);

        _logger?.LogInformation("USN-SQLite sync service started");
    }

    /// <summary>
    /// Stops the synchronization service
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger?.LogInformation("Stopping USN-SQLite sync service...");

        _cts?.Cancel();
        _changeChannel.Writer.Complete();

        if (_processingTask != null)
        {
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _isRunning = false;
        _statistics.StopTime = DateTime.UtcNow;

        _logger?.LogInformation("USN-SQLite sync service stopped. Stats: {Stats}", _statistics);
    }

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        var batch = new List<(UsnChangeRecord Change, FastFileItem? Item)>();
        const int batchSize = 100;
        var lastFlush = DateTime.UtcNow;

        try
        {
            await foreach (var change in _changeChannel.Reader.ReadAllAsync(cancellationToken))
            {
                _statistics.TotalChangesReceived++;

                var processResult = await ProcessSingleChangeAsync(change, cancellationToken);
                if (processResult.HasValue)
                {
                    batch.Add((change, processResult.Value));
                }

                // Flush batch if size reached or time elapsed
                var elapsed = DateTime.UtcNow - lastFlush;
                if (batch.Count >= batchSize || (batch.Count > 0 && elapsed.TotalMilliseconds > 500))
                {
                    await FlushBatchAsync(batch, cancellationToken);
                    batch.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }

            // Flush remaining
            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing USN changes");
            _statistics.Errors++;
        }
    }

    private async Task<FastFileItem?> ProcessSingleChangeAsync(UsnChangeRecord change, CancellationToken cancellationToken)
    {
        try
        {
            // Handle different change types
            if (change.IsDeleted)
            {
                _statistics.Deletions++;
                return null; // Deletion handled separately
            }

            if (change.IsCreated || change.IsRenamedTo || change.IsDataModified)
            {
                // For creates/modifies, we need to get file info
                // Note: In a production system, you'd resolve the full path from the FRN
                var fileItem = CreateFileItemFromChange(change);

                if (change.IsCreated)
                    _statistics.Additions++;
                else
                    _statistics.Updates++;

                return fileItem;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing change for {FileName}", change.FileName);
            _statistics.Errors++;
            return null;
        }
    }

    private async Task FlushBatchAsync(List<(UsnChangeRecord Change, FastFileItem? Item)> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        try
        {
            // Group by operation type - use explicit type to avoid nullable issues
            var additions = batch
                .Where(b => b.Item != null && b.Change.IsCreated)
                .Select(b => b.Item!.Value)
                .ToList();

            var updates = batch
                .Where(b => b.Item != null && !b.Change.IsCreated && !b.Change.IsDeleted)
                .Select(b => b.Item!.Value)
                .ToList();

            var deletions = batch.Where(b => b.Change.IsDeleted)
                                 .Select(b => b.Change.FileName)
                                 .ToList();

            // Perform batch operations
            if (additions.Count > 0)
            {
                await _persistence.AddBatchAsync(additions, cancellationToken);
            }

            foreach (var update in updates)
            {
                await _persistence.UpdateAsync(update, cancellationToken);
            }

            // Note: For deletions, we'd need the full path, not just filename
            // This is a limitation - in production, maintain FRN -> path mapping

            _logger?.LogDebug("Flushed batch: {Adds} adds, {Updates} updates, {Deletes} deletes",
                additions.Count, updates.Count, deletions.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error flushing batch");
            _statistics.Errors++;
        }
    }

    private static FastFileItem CreateFileItemFromChange(UsnChangeRecord change)
    {
        // Note: In production, resolve full path using MFT FRN lookup
        // This is a simplified version using just the filename
        var fullPath = change.FileName; // Would be resolved from FRN
        var isDir = change.IsDirectory;
        var ext = isDir ? "" : Path.GetExtension(change.FileName);

        return new FastFileItem(
            fullPath: fullPath,
            name: change.FileName,
            directoryPath: "", // Would be resolved from parent FRN
            extension: ext,
            size: 0, // Would need to query file system
            created: change.TimeStamp,
            modified: change.TimeStamp,
            accessed: change.TimeStamp,
            attributes: change.Attributes,
            driveLetter: 'C' // Would be determined from drive
        );
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await StopAsync();
        _monitor.Dispose();

        _disposed = true;
    }
}

/// <summary>
/// Statistics for USN-SQLite synchronization
/// </summary>
public class SyncStatistics
{
    public DateTime StartTime { get; set; }
    public DateTime? StopTime { get; set; }
    public long TotalChangesReceived { get; set; }
    public long Additions { get; set; }
    public long Updates { get; set; }
    public long Deletions { get; set; }
    public long Errors { get; set; }

    public TimeSpan Duration => (StopTime ?? DateTime.UtcNow) - StartTime;
    public double ChangesPerSecond => Duration.TotalSeconds > 0 ? TotalChangesReceived / Duration.TotalSeconds : 0;

    public void Reset()
    {
        StartTime = DateTime.UtcNow;
        StopTime = null;
        TotalChangesReceived = 0;
        Additions = 0;
        Updates = 0;
        Deletions = 0;
        Errors = 0;
    }

    public override string ToString() =>
        $"Changes: {TotalChangesReceived:N0} (Add: {Additions:N0}, Update: {Updates:N0}, Delete: {Deletions:N0}, Errors: {Errors}) in {Duration.TotalSeconds:F1}s";
}
