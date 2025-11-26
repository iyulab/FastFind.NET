using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;

namespace FastFind.Windows.Mft;

/// <summary>
/// High-performance pipeline that connects MFT enumeration directly to SQLite persistence.
/// Achieves Everything-level indexing speed by:
/// 1. Parallel MFT enumeration across drives
/// 2. Channel-based producer/consumer pattern
/// 3. Bulk SQLite inserts with optimized PRAGMA settings
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftSqlitePipeline : IDisposable
{
    private readonly ILogger<MftSqlitePipeline>? _logger;
    private readonly MftReader _mftReader;
    private readonly Channel<FastFileItem> _channel;
    private bool _disposed;

    /// <summary>
    /// Pipeline statistics
    /// </summary>
    public PipelineStatistics Statistics { get; private set; } = new();

    public MftSqlitePipeline(ILogger<MftSqlitePipeline>? logger = null)
    {
        _logger = logger;
        _mftReader = new MftReader();

        // Unbounded channel for maximum throughput
        _channel = Channel.CreateUnbounded<FastFileItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
    }

    /// <summary>
    /// Indexes all NTFS drives using MFT and stores results in SQLite.
    /// This is the main entry point for full system indexing.
    /// </summary>
    /// <param name="persistence">SQLite persistence instance (must support AddFromStreamAsync or AddBulkOptimizedAsync)</param>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of files indexed</returns>
    public async Task<int> IndexAllDrivesAsync(
        IIndexPersistence persistence,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!MftReader.IsAvailable())
        {
            throw new InvalidOperationException("MFT access not available. Requires administrator privileges.");
        }

        var drives = MftReader.GetNtfsDrives();
        if (drives.Length == 0)
        {
            _logger?.LogWarning("No NTFS drives found for indexing");
            return 0;
        }

        _logger?.LogInformation("Starting MFT indexing on {Count} drives: {Drives}",
            drives.Length, string.Join(", ", drives.Select(d => $"{d}:")));

        return await IndexDrivesAsync(drives, persistence, progress, cancellationToken);
    }

    /// <summary>
    /// Indexes specific drives using MFT and stores results in SQLite.
    /// </summary>
    public async Task<int> IndexDrivesAsync(
        char[] driveLetters,
        IIndexPersistence persistence,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stopwatch = Stopwatch.StartNew();
        var totalIndexed = 0;

        try
        {
            // Clear existing data for fresh index
            await persistence.ClearAsync(cancellationToken);

            // Start producer tasks for each drive
            var producerTasks = driveLetters.Select(async drive =>
            {
                var driveStats = new DriveIndexingStats { DriveLetter = drive };
                var driveStopwatch = Stopwatch.StartNew();

                try
                {
                    await foreach (var record in _mftReader.EnumerateFilesAsync(drive, cancellationToken))
                    {
                        var fileItem = ConvertToFastFileItem(record, drive);
                        await _channel.Writer.WriteAsync(fileItem, cancellationToken);

                        driveStats.RecordCount++;
                        if (record.IsDirectory) driveStats.DirectoryCount++;
                        else driveStats.FileCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error enumerating drive {Drive}", drive);
                    driveStats.Error = ex.Message;
                }

                driveStopwatch.Stop();
                driveStats.ElapsedTime = driveStopwatch.Elapsed;
                driveStats.RecordsPerSecond = driveStats.RecordCount / driveStopwatch.Elapsed.TotalSeconds;

                return driveStats;
            }).ToList();

            // Complete channel when all producers are done
            _ = Task.WhenAll(producerTasks).ContinueWith(_ => _channel.Writer.Complete(), cancellationToken);

            // Consumer: read from channel and bulk insert to SQLite
            totalIndexed = await ConsumeAndPersistAsync(persistence, progress, cancellationToken);

            // Wait for all producer stats
            var driveStats = await Task.WhenAll(producerTasks);

            stopwatch.Stop();
            Statistics = new PipelineStatistics
            {
                TotalRecords = totalIndexed,
                TotalFiles = driveStats.Sum(s => s.FileCount),
                TotalDirectories = driveStats.Sum(s => s.DirectoryCount),
                ElapsedTime = stopwatch.Elapsed,
                RecordsPerSecond = totalIndexed / stopwatch.Elapsed.TotalSeconds,
                DriveStats = driveStats
            };

            _logger?.LogInformation(
                "MFT indexing completed: {Total:N0} records ({Files:N0} files, {Dirs:N0} directories) in {Time:F2}s ({Rate:N0} records/sec)",
                Statistics.TotalRecords, Statistics.TotalFiles, Statistics.TotalDirectories,
                Statistics.ElapsedTime.TotalSeconds, Statistics.RecordsPerSecond);

            // Optimize after bulk load
            await persistence.OptimizeAsync(cancellationToken);

            return totalIndexed;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MFT indexing failed");
            throw;
        }
    }

    private async Task<int> ConsumeAndPersistAsync(
        IIndexPersistence persistence,
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int batchSize = 5000;
        var buffer = new List<FastFileItem>(batchSize);
        var totalInserted = 0;
        var lastProgressReport = Stopwatch.StartNew();

        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            buffer.Add(item);

            if (buffer.Count >= batchSize)
            {
                var inserted = await persistence.AddBatchAsync(buffer, cancellationToken);
                totalInserted += inserted;
                buffer.Clear();

                // Report progress at most every 500ms
                if (lastProgressReport.ElapsedMilliseconds > 500)
                {
                    progress?.Report(new IndexingProgress
                    {
                        TotalIndexed = totalInserted,
                        CurrentOperation = "Indexing files..."
                    });
                    lastProgressReport.Restart();
                }
            }
        }

        // Flush remaining
        if (buffer.Count > 0)
        {
            var inserted = await persistence.AddBatchAsync(buffer, cancellationToken);
            totalInserted += inserted;
        }

        progress?.Report(new IndexingProgress
        {
            TotalIndexed = totalInserted,
            CurrentOperation = "Indexing complete",
            IsComplete = true
        });

        return totalInserted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FastFileItem ConvertToFastFileItem(MftFileRecord record, char driveLetter)
    {
        var fullPath = $"{driveLetter}:\\{record.FileName}";
        var directoryPath = $"{driveLetter}:\\";
        var extension = record.IsDirectory ? "" : Path.GetExtension(record.FileName);

        return new FastFileItem(
            fullPath: fullPath,
            name: record.FileName,
            directoryPath: directoryPath,
            extension: extension,
            size: record.FileSize,
            created: record.CreationTime,
            modified: record.ModificationTime,
            accessed: record.AccessTime,
            attributes: record.Attributes,
            driveLetter: driveLetter
        );
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mftReader.Dispose();
    }
}

/// <summary>
/// Pipeline execution statistics
/// </summary>
public record PipelineStatistics
{
    public long TotalRecords { get; init; }
    public long TotalFiles { get; init; }
    public long TotalDirectories { get; init; }
    public TimeSpan ElapsedTime { get; init; }
    public double RecordsPerSecond { get; init; }
    public DriveIndexingStats[]? DriveStats { get; init; }
}

/// <summary>
/// Per-drive indexing statistics
/// </summary>
public record DriveIndexingStats
{
    public char DriveLetter { get; init; }
    public long RecordCount { get; set; }
    public long FileCount { get; set; }
    public long DirectoryCount { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public double RecordsPerSecond { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Progress information for indexing operations
/// </summary>
public record IndexingProgress
{
    public int TotalIndexed { get; init; }
    public string CurrentOperation { get; init; } = "";
    public bool IsComplete { get; init; }
}
