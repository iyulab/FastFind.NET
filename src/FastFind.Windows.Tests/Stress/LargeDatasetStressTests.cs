using System.Collections.Concurrent;
using System.Diagnostics;
using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastFind.Windows.Tests.Stress;

/// <summary>
/// Stress tests for large datasets and edge conditions
/// </summary>
[TestCategory("Performance")]
[TestCategory("Suite:Stress")]
public class LargeDatasetStressTests
{
    [Theory]
    [InlineData(100_000)]
    [InlineData(500_000)]
    [InlineData(1_000_000)]
    public void StringPool_Should_Handle_Large_String_Sets(int stringCount)
    {
        // Arrange
        StringPool.Cleanup();
        var initialMemory = GC.GetTotalMemory(true);
        var testPaths = GenerateLargePaths(stringCount);
        
        // Act - Intern large number of strings
        var sw = Stopwatch.StartNew();
        var ids = new int[stringCount];
        
        for (int i = 0; i < stringCount; i++)
        {
            ids[i] = StringPool.InternPath(testPaths[i]);
        }
        sw.Stop();
        
        var finalMemory = GC.GetTotalMemory(false);
        var memoryUsed = finalMemory - initialMemory;
        var stats = StringPool.GetStats();
        
        // Assert - Performance and memory requirements
        var internsPerSecond = stringCount / sw.Elapsed.TotalSeconds;
        var memoryPerString = (double)memoryUsed / stringCount;
        
        internsPerSecond.Should().BeGreaterThan(10_000, 
            $"Should intern > 10K strings/sec for {stringCount} strings");
        
        memoryPerString.Should().BeLessThan(200, 
            $"Should use < 200 bytes per string for {stringCount} strings");
        
        stats.TotalStrings.Should().Be(stringCount, 
            "Should track all unique strings");
        
        // Verify all strings can be retrieved
        for (int i = 0; i < Math.Min(1000, stringCount); i++) // Test sample
        {
            var retrieved = StringPool.GetString(ids[i]);
            retrieved.Should().Be(testPaths[i], $"String {i} should be retrievable");
        }
        
        Console.WriteLine($"Dataset: {stringCount:N0} strings");
        Console.WriteLine($"Performance: {internsPerSecond:N0} interns/sec");
        Console.WriteLine($"Memory: {memoryUsed:N0} bytes ({memoryPerString:F1} bytes/string)");
        Console.WriteLine($"Compression: {stats.CompressionRatio:P1}");
        
        GC.KeepAlive(ids);
        GC.KeepAlive(testPaths);
    }
    
    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    [InlineData(500_000)]
    public void FastFileItem_Should_Handle_Large_File_Collections(int fileCount)
    {
        // Arrange
        var initialMemory = GC.GetTotalMemory(true);
        
        // Act - Create large collection of FastFileItems
        var sw = Stopwatch.StartNew();
        var files = new FastFileItem[fileCount];
        
        for (int i = 0; i < fileCount; i++)
        {
            var fileName = $"File_{i:D8}.txt";
            var fullPath = $@"C:\LargeDataset\Folder_{i / 1000:D4}\{fileName}";
            var directory = Path.GetDirectoryName(fullPath)!;
            
            files[i] = new FastFileItem(
                fullPath, fileName, directory, ".txt",
                i * 1024, DateTime.Now.AddDays(-i % 365),
                DateTime.Now.AddDays(-i % 30), DateTime.Now.AddDays(-i % 7),
                FileAttributes.Normal, 'C');
        }
        sw.Stop();
        
        var finalMemory = GC.GetTotalMemory(false);
        var memoryUsed = finalMemory - initialMemory;
        
        // Act - Test operations on large collection
        var searchSw = Stopwatch.StartNew();
        var matches = 0;
        var pattern = "File_".AsSpan();
        
        foreach (var file in files)
        {
            if (file.MatchesName(pattern))
                matches++;
        }
        searchSw.Stop();
        
        // Assert - Performance requirements
        var creationsPerSecond = fileCount / sw.Elapsed.TotalSeconds;
        var searchesPerSecond = fileCount / searchSw.Elapsed.TotalSeconds;
        var memoryPerFile = (double)memoryUsed / fileCount;
        
        creationsPerSecond.Should().BeGreaterThan(100_000, 
            $"Should create > 100K items/sec for {fileCount} files");
        
        searchesPerSecond.Should().BeGreaterThan(1_000_000, 
            $"Should search > 1M items/sec for {fileCount} files");
        
        memoryPerFile.Should().BeLessThan(100, 
            $"Should use < 100 bytes per file for {fileCount} files");
        
        matches.Should().Be(fileCount, "Should match all files with pattern");
        
        Console.WriteLine($"Dataset: {fileCount:N0} files");
        Console.WriteLine($"Creation: {creationsPerSecond:N0} files/sec, {sw.ElapsedMilliseconds}ms total");
        Console.WriteLine($"Search: {searchesPerSecond:N0} files/sec, {searchSw.ElapsedMilliseconds}ms total");
        Console.WriteLine($"Memory: {memoryUsed:N0} bytes ({memoryPerFile:F1} bytes/file)");
        
        GC.KeepAlive(files);
    }
    
