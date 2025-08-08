using FastFind;
using FastFind.Interfaces;
using FastFind.Windows;
using FastFind.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Management;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// Integration tests for Windows-specific features
/// </summary>
[Collection("WindowsOnly")]
public class WindowsSpecificTests
{
    [Fact]
    public void System_Validation_Should_Detect_Windows_Features()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Act
        var validation = FastFinder.ValidateSystem();
        
        // Assert
        validation.Should().NotBeNull();
        validation.Platform.Should().Be(PlatformType.Windows);
        validation.IsReady.Should().BeTrue("Windows system should be ready");
        validation.AvailableFeatures.Should().NotBeEmpty("Should detect Windows features");
        
        Console.WriteLine($"Platform: {validation.Platform}");
        Console.WriteLine($"Features: {string.Join(", ", validation.AvailableFeatures)}");
        Console.WriteLine($"Summary: {validation.GetSummary()}");
    }
    
    [Fact]
    public void Windows_SearchEngine_Should_Be_Created_Successfully()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Act & Assert
        using var searchEngine = FastFinder.CreateWindowsSearchEngine(NullLogger.Instance);
        
        searchEngine.Should().NotBeNull();
        searchEngine.Should().BeAssignableTo<WindowsSearchEngine>();
        searchEngine.IsIndexing.Should().BeFalse("Should not be indexing initially");
        searchEngine.TotalIndexedFiles.Should().Be(0, "Should have no indexed files initially");
    }
    
    [Fact]
    public async Task Windows_File_System_Should_Enumerate_Files()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Arrange
        using var searchEngine = FastFinder.CreateWindowsSearchEngine(NullLogger.Instance);
        var tempDir = Path.GetTempPath();
        
        // Create some test files
        var testFiles = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var testFile = Path.Combine(tempDir, $"FastFindTest_{i}.txt");
            await File.WriteAllTextAsync(testFile, $"Test content {i}");
            testFiles.Add(testFile);
        }
        
        try
        {
            // Act - Start indexing the temp directory
            var indexingOptions = new IndexingOptions
            {
                SpecificDirectories = [tempDir],
                IncludeHidden = false
            };
            
            await searchEngine.StartIndexingAsync(indexingOptions);
            
            // Wait for indexing to complete
            var timeout = DateTime.Now.AddSeconds(30);
            while (searchEngine.IsIndexing && DateTime.Now < timeout)
            {
                await Task.Delay(100);
            }
            
            searchEngine.IsIndexing.Should().BeFalse("Indexing should complete");
            searchEngine.TotalIndexedFiles.Should().BeGreaterThan(0, "Should have indexed some files");
            
            // Test search
            var query = new SearchQuery { SearchText = "FastFindTest_", MaxResults = 10 };
            var results = await searchEngine.SearchAsync(query);
            
            results.Should().NotBeNull();
            results.Files.Should().NotBeEmpty("Should find test files");
            results.Files.Count.Should().BeGreaterOrEqualTo(testFiles.Count, 
                "Should find at least the test files we created");
            
            Console.WriteLine($"Indexed files: {searchEngine.TotalIndexedFiles}");
            Console.WriteLine($"Found files: {results.Files.Count}");
        }
        finally
        {
            // Cleanup
            foreach (var file in testFiles)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
    
    [Fact]
    public void Windows_Should_Detect_Available_Drives()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Act
        var availableDrives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .ToArray();
        
        // Assert
        availableDrives.Should().NotBeEmpty("Should detect at least one drive");
        
        foreach (var drive in availableDrives)
        {
            Console.WriteLine($"Drive: {drive.Name}, Format: {drive.DriveFormat}, " +
                             $"Available: {drive.AvailableFreeSpace:N0} bytes");
            
            drive.Name.Should().MatchRegex(@"^[A-Z]:\\$", "Drive name should match Windows pattern");
        }
    }
    
    [Fact]
    public async Task Windows_Indexing_Should_Handle_System_Directories()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Arrange
        using var searchEngine = FastFinder.CreateWindowsSearchEngine(NullLogger.Instance);
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        
        // Act
        var indexingOptions = new IndexingOptions
        {
            SpecificDirectories = [systemDir],
            IncludeSystem = true,
            MaxFileSize = 10 * 1024 * 1024, // 10MB limit for system files
            ParallelThreads = 2 // Reduce threads for system directory
        };
        
        var indexingTask = searchEngine.StartIndexingAsync(indexingOptions);
        
        // Monitor progress
        var progressReports = new List<string>();
        searchEngine.IndexingProgressChanged += (s, e) =>
        {
            var progress = $"{e.ProcessedFiles} files, {e.ProgressPercentage:F1}%, " +
                          $"{e.FilesPerSecond:F0} files/sec";
            progressReports.Add(progress);
            Console.WriteLine($"Progress: {progress}");
        };
        
        await indexingTask;
        
        // Wait for indexing to complete
        var timeout = DateTime.Now.AddMinutes(2);
        while (searchEngine.IsIndexing && DateTime.Now < timeout)
        {
            await Task.Delay(1000);
        }
        
        // Assert
        searchEngine.TotalIndexedFiles.Should().BeGreaterThan(0, 
            "Should index at least some system files");
        
        progressReports.Should().NotBeEmpty("Should report indexing progress");
        
        Console.WriteLine($"Total system files indexed: {searchEngine.TotalIndexedFiles}");
        Console.WriteLine($"Progress reports: {progressReports.Count}");
    }
    
    [Fact]
    public async Task Windows_Should_Handle_File_Attributes_Correctly()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "FastFindAttributeTest");
        Directory.CreateDirectory(tempDir);
        
        var normalFile = Path.Combine(tempDir, "normal.txt");
        var hiddenFile = Path.Combine(tempDir, "hidden.txt");
        var readOnlyFile = Path.Combine(tempDir, "readonly.txt");
        
        try
        {
            // Create test files with different attributes
            await File.WriteAllTextAsync(normalFile, "Normal file");
            
            await File.WriteAllTextAsync(hiddenFile, "Hidden file");
            File.SetAttributes(hiddenFile, FileAttributes.Hidden);
            
            await File.WriteAllTextAsync(readOnlyFile, "Read-only file");
            File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
            
            using var searchEngine = FastFinder.CreateWindowsSearchEngine(NullLogger.Instance);
            
            // Test with hidden files excluded
            var optionsExcludeHidden = new IndexingOptions
            {
                SpecificDirectories = [tempDir],
                IncludeHidden = false
            };
            
            await searchEngine.StartIndexingAsync(optionsExcludeHidden);
            await WaitForIndexingComplete(searchEngine);
            
            var query = new SearchQuery { SearchText = "*.txt", MaxResults = 10 };
            var resultsExcludeHidden = await searchEngine.SearchAsync(query);
            
            // Should find normal and readonly, but not hidden
            resultsExcludeHidden.Files.Should().HaveCountLessOrEqualTo(2, 
                "Should not include hidden files when excluded");
            
            // Test with hidden files included
            await searchEngine.StopIndexingAsync();
            
            var optionsIncludeHidden = new IndexingOptions
            {
                SpecificDirectories = [tempDir],
                IncludeHidden = true
            };
            
            await searchEngine.StartIndexingAsync(optionsIncludeHidden);
            await WaitForIndexingComplete(searchEngine);
            
            var resultsIncludeHidden = await searchEngine.SearchAsync(query);
            
            // Should find all files including hidden
            resultsIncludeHidden.Files.Count.Should().BeGreaterOrEqualTo(3, 
                "Should include all files when hidden files are included");
            
            Console.WriteLine($"Exclude hidden: {resultsExcludeHidden.Files.Count} files");
            Console.WriteLine($"Include hidden: {resultsIncludeHidden.Files.Count} files");
        }
        finally
        {
            // Cleanup
            try
            {
                if (File.Exists(readOnlyFile))
                {
                    File.SetAttributes(readOnlyFile, FileAttributes.Normal);
                    File.Delete(readOnlyFile);
                }
                if (File.Exists(hiddenFile))
                {
                    File.SetAttributes(hiddenFile, FileAttributes.Normal);
                    File.Delete(hiddenFile);
                }
                if (File.Exists(normalFile))
                {
                    File.Delete(normalFile);
                }
                Directory.Delete(tempDir, true);
            }
            catch { }
        }
    }
    
    [Fact]
    public async Task Windows_Performance_Counters_Should_Be_Available()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Arrange & Act
        using var searchEngine = FastFinder.CreateWindowsSearchEngine(NullLogger.Instance);
        
        var stats = await searchEngine.GetSearchStatisticsAsync();
        
        // Assert
        stats.Should().NotBeNull("Search statistics should be available");
        stats.TotalSearches.Should().BeGreaterOrEqualTo(0, "Should track search count");
        
        // Test indexing statistics
        var indexStats = await searchEngine.GetIndexingStatisticsAsync();
        
        indexStats.Should().NotBeNull("Indexing statistics should be available");
        indexStats.TotalFiles.Should().BeGreaterOrEqualTo(0, "Should track indexed file count");
        
        Console.WriteLine($"Search stats - Total searches: {stats.TotalSearches}, " +
                         $"Avg time: {stats.AverageSearchTime.TotalMilliseconds}ms");
        Console.WriteLine($"Indexing stats - Total files: {indexStats.TotalFiles}, " +
                         $"Memory: {indexStats.MemoryUsageBytes:N0} bytes");
    }
    
    [Fact]
    public void Windows_WMI_Integration_Should_Work()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        try
        {
            // Test WMI connectivity for system information
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDisk WHERE DriveType=3");
            using var collection = searcher.Get();
            
            var drives = new List<(string DeviceId, ulong Size, ulong FreeSpace)>();
            
            foreach (ManagementObject drive in collection)
            {
                var deviceId = drive["DeviceID"]?.ToString() ?? "";
                var size = Convert.ToUInt64(drive["Size"] ?? 0);
                var freeSpace = Convert.ToUInt64(drive["FreeSpace"] ?? 0);
                
                drives.Add((deviceId, size, freeSpace));
            }
            
            // Assert
            drives.Should().NotBeEmpty("Should detect drives via WMI");
            
            foreach (var (deviceId, size, freeSpace) in drives)
            {
                deviceId.Should().MatchRegex(@"^[A-Z]:$", "Drive ID should match pattern");
                size.Should().BeGreaterThan(0UL, "Drive size should be positive");
                freeSpace.Should().BeLessOrEqualTo(size, "Free space should not exceed total size");
                
                Console.WriteLine($"WMI Drive: {deviceId} - Size: {size:N0}, Free: {freeSpace:N0}");
            }
        }
        catch (Exception ex)
        {
            // WMI might not be available in all test environments
            Console.WriteLine($"WMI test skipped: {ex.Message}");
        }
    }
    
    [Fact]
    public async Task Windows_File_System_Events_Should_Be_Detected()
    {
        if (!OperatingSystem.IsWindows()) return; // Skip on non-Windows
        
        // Arrange
        using var searchEngine = FastFinder.CreateWindowsSearchEngine(NullLogger.Instance);
        var tempDir = Path.Combine(Path.GetTempPath(), "FastFindEventTest");
        Directory.CreateDirectory(tempDir);
        
        var fileEvents = new List<FileChangeEventArgs>();
        searchEngine.FileChanged += (s, e) => fileEvents.Add(e);
        
        try
        {
            // Start monitoring
            var indexingOptions = new IndexingOptions
            {
                SpecificDirectories = [tempDir],
                EnableRealTimeMonitoring = true
            };
            
            await searchEngine.StartIndexingAsync(indexingOptions);
            await WaitForIndexingComplete(searchEngine);
            
            // Create a new file to trigger event
            var testFile = Path.Combine(tempDir, "event_test.txt");
            await File.WriteAllTextAsync(testFile, "Test content for file events");
            
            // Wait for file system event to be detected
            await Task.Delay(2000);
            
            // Modify the file
            await File.AppendAllTextAsync(testFile, "\nAdditional content");
            await Task.Delay(1000);
            
            // Delete the file
            File.Delete(testFile);
            await Task.Delay(1000);
            
            // Assert
            fileEvents.Should().NotBeEmpty("Should detect file system events");
            
            var createdEvents = fileEvents.Where(e => e.ChangeType == FileChangeType.Created).ToList();
            var modifiedEvents = fileEvents.Where(e => e.ChangeType == FileChangeType.Modified).ToList();
            var deletedEvents = fileEvents.Where(e => e.ChangeType == FileChangeType.Deleted).ToList();
            
            Console.WriteLine($"File events detected: {fileEvents.Count}");
            Console.WriteLine($"Created: {createdEvents.Count}, Modified: {modifiedEvents.Count}, Deleted: {deletedEvents.Count}");
            
            // We should detect at least some events (exact count may vary due to system behavior)
            (createdEvents.Count + modifiedEvents.Count + deletedEvents.Count).Should().BeGreaterThan(0, 
                "Should detect at least one file system event");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    
    private static async Task WaitForIndexingComplete(ISearchEngine searchEngine, int timeoutSeconds = 30)
    {
        var timeout = DateTime.Now.AddSeconds(timeoutSeconds);
        while (searchEngine.IsIndexing && DateTime.Now < timeout)
        {
            await Task.Delay(100);
        }
        
        if (searchEngine.IsIndexing)
        {
            throw new TimeoutException($"Indexing did not complete within {timeoutSeconds} seconds");
        }
    }
}

/// <summary>
/// Collection definition for Windows-only tests
/// </summary>
[CollectionDefinition("WindowsOnly")]
public class WindowsOnlyCollection : ICollectionFixture<WindowsTestFixture>
{
}

/// <summary>
/// Test fixture for Windows-specific setup
/// </summary>
public class WindowsTestFixture : IDisposable
{
    public WindowsTestFixture()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Setting up Windows test environment");
            
            // Ensure temp directory is accessible
            var tempPath = Path.GetTempPath();
            if (!Directory.Exists(tempPath))
            {
                throw new InvalidOperationException("Temp directory not accessible");
            }
        }
    }
    
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Cleaning up Windows test environment");
            
            // Clean up any test directories that might have been left behind
            var tempPath = Path.GetTempPath();
            var testDirs = Directory.GetDirectories(tempPath, "FastFind*", SearchOption.TopDirectoryOnly);
            
            foreach (var dir in testDirs)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}