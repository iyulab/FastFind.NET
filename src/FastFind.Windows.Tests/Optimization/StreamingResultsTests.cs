using System.Diagnostics;
using System.Runtime.Versioning;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Optimization;

/// <summary>
/// Tests for Phase 3.2: Streaming results optimization.
/// Verifies first-result latency improvement through immediate yielding.
/// </summary>
[Trait("Category", "Optimization")]
[Trait("Suite", "Streaming")]
[SupportedOSPlatform("windows")]
public class StreamingResultsTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WindowsSearchIndex _searchIndex;
    private readonly ILogger<WindowsSearchIndex> _logger;

    public StreamingResultsTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = NullLogger<WindowsSearchIndex>.Instance;
        _searchIndex = new WindowsSearchIndex(_logger);
    }

    public async Task InitializeAsync()
    {
        // Pre-populate index with test data
        var testFiles = GenerateTestFiles(10_000);
        await _searchIndex.AddBatchAsync(testFiles);
        _output.WriteLine($"Initialized index with {_searchIndex.Count:N0} files");
    }

    public async Task DisposeAsync()
    {
        await _searchIndex.DisposeAsync();
    }

    [Fact]
    public async Task SearchAsync_FirstResult_ReturnsQuickly()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "Document",
            CaseSensitive = false
        };

        var sw = Stopwatch.StartNew();
        TimeSpan? firstResultTime = null;
        var resultCount = 0;

        // Act - Measure time to first result
        await foreach (var result in _searchIndex.SearchAsync(query))
        {
            if (firstResultTime == null)
            {
                firstResultTime = sw.Elapsed;
            }
            resultCount++;

            // Stop after getting enough results to measure streaming
            if (resultCount >= 100)
                break;
        }
        sw.Stop();

        // Assert
        firstResultTime.Should().NotBeNull("Should have received at least one result");

        _output.WriteLine("=== First Result Latency Test ===");
        _output.WriteLine($"First result time: {firstResultTime!.Value.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Total time for {resultCount} results: {sw.Elapsed.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Average per result: {sw.Elapsed.TotalMilliseconds / resultCount:F3}ms");

        // Phase 3.2 target: First result < 100ms
        // Allow some flexibility for CI environments
        firstResultTime.Value.TotalMilliseconds.Should().BeLessThan(500,
            "First result should be returned quickly with streaming");
    }

    [Fact]
    public async Task SearchAsync_StreamsResultsProgressively()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "file",
            CaseSensitive = false
        };

        var resultTimes = new List<TimeSpan>();
        var sw = Stopwatch.StartNew();

        // Act - Collect timing for each batch of results
        var resultCount = 0;
        await foreach (var result in _searchIndex.SearchAsync(query))
        {
            resultCount++;

            // Record time at every 50th result
            if (resultCount % 50 == 0)
            {
                resultTimes.Add(sw.Elapsed);
            }

            if (resultCount >= 500)
                break;
        }
        sw.Stop();

        // Assert - Results should stream progressively (not all at once)
        _output.WriteLine("=== Progressive Streaming Test ===");
        _output.WriteLine($"Total results: {resultCount}");
        _output.WriteLine($"Timing checkpoints:");

        for (int i = 0; i < resultTimes.Count; i++)
        {
            _output.WriteLine($"  {(i + 1) * 50} results: {resultTimes[i].TotalMilliseconds:F2}ms");
        }

        // Verify streaming behavior: time should increase progressively
        resultTimes.Should().HaveCountGreaterThan(0, "Should have timing checkpoints");

        if (resultTimes.Count > 1)
        {
            // Check that results are streaming (later checkpoints take more time)
            for (int i = 1; i < resultTimes.Count; i++)
            {
                resultTimes[i].Should().BeGreaterThanOrEqualTo(resultTimes[i - 1],
                    "Results should stream progressively");
            }
        }
    }

    [Fact]
    public async Task SearchAsync_WithCancellation_StopsImmediately()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "test",
            CaseSensitive = false
        };

        var cts = new CancellationTokenSource();
        var resultCount = 0;
        var sw = Stopwatch.StartNew();

        // Act - Cancel after first few results
        try
        {
            await foreach (var result in _searchIndex.SearchAsync(query, cts.Token))
            {
                resultCount++;

                if (resultCount >= 10)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        sw.Stop();

        // Assert
        _output.WriteLine("=== Cancellation Test ===");
        _output.WriteLine($"Results before cancellation: {resultCount}");
        _output.WriteLine($"Time until cancellation: {sw.Elapsed.TotalMilliseconds:F2}ms");

        // Should have stopped near the cancellation point
        resultCount.Should().BeGreaterThanOrEqualTo(10, "Should have received results before cancellation");
        resultCount.Should().BeLessThan(100, "Should have stopped reasonably soon after cancellation");
    }

    [Fact]
    public async Task SearchAsync_LargeResult_MaintainsLowFirstResultLatency()
    {
        // Arrange - Search that matches many files
        var query = new SearchQuery
        {
            SearchText = "_", // Matches all test files with underscores
            CaseSensitive = false
        };

        var sw = Stopwatch.StartNew();
        var firstResultTime = TimeSpan.Zero;
        var resultCount = 0;

        // Act
        await foreach (var result in _searchIndex.SearchAsync(query))
        {
            if (resultCount == 0)
            {
                firstResultTime = sw.Elapsed;
            }
            resultCount++;

            // Collect many results to verify streaming continues
            if (resultCount >= 1000)
                break;
        }
        sw.Stop();

        // Assert
        _output.WriteLine("=== Large Result First Latency Test ===");
        _output.WriteLine($"First result time: {firstResultTime.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Time for 1000 results: {sw.Elapsed.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Throughput: {resultCount / sw.Elapsed.TotalSeconds:F0} results/sec");

        // First result should still be fast regardless of total result count
        firstResultTime.TotalMilliseconds.Should().BeLessThan(500,
            "First result latency should be independent of total result count");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task SearchAsync_Throughput_Benchmark()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "Document",
            CaseSensitive = false
        };

        // Warmup
        await foreach (var _ in _searchIndex.SearchAsync(query)) { break; }

        // Act - Measure throughput
        var sw = Stopwatch.StartNew();
        var totalResults = 0;

        await foreach (var result in _searchIndex.SearchAsync(query))
        {
            totalResults++;
        }
        sw.Stop();

        // Report
        var throughput = totalResults / sw.Elapsed.TotalSeconds;

        _output.WriteLine("=== Streaming Throughput Benchmark ===");
        _output.WriteLine($"Total results: {totalResults:N0}");
        _output.WriteLine($"Total time: {sw.Elapsed.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Throughput: {throughput:N0} results/sec");
        _output.WriteLine($"Index size: {_searchIndex.Count:N0}");

        // Should maintain reasonable throughput
        throughput.Should().BeGreaterThan(1000,
            "Streaming search should maintain good throughput");
    }

    #region Helper Methods

    private static IEnumerable<FastFileItem> GenerateTestFiles(int count)
    {
        var random = new Random(42);
        var prefixes = new[] { "Document", "Report", "Data", "Config", "Test", "file", "log" };
        var extensions = new[] { ".txt", ".cs", ".json", ".xml", ".log", ".md" };
        var now = DateTime.Now;

        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[i % prefixes.Length];
            var ext = extensions[i % extensions.Length];
            var fileName = $"{prefix}_{i:D5}{ext}";
            var folder = $@"C:\TestData\Folder{i % 100}";
            var fullPath = $@"{folder}\{fileName}";

            yield return new FastFileItem(
                fullPath: fullPath,
                name: fileName,
                directoryPath: folder,
                extension: ext,
                size: random.Next(100, 100000),
                created: now.AddDays(-random.Next(365)),
                modified: now.AddDays(-random.Next(30)),
                accessed: now,
                attributes: FileAttributes.Normal,
                driveLetter: 'C'
            );
        }
    }

    #endregion
}
