using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Buffer size optimization tests for MFT enumeration.
/// Tests various buffer sizes to determine optimal configuration.
/// </summary>
[Trait("Category", "Performance")]
[Trait("Suite", "MFT")]
[SupportedOSPlatform("windows")]
public class MftBufferSizeTests
{
    private readonly ITestOutputHelper _output;

    // Buffer size candidates
    private const int BUFFER_64KB = 64 * 1024;       // Current default
    private const int BUFFER_256KB = 256 * 1024;    // 4x current
    private const int BUFFER_1MB = 1024 * 1024;     // 16x current
    private const int BUFFER_4MB = 4 * 1024 * 1024; // 64x current

    // Test parameters
    private const int WARMUP_ITERATIONS = 2;
    private const int TEST_ITERATIONS = 5;

    public MftBufferSizeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BufferSize_SystemInfo_ReportsCapabilities()
    {
        // Report system information relevant to buffer optimization
        var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var processorCount = Environment.ProcessorCount;

        _output.WriteLine("=== System Information ===");
        _output.WriteLine($"Total Available Memory: {totalMemory / (1024 * 1024):N0} MB");
        _output.WriteLine($"Processor Count: {processorCount}");
        _output.WriteLine($"64-bit Process: {Environment.Is64BitProcess}");
        _output.WriteLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        _output.WriteLine($"OS Version: {Environment.OSVersion}");
        _output.WriteLine("");

        _output.WriteLine("=== Buffer Size Candidates ===");
        _output.WriteLine($"64 KB:  {BUFFER_64KB:N0} bytes (current default)");
        _output.WriteLine($"256 KB: {BUFFER_256KB:N0} bytes");
        _output.WriteLine($"1 MB:   {BUFFER_1MB:N0} bytes");
        _output.WriteLine($"4 MB:   {BUFFER_4MB:N0} bytes");
        _output.WriteLine("");

        // Basic sanity check
        totalMemory.Should().BeGreaterThan(0);
        processorCount.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(BUFFER_64KB)]
    [InlineData(BUFFER_256KB)]
    [InlineData(BUFFER_1MB)]
    [InlineData(BUFFER_4MB)]
    public void BufferSize_AllocationTest_CanAllocate(int bufferSize)
    {
        // Verify we can allocate buffers of each size
        var sw = Stopwatch.StartNew();
        var buffer = new byte[bufferSize];
        sw.Stop();

        _output.WriteLine($"Buffer Size: {bufferSize / 1024} KB");
        _output.WriteLine($"Allocation Time: {sw.Elapsed.TotalMicroseconds:F2} us");

        buffer.Length.Should().Be(bufferSize);
    }

    [Theory]
    [InlineData(BUFFER_64KB)]
    [InlineData(BUFFER_256KB)]
    [InlineData(BUFFER_1MB)]
    [InlineData(BUFFER_4MB)]
    public void BufferSize_ArrayPool_RentAndReturn(int bufferSize)
    {
        // Test ArrayPool performance for different buffer sizes
        var pool = System.Buffers.ArrayPool<byte>.Shared;

        // Warmup
        for (int i = 0; i < WARMUP_ITERATIONS; i++)
        {
            var buf = pool.Rent(bufferSize);
            pool.Return(buf);
        }

        // Measure
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < TEST_ITERATIONS * 100; i++)
        {
            var buf = pool.Rent(bufferSize);
            pool.Return(buf);
        }
        sw.Stop();

        var avgTimeUs = sw.Elapsed.TotalMicroseconds / (TEST_ITERATIONS * 100);

        _output.WriteLine($"Buffer Size: {bufferSize / 1024} KB");
        _output.WriteLine($"Avg Rent/Return: {avgTimeUs:F3} us");

