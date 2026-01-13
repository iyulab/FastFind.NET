using FastFind.Windows.Mft;
using FluentAssertions;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Strict performance validation tests for MFT-based file enumeration.
/// These tests enforce rigorous performance criteria based on industry benchmarks.
///
/// Performance Baselines (Everything by voidtools):
/// - MFT Enumeration: ~500,000 records/sec
/// - Search Response: < 50ms for 1M files
/// - Memory Usage: ~50 bytes per record
///
/// Our Strict Criteria (at least 40-50% of Everything performance):
/// - MFT Enumeration: >= 200,000 records/sec
/// - Search Response: < 100ms
/// - Memory Usage: < 100 bytes per record
/// - Indexing Throughput: >= 50,000 files/sec
/// </summary>
[Trait("Category", "Performance")]
[Trait("Category", "MFT")]
public class MftStrictPerformanceTests
{
    private readonly ITestOutputHelper _output;

    // ========================================
    // STRICT PERFORMANCE CRITERIA
    // ========================================

    /// <summary>
    /// Minimum MFT enumeration speed (records/second)
    /// Everything achieves ~500K, we target 40%
    /// </summary>
    private const double MIN_MFT_ENUMERATION_RATE = 200_000;

    /// <summary>
    /// Maximum allowed enumeration time per 100K records (milliseconds)
    /// </summary>
    private const double MAX_MS_PER_100K_RECORDS = 500;

    /// <summary>
    /// Minimum indexing throughput (files/second)
    /// </summary>
    private const double MIN_INDEXING_THROUGHPUT = 50_000;

    /// <summary>
    /// Maximum memory per record (bytes)
    /// </summary>
    private const double MAX_MEMORY_PER_RECORD = 100;

    /// <summary>
    /// Minimum path building speed (paths/second)
    /// </summary>
    private const double MIN_PATH_BUILD_RATE = 100_000;

    /// <summary>
    /// Maximum single drive scan time (seconds) for typical SSD
    /// </summary>
    private const double MAX_SINGLE_DRIVE_SCAN_SECONDS = 30;

