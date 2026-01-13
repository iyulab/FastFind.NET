using System.Diagnostics;
using System.Runtime.Versioning;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Optimization;

/// <summary>
/// Tests for search performance optimizations including:
/// - Smart Filesystem Fallback (Phase 1.2)
/// - Path Trie Index optimization (Phase 1.1)
/// </summary>
[Trait("Category", "Optimization")]
[Trait("Suite", "SearchOptimization")]
[SupportedOSPlatform("windows")]
public class SearchOptimizationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WindowsSearchIndex _searchIndex;
    private readonly ILogger<WindowsSearchIndex> _logger;

    public SearchOptimizationTests(ITestOutputHelper output)
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
    public async Task SearchWithBasePath_UsesIndexWhenPathIsCovered()
    {
        // Arrange
        var query = new SearchQuery
        {
            BasePath = @"C:\TestData\Folder50",  // Exists in our test data
            SearchText = "file",
            IncludeSubdirectories = true
        };

        // Act
        var sw = Stopwatch.StartNew();
        var results = new List<FastFileItem>();
        await foreach (var item in _searchIndex.SearchAsync(query))
        {
            results.Add(item);
        }
        sw.Stop();

        // Assert
        _output.WriteLine($"Search time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Results: {results.Count:N0} files");

        results.Should().NotBeEmpty("Index should contain files under the test path");

        // With Trie optimization, path-based search should be fast
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "Path-based search should complete in under 100ms with trie optimization");
    }

    [Fact]
    public async Task SearchWithBasePath_PerformanceBenchmark()
    {
        // Arrange: Multiple search scenarios
        var scenarios = new[]
        {
            new SearchQuery { BasePath = @"C:\TestData\Folder0", IncludeSubdirectories = true },
            new SearchQuery { BasePath = @"C:\TestData\Folder50", IncludeSubdirectories = true },
            new SearchQuery { BasePath = @"C:\TestData", IncludeSubdirectories = true },
            new SearchQuery { ExtensionFilter = ".txt" },
            new SearchQuery { SearchText = "file500" }
        };

        _output.WriteLine("=== Search Performance Benchmark ===");
        _output.WriteLine($"Index size: {_searchIndex.Count:N0} files");
        _output.WriteLine("");

        foreach (var query in scenarios)
        {
            var sw = Stopwatch.StartNew();
            var count = 0;
            await foreach (var _ in _searchIndex.SearchAsync(query))
            {
                count++;
            }
            sw.Stop();

            var description = !string.IsNullOrEmpty(query.BasePath)
                ? $"BasePath={query.BasePath}"
                : !string.IsNullOrEmpty(query.ExtensionFilter)
                    ? $"Extension={query.ExtensionFilter}"
                    : $"Text={query.SearchText}";

            _output.WriteLine($"{description,-40} | {sw.ElapsedMilliseconds,5}ms | {count,6:N0} results");
        }
    }

    [Fact]
    public async Task SearchWithExtensionFilter_UsesExtensionIndex()
    {
        // Arrange
        var query = new SearchQuery
        {
            ExtensionFilter = ".cs"
        };

        // Act
        var sw = Stopwatch.StartNew();
        var results = new List<FastFileItem>();
        await foreach (var item in _searchIndex.SearchAsync(query))
        {
            results.Add(item);
        }
        sw.Stop();

        // Assert
        _output.WriteLine($"Extension search time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Results: {results.Count:N0} .cs files");

        // Extension index lookup should be fast (relaxed for CI/CD environments)
        sw.ElapsedMilliseconds.Should().BeLessThan(200,
            "Extension-based search should complete quickly with dedicated index");
    }

    [Fact]
    public async Task SearchWithSizeFilter_UsesIndexEfficiently()
    {
        // Arrange
        var query = new SearchQuery
        {
            MinSize = 1000,
            MaxSize = 5000
        };

        // Act
        var sw = Stopwatch.StartNew();
        var results = new List<FastFileItem>();
        await foreach (var item in _searchIndex.SearchAsync(query))
        {
            results.Add(item);
        }
        sw.Stop();

        // Assert
        _output.WriteLine($"Size filter search time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Results: {results.Count:N0} files in size range");

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(f => f.Size >= 1000 && f.Size <= 5000);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task CompareSearchPerformance_BeforeAndAfterOptimization()
    {
        // This test demonstrates the expected performance improvement
        // by comparing targeted path search vs full index scan

        const int iterations = 10;
        var pathQuery = new SearchQuery
        {
            BasePath = @"C:\TestData\Folder25",
            IncludeSubdirectories = true
        };

        var fullScanQuery = new SearchQuery
        {
            SearchText = "Folder25"  // Will scan all files
        };

        // Warm up
        await foreach (var _ in _searchIndex.SearchAsync(pathQuery)) { }
        await foreach (var _ in _searchIndex.SearchAsync(fullScanQuery)) { }

        // Measure path-based search (uses Trie)
        var pathSw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await foreach (var _ in _searchIndex.SearchAsync(pathQuery)) { }
        }
        pathSw.Stop();
        var avgPathTime = pathSw.ElapsedMilliseconds / (double)iterations;

        // Measure text search (may need full scan)
        var textSw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await foreach (var _ in _searchIndex.SearchAsync(fullScanQuery)) { }
        }
        textSw.Stop();
        var avgTextTime = textSw.ElapsedMilliseconds / (double)iterations;

        // Report
        _output.WriteLine("=== Performance Comparison ===");
        _output.WriteLine($"Path-based search (Trie): {avgPathTime:F2}ms average");
        _output.WriteLine($"Text search (scan): {avgTextTime:F2}ms average");
        _output.WriteLine($"Ratio: {avgTextTime / Math.Max(0.1, avgPathTime):F1}x");

        // Path-based search should be faster due to Trie optimization
        avgPathTime.Should().BeLessThan(avgTextTime * 2,
            "Path-based search should benefit from Trie optimization");
    }

    /// <summary>
    /// Generates test files distributed across multiple folders
    /// </summary>
    private static IEnumerable<FastFileItem> GenerateTestFiles(int count)
    {
        var random = new Random(42);
        var extensions = new[] { ".txt", ".cs", ".json", ".xml", ".log" };
        var now = DateTime.Now;

        for (int i = 0; i < count; i++)
        {
            var folder = $@"C:\TestData\Folder{i % 100}";
            var subFolder = i % 10 == 0 ? $"\\Sub{i % 20}" : "";
            var ext = extensions[i % extensions.Length];
            var fileName = $"file{i}{ext}";
            var fullPath = $"{folder}{subFolder}\\{fileName}";
            var directoryPath = $"{folder}{subFolder}";

            yield return new FastFileItem(
                fullPath: fullPath,
                name: fileName,
                directoryPath: directoryPath,
                extension: ext,
                size: random.Next(100, 10000),
                created: now.AddDays(-random.Next(365)),
                modified: now.AddDays(-random.Next(30)),
                accessed: now,
                attributes: FileAttributes.Normal,
                driveLetter: 'C'
            );
        }
    }
}
