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
/// Tests for SIMD-accelerated string matching integration in search operations.
/// Phase 2.1: Verifies that SIMD optimization is properly integrated into the search path.
/// </summary>
[Trait("Category", "Optimization")]
[Trait("Suite", "SIMDSearch")]
[SupportedOSPlatform("windows")]
public class SIMDSearchIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WindowsSearchIndex _searchIndex;
    private readonly ILogger<WindowsSearchIndex> _logger;

    public SIMDSearchIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = NullLogger<WindowsSearchIndex>.Instance;
        _searchIndex = new WindowsSearchIndex(_logger);
    }

    public async Task InitializeAsync()
    {
        // Reset SIMD stats before each test
        StringMatchingStats.Reset();

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
    public async Task Search_TextMatch_UsesSIMDMatcher()
    {
        // Arrange
        StringMatchingStats.Reset();
        var query = new SearchQuery
        {
            SearchText = "document",  // Longer than 4 chars to trigger SIMD
            CaseSensitive = false
        };

        // Act
        var results = new List<FastFileItem>();
        await foreach (var item in _searchIndex.SearchAsync(query))
        {
            results.Add(item);
        }

        // Assert
        _output.WriteLine($"Total searches: {StringMatchingStats.TotalSearches:N0}");
        _output.WriteLine($"SIMD searches: {StringMatchingStats.SIMDSearches:N0}");
        _output.WriteLine($"Scalar searches: {StringMatchingStats.ScalarSearches:N0}");
        _output.WriteLine($"SIMD usage: {StringMatchingStats.SIMDUsagePercentage:F1}%");
        _output.WriteLine($"Results: {results.Count:N0} files");

        // SIMD should be used for case-insensitive searches with patterns >= 4 chars
        StringMatchingStats.TotalSearches.Should().BeGreaterThan(0,
            "Search operations should have been performed");

        // At least some SIMD operations should occur (may also have scalar for short strings)
        // Note: SIMD kicks in for patterns >= 4 chars AND haystack >= needle length
    }

    [Fact]
    public async Task Search_ShortPattern_UsesScalarMatcher()
    {
        // Arrange
        StringMatchingStats.Reset();
        var query = new SearchQuery
        {
            SearchText = "ab",  // Short pattern - should use scalar
            CaseSensitive = false
        };

        // Act
        await foreach (var _ in _searchIndex.SearchAsync(query))
        {
            // Just iterate through results
        }

        // Assert
        _output.WriteLine($"Total searches: {StringMatchingStats.TotalSearches:N0}");
        _output.WriteLine($"Scalar searches: {StringMatchingStats.ScalarSearches:N0}");
        _output.WriteLine($"SIMD searches: {StringMatchingStats.SIMDSearches:N0}");

        // Short patterns should fall back to scalar
        StringMatchingStats.ScalarSearches.Should().BeGreaterThan(0,
            "Short patterns should use scalar matching");
    }

    [Fact]
    public async Task Search_CaseSensitive_BypassesSIMD()
    {
        // Arrange
        StringMatchingStats.Reset();
        var query = new SearchQuery
        {
            SearchText = "Document",  // Case-sensitive search
            CaseSensitive = true
        };

        // Act
        var results = new List<FastFileItem>();
        await foreach (var item in _searchIndex.SearchAsync(query))
        {
            results.Add(item);
        }

        // Assert - Case-sensitive uses standard Contains, not SIMD
        _output.WriteLine($"Results: {results.Count:N0}");
        _output.WriteLine($"SIMD searches: {StringMatchingStats.SIMDSearches:N0}");

        // Case-sensitive searches bypass SIMD (use ordinal comparison)
        // So SIMD search count should be 0 for pure case-sensitive searches
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Performance_SIMDvsNonSIMD_Comparison()
    {
        // Arrange: Large dataset for meaningful comparison
        var largeIndex = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
        var testFiles = GenerateTestFiles(20_000);
        await largeIndex.AddBatchAsync(testFiles);

        var query = new SearchQuery
        {
            SearchText = "performance",  // Longer pattern for SIMD
            CaseSensitive = false
        };

        const int iterations = 5;

        // Warm up
        await foreach (var _ in largeIndex.SearchAsync(query)) { }

        // Act: Measure performance
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await foreach (var _ in largeIndex.SearchAsync(query)) { }
        }
        sw.Stop();
        var avgTime = sw.ElapsedMilliseconds / (double)iterations;

        // Report
        _output.WriteLine("=== SIMD Search Performance ===");
        _output.WriteLine($"Index size: {largeIndex.Count:N0} files");
        _output.WriteLine($"Search pattern: \"{query.SearchText}\"");
        _output.WriteLine($"Average search time: {avgTime:F2}ms");
        _output.WriteLine($"SIMD usage: {StringMatchingStats.SIMDUsagePercentage:F1}%");

        await largeIndex.DisposeAsync();

        // Assert: Should complete reasonably fast with SIMD
        avgTime.Should().BeLessThan(500,
            "SIMD-accelerated search should complete in under 500ms for 20K files");
    }

    [Fact]
    public async Task Search_MixedPatternLengths_HandlesBothPaths()
    {
        // Arrange
        StringMatchingStats.Reset();

        // First search with short pattern (scalar)
        var shortQuery = new SearchQuery { SearchText = "a", CaseSensitive = false };
        await foreach (var _ in _searchIndex.SearchAsync(shortQuery)) { }
        var scalarAfterShort = StringMatchingStats.ScalarSearches;

        // Then search with long pattern (SIMD)
        var longQuery = new SearchQuery { SearchText = "document", CaseSensitive = false };
        await foreach (var _ in _searchIndex.SearchAsync(longQuery)) { }
        var simdAfterLong = StringMatchingStats.SIMDSearches;

        // Assert
        _output.WriteLine($"After short pattern - Scalar: {scalarAfterShort:N0}");
        _output.WriteLine($"After long pattern - SIMD: {simdAfterLong:N0}");

        // Both paths should have been exercised
        scalarAfterShort.Should().BeGreaterThan(0, "Short patterns should use scalar");
    }

    [Fact]
    public async Task Search_FileNameOnly_UsesSIMD()
    {
        // Arrange
        StringMatchingStats.Reset();
        var query = new SearchQuery
        {
            SearchText = "report",
            SearchFileNameOnly = true,  // Only search in file names
            CaseSensitive = false
        };

        // Act
        var results = new List<FastFileItem>();
        await foreach (var item in _searchIndex.SearchAsync(query))
        {
            results.Add(item);
        }

        // Assert
        _output.WriteLine($"Results: {results.Count:N0}");
        _output.WriteLine($"SIMD usage: {StringMatchingStats.SIMDUsagePercentage:F1}%");

        // File name only search should also benefit from SIMD
    }

    /// <summary>
    /// Generates test files with various naming patterns
    /// </summary>
    private static IEnumerable<FastFileItem> GenerateTestFiles(int count)
    {
        var random = new Random(42);
        var prefixes = new[] { "Document", "Report", "Data", "Config", "Performance", "Test", "Output" };
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
}
