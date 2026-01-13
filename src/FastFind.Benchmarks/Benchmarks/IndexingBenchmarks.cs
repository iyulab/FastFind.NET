using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows;
using FastFind.Benchmarks.Infrastructure;

namespace FastFind.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for indexing operations
/// Measures file enumeration and indexing performance
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[ShortRunJob]
public class IndexingBenchmarks
{
    private string _testDirectory = null!;

    [GlobalSetup]
    public void Setup()
    {
        WindowsRegistration.EnsureRegistered();
        _testDirectory = TestDataGenerator.GetTestDirectory();
    }

    [Benchmark(Baseline = true)]
    public int DirectoryEnumerate_GetFiles()
    {
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_testDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true
            }))
            {
                count++;
            }
        }
        catch
        {
            // Ignore access errors
        }
        return count;
    }

    [Benchmark]
    public int DirectoryEnumerate_GetFileSystemEntries()
    {
        var count = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_testDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true
            }))
            {
                count++;
            }
        }
        catch
        {
            // Ignore access errors
        }
        return count;
    }

    [Benchmark]
    public async Task<long> SearchEngine_StartIndexing()
    {
        using var engine = FastFinder.CreateWindowsSearchEngine();

        var options = new IndexingOptions
        {
            SpecificDirectories = [_testDirectory],
            IncludeHidden = false,
            IncludeSystem = false,
            MaxDepth = 3
        };

        await engine.StartIndexingAsync(options);

        // Wait for completion with timeout
        var timeout = TimeSpan.FromSeconds(15);
        var startTime = DateTime.UtcNow;
        while (engine.IsIndexing && DateTime.UtcNow - startTime < timeout)
        {
            await Task.Delay(50);
        }

        return engine.TotalIndexedFiles;
    }

    [Benchmark]
    public int FileInfo_Creation()
    {
        var count = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_testDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 2,
                IgnoreInaccessible = true
            }).Take(1000))
            {
                var info = new FileInfo(path);
                // Access some properties to simulate real usage
                _ = info.Length;
                _ = info.Name;
                count++;
            }
        }
        catch
        {
            // Ignore access errors
        }
        return count;
    }
}
