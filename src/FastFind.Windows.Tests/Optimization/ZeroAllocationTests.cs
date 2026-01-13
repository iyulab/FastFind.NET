using System.Diagnostics;
using FastFind.Models;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Optimization;

/// <summary>
/// Tests for Phase 2.2: Zero-allocation pattern matching optimizations.
/// Verifies StringPool.GetSpan and GC pressure reduction.
/// </summary>
[Trait("Category", "Optimization")]
[Trait("Suite", "ZeroAllocation")]
[Collection("StringPool")]
public class ZeroAllocationTests
{
    private readonly ITestOutputHelper _output;

    public ZeroAllocationTests(ITestOutputHelper output)
    {
        _output = output;
        StringPool.Reset();
    }

    #region GetSpan Functional Tests

    [Fact]
    public void GetSpan_ValidId_ReturnsCorrectSpan()
    {
        // Arrange
        var testString = "TestFileName.txt";
        var id = StringPool.Intern(testString);

        // Act
        var span = StringPool.GetSpan(id);

        // Assert
        span.ToString().Should().Be(testString);
    }

    [Fact]
    public void GetSpan_ZeroId_ReturnsEmpty()
    {
        // Act
        var span = StringPool.GetSpan(0);

        // Assert
        span.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void GetSpan_InvalidId_ReturnsEmpty()
    {
        // Act
        var span = StringPool.GetSpan(999999);

        // Assert
        span.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void GetSpan_MultipleStrings_ReturnsCorrectSpans()
    {
        // Arrange
        var strings = new[] { "file1.txt", "file2.doc", "file3.pdf" };
        var ids = strings.Select(s => StringPool.Intern(s)).ToArray();

        // Act & Assert
        for (int i = 0; i < strings.Length; i++)
        {
            var span = StringPool.GetSpan(ids[i]);
            span.ToString().Should().Be(strings[i]);
        }
    }

    #endregion

    #region GetMemory Functional Tests

    [Fact]
    public void GetMemory_ValidId_ReturnsCorrectMemory()
    {
        // Arrange
        var testString = "AsyncTestFile.txt";
        var id = StringPool.Intern(testString);

        // Act
        var memory = StringPool.GetMemory(id);

        // Assert
        memory.ToString().Should().Be(testString);
    }

    [Fact]
    public void GetMemory_ZeroId_ReturnsEmpty()
    {
        // Act
        var memory = StringPool.GetMemory(0);

        // Assert
        memory.IsEmpty.Should().BeTrue();
    }

    #endregion

    #region TryGetSpan Functional Tests

    [Fact]
    public void TryGetSpan_ValidId_ReturnsTrueAndCorrectSpan()
    {
        // Arrange
        var testString = "TryGetTest.txt";
        var id = StringPool.Intern(testString);

        // Act
        var result = StringPool.TryGetSpan(id, out var span);

        // Assert
        result.Should().BeTrue();
        span.ToString().Should().Be(testString);
    }

    [Fact]
    public void TryGetSpan_InvalidId_ReturnsFalse()
    {
        // Act
        var result = StringPool.TryGetSpan(999999, out var span);

        // Assert
        result.Should().BeFalse();
        span.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryGetSpan_ZeroId_ReturnsTrueWithEmpty()
    {
        // Act
        var result = StringPool.TryGetSpan(0, out var span);

        // Assert
        result.Should().BeTrue(); // ID 0 is valid (empty string)
        span.IsEmpty.Should().BeTrue();
    }

    #endregion

    #region Zero-Allocation Verification Tests

    [Fact]
    public void GetSpan_NoAllocation_OnCacheHit()
    {
        // Arrange
        var testStrings = Enumerable.Range(0, 100)
            .Select(i => $"File_{i}.txt")
            .ToArray();
        var ids = testStrings.Select(s => StringPool.Intern(s)).ToArray();

        // Force GC to establish baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var gen0Before = GC.CollectionCount(0);

        // Act - Many GetSpan calls should not trigger allocations
        for (int iter = 0; iter < 10_000; iter++)
        {
            foreach (var id in ids)
            {
                var span = StringPool.GetSpan(id);
                // Use span to prevent optimization away
                _ = span.Length;
            }
        }

        var gen0After = GC.CollectionCount(0);

        // Assert - No GC should have occurred (or minimal)
        var gcDelta = gen0After - gen0Before;
        _output.WriteLine($"GC Gen0 collections during GetSpan: {gcDelta}");
        _output.WriteLine($"Total GetSpan calls: {10_000 * ids.Length:N0}");

        // Allow for some GC from test infrastructure
        gcDelta.Should().BeLessThan(5,
            "GetSpan should not cause significant GC pressure");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void GetSpan_Performance_VsGetString()
    {
        // Arrange
        var testStrings = Enumerable.Range(0, 1000)
            .Select(i => $"PerformanceTestFile_{i}.txt")
            .ToArray();
        var ids = testStrings.Select(s => StringPool.Intern(s)).ToArray();

        const int iterations = 10_000;

        // Warmup
        foreach (var id in ids)
        {
            _ = StringPool.GetString(id);
            _ = StringPool.GetSpan(id);
        }

        // Benchmark GetString
        var swString = Stopwatch.StartNew();
        for (int iter = 0; iter < iterations; iter++)
        {
            foreach (var id in ids)
            {
                var str = StringPool.GetString(id);
                _ = str.Length;
            }
        }
        swString.Stop();

        // Benchmark GetSpan
        var swSpan = Stopwatch.StartNew();
        for (int iter = 0; iter < iterations; iter++)
        {
            foreach (var id in ids)
            {
                var span = StringPool.GetSpan(id);
                _ = span.Length;
            }
        }
        swSpan.Stop();

        // Report
        var totalOps = iterations * ids.Length;
        var stringRate = totalOps / swString.Elapsed.TotalSeconds;
        var spanRate = totalOps / swSpan.Elapsed.TotalSeconds;

        _output.WriteLine("=== GetString vs GetSpan Performance ===");
        _output.WriteLine($"Total operations: {totalOps:N0}");
        _output.WriteLine($"GetString: {stringRate:N0} ops/sec ({swString.Elapsed.TotalMilliseconds:F2} ms)");
        _output.WriteLine($"GetSpan: {spanRate:N0} ops/sec ({swSpan.Elapsed.TotalMilliseconds:F2} ms)");
        _output.WriteLine($"Ratio: {spanRate / stringRate:F2}x");
        _output.WriteLine("Note: GetSpan's main benefit is zero-allocation downstream, not raw lookup speed");

        // GetSpan may have slight overhead from AsSpan() call, but should still be reasonably fast
        // The main value is zero-allocation when using the result with span-based APIs
        spanRate.Should().BeGreaterThan(1_000_000,
            "GetSpan should still be performant (>1M ops/sec)");
    }

    #endregion

    #region SIMD Integration Tests

    [Fact]
    public void GetSpan_WithSIMDMatcher_ZeroAllocation()
    {
        // Arrange
        var fileName = "TestDocument.txt";
        var pattern = "Document";
        var id = StringPool.Intern(fileName);

        // Force GC baseline
        GC.Collect();
        var gen0Before = GC.CollectionCount(0);

        // Act - Use GetSpan with SIMD matcher
        for (int i = 0; i < 100_000; i++)
        {
            var nameSpan = StringPool.GetSpan(id);
            var patternSpan = pattern.AsSpan();
            _ = SIMDStringMatcher.ContainsVectorized(nameSpan, patternSpan);
        }

        var gen0After = GC.CollectionCount(0);
        var gcDelta = gen0After - gen0Before;

        _output.WriteLine($"GC Gen0 collections during SIMD matching: {gcDelta}");

        // Minimal GC expected (only from test infrastructure)
        gcDelta.Should().BeLessThan(3,
            "GetSpan + SIMD matching should be zero-allocation");
    }

    [Fact]
    public void GetSpan_PatternMatching_Functional()
    {
        // Arrange
        var testCases = new[]
        {
            ("Document.txt", "doc", true),
            ("README.md", "read", true),
            ("config.json", "CONFIG", true), // Case-insensitive
            ("notes.txt", "xyz", false)
        };

        foreach (var (fileName, pattern, expected) in testCases)
        {
            var id = StringPool.Intern(fileName);
            var nameSpan = StringPool.GetSpan(id);
            var patternSpan = pattern.AsSpan();

            // Act
            var result = SIMDStringMatcher.ContainsVectorized(nameSpan, patternSpan);

            // Assert
            result.Should().Be(expected, $"'{fileName}' contains '{pattern}' should be {expected}");
        }
    }

    #endregion
}
