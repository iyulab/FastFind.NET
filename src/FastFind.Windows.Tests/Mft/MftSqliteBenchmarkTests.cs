using FastFind.Models;
using FastFind.SQLite;
using FastFind.Windows.Mft;
using FluentAssertions;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Benchmark tests for MFT to SQLite pipeline performance.
/// Run with admin rights for full MFT access.
/// </summary>
[Trait("Category", "Performance")]
public class MftSqliteBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDbPath;

    public MftSqliteBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        _testDbPath = Path.Combine(Path.GetTempPath(), $"fastfind_bench_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }
        catch { }
    }

    [Fact(Skip = "Performance test - run manually")]
    public async Task Benchmark_SqliteBulkInsert_Performance()
    {
        _output.WriteLine("=== SQLite Bulk Insert Benchmark ===\n");

        var testSizes = new[] { 1000, 10000, 50000, 100000 };

        foreach (var size in testSizes)
        {
            // Fresh database for each test
            var dbPath = _testDbPath + $"_{size}";
            try
            {
                await using var persistence = SqlitePersistence.CreateHighPerformance(dbPath);
                await persistence.InitializeAsync();

                var items = GenerateTestItems(size);

                // Warm-up
                GC.Collect();
                GC.WaitForPendingFinalizers();

                var stopwatch = Stopwatch.StartNew();
                var count = await persistence.AddBulkOptimizedAsync(items.ToList());
                stopwatch.Stop();

                var rate = count / stopwatch.Elapsed.TotalSeconds;
                var dbSize = new FileInfo(dbPath).Length / 1024.0 / 1024.0;

                _output.WriteLine($"{size,8:N0} items: {stopwatch.ElapsedMilliseconds,6}ms ({rate,10:N0} items/sec) - DB: {dbSize:F2}MB");

                count.Should().Be(size);
            }
            finally
            {
                try { File.Delete(dbPath); File.Delete(dbPath + "-wal"); File.Delete(dbPath + "-shm"); } catch { }
            }
        }
    }

    [Fact(Skip = "Performance test - run manually")]
    public async Task Benchmark_SqliteSearch_Performance()
    {
        _output.WriteLine("=== SQLite Search Benchmark ===\n");

        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        // Insert 100K items
        var items = GenerateTestItems(100000);
        await persistence.AddBulkOptimizedAsync(items.ToList());
        await persistence.OptimizeAsync();

        _output.WriteLine($"Database prepared with {persistence.Count:N0} items\n");

        // Benchmark different search patterns
        var searchPatterns = new[]
        {
            ("Exact name", new SearchQuery { SearchText = "file_50000.txt" }),
            ("Wildcard prefix", new SearchQuery { SearchText = "file_5*" }),
            ("Extension filter", new SearchQuery { SearchText = "*.pdf" }),
            ("Full-text search", new SearchQuery { SearchText = "document" }),
        };

        foreach (var (name, query) in searchPatterns)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await persistence.SearchAsync(query).ToListAsync();
            stopwatch.Stop();

            _output.WriteLine($"{name,20}: {results.Count,6} results in {stopwatch.ElapsedMilliseconds,4}ms");
        }

        // Benchmark extension queries
        _output.WriteLine("\nExtension Queries:");
        var extensions = new[] { ".txt", ".pdf", ".exe", ".dll" };
        foreach (var ext in extensions)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await persistence.GetByExtensionAsync(ext).ToListAsync();
            stopwatch.Stop();

            _output.WriteLine($"  {ext}: {results.Count,6} results in {stopwatch.ElapsedMilliseconds,4}ms");
        }
    }

    [Fact(Skip = "Performance test - run manually with admin rights")]
    public async Task Benchmark_MftEnumeration_SingleDrive()
    {
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - requires admin rights");
            return;
        }

        var drives = MftReader.GetNtfsDrives();
        if (drives.Length == 0)
        {
            _output.WriteLine("No NTFS drives found");
            return;
        }

        _output.WriteLine("=== MFT Enumeration Benchmark ===\n");

        using var reader = new MftReader();

        foreach (var drive in drives)
        {
            var stopwatch = Stopwatch.StartNew();
            long fileCount = 0, dirCount = 0;

            await foreach (var record in reader.EnumerateFilesAsync(drive))
            {
                if (record.IsDirectory) dirCount++;
                else fileCount++;
            }

            stopwatch.Stop();
            var total = fileCount + dirCount;
            var rate = total / stopwatch.Elapsed.TotalSeconds;

            _output.WriteLine($"Drive {drive}: {total,10:N0} records ({fileCount:N0} files, {dirCount:N0} dirs)");
            _output.WriteLine($"         Time: {stopwatch.Elapsed.TotalSeconds:F2}s, Rate: {rate:N0} records/sec\n");
        }
    }

    [Fact(Skip = "Performance test - run manually with admin rights")]
    public async Task Benchmark_FullPipeline_MftToSqlite()
    {
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - requires admin rights");
            return;
        }

        var drives = MftReader.GetNtfsDrives();
        if (drives.Length == 0)
        {
            _output.WriteLine("No NTFS drives found");
            return;
        }

        _output.WriteLine("=== Full Pipeline Benchmark (MFT -> SQLite) ===\n");

        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        using var pipeline = new MftSqlitePipeline();

        var lastReport = DateTime.UtcNow;
        var progress = new Progress<IndexingProgress>(p =>
        {
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalSeconds >= 2 || p.IsComplete)
            {
                _output.WriteLine($"  {p.CurrentOperation}: {p.TotalIndexed:N0} items");
                lastReport = now;
            }
        });

        // Index first drive only for faster benchmark
        var driveLetter = drives[0];
        _output.WriteLine($"Indexing drive {driveLetter}:\n");

        var stopwatch = Stopwatch.StartNew();
        var count = await pipeline.IndexDrivesAsync(new[] { driveLetter }, persistence, progress);
        stopwatch.Stop();

        var stats = pipeline.Statistics;
        var dbSize = new FileInfo(_testDbPath).Length / 1024.0 / 1024.0;

        _output.WriteLine($"\n=== Results ===");
        _output.WriteLine($"Total records: {stats.TotalRecords:N0}");
        _output.WriteLine($"  Files: {stats.TotalFiles:N0}");
        _output.WriteLine($"  Directories: {stats.TotalDirectories:N0}");
        _output.WriteLine($"Time: {stats.ElapsedTime.TotalSeconds:F2}s");
        _output.WriteLine($"Throughput: {stats.RecordsPerSecond:N0} records/sec");
        _output.WriteLine($"Database size: {dbSize:F2} MB");

        // Performance assertions
        stats.RecordsPerSecond.Should().BeGreaterThan(10000,
            "Full pipeline should achieve at least 10K records/sec");

        // Test search after indexing
        _output.WriteLine("\n=== Search Performance ===");
        var searchTests = new[] { "*.exe", "*.dll", "system", "windows" };

        foreach (var searchText in searchTests)
        {
            var searchStopwatch = Stopwatch.StartNew();
            var query = new SearchQuery { SearchText = searchText, MaxResults = 1000 };
            var results = await persistence.SearchAsync(query).ToListAsync();
            searchStopwatch.Stop();

            _output.WriteLine($"  '{searchText}': {results.Count} results in {searchStopwatch.ElapsedMilliseconds}ms");
        }
    }

    [Fact(Skip = "Performance test - run manually with admin rights")]
    public async Task Benchmark_ParallelDrives_MftToSqlite()
    {
        if (!MftReader.IsAvailable())
        {
            _output.WriteLine("MFT access not available - requires admin rights");
            return;
        }

        var drives = MftReader.GetNtfsDrives();
        if (drives.Length < 2)
        {
            _output.WriteLine("Need at least 2 NTFS drives for parallel benchmark");
            return;
        }

        _output.WriteLine("=== Parallel Multi-Drive Benchmark ===\n");
        _output.WriteLine($"Indexing {drives.Length} drives: {string.Join(", ", drives.Select(d => $"{d}:"))}\n");

        await using var persistence = SqlitePersistence.CreateHighPerformance(_testDbPath);
        await persistence.InitializeAsync();

        using var pipeline = new MftSqlitePipeline();

        var stopwatch = Stopwatch.StartNew();
        var count = await pipeline.IndexAllDrivesAsync(persistence);
        stopwatch.Stop();

        var stats = pipeline.Statistics;
        var dbSize = new FileInfo(_testDbPath).Length / 1024.0 / 1024.0;

        _output.WriteLine($"=== Results ===");
        _output.WriteLine($"Total records: {stats.TotalRecords:N0}");
        _output.WriteLine($"Time: {stats.ElapsedTime.TotalSeconds:F2}s");
        _output.WriteLine($"Throughput: {stats.RecordsPerSecond:N0} records/sec");
        _output.WriteLine($"Database size: {dbSize:F2} MB");

        if (stats.DriveStats != null)
        {
            _output.WriteLine("\nPer-drive statistics:");
            foreach (var driveStat in stats.DriveStats)
            {
                _output.WriteLine($"  {driveStat.DriveLetter}: {driveStat.RecordCount:N0} records at {driveStat.RecordsPerSecond:N0}/sec");
            }
        }
    }

    #region Helper Methods

    private static List<FastFileItem> GenerateTestItems(int count)
    {
        var items = new List<FastFileItem>(count);
        var extensions = new[] { ".txt", ".pdf", ".docx", ".xlsx", ".jpg", ".png", ".exe", ".dll", ".cs", ".json" };
        var random = new Random(42);

        for (var i = 0; i < count; i++)
        {
            var ext = extensions[random.Next(extensions.Length)];
            var isDir = random.NextDouble() < 0.1;
            var name = isDir ? $"folder_{i}" : $"file_{i}{ext}";
            var folderNum = i / 1000;
            var path = $"C:\\TestData\\Level1_{folderNum / 10}\\Level2_{folderNum % 10}\\{name}";

            items.Add(new FastFileItem(
                fullPath: path,
                name: name,
                directoryPath: Path.GetDirectoryName(path)!,
                extension: isDir ? "" : ext,
                size: isDir ? 0 : random.Next(100, 50000000),
                created: DateTime.UtcNow.AddDays(-random.Next(730)),
                modified: DateTime.UtcNow.AddDays(-random.Next(365)),
                accessed: DateTime.UtcNow.AddDays(-random.Next(30)),
                attributes: isDir ? FileAttributes.Directory : FileAttributes.Normal,
                driveLetter: 'C'
            ));
        }

        return items;
    }

    #endregion
}
