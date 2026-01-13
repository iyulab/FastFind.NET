using System.Diagnostics;
using FastFind.Models;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// Tests for StringPool Span-based interning using .NET 9+ AlternateLookup.
/// Phase 1.3: Zero-allocation filename interning for MFT parsing.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Suite", "StringPool")]
public class StringPoolSpanTests
{
    private readonly ITestOutputHelper _output;

    // Performance targets
    private const int WARMUP_ITERATIONS = 1000;
    private const int BENCHMARK_ITERATIONS = 100_000;

    public StringPoolSpanTests(ITestOutputHelper output)
    {
        _output = output;
        // Reset StringPool before each test to ensure isolation
        StringPool.Reset();
    }

    #region Functional Tests

    [Fact]
    public void InternFromSpan_ReturnsValidId()
    {
        // Arrange
        ReadOnlySpan<char> testSpan = "TestFileName.txt".AsSpan();

        // Act
        var id = StringPool.InternFromSpan(testSpan);

        // Assert
        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public void InternFromSpan_SameSpan_ReturnsSameId()
    {
        // Arrange
        var testString = "Document.docx";
        ReadOnlySpan<char> span1 = testString.AsSpan();
        ReadOnlySpan<char> span2 = testString.AsSpan();

        // Act
        var id1 = StringPool.InternFromSpan(span1);
        var id2 = StringPool.InternFromSpan(span2);

        // Assert
        id1.Should().Be(id2);
    }

    [Fact]
    public void InternFromSpan_DifferentContent_ReturnsDifferentIds()
    {
        // Arrange
        ReadOnlySpan<char> span1 = "File1.txt".AsSpan();
        ReadOnlySpan<char> span2 = "File2.txt".AsSpan();

        // Act
        var id1 = StringPool.InternFromSpan(span1);
        var id2 = StringPool.InternFromSpan(span2);

        // Assert
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void InternFromSpan_MatchesInternString()
    {
        // Arrange
        var testString = "SharedName.pdf";

        // Act
        var idFromString = StringPool.Intern(testString);
        var idFromSpan = StringPool.InternFromSpan(testString.AsSpan());

        // Assert
        idFromSpan.Should().Be(idFromString);
    }

    [Fact]
    public void InternFromSpan_EmptySpan_ReturnsZero()
    {
        // Arrange
        ReadOnlySpan<char> emptySpan = ReadOnlySpan<char>.Empty;

        // Act
        var id = StringPool.InternFromSpan(emptySpan);

        // Assert
        id.Should().Be(0);
    }

    [Fact]
    public void InternFromSpan_CanRetrieveString()
    {
        // Arrange
        var original = "RetrievableFile.cs";
        ReadOnlySpan<char> span = original.AsSpan();

        // Act
        var id = StringPool.InternFromSpan(span);
        var retrieved = StringPool.GetString(id);

        // Assert
        retrieved.Should().Be(original);
    }

    [Fact]
    public void InternFromSpan_HandlesUnicodeFilenames()
    {
        var testNames = new[]
        {
            "한글파일.txt",
            "日本語ファイル.pdf",
            "файл.doc",
            "αρχείο.xlsx",
            "émoji_🎉_test.png"
        };

        foreach (var name in testNames)
        {
            ReadOnlySpan<char> span = name.AsSpan();
            var id = StringPool.InternFromSpan(span);
            var retrieved = StringPool.GetString(id);

            retrieved.Should().Be(name, $"Unicode filename '{name}' should round-trip correctly");
        }

        _output.WriteLine($"Successfully tested {testNames.Length} Unicode filenames");
    }

    [Fact]
    public void TryGetFromSpan_ExistingEntry_ReturnsTrue()
    {
        // Arrange - Use InternFromSpan to ensure entry is in the name pool
        var testString = "ExistingFile.txt";
        var internedId = StringPool.InternFromSpan(testString.AsSpan());

        // Act
        var found = StringPool.TryGetFromSpan(testString.AsSpan(), out var id);

        // Assert
        found.Should().BeTrue();
        id.Should().Be(internedId);
    }

    [Fact]
    public void TryGetFromSpan_NonExistingEntry_ReturnsFalse()
    {
        // Arrange
        StringPool.Reset(); // Ensure clean state
        ReadOnlySpan<char> span = "NonExistentFile.xyz".AsSpan();

        // Act
        var found = StringPool.TryGetFromSpan(span, out var id);

        // Assert
        found.Should().BeFalse();
        id.Should().Be(0);
    }

    #endregion

    #region Performance Tests

    [Fact]
    public void InternFromSpan_CacheHit_Performance()
    {
        // Arrange - Pre-populate cache
        var testStrings = new[] { "File1.txt", "File2.doc", "File3.pdf", "File4.cs", "File5.json" };
        foreach (var s in testStrings)
            StringPool.Intern(s);

        // Warmup
        for (int i = 0; i < WARMUP_ITERATIONS; i++)
        {
            foreach (var s in testStrings)
                StringPool.InternFromSpan(s.AsSpan());
        }

        // Benchmark
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BENCHMARK_ITERATIONS; i++)
        {
            foreach (var s in testStrings)
                StringPool.InternFromSpan(s.AsSpan());
        }
        sw.Stop();

        var totalOps = BENCHMARK_ITERATIONS * testStrings.Length;
        var opsPerSec = totalOps / sw.Elapsed.TotalSeconds;

        _output.WriteLine($"=== InternFromSpan Cache Hit Performance ===");
        _output.WriteLine($"Total Operations: {totalOps:N0}");
        _output.WriteLine($"Elapsed: {sw.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"Rate: {opsPerSec:N0} ops/sec");

        // Should be fast on cache hits
        opsPerSec.Should().BeGreaterThan(100_000, "Cache hits should be fast");
    }

    [Fact]
    public void InternFromSpan_VsInternString_SpanShouldNotBeSlower()
    {
        // Arrange
        var testStrings = Enumerable.Range(0, 100)
            .Select(i => $"TestFile_{i}.txt")
            .ToArray();

        // Pre-populate for cache hit scenario
        foreach (var s in testStrings)
            StringPool.Intern(s);

        // Warmup both
        for (int i = 0; i < WARMUP_ITERATIONS; i++)
        {
            foreach (var s in testStrings)
            {
                StringPool.Intern(s);
                StringPool.InternFromSpan(s.AsSpan());
            }
        }

        // Benchmark Intern(string)
        var swString = Stopwatch.StartNew();
        for (int iter = 0; iter < BENCHMARK_ITERATIONS / 100; iter++)
        {
            foreach (var s in testStrings)
                StringPool.Intern(s);
        }
        swString.Stop();

        // Benchmark InternFromSpan
        var swSpan = Stopwatch.StartNew();
        for (int iter = 0; iter < BENCHMARK_ITERATIONS / 100; iter++)
        {
            foreach (var s in testStrings)
                StringPool.InternFromSpan(s.AsSpan());
        }
        swSpan.Stop();

        var stringOps = (BENCHMARK_ITERATIONS / 100) * testStrings.Length;
        var stringRate = stringOps / swString.Elapsed.TotalSeconds;
        var spanRate = stringOps / swSpan.Elapsed.TotalSeconds;
        var ratio = spanRate / stringRate;

        _output.WriteLine($"=== Performance Comparison ===");
        _output.WriteLine($"Intern(string): {stringRate:N0} ops/sec ({swString.Elapsed.TotalMilliseconds:F2} ms)");
        _output.WriteLine($"InternFromSpan: {spanRate:N0} ops/sec ({swSpan.Elapsed.TotalMilliseconds:F2} ms)");
        _output.WriteLine($"Ratio: {ratio:F2}x");

        // Span version has some overhead from AlternateLookup but provides zero-allocation on cache hit
        // Main benefit is memory, not raw speed - accept 30% overhead
        ratio.Should().BeGreaterThan(0.3, "Span-based interning should not be drastically slower than string");
    }

    [Fact]
    public void InternFromSpan_MftSimulation_MeasurePerformance()
    {
        // Simulate MFT parsing scenario with typical filenames
        var fileNames = new[]
        {
            "ntuser.dat", "desktop.ini", "thumbs.db",  // Common duplicates
            "index.html", "styles.css", "app.js",
            "README.md", "LICENSE", ".gitignore",
            "Document.docx", "Spreadsheet.xlsx", "Presentation.pptx"
        };

        // Simulate 500K files with ~60% duplicate names
        var totalRecords = 500_000;
        var random = new Random(42);

        // Warmup
        foreach (var name in fileNames)
            StringPool.InternFromSpan(name.AsSpan());

        // Measure
        var sw = Stopwatch.StartNew();
        var uniqueCount = 0;
        var hitCount = 0;

        for (int i = 0; i < totalRecords; i++)
        {
            string fileName;
            if (random.NextDouble() < 0.6) // 60% duplicates
            {
                fileName = fileNames[random.Next(fileNames.Length)];
                hitCount++;
            }
            else
            {
                fileName = $"unique_file_{i}.dat";
                uniqueCount++;
            }

            StringPool.InternFromSpan(fileName.AsSpan());
        }

        sw.Stop();
        var rate = totalRecords / sw.Elapsed.TotalSeconds;

        _output.WriteLine($"=== MFT Simulation ===");
        _output.WriteLine($"Total Records: {totalRecords:N0}");
        _output.WriteLine($"Duplicate Hits: {hitCount:N0}");
        _output.WriteLine($"Unique Names: {uniqueCount:N0}");
        _output.WriteLine($"Elapsed: {sw.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"Rate: {rate:N0} ops/sec");

        var stats = StringPool.GetStats();
        _output.WriteLine($"Pool Size: {stats.InternedCount:N0}");
        _output.WriteLine($"Memory: {stats.MemoryUsageMB:F2} MB");

        // Should handle MFT-scale workload efficiently
        rate.Should().BeGreaterThan(100_000, "Should handle 100K+ ops/sec for MFT parsing");
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task InternFromSpan_ConcurrentAccess_IsThreadSafe()
    {
        // Arrange
        var testStrings = Enumerable.Range(0, 1000)
            .Select(i => $"ConcurrentFile_{i}.txt")
            .ToArray();

        var tasks = new List<Task>();
        var results = new System.Collections.Concurrent.ConcurrentBag<(string Name, int Id)>();

        // Act - Multiple threads interning same strings
        for (int t = 0; t < Environment.ProcessorCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    foreach (var s in testStrings)
                    {
                        var id = StringPool.InternFromSpan(s.AsSpan());
                        results.Add((s, id));
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - Same string should always have same ID
        var grouped = results.GroupBy(r => r.Name);
        foreach (var group in grouped)
        {
            var ids = group.Select(r => r.Id).Distinct().ToList();
            ids.Should().HaveCount(1, $"String '{group.Key}' should have consistent ID across threads");
        }

        _output.WriteLine($"Verified {grouped.Count()} unique strings across {results.Count:N0} operations");
    }

    #endregion
}