        avgTimeUs.Should().BeLessThan(100, "ArrayPool operations should be fast");
    }

    [Fact(Skip = "Requires admin rights and real NTFS volume - run manually")]
    public void BufferSize_RealMftEnumeration_ComparePerformance()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Test requires Windows");
            return;
        }

        if (!FastFind.Windows.Mft.MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available (requires admin rights)");
            return;
        }

        var results = new Dictionary<int, (long Records, TimeSpan Time, double Rate)>();
        var driveToTest = 'C';

        // Test each buffer size
        foreach (var bufferSize in new[] { BUFFER_64KB, BUFFER_256KB, BUFFER_1MB, BUFFER_4MB })
        {
            _output.WriteLine($"\nTesting buffer size: {bufferSize / 1024} KB...");

            // For this test, we'd need to modify MftReader to accept buffer size
            // This is a placeholder for the actual implementation
            using var reader = new FastFind.Windows.Mft.MftReader();
            var sw = Stopwatch.StartNew();
            long recordCount = 0;

            // Note: Current MftReader uses fixed 64KB buffer
            // Phase 1.2 implementation will add configurable buffer size
            var enumerator = reader.EnumerateFilesAsync(driveToTest);
            var task = Task.Run(async () =>
            {
                await foreach (var record in enumerator)
                {
                    recordCount++;
                    if (recordCount >= 100000) break; // Limit for test
                }
            });
            task.Wait();

            sw.Stop();
            var rate = recordCount / sw.Elapsed.TotalSeconds;
            results[bufferSize] = (recordCount, sw.Elapsed, rate);

            _output.WriteLine($"  Records: {recordCount:N0}");
            _output.WriteLine($"  Time: {sw.Elapsed.TotalMilliseconds:F2} ms");
            _output.WriteLine($"  Rate: {rate:N0} records/sec");
        }

        // Report comparison
        _output.WriteLine("\n=== BUFFER SIZE COMPARISON ===");
        var baseline = results[BUFFER_64KB].Rate;
        foreach (var (size, (records, time, rate)) in results.OrderBy(r => r.Key))
        {
            var speedup = rate / baseline;
            _output.WriteLine($"{size / 1024,5} KB: {rate,12:N0} rec/s ({speedup:F2}x vs 64KB)");
        }
    }

    [Fact]
    public void BufferSize_MemoryPressure_LargeBufferImpact()
    {
        // Test memory pressure with large buffers
        var gcBefore = GC.GetTotalMemory(true);

        // Allocate multiple large buffers (simulating multi-drive enumeration)
        var buffers = new List<byte[]>();
        var targetSize = BUFFER_4MB;
        var numBuffers = Environment.ProcessorCount;

        for (int i = 0; i < numBuffers; i++)
        {
            buffers.Add(new byte[targetSize]);
        }

        var gcAfter = GC.GetTotalMemory(false);
        var memoryIncrease = gcAfter - gcBefore;
        var expectedIncrease = (long)targetSize * numBuffers;

        _output.WriteLine($"Buffer Size: {targetSize / 1024 / 1024} MB");
        _output.WriteLine($"Num Buffers: {numBuffers}");
        _output.WriteLine($"Expected Memory: {expectedIncrease / (1024 * 1024):N0} MB");
        _output.WriteLine($"Actual Increase: {memoryIncrease / (1024 * 1024):N0} MB");

        // Clean up
        buffers.Clear();
        GC.Collect();

        var gcFinal = GC.GetTotalMemory(true);
        _output.WriteLine($"After Cleanup: {(gcFinal - gcBefore) / (1024 * 1024):N0} MB delta");

        // Memory should have been allocated
        memoryIncrease.Should().BeGreaterThan(0);
    }

    [Fact]
    public void BufferSize_IOCPOverhead_Estimation()
    {
        // Estimate I/O overhead reduction from larger buffers
        // Based on research: ~1-2ms per DeviceIoControl syscall

        const double syscallOverheadMs = 1.5; // Conservative estimate
        const long typicalRecordsPerVolume = 500_000;
        const int avgRecordSize = 100; // bytes per USN record

        _output.WriteLine("=== I/O Overhead Estimation ===");
        _output.WriteLine($"Assumed syscall overhead: {syscallOverheadMs} ms");
        _output.WriteLine($"Typical records per volume: {typicalRecordsPerVolume:N0}");
        _output.WriteLine($"Average record size: {avgRecordSize} bytes");
        _output.WriteLine("");

        foreach (var bufferSize in new[] { BUFFER_64KB, BUFFER_256KB, BUFFER_1MB, BUFFER_4MB })
        {
            var recordsPerBuffer = bufferSize / avgRecordSize;
            var numSyscalls = (double)typicalRecordsPerVolume / recordsPerBuffer;
            var totalOverheadMs = numSyscalls * syscallOverheadMs;

            _output.WriteLine($"{bufferSize / 1024,5} KB: ~{numSyscalls:N0} syscalls, {totalOverheadMs:N0} ms overhead");
        }

        _output.WriteLine("");
        _output.WriteLine("Larger buffers = fewer syscalls = less overhead");
    }

    [Fact]
    public void BufferSize_Recommendation_BasedOnSystemResources()
    {
        // Provide buffer size recommendation based on available resources
        var totalMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var processorCount = Environment.ProcessorCount;

        int recommendedBufferSize;
        string reason;

        if (totalMemoryMb >= 16384) // 16GB+
        {
            recommendedBufferSize = BUFFER_4MB;
            reason = "High memory system - use maximum buffer for best I/O throughput";
        }
        else if (totalMemoryMb >= 8192) // 8GB+
        {
            recommendedBufferSize = BUFFER_1MB;
            reason = "Medium memory system - balanced buffer size";
        }
        else if (totalMemoryMb >= 4096) // 4GB+
        {
            recommendedBufferSize = BUFFER_256KB;
            reason = "Lower memory system - conservative buffer size";
        }
        else
        {
            recommendedBufferSize = BUFFER_64KB;
            reason = "Limited memory - use default buffer size";
        }

        _output.WriteLine("=== BUFFER SIZE RECOMMENDATION ===");
        _output.WriteLine($"System Memory: {totalMemoryMb:N0} MB");
        _output.WriteLine($"Processors: {processorCount}");
        _output.WriteLine($"Recommended: {recommendedBufferSize / 1024} KB");
        _output.WriteLine($"Reason: {reason}");

        recommendedBufferSize.Should().BeGreaterThan(0);
    }
}