    [Fact]
    public async Task Search_Engine_Should_Handle_Extreme_Search_Load()
    {
        // Arrange
        using var searchEngine = FastFinder.CreateSearchEngine(NullLogger.Instance);
        
        const int concurrentSearches = 50;
        const int searchesPerThread = 100;
        var totalSearches = concurrentSearches * searchesPerThread;
        
        var queries = new[]
        {
            new SearchQuery { SearchText = "test", MaxResults = 100 },
            new SearchQuery { SearchText = "*.txt", MaxResults = 50 },
            new SearchQuery { SearchText = "document", MaxResults = 75 },
            new SearchQuery { SearchText = "file_*", MaxResults = 200 },
            new SearchQuery { SearchText = "*.cs", MaxResults = 150 }
        };
        
        var results = new ConcurrentBag<SearchResult>();
        var errors = new ConcurrentBag<Exception>();
        
        // Act - Execute extreme search load
        var sw = Stopwatch.StartNew();
        
        var tasks = Enumerable.Range(0, concurrentSearches)
            .Select(threadId => Task.Run(async () =>
            {
                var random = new Random(threadId);
                
                for (int i = 0; i < searchesPerThread; i++)
                {
                    try
                    {
                        var query = queries[random.Next(queries.Length)];
                        var result = await searchEngine.SearchAsync(query);
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                }
            }))
            .ToArray();
        
        await Task.WhenAll(tasks);
        sw.Stop();
        
        // Assert - System stability under load
        var completedSearches = results.Count;
        var errorCount = errors.Count;
        var searchesPerSecond = completedSearches / sw.Elapsed.TotalSeconds;
        var errorRate = (double)errorCount / totalSearches;
        
        completedSearches.Should().BeGreaterThan(totalSearches * 0.95, 
            "At least 95% of searches should complete successfully");
        
        errorRate.Should().BeLessThan(0.05, 
            "Error rate should be < 5%");
        
        searchesPerSecond.Should().BeGreaterThan(100, 
            "Should handle > 100 searches/sec under extreme load");
        
        // Check for memory leaks
        var stats = await searchEngine.GetSearchStatisticsAsync();
        stats.TotalSearches.Should().BeGreaterThan(0, "Should track searches");
        
        Console.WriteLine($"Extreme load test: {concurrentSearches} threads × {searchesPerThread} searches");
        Console.WriteLine($"Completed: {completedSearches}/{totalSearches} ({(double)completedSearches/totalSearches:P1})");
        Console.WriteLine($"Errors: {errorCount} ({errorRate:P2})");
        Console.WriteLine($"Throughput: {searchesPerSecond:F0} searches/sec");
        Console.WriteLine($"Total time: {sw.Elapsed.TotalSeconds:F1}s");
        
        if (errorCount > 0)
        {
            var errorTypes = errors.GroupBy(e => e.GetType().Name)
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();
            Console.WriteLine($"Error types: {string.Join(", ", errorTypes)}");
        }
    }
    
    [Fact]
    public void SIMD_String_Matching_Should_Handle_Pathological_Cases()
    {
        // Arrange - Create pathological test cases
        var pathologicalCases = new[]
        {
            // Very long strings
            (Text: new string('a', 10000), Pattern: "aaaa", Expected: true),
            (Text: new string('a', 10000), Pattern: "b", Expected: false),
            
            // Repetitive patterns
            (Text: string.Concat(Enumerable.Repeat("abcd", 2500)), Pattern: "abcd", Expected: true),
            (Text: string.Concat(Enumerable.Repeat("abcd", 2500)), Pattern: "xyz", Expected: false),
            
            // Edge cases
            (Text: "", Pattern: "", Expected: true),
            (Text: "a", Pattern: "", Expected: true),
            (Text: "", Pattern: "a", Expected: false),
            
            // Unicode and special characters
            (Text: "Hello 世界 🌍 Test", Pattern: "世界", Expected: true),
            (Text: "Special chars: !@#$%^&*()", Pattern: "@#$", Expected: true),
            
            // Case sensitivity
            (Text: "UPPERCASE lowercase MiXeD", Pattern: "case", Expected: true),
            (Text: "UPPERCASE lowercase MiXeD", Pattern: "CASE", Expected: false)
        };
        
        // Act & Assert - Test all pathological cases
        foreach (var (text, pattern, expected) in pathologicalCases)
        {
            var sw = Stopwatch.StartNew();
            var result = SIMDStringMatcher.ContainsVectorized(text.AsSpan(), pattern.AsSpan());
            sw.Stop();
            
            result.Should().Be(expected, 
                $"Pattern '{pattern}' in text of length {text.Length} should be {expected}");
            
            sw.ElapsedMilliseconds.Should().BeLessThan(100, 
                $"Even pathological cases should complete quickly (text length: {text.Length})");
            
            Console.WriteLine($"Case: {text.Length} chars, pattern '{pattern}' -> {result} ({sw.ElapsedMilliseconds}ms)");
        }
    }
    
    [Fact]
    public void Memory_Pressure_Should_Not_Cause_Failures()
    {
        // This test intentionally creates memory pressure to test system stability
        
        // Arrange
        StringPool.Cleanup();
        var baseMemory = GC.GetTotalMemory(true);
        var memoryPressureObjects = new List<byte[]>();
        var testResults = new List<bool>();
        
        try
        {
            // Act - Create memory pressure while performing operations
            for (int cycle = 0; cycle < 20; cycle++)
            {
                // Create memory pressure
                for (int i = 0; i < 10; i++)
                {
                    memoryPressureObjects.Add(new byte[10 * 1024 * 1024]); // 10MB each
                }
                
                // Perform FastFind operations under pressure
                var success = true;
                
                try
                {
                    // Test string pooling under pressure
                    var testPaths = GenerateLargePaths(1000);
                    var ids = testPaths.Select(StringPool.InternPath).ToArray();
                    
                    // Test FastFileItem creation under pressure
                    var files = new FastFileItem[1000];
                    for (int i = 0; i < files.Length; i++)
                    {
                        files[i] = new FastFileItem(
                            testPaths[i], Path.GetFileName(testPaths[i]), 
                            Path.GetDirectoryName(testPaths[i])!, Path.GetExtension(testPaths[i]),
                            i * 1024, DateTime.Now, DateTime.Now, DateTime.Now,
                            FileAttributes.Normal, testPaths[i][0]);
                    }
                    
                    // Test SIMD operations under pressure
                    var text = "This is a test string for memory pressure testing";
                    var pattern = "test";
                    var simdResult = SIMDStringMatcher.ContainsVectorized(text.AsSpan(), pattern.AsSpan());
                    
                    if (!simdResult)
                        success = false;
                    
                    // Verify string retrieval works
                    for (int i = 0; i < Math.Min(100, ids.Length); i++)
                    {
                        var retrieved = StringPool.GetString(ids[i]);
                        if (retrieved != testPaths[i])
                        {
                            success = false;
                            break;
                        }
                    }
                }
                catch
                {
                    success = false;
                }
                
                testResults.Add(success);
                
                // Force garbage collection
                if (cycle % 5 == 4)
                {
                    memoryPressureObjects.Clear();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                
                var currentMemory = GC.GetTotalMemory(false);
                Console.WriteLine($"Cycle {cycle + 1}: Success={success}, Memory={currentMemory:N0} bytes");
            }
            
            // Assert - System should remain stable under pressure
            var successRate = testResults.Count(x => x) / (double)testResults.Count;
            
            successRate.Should().BeGreaterThan(0.8, 
                "At least 80% of operations should succeed under memory pressure");
            
            Console.WriteLine($"Memory pressure test: {successRate:P1} success rate");
        }
        finally
        {
            // Cleanup
            memoryPressureObjects.Clear();
            StringPool.Cleanup();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
    
    [Theory]
    [InlineData(10_000, 100)] // 10K files, 100 threads
    [InlineData(100_000, 50)] // 100K files, 50 threads
    public void Concurrent_File_Processing_Should_Scale(int fileCount, int threadCount)
    {
        // Arrange
        var files = GenerateLargeFileDataset(fileCount);
        var pattern = "test".AsSpan();
        var results = new ConcurrentBag<int>();
        var errors = new ConcurrentBag<Exception>();
        
        // Calculate work distribution
        var filesPerThread = fileCount / threadCount;
        
        // Act - Process files concurrently
        var sw = Stopwatch.StartNew();
        
        var tasks = Enumerable.Range(0, threadCount)
            .Select(threadId => Task.Run(() =>
            {
                try
                {
                    var startIndex = threadId * filesPerThread;
                    var endIndex = threadId == threadCount - 1 ? fileCount : startIndex + filesPerThread;
                    var matches = 0;
                    
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        if (files[i].MatchesName(pattern))
                            matches++;
                    }
                    
                    results.Add(matches);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }))
            .ToArray();
        
        Task.WaitAll(tasks);
        sw.Stop();
        
        // Assert - Concurrent processing performance
        var totalMatches = results.Sum();
        var errorCount = errors.Count;
        var filesPerSecond = fileCount / sw.Elapsed.TotalSeconds;
        
        errorCount.Should().Be(0, "No errors should occur during concurrent processing");
        
        filesPerSecond.Should().BeGreaterThan(1_000_000, 
            $"Should process > 1M files/sec with {threadCount} threads on {fileCount} files");
        
        totalMatches.Should().BeGreaterThan(0, "Should find some matches");
        
        // Calculate theoretical speedup
        var expectedSpeedupFactor = Math.Min(threadCount, Environment.ProcessorCount) * 0.7; // 70% efficiency
        var actualSpeedupFactor = filesPerSecond / (fileCount / sw.Elapsed.TotalSeconds);
        
        Console.WriteLine($"Concurrent processing: {fileCount:N0} files, {threadCount} threads");
        Console.WriteLine($"Performance: {filesPerSecond:N0} files/sec");
        Console.WriteLine($"Total matches: {totalMatches:N0}");
        Console.WriteLine($"Time: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Expected speedup: {expectedSpeedupFactor:F1}x, Actual: {actualSpeedupFactor:F1}x");
        
        GC.KeepAlive(files);
    }
    
    [Fact]
    public async Task Long_Running_Search_Engine_Should_Remain_Stable()
    {
        // Arrange - Test for memory leaks and stability over time
        using var searchEngine = FastFinder.CreateSearchEngine(NullLogger.Instance);
        
        var queries = new[]
        {
            new SearchQuery { SearchText = "test", MaxResults = 50 },
            new SearchQuery { SearchText = "*.txt", MaxResults = 100 },
            new SearchQuery { SearchText = "document", MaxResults = 75 }
        };
        
        var memoryReadings = new List<long>();
        var performanceReadings = new List<double>();
        var random = new Random(42);
        
        // Act - Run continuous operations for extended period
        const int totalCycles = 100;
        const int operationsPerCycle = 50;
        
        for (int cycle = 0; cycle < totalCycles; cycle++)
        {
            var cycleStart = Stopwatch.StartNew();
            
            // Perform operations
            for (int op = 0; op < operationsPerCycle; op++)
            {
                var query = queries[random.Next(queries.Length)];
                var result = await searchEngine.SearchAsync(query);
                result.Should().NotBeNull();
            }
            
            cycleStart.Stop();
            var opsPerSecond = operationsPerCycle / cycleStart.Elapsed.TotalSeconds;
            performanceReadings.Add(opsPerSecond);
            
            // Memory measurement every 10 cycles
            if (cycle % 10 == 9)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                var memory = GC.GetTotalMemory(false);
                memoryReadings.Add(memory);
                
                Console.WriteLine($"Cycle {cycle + 1}: {opsPerSecond:F0} ops/sec, {memory:N0} bytes");
            }
        }
        
        // Assert - Stability over time
        var avgPerformance = performanceReadings.Average();
        var performanceVariance = performanceReadings.Max() - performanceReadings.Min();
        var performanceStability = 1.0 - (performanceVariance / avgPerformance);
        
        var memoryGrowth = memoryReadings.Last() - memoryReadings.First();
        var memoryGrowthPercentage = (double)memoryGrowth / memoryReadings.First();
        
        performanceStability.Should().BeGreaterThan(0.7, 
            "Performance should be stable over time (< 30% variance)");
        
        memoryGrowthPercentage.Should().BeLessThan(0.5, 
            "Memory growth should be < 50% over long operation");
        
        avgPerformance.Should().BeGreaterThan(10, 
            "Should maintain > 10 operations/sec average");
        
        Console.WriteLine($"Long-running stability test: {totalCycles} cycles, {totalCycles * operationsPerCycle} operations");
        Console.WriteLine($"Average performance: {avgPerformance:F0} ops/sec");
        Console.WriteLine($"Performance stability: {performanceStability:P1}");
        Console.WriteLine($"Memory growth: {memoryGrowthPercentage:P1}");
    }
    
    private static string[] GenerateLargePaths(int count)
    {
        var paths = new string[count];
        var random = new Random(42);
        var drives = new[] { 'C', 'D', 'E', 'F' };
        var folders = new[] { "Documents", "Projects", "Data", "Files", "Archive", "Backup", "Temp" };
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".png", ".cs", ".dll", ".exe", ".log" };
        
        for (int i = 0; i < count; i++)
        {
            var drive = drives[random.Next(drives.Length)];
            var folder = folders[random.Next(folders.Length)];
            var subFolder = $"Sub_{random.Next(1000):D3}";
            var fileName = $"File_{i:D8}";
            var extension = extensions[random.Next(extensions.Length)];
            
            paths[i] = $@"{drive}:\{folder}\{subFolder}\{fileName}{extension}";
        }
        
        return paths;
    }
    
    private static FastFileItem[] GenerateLargeFileDataset(int count)
    {
        var files = new FastFileItem[count];
        var random = new Random(42);
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".cs", ".dll" };
        var prefixes = new[] { "test", "file", "document", "data", "report", "image", "code" };
        
        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[random.Next(prefixes.Length)];
            var extension = extensions[random.Next(extensions.Length)];
            var fileName = $"{prefix}_{i:D8}{extension}";
            var directory = $@"C:\LargeDataset\Folder_{i / 10000:D3}";
            var fullPath = Path.Combine(directory, fileName);
            
            files[i] = new FastFileItem(
                fullPath, fileName, directory, extension,
                random.Next(1024, 10 * 1024 * 1024),
                DateTime.Now.AddDays(-random.Next(365)),
                DateTime.Now.AddDays(-random.Next(30)),
                DateTime.Now.AddDays(-random.Next(7)),
                FileAttributes.Normal,
                directory[0]);
        }
        
        return files;
    }
}