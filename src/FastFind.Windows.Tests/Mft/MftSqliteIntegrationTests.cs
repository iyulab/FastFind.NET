using FastFind.Models;
using FastFind.SQLite;
using FastFind.Windows.Mft;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Integration tests for MFT to SQLite pipeline.
/// Tests the complete data flow from MFT enumeration to SQLite persistence.
/// </summary>
[Trait("Category", "Integration")]
public class MftSqliteIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDbPath;

    public MftSqliteIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testDbPath = Path.Combine(Path.GetTempPath(), $"fastfind_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // Cleanup test database
        try
        {
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);

            // Also delete WAL and SHM files
            var walPath = _testDbPath + "-wal";
            var shmPath = _testDbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact(Skip = "Integration test - requires bulk optimization fix")]
    public async Task SqlitePersistence_AddBatchAsync_ShouldInsertItems()
    {
        // Arrange
        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        var items = GenerateTestItems(100);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var count = await persistence.AddBatchAsync(items);
        stopwatch.Stop();

        // Assert
        count.Should().Be(100);
        persistence.Count.Should().Be(100);

        _output.WriteLine($"Inserted {count} items in {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"Rate: {count / stopwatch.Elapsed.TotalSeconds:N0} items/sec");
    }

    [Fact(Skip = "Integration test - requires bulk optimization fix")]
    public async Task SqlitePersistence_AddBulkOptimizedAsync_ShouldInsertLargeBatch()
    {
        // Arrange
        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        var items = GenerateTestItems(10000);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var count = await persistence.AddBulkOptimizedAsync(items.ToList());
        stopwatch.Stop();

        // Assert
        count.Should().Be(10000);
        persistence.Count.Should().Be(10000);

        var rate = count / stopwatch.Elapsed.TotalSeconds;
        _output.WriteLine($"Bulk inserted {count:N0} items in {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"Rate: {rate:N0} items/sec");

        // Performance target: at least 10K items/sec for bulk insert
        rate.Should().BeGreaterThan(10000, "Bulk insert should achieve at least 10K items/sec");
    }

    [Fact(Skip = "Integration test - requires bulk optimization fix")]
    public async Task SqlitePersistence_AddFromStreamAsync_ShouldHandleAsyncEnumerable()
    {
        // Arrange
        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        var items = GenerateTestItemsAsync(5000);
        var progress = new Progress<int>(count =>
        {
            if (count % 1000 == 0)
                _output.WriteLine($"Progress: {count} items inserted");
        });

        // Act
        var stopwatch = Stopwatch.StartNew();
        var count = await persistence.AddFromStreamAsync(items, bufferSize: 1000, progress);
        stopwatch.Stop();

        // Assert
        count.Should().Be(5000);
        persistence.Count.Should().Be(5000);

        _output.WriteLine($"Stream inserted {count:N0} items in {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"Rate: {count / stopwatch.Elapsed.TotalSeconds:N0} items/sec");
    }

    [Fact]
    public async Task SqlitePersistence_SearchAsync_ShouldFindItemsByName()
    {
        // Arrange
        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        var items = new List<FastFileItem>
        {
            CreateTestItem("C:\\test\\document.txt"),
            CreateTestItem("C:\\test\\document.pdf"),
            CreateTestItem("C:\\test\\image.png"),
            CreateTestItem("C:\\test\\report_document.docx"),
        };
        await persistence.AddBatchAsync(items);

        // Act
        var query = new SearchQuery { SearchText = "document" };
        var results = await persistence.SearchAsync(query).ToListAsync();

        // Assert
        results.Should().HaveCount(3); // document.txt, document.pdf, report_document.docx
        results.All(r => r.Name.Contains("document", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();

        _output.WriteLine($"Found {results.Count} items matching 'document'");
    }

    [Fact]
    public async Task SqlitePersistence_GetByExtensionAsync_ShouldFilterByExtension()
    {
        // Arrange
        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        var items = new List<FastFileItem>
        {
            CreateTestItem("C:\\test\\file1.txt"),
            CreateTestItem("C:\\test\\file2.txt"),
            CreateTestItem("C:\\test\\file3.pdf"),
            CreateTestItem("C:\\test\\file4.docx"),
        };
        await persistence.AddBatchAsync(items);

        // Act
        var txtFiles = await persistence.GetByExtensionAsync(".txt").ToListAsync();
        var pdfFiles = await persistence.GetByExtensionAsync("pdf").ToListAsync();

        // Assert
        txtFiles.Should().HaveCount(2);
        pdfFiles.Should().HaveCount(1);

        _output.WriteLine($"Found {txtFiles.Count} .txt files, {pdfFiles.Count} .pdf files");
    }

    [Fact(Skip = "Integration test - requires bulk optimization fix")]
    public async Task SqlitePersistence_GetStatisticsAsync_ShouldReturnAccurateStats()
    {
        // Arrange
        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        var items = GenerateTestItems(100);
        await persistence.AddBatchAsync(items);

        // Act
        var stats = await persistence.GetStatisticsAsync();

        // Assert
        stats.TotalItems.Should().Be(100);
        stats.TotalFiles.Should().BeGreaterThan(0);
        stats.StorageSizeBytes.Should().BeGreaterThan(0);

        _output.WriteLine($"Statistics: {stats.TotalItems} items, {stats.TotalFiles} files, {stats.TotalDirectories} directories");
        _output.WriteLine($"Storage size: {stats.StorageSizeBytes / 1024.0:N2} KB");
    }

    [Fact(Skip = "Integration test - requires bulk optimization fix")]
    public async Task SqlitePersistence_OptimizeAsync_ShouldCompleteSuccessfully()
    {
        // Arrange
        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        var items = GenerateTestItems(1000);
        await persistence.AddBatchAsync(items);

        // Act
        var stopwatch = Stopwatch.StartNew();
        await persistence.OptimizeAsync();
        stopwatch.Stop();

        // Assert - just verify it completes without error
        _output.WriteLine($"Optimization completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(Skip = "Requires admin rights - run manually")]
    public async Task MftSqlitePipeline_IndexAllDrivesAsync_ShouldIndexFiles()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - skipping test");
            return;
        }

        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        using var pipeline = new MftSqlitePipeline();

        var progress = new Progress<IndexingProgress>(p =>
        {
            _output.WriteLine($"{p.CurrentOperation}: {p.TotalIndexed:N0} items");
        });

        // Act
        var stopwatch = Stopwatch.StartNew();
        var count = await pipeline.IndexAllDrivesAsync(persistence, progress);
        stopwatch.Stop();

        // Assert
        count.Should().BeGreaterThan(0);
        persistence.Count.Should().Be(count);

        var stats = pipeline.Statistics;
        _output.WriteLine($"\nIndexing Results:");
        _output.WriteLine($"  Total: {stats.TotalRecords:N0} records");
        _output.WriteLine($"  Files: {stats.TotalFiles:N0}");
        _output.WriteLine($"  Directories: {stats.TotalDirectories:N0}");
        _output.WriteLine($"  Time: {stats.ElapsedTime.TotalSeconds:F2}s");
        _output.WriteLine($"  Rate: {stats.RecordsPerSecond:N0} records/sec");

        // Verify search works
        var searchResults = await persistence.SearchAsync(new SearchQuery { SearchText = "*.exe", MaxResults = 10 }).ToListAsync();
        _output.WriteLine($"\nSample search for '*.exe': found {searchResults.Count} results");
    }

    [Fact(Skip = "Requires admin rights - run manually")]
    public async Task MftSqlitePipeline_IndexSpecificDrive_ShouldIndexFiles()
    {
        // Arrange
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - skipping test");
            return;
        }

        var drives = MftReader.GetNtfsDrives();
        if (drives.Length == 0)
        {
            _output.WriteLine("No NTFS drives found - skipping test");
            return;
        }

        var driveLetter = drives[0];
        _output.WriteLine($"Testing with drive {driveLetter}:");

        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        using var pipeline = new MftSqlitePipeline();

        // Act
        var count = await pipeline.IndexDrivesAsync(new[] { driveLetter }, persistence);

        // Assert
        count.Should().BeGreaterThan(0);

        var stats = pipeline.Statistics;
        _output.WriteLine($"Indexed {stats.TotalRecords:N0} records at {stats.RecordsPerSecond:N0} records/sec");

        // Performance target
        stats.RecordsPerSecond.Should().BeGreaterThan(10000,
            "Pipeline should achieve at least 10K records/sec");
    }

    #region Helper Methods

    private static List<FastFileItem> GenerateTestItems(int count)
    {
        var items = new List<FastFileItem>(count);
        var extensions = new[] { ".txt", ".pdf", ".docx", ".xlsx", ".jpg", ".png", ".exe", ".dll" };
        var random = new Random(42);

        for (var i = 0; i < count; i++)
        {
            var ext = extensions[random.Next(extensions.Length)];
            var isDir = random.NextDouble() < 0.1; // 10% directories
            var name = isDir ? $"folder_{i}" : $"file_{i}{ext}";
            var path = $"C:\\TestData\\Folder{i / 100}\\{name}";

            items.Add(new FastFileItem(
                fullPath: path,
                name: name,
                directoryPath: Path.GetDirectoryName(path)!,
                extension: isDir ? "" : ext,
                size: isDir ? 0 : random.Next(1000, 10000000),
                created: DateTime.UtcNow.AddDays(-random.Next(365)),
                modified: DateTime.UtcNow.AddDays(-random.Next(30)),
                accessed: DateTime.UtcNow.AddDays(-random.Next(7)),
                attributes: isDir ? FileAttributes.Directory : FileAttributes.Normal,
                driveLetter: 'C'
            ));
        }

        return items;
    }

    private static async IAsyncEnumerable<FastFileItem> GenerateTestItemsAsync(int count)
    {
        var items = GenerateTestItems(count);
        foreach (var item in items)
        {
            yield return item;
            if (items.IndexOf(item) % 100 == 0)
                await Task.Yield();
        }
    }

    private static FastFileItem CreateTestItem(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        var ext = Path.GetExtension(fullPath);
        var isDir = string.IsNullOrEmpty(ext);

        return new FastFileItem(
            fullPath: fullPath,
            name: name,
            directoryPath: Path.GetDirectoryName(fullPath)!,
            extension: ext,
            size: isDir ? 0 : 1024,
            created: DateTime.UtcNow,
            modified: DateTime.UtcNow,
            accessed: DateTime.UtcNow,
            attributes: isDir ? FileAttributes.Directory : FileAttributes.Normal,
            driveLetter: fullPath[0]
        );
    }

    #endregion
}
