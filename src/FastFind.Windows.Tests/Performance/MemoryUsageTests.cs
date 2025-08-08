using System.Diagnostics;
using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastFind.Windows.Tests.Performance;

/// <summary>
/// Memory usage optimization tests
/// </summary>
[TestCategory("Performance")]
[TestCategory("Suite:Core")]
public class MemoryUsageTests
{
    [Fact]
    public void FastFileItem_Should_Use_Less_Memory_Than_FileItem()
    {
        // Arrange
        const int itemCount = 10_000;
        
        // Act - Measure FileItem memory usage
        var initialMemory = GC.GetTotalMemory(true);
        var fileItems = new FileItem[itemCount];
        
        for (int i = 0; i < itemCount; i++)
        {
            fileItems[i] = new FileItem
            {
                FullPath = $@"C:\Test\File_{i:D6}.txt",
                Name = $"File_{i:D6}.txt",
                Directory = @"C:\Test",
                Extension = ".txt",
                Size = i * 1024,
                CreatedTime = DateTime.Now,
                ModifiedTime = DateTime.Now,
                AccessedTime = DateTime.Now,
                Attributes = FileAttributes.Normal
            };
        }
        
        var fileItemMemory = GC.GetTotalMemory(false) - initialMemory;
        
        // Act - Measure FastFileItem memory usage
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        var fastInitialMemory = GC.GetTotalMemory(true);
        var fastFileItems = new FastFileItem[itemCount];
        
        for (int i = 0; i < itemCount; i++)
        {
            fastFileItems[i] = new FastFileItem(
                $@"C:\Test\File_{i:D6}.txt", $"File_{i:D6}.txt", @"C:\Test", ".txt",
                i * 1024, DateTime.Now, DateTime.Now, DateTime.Now,
                FileAttributes.Normal, 'C');
        }
        
        var fastFileItemMemory = GC.GetTotalMemory(false) - fastInitialMemory;
        
        // Assert
        var memoryReduction = ((double)(fileItemMemory - fastFileItemMemory) / fileItemMemory) * 100;
        
        fastFileItemMemory.Should().BeLessThan(fileItemMemory, 
            "FastFileItem should use less memory than FileItem");
        
        memoryReduction.Should().BeGreaterThan(20, 
            "FastFileItem should use at least 20% less memory than FileItem");
        
        Console.WriteLine($"FileItem memory: {fileItemMemory:N0} bytes");
        Console.WriteLine($"FastFileItem memory: {fastFileItemMemory:N0} bytes");
        Console.WriteLine($"Memory reduction: {memoryReduction:F1}%");
        
        // Keep references alive for accurate measurement
        GC.KeepAlive(fileItems);
        GC.KeepAlive(fastFileItems);
    }
    
    [Fact]
    public void StringPool_Should_Reduce_Memory_Usage_With_Duplicates()
    {
        // Arrange
        StringPool.Cleanup();
        const int pathCount = 5_000;
        const int duplicateFactor = 5; // Each unique path repeated 5 times
        
        // Generate paths with many duplicates
        var basePaths = GenerateUniquePaths(pathCount);
        var allPaths = new List<string>();
        
        for (int i = 0; i < duplicateFactor; i++)
        {
            allPaths.AddRange(basePaths);
        }
        
        var totalPaths = allPaths.Count;
        
        // Act - Measure memory without string pooling
        var initialMemory = GC.GetTotalMemory(true);
        var pathArray = allPaths.ToArray();
        var withoutPoolingMemory = GC.GetTotalMemory(false) - initialMemory;
        
        // Act - Measure memory with string pooling
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        var poolInitialMemory = GC.GetTotalMemory(true);
        var pooledIds = new int[totalPaths];
        
        for (int i = 0; i < totalPaths; i++)
        {
            pooledIds[i] = StringPool.InternPath(allPaths[i]);
        }
        
        var withPoolingMemory = GC.GetTotalMemory(false) - poolInitialMemory;
        var poolStats = StringPool.GetStats();
        
        // Assert
        var memoryReduction = ((double)(withoutPoolingMemory - withPoolingMemory) / withoutPoolingMemory) * 100;
        
        withPoolingMemory.Should().BeLessThan(withoutPoolingMemory, 
            "String pooling should reduce memory usage");
        
        poolStats.TotalStrings.Should().Be(pathCount, 
            $"Should have {pathCount} unique strings in pool");
        
        memoryReduction.Should().BeGreaterThan(50, 
            "String pooling should reduce memory by at least 50% with duplicates");
        
        Console.WriteLine($"Without pooling: {withoutPoolingMemory:N0} bytes ({totalPaths} strings)");
        Console.WriteLine($"With pooling: {withPoolingMemory:N0} bytes ({poolStats.TotalStrings} unique strings)");
        Console.WriteLine($"Memory reduction: {memoryReduction:F1}%");
        Console.WriteLine($"Compression ratio: {poolStats.CompressionRatio:P1}");
        
        // Keep references alive
        GC.KeepAlive(pathArray);
        GC.KeepAlive(pooledIds);
    }
    
    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void Memory_Usage_Should_Scale_Linearly(int fileCount)
    {
        // Arrange
        StringPool.Cleanup();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        var initialMemory = GC.GetTotalMemory(true);
        
        // Act
        var fastItems = new FastFileItem[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            fastItems[i] = new FastFileItem(
                $@"C:\Test\File_{i:D8}.txt", $"File_{i:D8}.txt", 
                @"C:\Test", ".txt", i * 1024,
                DateTime.Now, DateTime.Now, DateTime.Now,
                FileAttributes.Normal, 'C');
        }
        
        var finalMemory = GC.GetTotalMemory(false);
        var totalMemoryUsed = finalMemory - initialMemory;
        var memoryPerFile = (double)totalMemoryUsed / fileCount;
        
        // Assert
        memoryPerFile.Should().BeLessThan(1000, 
            "Memory per file should be less than 1KB");
        
        // Check for reasonable scaling
        if (fileCount == 10_000)
        {
            memoryPerFile.Should().BeLessThan(500, 
                "With string pooling, memory per file should be even lower at scale");
        }
        
        Console.WriteLine($"File count: {fileCount:N0}");
        Console.WriteLine($"Total memory: {totalMemoryUsed:N0} bytes");
        Console.WriteLine($"Memory per file: {memoryPerFile:F1} bytes");
        Console.WriteLine($"StringPool stats: {StringPool.GetStats().TotalStrings} unique strings");
        
        GC.KeepAlive(fastItems);
    }
    
