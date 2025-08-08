using System.Collections.Concurrent;
using System.Diagnostics;
using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastFind.Windows.Tests.Performance;

/// <summary>
/// Speed and throughput performance tests
/// </summary>
[TestCategory("Performance")]
[TestCategory("Suite:Integration")]
public class SpeedAndThroughputTests
{
    [Fact]
    public async Task Search_Response_Time_Should_Be_Under_Target()
    {
        // Arrange
        using var searchEngine = FastFinder.CreateSearchEngine(NullLogger.Instance);
        var query = new SearchQuery { SearchText = "test", MaxResults = 100 };
        
        // Warm up
        await searchEngine.SearchAsync(query);
        
        var measurements = new List<long>();
        const int iterations = 50;
        
        // Act - Measure response times
        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var result = await searchEngine.SearchAsync(query);
            sw.Stop();
            
            measurements.Add(sw.ElapsedMilliseconds);
        }
        
        // Assert - Response time targets
        var averageMs = measurements.Average();
        var p95Ms = measurements.OrderBy(x => x).Skip((int)(iterations * 0.95)).First();
        var maxMs = measurements.Max();
        
        averageMs.Should().BeLessThan(100, "Average search response time should be < 100ms");
        p95Ms.Should().BeLessThan(200, "95th percentile response time should be < 200ms");
        maxMs.Should().BeLessThan(500, "Maximum response time should be < 500ms");
        