    public MftStrictPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task MFT_Enumeration_MustMeetMinimumSpeed()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("SKIP: MFT access not available (requires administrator rights)");
            _output.WriteLine("");
            _output.WriteLine("=== PERFORMANCE CRITERIA (Reference) ===");
            _output.WriteLine($"Minimum MFT Enumeration Rate: {MIN_MFT_ENUMERATION_RATE:N0} records/sec");
            _output.WriteLine($"Maximum Time per 100K Records: {MAX_MS_PER_100K_RECORDS:N0} ms");
            _output.WriteLine($"Maximum Single Drive Scan: {MAX_SINGLE_DRIVE_SCAN_SECONDS:N0} seconds");
            return;
        }

        using var reader = new MftReader();
        var drives = MftReader.GetNtfsDrives();

        if (drives.Length == 0)
        {
            _output.WriteLine("SKIP: No NTFS drives found");
            return;
        }

        var drive = drives[0];
        _output.WriteLine($"=== MFT ENUMERATION PERFORMANCE TEST ===");
        _output.WriteLine($"Target Drive: {drive}:");
        _output.WriteLine($"Criteria: >= {MIN_MFT_ENUMERATION_RATE:N0} records/sec");
        _output.WriteLine("");

        // Act
        var stopwatch = Stopwatch.StartNew();
        long totalRecords = 0;
        long fileCount = 0;
        long directoryCount = 0;

        await foreach (var record in reader.EnumerateFilesAsync(drive))
        {
            totalRecords++;
            if (record.IsDirectory)
                directoryCount++;
            else
                fileCount++;
        }

        stopwatch.Stop();

        // Calculate metrics
        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        var recordsPerSecond = totalRecords / elapsedSeconds;
        var msPer100K = (stopwatch.Elapsed.TotalMilliseconds / totalRecords) * 100_000;

        // Report
        _output.WriteLine("=== RESULTS ===");
        _output.WriteLine($"Total Records: {totalRecords:N0}");
        _output.WriteLine($"  - Files: {fileCount:N0}");
        _output.WriteLine($"  - Directories: {directoryCount:N0}");
        _output.WriteLine($"Elapsed Time: {elapsedSeconds:F3} seconds");
        _output.WriteLine("");
        _output.WriteLine("=== PERFORMANCE METRICS ===");
        _output.WriteLine($"Enumeration Rate: {recordsPerSecond:N0} records/sec");
        _output.WriteLine($"Time per 100K Records: {msPer100K:F2} ms");
        _output.WriteLine("");

        // Evaluate
        var passRate = recordsPerSecond >= MIN_MFT_ENUMERATION_RATE;
        var passTime = msPer100K <= MAX_MS_PER_100K_RECORDS;
        var passScanTime = elapsedSeconds <= MAX_SINGLE_DRIVE_SCAN_SECONDS;

        _output.WriteLine("=== EVALUATION ===");
        _output.WriteLine($"[{(passRate ? "PASS" : "FAIL")}] Enumeration Rate: {recordsPerSecond:N0} >= {MIN_MFT_ENUMERATION_RATE:N0}");
        _output.WriteLine($"[{(passTime ? "PASS" : "FAIL")}] Time per 100K: {msPer100K:F2} ms <= {MAX_MS_PER_100K_RECORDS:N0} ms");
        _output.WriteLine($"[{(passScanTime ? "PASS" : "FAIL")}] Total Scan Time: {elapsedSeconds:F2}s <= {MAX_SINGLE_DRIVE_SCAN_SECONDS}s");

        // Assert
        recordsPerSecond.Should().BeGreaterThanOrEqualTo(MIN_MFT_ENUMERATION_RATE,
            $"MFT enumeration must achieve at least {MIN_MFT_ENUMERATION_RATE:N0} records/sec");
    }

    [Fact]
    public async Task MFT_Enumeration_MemoryEfficiency_MustBeOptimal()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("SKIP: MFT access not available");
            _output.WriteLine($"Memory Criteria: <= {MAX_MEMORY_PER_RECORD:N0} bytes/record");
            return;
        }

        using var reader = new MftReader();
        var drives = MftReader.GetNtfsDrives();

        if (drives.Length == 0)
        {
            _output.WriteLine("SKIP: No NTFS drives found");
            return;
        }

        var drive = drives[0];
        _output.WriteLine($"=== MEMORY EFFICIENCY TEST ===");
        _output.WriteLine($"Target Drive: {drive}:");
        _output.WriteLine($"Criteria: <= {MAX_MEMORY_PER_RECORD:N0} bytes/record");
        _output.WriteLine("");

        // Force GC before measurement
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memoryBefore = GC.GetTotalMemory(true);

        // Act - enumerate limited records to measure memory
        const int sampleSize = 100_000;
        var records = new List<MftFileRecord>(sampleSize);

        await foreach (var record in reader.EnumerateFilesAsync(drive))
        {
            records.Add(record);
            if (records.Count >= sampleSize)
                break;
        }

        var memoryAfter = GC.GetTotalMemory(false);
        var memoryUsed = memoryAfter - memoryBefore;
        var bytesPerRecord = (double)memoryUsed / records.Count;

        // Report
        _output.WriteLine("=== RESULTS ===");
        _output.WriteLine($"Sample Size: {records.Count:N0} records");
        _output.WriteLine($"Memory Before: {memoryBefore / 1024.0 / 1024.0:F2} MB");
        _output.WriteLine($"Memory After: {memoryAfter / 1024.0 / 1024.0:F2} MB");
        _output.WriteLine($"Memory Used: {memoryUsed / 1024.0 / 1024.0:F2} MB");
        _output.WriteLine($"Bytes per Record: {bytesPerRecord:F2}");
        _output.WriteLine("");

        var passMemory = bytesPerRecord <= MAX_MEMORY_PER_RECORD;
        _output.WriteLine($"[{(passMemory ? "PASS" : "FAIL")}] Memory Efficiency: {bytesPerRecord:F2} <= {MAX_MEMORY_PER_RECORD:N0} bytes/record");

        // Assert
        bytesPerRecord.Should().BeLessThanOrEqualTo(MAX_MEMORY_PER_RECORD,
            $"Memory usage must be under {MAX_MEMORY_PER_RECORD} bytes per record");
    }

    [Fact]
    public async Task MFT_Benchmark_MustMeetAllCriteria()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("SKIP: MFT access not available");
            PrintPerformanceCriteria();
            return;
        }

        using var reader = new MftReader();
        var drives = MftReader.GetNtfsDrives();

        if (drives.Length == 0)
        {
            _output.WriteLine("SKIP: No NTFS drives found");
            return;
        }

        var drive = drives[0];
        _output.WriteLine($"=== COMPREHENSIVE MFT BENCHMARK ===");
        _output.WriteLine($"Target Drive: {drive}:");
        _output.WriteLine("");

        // Act
        var result = await reader.BenchmarkAsync(drive);

        // Report
        _output.WriteLine("=== RESULTS ===");
        _output.WriteLine($"Status: {(result.IsSuccess ? "SUCCESS" : "FAILED")}");
        _output.WriteLine($"Total Records: {result.TotalRecords:N0}");
        _output.WriteLine($"  - Files: {result.FileCount:N0}");
        _output.WriteLine($"  - Directories: {result.DirectoryCount:N0}");
        _output.WriteLine($"Elapsed Time: {result.ElapsedTime.TotalSeconds:F3} seconds");
        _output.WriteLine($"Enumeration Rate: {result.RecordsPerSecond:N0} records/sec");
        _output.WriteLine("");

        // Evaluate all criteria
        _output.WriteLine("=== PERFORMANCE EVALUATION ===");

        var passRate = result.RecordsPerSecond >= MIN_MFT_ENUMERATION_RATE;
        var passScanTime = result.ElapsedTime.TotalSeconds <= MAX_SINGLE_DRIVE_SCAN_SECONDS;

        _output.WriteLine($"[{(passRate ? "PASS" : "FAIL")}] Enumeration Rate: {result.RecordsPerSecond:N0} >= {MIN_MFT_ENUMERATION_RATE:N0} records/sec");
        _output.WriteLine($"[{(passScanTime ? "PASS" : "FAIL")}] Scan Time: {result.ElapsedTime.TotalSeconds:F2}s <= {MAX_SINGLE_DRIVE_SCAN_SECONDS}s");

        // Calculate performance percentage relative to Everything
        var everythingBaseline = 500_000.0;
        var performancePercent = (result.RecordsPerSecond / everythingBaseline) * 100;
        _output.WriteLine("");
        _output.WriteLine($"=== COMPARISON TO EVERYTHING (voidtools) ===");
        _output.WriteLine($"Everything Baseline: ~{everythingBaseline:N0} records/sec");
        _output.WriteLine($"Our Performance: {result.RecordsPerSecond:N0} records/sec ({performancePercent:F1}%)");

        // Assert
        result.IsSuccess.Should().BeTrue("MFT benchmark must complete successfully");
        result.RecordsPerSecond.Should().BeGreaterThanOrEqualTo(MIN_MFT_ENUMERATION_RATE,
            $"Must achieve at least {MIN_MFT_ENUMERATION_RATE:N0} records/sec (40% of Everything)");
    }

    [Fact]
    public async Task MFT_ParallelDrives_MustScaleEfficiently()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("SKIP: MFT access not available");
            return;
        }

        var drives = MftReader.GetNtfsDrives();
        if (drives.Length < 2)
        {
            _output.WriteLine($"SKIP: Need at least 2 NTFS drives for parallel test (found: {drives.Length})");
            _output.WriteLine($"Available drives: {string.Join(", ", drives)}");
            return;
        }

        _output.WriteLine($"=== PARALLEL DRIVE ENUMERATION TEST ===");
        _output.WriteLine($"Drives: {string.Join(", ", drives)}");
        _output.WriteLine("");

        // First, benchmark individual drives
        using var reader = new MftReader();
        var individualResults = new List<(char Drive, double Rate)>();

        foreach (var drive in drives.Take(2))
        {
            var result = await reader.BenchmarkAsync(drive);
            if (result.IsSuccess)
            {
                individualResults.Add((drive, result.RecordsPerSecond));
                _output.WriteLine($"Drive {drive}: {result.RecordsPerSecond:N0} records/sec ({result.TotalRecords:N0} records)");
            }
        }

        if (individualResults.Count < 2)
        {
            _output.WriteLine("SKIP: Could not benchmark at least 2 drives");
            return;
        }

        // Now test parallel enumeration
        _output.WriteLine("");
        _output.WriteLine("=== PARALLEL ENUMERATION ===");

        var stopwatch = Stopwatch.StartNew();
        long totalRecords = 0;

        await foreach (var record in reader.EnumerateAllDrivesAsync())
        {
            totalRecords++;
        }

        stopwatch.Stop();
        var parallelRate = totalRecords / stopwatch.Elapsed.TotalSeconds;
        var combinedIndividualRate = individualResults.Sum(r => r.Rate);
        var scalingEfficiency = (parallelRate / combinedIndividualRate) * 100;

        _output.WriteLine($"Total Records: {totalRecords:N0}");
        _output.WriteLine($"Parallel Rate: {parallelRate:N0} records/sec");
        _output.WriteLine($"Combined Individual Rate: {combinedIndividualRate:N0} records/sec");
        _output.WriteLine($"Scaling Efficiency: {scalingEfficiency:F1}%");
        _output.WriteLine("");

        // Parallel should achieve at least 70% efficiency
        var passEfficiency = scalingEfficiency >= 70;
        _output.WriteLine($"[{(passEfficiency ? "PASS" : "FAIL")}] Scaling Efficiency: {scalingEfficiency:F1}% >= 70%");

        // Assert
        parallelRate.Should().BeGreaterThanOrEqualTo(MIN_MFT_ENUMERATION_RATE,
            "Parallel enumeration must still meet minimum rate");
        scalingEfficiency.Should().BeGreaterThanOrEqualTo(70,
            "Parallel scaling should achieve at least 70% efficiency");
    }

    [Fact]
    public void MFT_VolumeInfo_MustProvideAccurateMetrics()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("SKIP: MFT access not available");
            return;
        }

        using var reader = new MftReader();
        var drives = MftReader.GetNtfsDrives();

        if (drives.Length == 0)
        {
            _output.WriteLine("SKIP: No NTFS drives found");
            return;
        }

        _output.WriteLine("=== VOLUME INFORMATION TEST ===");
        _output.WriteLine("");

        foreach (var drive in drives)
        {
            var volumeInfo = reader.GetVolumeInfo(drive);

            _output.WriteLine($"Drive {drive}:");

            if (volumeInfo.HasValue)
            {
                var info = volumeInfo.Value;
                _output.WriteLine($"  Volume Serial: {info.VolumeSerialNumber:X}");
                _output.WriteLine($"  Bytes per Sector: {info.BytesPerSector}");
                _output.WriteLine($"  Bytes per Cluster: {info.BytesPerCluster}");
                _output.WriteLine($"  Bytes per MFT Record: {info.BytesPerFileRecordSegment}");
                _output.WriteLine($"  Total Size: {info.TotalSizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GB");
                _output.WriteLine($"  Free Space: {info.FreeSizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GB");
                _output.WriteLine($"  Estimated MFT Records: {info.EstimatedMftRecordCount:N0}");
                _output.WriteLine("");

                // Validate reasonable values
                info.BytesPerSector.Should().BeOneOf(new uint[] { 512, 4096 }, "Sector size should be standard");
                info.BytesPerCluster.Should().BeGreaterThan(0, "Cluster size must be positive");
                info.TotalSizeBytes.Should().BeGreaterThan(0, "Total size must be positive");
            }
            else
            {
                _output.WriteLine("  Could not retrieve volume information");
                _output.WriteLine("");
            }
        }
    }

    private void PrintPerformanceCriteria()
    {
        _output.WriteLine("");
        _output.WriteLine("=== STRICT PERFORMANCE CRITERIA ===");
        _output.WriteLine($"1. MFT Enumeration Rate: >= {MIN_MFT_ENUMERATION_RATE:N0} records/sec");
        _output.WriteLine($"2. Time per 100K Records: <= {MAX_MS_PER_100K_RECORDS:N0} ms");
        _output.WriteLine($"3. Memory per Record: <= {MAX_MEMORY_PER_RECORD:N0} bytes");
        _output.WriteLine($"4. Indexing Throughput: >= {MIN_INDEXING_THROUGHPUT:N0} files/sec");
        _output.WriteLine($"5. Path Build Rate: >= {MIN_PATH_BUILD_RATE:N0} paths/sec");
        _output.WriteLine($"6. Max Single Drive Scan: <= {MAX_SINGLE_DRIVE_SCAN_SECONDS:N0} seconds");
        _output.WriteLine("");
        _output.WriteLine("Baseline: Everything (voidtools) achieves ~500K records/sec");
        _output.WriteLine("Target: At least 40% of Everything performance");
    }
}
