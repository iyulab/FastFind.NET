using System.Collections.Concurrent;
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
/// Tests for Phase 3.1: Lock-free read operations.
/// Verifies that concurrent reads work correctly without explicit locking.
/// </summary>
[Trait("Category", "Optimization")]
[Trait("Suite", "LockFree")]
[SupportedOSPlatform("windows")]
public class LockFreeReadTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WindowsSearchIndex _searchIndex;
    private readonly ILogger<WindowsSearchIndex> _logger;

    public LockFreeReadTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = NullLogger<WindowsSearchIndex>.Instance;
        _searchIndex = new WindowsSearchIndex(_logger);
    }

    public async Task InitializeAsync()
    {
        // Pre-populate index with test data
        var testFiles = GenerateTestFiles(5_000);
        await _searchIndex.AddBatchAsync(testFiles);
        _output.WriteLine($"Initialized index with {_searchIndex.Count:N0} files");
    }

    public async Task DisposeAsync()
    {
        await _searchIndex.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentSearches_AllComplete_Successfully()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "Document",
            CaseSensitive = false
        };

        const int concurrentSearches = 10;
        var tasks = new List<Task<int>>();
        var errors = new ConcurrentBag<Exception>();

        // Act - Launch multiple concurrent searches
        for (int i = 0; i < concurrentSearches; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var count = 0;
                    await foreach (var _ in _searchIndex.SearchAsync(query))
                    {
                        count++;
                    }
                    return count;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    return -1;
                }
            }));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        errors.Should().BeEmpty("All concurrent searches should complete without errors");
        results.Should().AllBeEquivalentTo(results[0], "All searches should return consistent results");

        _output.WriteLine($"Concurrent searches: {concurrentSearches}");
        _output.WriteLine($"Results per search: {results[0]}");
    }

    [Fact]
    public async Task ConcurrentReadAndWrite_ReadCompletes_WithoutDeadlock()
    {
        // Arrange
        var readTasks = new List<Task<int>>();
        var writeTasks = new List<Task>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act - Start concurrent reads and writes
        for (int i = 0; i < 5; i++)
        {
            // Read task
            readTasks.Add(Task.Run(async () =>
            {
                var count = 0;
                var query = new SearchQuery { SearchText = "file" };
                await foreach (var _ in _searchIndex.SearchAsync(query, cts.Token))
                {
                    count++;
                    if (count % 100 == 0) await Task.Yield();
                }
                return count;
            }, cts.Token));

            // Write task (add new files)
            var batchNum = i;
            writeTasks.Add(Task.Run(async () =>
            {
                var newFiles = Enumerable.Range(0, 100)
                    .Select(j => CreateTestFile($"NewFile_{batchNum}_{j}.txt"))
                    .ToList();
                await _searchIndex.AddBatchAsync(newFiles, cts.Token);
            }, cts.Token));
        }

        // Wait for all tasks
        await Task.WhenAll(writeTasks);
        var readResults = await Task.WhenAll(readTasks);

        // Assert
        readResults.Should().OnlyContain(r => r >= 0, "All reads should complete successfully");
        _output.WriteLine($"Reads completed: {readResults.Length}");
        _output.WriteLine($"Final index size: {_searchIndex.Count:N0}");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task ParallelSearches_Performance_Benchmark()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "test",
            CaseSensitive = false
        };

        var parallelTasks = Math.Max(4, Environment.ProcessorCount);
        const int iterationsPerTask = 20;

        // Warmup
        await foreach (var item in _searchIndex.SearchAsync(query)) { _ = item; }

        // Act - Measure parallel search performance
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, parallelTasks).Select(async taskNum =>
        {
            var totalResults = 0;
            for (int i = 0; i < iterationsPerTask; i++)
            {
                await foreach (var result in _searchIndex.SearchAsync(query))
                {
                    totalResults++;
                }
            }
            return totalResults;
        });

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        // Report
        var totalSearches = parallelTasks * iterationsPerTask;
        var searchesPerSecond = totalSearches / sw.Elapsed.TotalSeconds;

        _output.WriteLine("=== Parallel Search Performance ===");
        _output.WriteLine($"Parallel tasks: {parallelTasks}");
        _output.WriteLine($"Iterations per task: {iterationsPerTask}");
        _output.WriteLine($"Total searches: {totalSearches}");
        _output.WriteLine($"Total time: {sw.Elapsed.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Searches/sec: {searchesPerSecond:F1}");
        _output.WriteLine($"Index size: {_searchIndex.Count:N0}");

        // Assert - Should handle parallel searches without deadlock
        // Performance varies by system - just verify it completes reasonably
        searchesPerSecond.Should().BeGreaterThan(1,
            "Parallel searches should complete without deadlock");
    }

    [Fact]
    public async Task GetAsync_Concurrent_ReturnsConsistentResults()
    {
        // Arrange
        var testPath = @"C:\TestData\Folder0\Document_00000.txt";

        // First, verify the file exists in index
        var exists = await _searchIndex.ContainsAsync(testPath);
        exists.Should().BeTrue("Test file should exist in index");

        const int concurrentReads = 100;
        var tasks = new List<Task<FastFileItem?>>();

        // Act - Multiple concurrent GetAsync calls
        for (int i = 0; i < concurrentReads; i++)
        {
            tasks.Add(_searchIndex.GetAsync(testPath));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - All should return the same item
        var validResults = results.Where(r => r.HasValue).Select(r => r!.Value).ToArray();
        validResults.Should().HaveCount(concurrentReads);
        validResults.Should().AllBeEquivalentTo(validResults[0],
            "All concurrent reads should return identical results");
    }

    [Fact]
    public async Task ContainsAsync_Concurrent_AllSucceed()
    {
        // Arrange
        var testPaths = Enumerable.Range(0, 100)
            .Select(i => $@"C:\TestData\Folder{i % 50}\Document_{i:D5}.txt")
            .ToArray();

        // Act - Concurrent ContainsAsync calls
        var tasks = testPaths.Select(path => _searchIndex.ContainsAsync(path));
        var results = await Task.WhenAll(tasks);

        // Assert
        _output.WriteLine($"ContainsAsync calls: {results.Length}");
        _output.WriteLine($"Found: {results.Count(r => r)}");

        // All should complete without exception
        results.Should().NotBeEmpty();
    }

    #region Helper Methods

    private static IEnumerable<FastFileItem> GenerateTestFiles(int count)
    {
        var random = new Random(42);
        var prefixes = new[] { "Document", "Report", "Data", "Config", "Test" };
        var extensions = new[] { ".txt", ".cs", ".json", ".xml", ".log" };
        var now = DateTime.Now;

        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[i % prefixes.Length];
            var ext = extensions[i % extensions.Length];
            var fileName = $"{prefix}_{i:D5}{ext}";
            var folder = $@"C:\TestData\Folder{i % 50}";
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

    private static FastFileItem CreateTestFile(string fileName)
    {
        var now = DateTime.Now;
        var folder = @"C:\TestData\NewFiles";
        return new FastFileItem(
            fullPath: $@"{folder}\{fileName}",
            name: fileName,
            directoryPath: folder,
            extension: Path.GetExtension(fileName),
            size: 1000,
            created: now,
            modified: now,
            accessed: now,
            attributes: FileAttributes.Normal,
            driveLetter: 'C'
        );
    }

    #endregion
}