    [Fact]
    public void Large_Dataset_Memory_Should_Remain_Stable()
    {
        // Arrange - Simulate processing large number of files in batches
        const int batchSize = 10_000;
        const int numberOfBatches = 5;
        var memoryReadings = new List<long>();
        
        StringPool.Cleanup();
        GC.Collect();
        
        // Act - Process batches and monitor memory
        for (int batch = 0; batch < numberOfBatches; batch++)
        {
            var batchItems = new FastFileItem[batchSize];
            
            for (int i = 0; i < batchSize; i++)
            {
                var fileId = batch * batchSize + i;
                batchItems[i] = new FastFileItem(
                    $@"C:\Batch{batch}\File_{fileId:D8}.txt",
                    $"File_{fileId:D8}.txt",
                    $@"C:\Batch{batch}",
                    ".txt",
                    fileId * 1024,
                    DateTime.Now, DateTime.Now, DateTime.Now,
                    FileAttributes.Normal, 'C');
            }
            
            // Simulate some processing
            var matches = 0;
            var pattern = "File".AsSpan();
            foreach (var item in batchItems)
            {
                if (item.MatchesName(pattern))
                    matches++;
            }
            
            // Force garbage collection and measure memory
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            var currentMemory = GC.GetTotalMemory(false);
            memoryReadings.Add(currentMemory);
            
            Console.WriteLine($"Batch {batch + 1}: {currentMemory:N0} bytes, {matches} matches");
            
            // Don't keep references to allow GC
            batchItems = null;
        }
        
        // Assert - Memory should not grow excessively
        var memoryGrowth = memoryReadings.Last() - memoryReadings.First();
        var maxMemory = memoryReadings.Max();
        var minMemory = memoryReadings.Min();
        var memoryVariance = maxMemory - minMemory;
        
        // Memory growth should be reasonable (less than 50MB for string pool growth)
        memoryGrowth.Should().BeLessThan(50 * 1024 * 1024, 
            "Memory growth across batches should be minimal due to string pooling");
        
        // Memory variance should be reasonable
        memoryVariance.Should().BeLessThan(100 * 1024 * 1024, 
            "Memory variance should be reasonable across batches");
        
        Console.WriteLine($"Memory growth: {memoryGrowth:N0} bytes");
        Console.WriteLine($"Memory variance: {memoryVariance:N0} bytes");
        Console.WriteLine($"Final StringPool: {StringPool.GetStats().TotalStrings} strings");
    }
    
