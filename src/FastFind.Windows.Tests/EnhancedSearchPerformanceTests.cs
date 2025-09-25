using FastFind.Models;
using FluentAssertions;
using System.Diagnostics;
using Xunit;

namespace FastFind.Windows.Tests;

/// <summary>
/// Performance tests for enhanced search functionality
/// </summary>
[Trait("Category", "Performance")]
[Trait("Suite", "EnhancedSearch")]
public class EnhancedSearchPerformanceTests
{
    [Fact(Skip = "Performance test - run manually")]
    public void SearchQuery_Creation_ShouldBeFast()
    {
        // Arrange
        const int iterations = 100000;
        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            var query = new SearchQuery
            {
                BasePath = $"D:\\TestPath{i % 100}",
                SearchText = $"search{i % 50}",
                IncludeSubdirectories = i % 2 == 0,
                SearchFileNameOnly = i % 3 == 0,
                ExtensionFilter = i % 4 == 0 ? ".txt" : null,
                CaseSensitive = i % 5 == 0,
                UseRegex = i % 10 == 0,
                MaxResults = i % 20 == 0 ? 1000 : (int?)null
            };
        }
        stopwatch.Stop();

        // Assert
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        var queriesPerSecond = iterations / (elapsedMs / 1000.0);

        Console.WriteLine($"SearchQuery creation performance:");
        Console.WriteLine($"- Total time: {elapsedMs}ms");
        Console.WriteLine($"- Queries per second: {queriesPerSecond:N0}");
        Console.WriteLine($"- Average per query: {(elapsedMs * 1000.0) / iterations:F2} microseconds");