        Console.WriteLine($"Average: {averageMs:F1}ms, P95: {p95Ms}ms, Max: {maxMs}ms");
    }
    
    [Fact]
    public void SIMD_String_Matching_Should_Achieve_Target_Throughput()
    {
        // Arrange
        var testStrings = GenerateTestStrings(10_000);
        var pattern = "performance".AsSpan();
        const int iterations = 100;
        
        // Act - Measure throughput
        var sw = Stopwatch.StartNew();
        var totalMatches = 0;
        
        for (int iter = 0; iter < iterations; iter++)
        {
            foreach (var text in testStrings)
            {
                if (SIMDStringMatcher.ContainsVectorized(text.AsSpan(), pattern))
                {
                    totalMatches++;
                }
            }
        }
        
        sw.Stop();
        
        var totalOperations = (long)testStrings.Length * iterations;
        var operationsPerSecond = (double)totalOperations / sw.Elapsed.TotalSeconds;
        var operationsPerMillisecond = operationsPerSecond / 1000;
        
        // Assert - Throughput targets
        operationsPerSecond.Should().BeGreaterThan(1_000_000, 
            "SIMD string matching should achieve > 1M operations/second");
        
        operationsPerMillisecond.Should().BeGreaterThan(1000, 
            "Should process > 1000 operations per millisecond");
        
        Console.WriteLine($"Throughput: {operationsPerSecond:N0} ops/sec ({operationsPerMillisecond:N0} ops/ms)");
        Console.WriteLine($"Total matches: {totalMatches:N0}");
    }
    
    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void File_Processing_Throughput_Should_Scale_With_Dataset_Size(int fileCount)
    {
        // Arrange
        var files = GenerateTestFiles(fileCount);
        var query = "document";
        
        // Act - Measure processing speed
        var sw = Stopwatch.StartNew();
        var matches = 0;
        
        foreach (var file in files)
        {
            if (file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches++;
            }
        }
        
        sw.Stop();
        
        var filesPerSecond = fileCount / sw.Elapsed.TotalSeconds;
        var timePerFile = sw.Elapsed.TotalMicroseconds / fileCount;
        
        // Assert - Throughput should be high
        filesPerSecond.Should().BeGreaterThan(100_000, 
            "Should process > 100K files per second");
        
        timePerFile.Should().BeLessThan(10, 
            "Should process each file in < 10 microseconds");
        
        Console.WriteLine($"File count: {fileCount:N0}");
        Console.WriteLine($"Throughput: {filesPerSecond:N0} files/sec");
        Console.WriteLine($"Time per file: {timePerFile:F3} μs");
        Console.WriteLine($"Matches found: {matches:N0}");
    }
    
    [Fact]
    public void FastFileItem_Creation_Should_Be_Fast()
    {
        // Arrange
        const int iterations = 100_000;
        var paths = GenerateTestPaths(iterations);
        
        // Act - Measure FastFileItem creation speed
        var sw = Stopwatch.StartNew();
        var items = new FastFileItem[iterations];
        
        for (int i = 0; i < iterations; i++)
        {
            var path = paths[i];
            var name = Path.GetFileName(path);
            var directory = Path.GetDirectoryName(path)!;
            var extension = Path.GetExtension(path);
            
            items[i] = new FastFileItem(
                path, name, directory, extension,
                i * 1024, DateTime.Now, DateTime.Now, DateTime.Now,
                FileAttributes.Normal, path[0]);
        }
        
        sw.Stop();
        
        var creationsPerSecond = iterations / sw.Elapsed.TotalSeconds;
        var timePerCreation = sw.Elapsed.TotalNanoseconds / iterations;
        
        // Assert - Creation should be very fast
        creationsPerSecond.Should().BeGreaterThan(500_000, 
            "Should create > 500K FastFileItems per second");
        
        timePerCreation.Should().BeLessThan(2000, 
            "Should create each item in < 2000 nanoseconds");
        
        Console.WriteLine($"Creation rate: {creationsPerSecond:N0} items/sec");
        Console.WriteLine($"Time per creation: {timePerCreation:F1} ns");
        
        GC.KeepAlive(items);
    }
    
    [Fact]
    public void StringPool_Interning_Should_Be_Fast_For_New_Strings()
    {
        // Arrange
        StringPool.Cleanup();
        var uniquePaths = GenerateUniquePaths(50_000);
        
        // Act - Measure interning speed for new strings
        var sw = Stopwatch.StartNew();
        var ids = new int[uniquePaths.Length];
        
        for (int i = 0; i < uniquePaths.Length; i++)
        {
            ids[i] = StringPool.InternPath(uniquePaths[i]);
        }
        
        sw.Stop();
        
        var internsPerSecond = uniquePaths.Length / sw.Elapsed.TotalSeconds;
        var timePerIntern = sw.Elapsed.TotalMicroseconds / uniquePaths.Length;
        
        // Assert - New string interning performance
        internsPerSecond.Should().BeGreaterThan(100_000, 
            "Should intern > 100K new strings per second");
        
        timePerIntern.Should().BeLessThan(10, 
            "Should intern each new string in < 10 microseconds");
        
        Console.WriteLine($"New string interning: {internsPerSecond:N0} interns/sec");
        Console.WriteLine($"Time per intern: {timePerIntern:F1} μs");
        
        GC.KeepAlive(ids);
    }
    
    [Fact]
    public void StringPool_Lookup_Should_Be_Extremely_Fast()
    {
        // Arrange
        StringPool.Cleanup();
        var paths = GenerateTestPaths(10_000);
        var ids = paths.Select(StringPool.InternPath).ToArray();
        
        const int lookupIterations = 100_000;
        var random = new Random(42);
        
        // Act - Measure lookup speed
        var sw = Stopwatch.StartNew();
        var retrievedPaths = new string[lookupIterations];
        
        for (int i = 0; i < lookupIterations; i++)
        {
            var randomId = ids[random.Next(ids.Length)];
            retrievedPaths[i] = StringPool.GetString(randomId);
        }
        
        sw.Stop();
        
        var lookupsPerSecond = lookupIterations / sw.Elapsed.TotalSeconds;
        var timePerLookup = sw.Elapsed.TotalNanoseconds / lookupIterations;
        
        // Assert - Lookup should be extremely fast
        lookupsPerSecond.Should().BeGreaterThan(5_000_000, 
            "Should perform > 5M lookups per second");
        
        timePerLookup.Should().BeLessThan(200, 
            "Should perform each lookup in < 200 nanoseconds");
        
        Console.WriteLine($"Lookup rate: {lookupsPerSecond:N0} lookups/sec");
        Console.WriteLine($"Time per lookup: {timePerLookup:F1} ns");
        
        GC.KeepAlive(retrievedPaths);
    }
    
    [Fact]
    public async Task Concurrent_Search_Performance_Should_Scale()
    {
        // Arrange
        using var searchEngine = FastFinder.CreateSearchEngine(NullLogger.Instance);
        var query = new SearchQuery { SearchText = "test", MaxResults = 50 };
        
        var concurrencyLevels = new[] { 1, 2, 4, 8 };
        var results = new Dictionary<int, double>();
        
        // Warm up
        await searchEngine.SearchAsync(query);
        
        foreach (var concurrency in concurrencyLevels)
        {
            // Act - Measure concurrent performance
            const int operationsPerThread = 20;
            var sw = Stopwatch.StartNew();
            
            var tasks = Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(async () =>
                {
                    for (int i = 0; i < operationsPerThread; i++)
                    {
                        await searchEngine.SearchAsync(query);
                    }
                }))
                .ToArray();
            
            await Task.WhenAll(tasks);
            sw.Stop();
            
            var totalOperations = concurrency * operationsPerThread;
            var operationsPerSecond = totalOperations / sw.Elapsed.TotalSeconds;
            results[concurrency] = operationsPerSecond;
            
            Console.WriteLine($"Concurrency {concurrency}: {operationsPerSecond:F0} ops/sec");
        }
        
        // Assert - Performance should improve with concurrency (up to a point)
        results[2].Should().BeGreaterThan(results[1] * 0.8, 
            "2-thread performance should be at least 80% of 2x single-thread");
        
        results[4].Should().BeGreaterThan(results[2] * 0.7, 
            "4-thread performance should be at least 70% improvement over 2-thread");
        
        var maxThroughput = results.Values.Max();
        maxThroughput.Should().BeGreaterThan(50, 
            "Maximum concurrent throughput should be > 50 searches/sec");
    }
    
    [Fact]
    public void Wildcard_Matching_Performance_Should_Be_Optimized()
    {
        // Arrange
        var fileNames = GenerateTestFileNames(100_000);
        var patterns = new[] { "*.txt", "test_*", "*_file_*", "*.{jpg,png,gif}" };
        
        foreach (var pattern in patterns)
        {
            // Act - Measure wildcard matching speed
            var sw = Stopwatch.StartNew();
            var matches = 0;
            
            foreach (var fileName in fileNames)
            {
                if (SIMDStringMatcher.MatchesWildcard(fileName.AsSpan(), pattern.AsSpan()))
                {
                    matches++;
                }
            }
            
            sw.Stop();
            
            var matchesPerSecond = fileNames.Length / sw.Elapsed.TotalSeconds;
            var timePerMatch = sw.Elapsed.TotalNanoseconds / fileNames.Length;
            
            // Assert - Wildcard matching should be fast
            matchesPerSecond.Should().BeGreaterThan(1_000_000, 
                $"Wildcard pattern '{pattern}' should process > 1M files/sec");
            
            timePerMatch.Should().BeLessThan(1000, 
                $"Each wildcard match should take < 1000ns for pattern '{pattern}'");
            
            Console.WriteLine($"Pattern '{pattern}': {matchesPerSecond:N0} files/sec, " +
                             $"{timePerMatch:F1}ns per file, {matches} matches");
        }
    }
    
    [Fact]
    public void Large_String_Processing_Should_Maintain_Performance()
    {
        // Arrange - Test performance with various string lengths
        var testCases = new[]
        {
            (Length: 100, Expected: 10_000_000), // 10M ops/sec for short strings
            (Length: 1_000, Expected: 5_000_000), // 5M ops/sec for medium strings
            (Length: 10_000, Expected: 1_000_000), // 1M ops/sec for long strings
            (Length: 100_000, Expected: 100_000) // 100K ops/sec for very long strings
        };
        
        foreach (var (length, expectedOpsPerSec) in testCases)
        {
            // Arrange
            var longText = GenerateLongTestString(length);
            var pattern = "performance_test_pattern";
            const int iterations = 1000;
            
            // Act
            var sw = Stopwatch.StartNew();
            var found = 0;
            
            for (int i = 0; i < iterations; i++)
            {
                if (SIMDStringMatcher.ContainsVectorized(longText.AsSpan(), pattern.AsSpan()))
                {
                    found++;
                }
            }
            
            sw.Stop();
            
            var operationsPerSecond = iterations / sw.Elapsed.TotalSeconds;
            
            // Assert
            operationsPerSecond.Should().BeGreaterThan(expectedOpsPerSec * 0.5, 
                $"Should achieve at least 50% of target performance for {length}-char strings");
            
            Console.WriteLine($"String length {length}: {operationsPerSecond:N0} ops/sec " +
                             $"(target: {expectedOpsPerSec:N0})");
        }
    }
    
    [Fact]
    public void Parallel_Processing_Should_Maximize_CPU_Usage()
    {
        // Arrange
        var files = GenerateFastTestFiles(50_000);
        var pattern = "test".AsSpan();
        var coreCount = Environment.ProcessorCount;
        
        // Act - Single-threaded processing
        var sw1 = Stopwatch.StartNew();
        var singleThreadMatches = 0;
        
        foreach (var file in files)
        {
            if (file.MatchesName(pattern))
                singleThreadMatches++;
        }
        sw1.Stop();
        
        // Act - Multi-threaded processing
        var sw2 = Stopwatch.StartNew();
        var multiThreadMatches = 0;
        
        Parallel.ForEach(files, file =>
        {
            if (file.MatchesName(pattern))
                Interlocked.Increment(ref multiThreadMatches);
        });
        sw2.Stop();
        
        var singleThreadTime = sw1.ElapsedMilliseconds;
        var multiThreadTime = sw2.ElapsedMilliseconds;
        var speedup = (double)singleThreadTime / multiThreadTime;
        var efficiency = speedup / coreCount;
        
        // Assert
        singleThreadMatches.Should().Be(multiThreadMatches, 
            "Both approaches should find same number of matches");
        
        speedup.Should().BeGreaterThan(1.5, 
            "Multi-threading should provide at least 1.5x speedup");
        
        efficiency.Should().BeGreaterThan(0.3, 
            "Parallel efficiency should be > 30%");
        
        Console.WriteLine($"Cores: {coreCount}");
        Console.WriteLine($"Single-thread: {singleThreadTime}ms");
        Console.WriteLine($"Multi-thread: {multiThreadTime}ms");
        Console.WriteLine($"Speedup: {speedup:F1}x");
        Console.WriteLine($"Efficiency: {efficiency:P1}");
        Console.WriteLine($"Matches: {singleThreadMatches}");
    }
    
    private static List<string> GenerateTestStrings(int count)
    {
        var strings = new List<string>(count);
        var random = new Random(42);
        var words = new[] { "performance", "test", "string", "data", "search", "fast", "optimization", "memory" };
        
        for (int i = 0; i < count; i++)
        {
            var wordCount = random.Next(5, 15);
            var selectedWords = new string[wordCount];
            
            for (int j = 0; j < wordCount; j++)
            {
                selectedWords[j] = words[random.Next(words.Length)];
            }
            
            strings.Add(string.Join(" ", selectedWords));
        }
        
        return strings;
    }
    
    private static List<FileItem> GenerateTestFiles(int count)
    {
        var files = new List<FileItem>(count);
        var random = new Random(42);
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".cs" };
        var prefixes = new[] { "document", "file", "test", "data", "report" };
        
        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[random.Next(prefixes.Length)];
            var extension = extensions[random.Next(extensions.Length)];
            var fileName = $"{prefix}_{i:D6}{extension}";
            
            files.Add(new FileItem
            {
                FullPath = $@"C:\Test\{fileName}",
                Name = fileName,
                Directory = @"C:\Test",
                Extension = extension,
                Size = random.Next(1024, 1024 * 1024),
                CreatedTime = DateTime.Now.AddDays(-random.Next(365)),
                ModifiedTime = DateTime.Now.AddDays(-random.Next(30)),
                AccessedTime = DateTime.Now.AddDays(-random.Next(7)),
                Attributes = FileAttributes.Normal
            });
        }
        
        return files;
    }
    
    private static List<FastFileItem> GenerateFastTestFiles(int count)
    {
        var files = new List<FastFileItem>(count);
        var random = new Random(42);
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".cs" };
        var prefixes = new[] { "document", "file", "test", "data", "report" };
        
        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[random.Next(prefixes.Length)];
            var extension = extensions[random.Next(extensions.Length)];
            var fileName = $"{prefix}_{i:D6}{extension}";
            var fullPath = $@"C:\Test\{fileName}";
            
            files.Add(new FastFileItem(
                fullPath, fileName, @"C:\Test", extension,
                random.Next(1024, 1024 * 1024),
                DateTime.Now.AddDays(-random.Next(365)),
                DateTime.Now.AddDays(-random.Next(30)),
                DateTime.Now.AddDays(-random.Next(7)),
                FileAttributes.Normal, 'C'));
        }
        
        return files;
    }
    
    private static string[] GenerateTestPaths(int count)
    {
        var paths = new string[count];
        var random = new Random(42);
        var folders = new[] { "Documents", "Projects", "Test", "Data" };
        var extensions = new[] { ".txt", ".cs", ".dll", ".exe", ".pdf" };
        
        for (int i = 0; i < count; i++)
        {
            var folder = folders[random.Next(folders.Length)];
            var extension = extensions[random.Next(extensions.Length)];
            paths[i] = $@"C:\{folder}\File_{i:D6}{extension}";
        }
        
        return paths;
    }
    
    private static string[] GenerateUniquePaths(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => $@"C:\Unique\Path_{i:D8}\File_{i:D8}.txt")
            .ToArray();
    }
    
    private static string[] GenerateTestFileNames(int count)
    {
        var names = new string[count];
        var random = new Random(42);
        var extensions = new[] { ".txt", ".jpg", ".png", ".gif", ".doc", ".pdf" };
        var prefixes = new[] { "test", "file", "document", "image", "data" };
        
        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[random.Next(prefixes.Length)];
            var extension = extensions[random.Next(extensions.Length)];
            names[i] = $"{prefix}_file_{i:D5}{extension}";
        }
        
        return names;
    }
    
    private static string GenerateLongTestString(int length)
    {
        var chars = new char[length];
        var random = new Random(42);
        
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)('a' + random.Next(26));
        }
        
        return new string(chars);
    }
}