    [Fact]
    public void SearchEngine_Memory_Should_Be_Bounded()
    {
        // Arrange
        using var searchEngine = FastFinder.CreateSearchEngine(NullLogger.Instance);
        var initialMemory = GC.GetTotalMemory(true);
        
        // Act - Perform many searches to test for memory leaks
        var query = new SearchQuery { SearchText = "test", MaxResults = 100 };
        
        for (int i = 0; i < 100; i++)
        {
            var result = searchEngine.SearchAsync(query).GetAwaiter().GetResult();
            
            // Force cleanup every 10 iterations
            if (i % 10 == 9)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        
        var finalMemory = GC.GetTotalMemory(true);
        var memoryIncrease = finalMemory - initialMemory;
        
        // Assert - Memory increase should be minimal
        memoryIncrease.Should().BeLessThan(10 * 1024 * 1024, 
            "Memory increase after many searches should be minimal (< 10MB)");
        
        Console.WriteLine($"Initial memory: {initialMemory:N0} bytes");
        Console.WriteLine($"Final memory: {finalMemory:N0} bytes");
        Console.WriteLine($"Memory increase: {memoryIncrease:N0} bytes");
    }
    
    [Fact]
    public void Memory_Cleanup_Should_Be_Effective()
    {
        // Arrange
        StringPool.Cleanup();
        var baseMemory = GC.GetTotalMemory(true);
        
        // Act - Create and release large dataset
        {
            var largeDataset = new FastFileItem[50_000];
            for (int i = 0; i < largeDataset.Length; i++)
            {
                largeDataset[i] = new FastFileItem(
                    $@"C:\LargeTest\File_{i:D8}.txt", $"File_{i:D8}.txt",
                    @"C:\LargeTest", ".txt", i * 1024,
                    DateTime.Now, DateTime.Now, DateTime.Now,
                    FileAttributes.Normal, 'C');
            }
            
            var peakMemory = GC.GetTotalMemory(false);
            Console.WriteLine($"Peak memory with dataset: {peakMemory:N0} bytes");
        } // Dataset goes out of scope
        
        // Force cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        // Cleanup string pool
        StringPool.Cleanup();
        
        var afterCleanupMemory = GC.GetTotalMemory(true);
        var memoryReclaimed = afterCleanupMemory - baseMemory;
        
        // Assert - Most memory should be reclaimed
        memoryReclaimed.Should().BeLessThan(5 * 1024 * 1024, 
            "Memory should be effectively reclaimed after cleanup (< 5MB remaining)");
        
        Console.WriteLine($"Base memory: {baseMemory:N0} bytes");
        Console.WriteLine($"After cleanup: {afterCleanupMemory:N0} bytes");
        Console.WriteLine($"Memory retained: {memoryReclaimed:N0} bytes");
    }
    
    [Fact]
    public void Memory_Pressure_Should_Trigger_Cleanup()
    {
        // This test simulates memory pressure and verifies cleanup mechanisms work
        
        // Arrange
        StringPool.Cleanup();
        var memoryReadings = new List<long>();
        
        // Act - Simulate memory pressure with temporary objects
        for (int cycle = 0; cycle < 10; cycle++)
        {
            // Create temporary memory pressure
            var tempObjects = new List<byte[]>();
            for (int i = 0; i < 100; i++)
            {
                tempObjects.Add(new byte[1024 * 1024]); // 1MB each
            }
            
            // Create some FastFileItems during pressure
            var items = new FastFileItem[1000];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new FastFileItem(
                    $@"C:\Pressure\File_{cycle}_{i:D4}.txt",
                    $"File_{cycle}_{i:D4}.txt",
                    $@"C:\Pressure",
                    ".txt", i * 1024,
                    DateTime.Now, DateTime.Now, DateTime.Now,
                    FileAttributes.Normal, 'C');
            }
            
            // Release temporary pressure
            tempObjects.Clear();
            
            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            var currentMemory = GC.GetTotalMemory(false);
            memoryReadings.Add(currentMemory);
            
            Console.WriteLine($"Cycle {cycle}: {currentMemory:N0} bytes, StringPool: {StringPool.GetStats().TotalStrings} strings");
        }
        
        // Assert - Memory should stabilize, not continuously grow
        var firstHalf = memoryReadings.Take(5).Average();
        var secondHalf = memoryReadings.Skip(5).Average();
        var growthRate = (secondHalf - firstHalf) / firstHalf;
        
        growthRate.Should().BeLessThan(0.5, 
            "Memory growth rate should be controlled under pressure (< 50%)");
        
        Console.WriteLine($"First half average: {firstHalf:N0} bytes");
        Console.WriteLine($"Second half average: {secondHalf:N0} bytes");
        Console.WriteLine($"Growth rate: {growthRate:P1}");
    }
    
    private static List<string> GenerateUniquePaths(int count)
    {
        var paths = new List<string>(count);
        var random = new Random(42);
        var drives = new[] { 'C', 'D', 'E' };
        var folders = new[] { "Documents", "Projects", "Test", "Data", "Files" };
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".cs", ".dll" };
        
        for (int i = 0; i < count; i++)
        {
            var drive = drives[random.Next(drives.Length)];
            var folder = folders[random.Next(folders.Length)];
            var subFolder = $"Sub_{random.Next(100):D2}";
            var fileName = $"UniqueFile_{i:D6}";
            var extension = extensions[random.Next(extensions.Length)];
            
            paths.Add($@"{drive}:\{folder}\{subFolder}\{fileName}{extension}");
        }
        
        return paths;
    }
}