        // Should create at least 10,000 queries per second
        queriesPerSecond.Should().BeGreaterThan(10000, "SearchQuery creation should be very fast");
    }

    [Fact(Skip = "Performance test - run manually")]
    public void SearchQuery_Clone_ShouldBeFast()
    {
        // Arrange
        const int iterations = 50000;
        var originalQuery = new SearchQuery
        {
            BasePath = "D:\\TestPath",
            SearchText = "search_term_for_performance_test",
            IncludeSubdirectories = true,
            SearchFileNameOnly = false,
            ExtensionFilter = ".txt",
            CaseSensitive = false,
            UseRegex = false,
            MaxResults = 1000,
            MinSize = 1024,
            MaxSize = 1024 * 1024,
            IncludeFiles = true,
            IncludeDirectories = false,
            IncludeHidden = false,
            IncludeSystem = false
        };

        // Add some search locations and excluded paths
        originalQuery.SearchLocations.Add("C:\\TestLocation1");
        originalQuery.SearchLocations.Add("C:\\TestLocation2");
        originalQuery.ExcludedPaths.Add("temp");
        originalQuery.ExcludedPaths.Add("cache");

        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            var clonedQuery = originalQuery.Clone();
        }
        stopwatch.Stop();

        // Assert
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        var clonesPerSecond = iterations / (elapsedMs / 1000.0);

        Console.WriteLine($"SearchQuery cloning performance:");
        Console.WriteLine($"- Total time: {elapsedMs}ms");
        Console.WriteLine($"- Clones per second: {clonesPerSecond:N0}");
        Console.WriteLine($"- Average per clone: {(elapsedMs * 1000.0) / iterations:F2} microseconds");

        // Should clone at least 5,000 queries per second
        clonesPerSecond.Should().BeGreaterThan(5000, "SearchQuery cloning should be fast");
    }

    [Fact(Skip = "Performance test - run manually")]
    public void SearchQuery_Validation_ShouldBeFast()
    {
        // Arrange
        const int iterations = 75000;
        var queries = new SearchQuery[10];

        // Create different query types
        for (int i = 0; i < 10; i++)
        {
            queries[i] = new SearchQuery
            {
                BasePath = $"D:\\Path{i}",
                SearchText = i % 2 == 0 ? $"search{i}" : string.Empty,
                IncludeSubdirectories = i % 3 == 0,
                SearchFileNameOnly = i % 4 == 0,
                ExtensionFilter = i % 5 == 0 ? ".txt" : null,
                UseRegex = i % 7 == 0,
                CaseSensitive = i % 6 == 0,
                MinSize = i % 8 == 0 ? 1024 : null,
                MaxResults = i % 9 == 0 ? 1000 : null
            };
        }

        var stopwatch = new Stopwatch();
        int validCount = 0;

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            var (isValid, _) = queries[i % 10].Validate();
            if (isValid) validCount++;
        }
        stopwatch.Stop();

        // Assert
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        var validationsPerSecond = iterations / (elapsedMs / 1000.0);

        Console.WriteLine($"SearchQuery validation performance:");
        Console.WriteLine($"- Total time: {elapsedMs}ms");
        Console.WriteLine($"- Validations per second: {validationsPerSecond:N0}");
        Console.WriteLine($"- Average per validation: {(elapsedMs * 1000.0) / iterations:F2} microseconds");
        Console.WriteLine($"- Valid queries: {validCount}/{iterations}");

        // Should validate at least 15,000 queries per second
        validationsPerSecond.Should().BeGreaterThan(15000, "SearchQuery validation should be very fast");
    }

    [Theory(Skip = "Performance test - run manually")]
    [InlineData(1000)]
    [InlineData(10000)]
    [InlineData(50000)]
    public void SearchQuery_PropertyAccess_ShouldBeFast(int iterations)
    {
        // Arrange
        var query = new SearchQuery
        {
            BasePath = "D:\\LongPathForPerformanceTesting\\SubDirectory\\AnotherLevel",
            SearchText = "complex_search_pattern_with_many_characters_for_performance_testing",
            IncludeSubdirectories = true,
            SearchFileNameOnly = false,
            ExtensionFilter = ".performance",
            CaseSensitive = false,
            UseRegex = false,
            MaxResults = 5000,
            MinSize = 2048,
            MaxSize = 10 * 1024 * 1024
        };

        var stopwatch = new Stopwatch();
        string accumulatedData = string.Empty;

        // Act - Access all properties multiple times
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            // Access all the enhanced properties
            var basePath = query.BasePath;
            var searchText = query.SearchText;
            var includeSubdirs = query.IncludeSubdirectories;
            var fileNameOnly = query.SearchFileNameOnly;
            var extension = query.ExtensionFilter;
            var caseSensitive = query.CaseSensitive;
            var useRegex = query.UseRegex;
            var maxResults = query.MaxResults;

            // Accumulate some data to prevent optimization
            if (i % 1000 == 0)
            {
                accumulatedData += basePath?.Length.ToString() ?? "0";
            }
        }
        stopwatch.Stop();

        // Assert
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        var accessesPerSecond = (iterations * 8) / (elapsedMs / 1000.0); // 8 properties accessed

        Console.WriteLine($"SearchQuery property access performance ({iterations:N0} iterations):");
        Console.WriteLine($"- Total time: {elapsedMs}ms");
        Console.WriteLine($"- Property accesses per second: {accessesPerSecond:N0}");
        Console.WriteLine($"- Average per access: {(elapsedMs * 1000.0) / (iterations * 8):F3} microseconds");

        // Should access properties very fast
        accessesPerSecond.Should().BeGreaterThan(1000000, "Property access should be extremely fast");
    }

    [Fact(Skip = "Performance test - run manually")]
    public void SearchQuery_MemoryFootprint_ShouldBeReasonable()
    {
        // Arrange
        const int queryCount = 10000;
        var queries = new List<SearchQuery>(queryCount);

        long initialMemory = GC.GetTotalMemory(true);

        // Act - Create many queries
        for (int i = 0; i < queryCount; i++)
        {
            var query = new SearchQuery
            {
                BasePath = $"D:\\TestPath{i % 100}",
                SearchText = $"search_term_{i % 50}",
                IncludeSubdirectories = i % 2 == 0,
                SearchFileNameOnly = i % 3 == 0,
                ExtensionFilter = i % 4 == 0 ? $".ext{i % 10}" : null,
                CaseSensitive = i % 5 == 0,
                UseRegex = i % 10 == 0,
                MaxResults = i % 20 == 0 ? i % 1000 + 100 : (int?)null
            };

            // Add some collections data
            if (i % 5 == 0)
            {
                query.SearchLocations.Add($"C:\\Location{i % 20}");
                query.ExcludedPaths.Add($"exclude{i % 15}");
            }

            queries.Add(query);
        }

        long finalMemory = GC.GetTotalMemory(true);
        long memoryUsed = finalMemory - initialMemory;

        // Assert
        var bytesPerQuery = (double)memoryUsed / queryCount;
        var mbTotal = memoryUsed / (1024.0 * 1024.0);

        Console.WriteLine($"SearchQuery memory usage:");
        Console.WriteLine($"- Total queries: {queryCount:N0}");
        Console.WriteLine($"- Total memory used: {mbTotal:F2} MB");
        Console.WriteLine($"- Average per query: {bytesPerQuery:F0} bytes");
        Console.WriteLine($"- Memory efficiency: {queryCount / mbTotal:F0} queries per MB");

        // Should use reasonable amount of memory
        bytesPerQuery.Should().BeLessThan(1000, "Each SearchQuery should use less than 1KB on average");
        mbTotal.Should().BeLessThan(50, "Total memory for 10K queries should be under 50MB");
    }

    [Fact(Skip = "Performance test - run manually")]
    public void SearchQuery_ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        const int threadsCount = 8;
        const int operationsPerThread = 5000;
        var query = new SearchQuery
        {
            BasePath = "D:\\ConcurrentTest",
            SearchText = "concurrent_search",
            IncludeSubdirectories = true,
            SearchFileNameOnly = false
        };

        var tasks = new Task[threadsCount];
        var stopwatch = new Stopwatch();
        var totalOperations = threadsCount * operationsPerThread;

        // Act
        stopwatch.Start();
        for (int i = 0; i < threadsCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < operationsPerThread; j++)
                {
                    // Read properties (should be thread-safe)
                    var basePath = query.BasePath;
                    var searchText = query.SearchText;
                    var includeSubdirs = query.IncludeSubdirectories;
                    var fileNameOnly = query.SearchFileNameOnly;

                    // Clone (creates new instance, should be safe)
                    var cloned = query.Clone();

                    // Validate (should be thread-safe)
                    var (isValid, _) = query.Validate();

                    // Access collections (read-only access should be safe)
                    var locationsCount = query.SearchLocations.Count;
                    var excludedCount = query.ExcludedPaths.Count;
                }
            });
        }

        Task.WaitAll(tasks);
        stopwatch.Stop();

        // Assert
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        var operationsPerSecond = totalOperations / (elapsedMs / 1000.0);

        Console.WriteLine($"SearchQuery concurrent access performance:");
        Console.WriteLine($"- Threads: {threadsCount}");
        Console.WriteLine($"- Operations per thread: {operationsPerThread:N0}");
        Console.WriteLine($"- Total operations: {totalOperations:N0}");
        Console.WriteLine($"- Total time: {elapsedMs}ms");
        Console.WriteLine($"- Operations per second: {operationsPerSecond:N0}");

        // Should handle concurrent access efficiently
        operationsPerSecond.Should().BeGreaterThan(50000, "Concurrent access should be efficient");
    }
}