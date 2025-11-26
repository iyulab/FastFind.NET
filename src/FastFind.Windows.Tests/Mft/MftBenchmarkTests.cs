using FastFind.Windows.Mft;
using FluentAssertions;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Benchmark tests for MFT-based file enumeration.
/// These tests verify Everything-level performance targets.
/// </summary>
[Trait("Category", "Performance")]
public class MftBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public MftBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = "Performance test - run manually with admin rights")]
    public async Task MftReader_ShouldEnumerateFiles_AtHighSpeed()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - skipping test (requires admin rights)");
            return;
        }

        using var reader = new MftReader();
        var ntfsDrives = MftReader.GetNtfsDrives();

        if (ntfsDrives.Length == 0)
        {
            _output.WriteLine("No NTFS drives found - skipping test");
            return;
        }

        var driveLetter = ntfsDrives[0];
        _output.WriteLine($"Testing MFT enumeration on drive {driveLetter}:");

        // Act
        var stopwatch = Stopwatch.StartNew();
        long fileCount = 0;
        long directoryCount = 0;

        await foreach (var record in reader.EnumerateFilesAsync(driveLetter))
        {
            if (record.IsDirectory)
                directoryCount++;
            else
                fileCount++;
        }

        stopwatch.Stop();

        // Assert & Report
        var totalRecords = fileCount + directoryCount;
        var recordsPerSecond = totalRecords / stopwatch.Elapsed.TotalSeconds;

        _output.WriteLine($"  Total records: {totalRecords:N0}");
        _output.WriteLine($"  Files: {fileCount:N0}");
        _output.WriteLine($"  Directories: {directoryCount:N0}");
        _output.WriteLine($"  Time: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _output.WriteLine($"  Speed: {recordsPerSecond:N0} records/second");

        // Performance target: > 100,000 records/second (Everything achieves ~500K)
        recordsPerSecond.Should().BeGreaterThan(100000,
            "MFT enumeration should achieve at least 100K records/second");
    }

    [Fact(Skip = "Performance test - run manually with admin rights")]
    public async Task MftReader_BenchmarkAsync_ShouldReportAccurateMetrics()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - skipping test (requires admin rights)");
            return;
        }

        using var reader = new MftReader();
        var ntfsDrives = MftReader.GetNtfsDrives();

        if (ntfsDrives.Length == 0)
        {
            _output.WriteLine("No NTFS drives found - skipping test");
            return;
        }

        var driveLetter = ntfsDrives[0];
        _output.WriteLine($"Running MFT benchmark on drive {driveLetter}:");

        // Act
        var result = await reader.BenchmarkAsync(driveLetter);

        // Assert & Report
        result.IsSuccess.Should().BeTrue("Benchmark should complete successfully");

        _output.WriteLine($"  Total records: {result.TotalRecords:N0}");
        _output.WriteLine($"  Files: {result.FileCount:N0}");
        _output.WriteLine($"  Directories: {result.DirectoryCount:N0}");
        _output.WriteLine($"  Time: {result.ElapsedTime.TotalSeconds:F2} seconds");
        _output.WriteLine($"  Speed: {result.RecordsPerSecond:N0} records/second");

        result.RecordsPerSecond.Should().BeGreaterThan(100000);
    }

    [Fact(Skip = "Performance test - run manually with admin rights")]
    public async Task MftReader_ParallelDriveEnumeration_ShouldBeEfficient()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - skipping test (requires admin rights)");
            return;
        }

        var ntfsDrives = MftReader.GetNtfsDrives();
        if (ntfsDrives.Length < 2)
        {
            _output.WriteLine("Need at least 2 NTFS drives for parallel test");
            return;
        }

        using var reader = new MftReader();
        _output.WriteLine($"Testing parallel enumeration on drives: {string.Join(", ", ntfsDrives)}");

        // Act
        var stopwatch = Stopwatch.StartNew();
        long totalRecords = 0;

        await foreach (var record in reader.EnumerateAllDrivesAsync())
        {
            totalRecords++;
        }

        stopwatch.Stop();

        // Report
        var recordsPerSecond = totalRecords / stopwatch.Elapsed.TotalSeconds;

        _output.WriteLine($"  Total records: {totalRecords:N0}");
        _output.WriteLine($"  Time: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _output.WriteLine($"  Speed: {recordsPerSecond:N0} records/second");

        // Parallel should be faster than single drive
        recordsPerSecond.Should().BeGreaterThan(100000);
    }

    [Fact]
    public void MftReader_IsAvailable_ShouldReturnCorrectStatus()
    {
        // Act
        var isAvailable = MftReader.IsAvailable();

        // Report
        _output.WriteLine($"MFT Available: {isAvailable}");

        if (!isAvailable)
        {
            _output.WriteLine("MFT is not available. Possible reasons:");
            _output.WriteLine("  - Not running as administrator");
            _output.WriteLine("  - Not running on Windows");
        }

        // No assertion - just informational
    }

    [Fact]
    public void MftReader_GetNtfsDrives_ShouldReturnDrives()
    {
        // Act
        var drives = MftReader.GetNtfsDrives();

        // Report
        _output.WriteLine($"NTFS Drives found: {drives.Length}");
        foreach (var drive in drives)
        {
            _output.WriteLine($"  {drive}:");
        }

        // At least one drive should exist on most systems
        // But don't fail if there are none
    }

    [Fact(Skip = "Performance test - run manually with admin rights")]
    public async Task HybridProvider_ShouldSelectOptimalMode()
    {
        // Arrange
        var diagnostics = HybridFileSystemProvider.CheckMftAvailability();

        _output.WriteLine("MFT Diagnostics:");
        _output.WriteLine($"  Is Administrator: {diagnostics.IsAdministrator}");
        _output.WriteLine($"  NTFS Drives: {string.Join(", ", diagnostics.NtfsDrives)}");
        _output.WriteLine($"  Can Use MFT: {diagnostics.CanUseMft}");
        _output.WriteLine($"  Reason: {diagnostics.Reason}");

        // Act
        await using var provider = new HybridFileSystemProvider();
        var status = provider.GetStatus();

        // Report
        _output.WriteLine($"\nProvider Status:");
        _output.WriteLine($"  Mode: {status.Mode}");
        _output.WriteLine($"  Is Available: {status.IsAvailable}");
        _output.WriteLine($"  Is MFT Capable: {status.IsMftCapable}");
        _output.WriteLine($"  Estimated Speed: {status.Performance.EstimatedFilesPerSecond:N0} files/sec");

        // If MFT is available, it should be selected
        if (diagnostics.CanUseMft)
        {
            status.Mode.Should().Be(ProviderMode.Mft, "MFT mode should be selected when available");
        }
    }

    [Fact]
    public void VolumeInfo_ShouldBeRetrievable_WhenAdmin()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("Skipping - requires admin rights");
            return;
        }

        using var reader = new MftReader();
        var ntfsDrives = MftReader.GetNtfsDrives();

        // Act & Report
        foreach (var drive in ntfsDrives)
        {
            var volumeInfo = reader.GetVolumeInfo(drive);
            if (volumeInfo != null)
            {
                _output.WriteLine($"Volume {drive}:");
                _output.WriteLine($"  Serial: {volumeInfo.Value.VolumeSerialNumber:X}");
                _output.WriteLine($"  Bytes per sector: {volumeInfo.Value.BytesPerSector}");
                _output.WriteLine($"  Bytes per cluster: {volumeInfo.Value.BytesPerCluster}");
                _output.WriteLine($"  Total size: {volumeInfo.Value.TotalSizeBytes / 1024 / 1024 / 1024:N0} GB");
                _output.WriteLine($"  Free space: {volumeInfo.Value.FreeSizeBytes / 1024 / 1024 / 1024:N0} GB");
                _output.WriteLine($"  Est. MFT records: {volumeInfo.Value.EstimatedMftRecordCount:N0}");
            }
            else
            {
                _output.WriteLine($"Volume {drive}: Could not retrieve info");
            }
        }
    }
}
