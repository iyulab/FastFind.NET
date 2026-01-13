using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows;
using FastFind.Benchmarks.Infrastructure;

namespace FastFind.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for search engine operations
/// Measures search performance with various query types
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[ShortRunJob]
public class SearchEngineBenchmarks
{
    private ISearchEngine _searchEngine = null!;
    private string _testDirectory = null!;
    private bool _isIndexed = false;

    [GlobalSetup]
    public async Task Setup()
    {
        WindowsRegistration.EnsureRegistered();
        _searchEngine = FastFinder.CreateWindowsSearchEngine();
        _testDirectory = TestDataGenerator.GetTestDirectory();

        // Index a small directory for benchmarking
        var options = new IndexingOptions
        {
            SpecificDirectories = [_testDirectory],
            IncludeHidden = false,
            IncludeSystem = false,
            MaxDepth = 3 // Limit depth for faster indexing
        };

        try
        {
            await _searchEngine.StartIndexingAsync(options);

            // Wait for indexing with timeout
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.UtcNow;
            while (_searchEngine.IsIndexing && DateTime.UtcNow - startTime < timeout)
            {
                await Task.Delay(100);
            }

            _isIndexed = _searchEngine.TotalIndexedFiles > 0;
        }
        catch
        {
            _isIndexed = false;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _searchEngine?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Search_SimpleText()
    {
        if (!_isIndexed) return 0;

        var query = new SearchQuery
        {
            SearchText = "test",
            MaxResults = 100
        };

        var result = await _searchEngine.SearchAsync(query);
        return await result.Files.CountAsync();
    }

    [Benchmark]
    public async Task<int> Search_WithExtension()
    {
        if (!_isIndexed) return 0;

        var query = new SearchQuery
        {
            SearchText = "*",
            ExtensionFilter = ".txt",
            MaxResults = 100
        };

        var result = await _searchEngine.SearchAsync(query);
        return await result.Files.CountAsync();
    }

    [Benchmark]
    public async Task<int> Search_WithBasePath()
    {
        if (!_isIndexed) return 0;

        var query = new SearchQuery
        {
            BasePath = _testDirectory,
            SearchText = "doc",
            MaxResults = 100
        };

        var result = await _searchEngine.SearchAsync(query);
        return await result.Files.CountAsync();
    }

    [Benchmark]
    public async Task<int> Search_FileNameOnly()
    {
        if (!_isIndexed) return 0;

        var query = new SearchQuery
        {
            SearchText = "readme",
            SearchFileNameOnly = true,
            MaxResults = 100
        };

        var result = await _searchEngine.SearchAsync(query);
        return await result.Files.CountAsync();
    }

    [Benchmark]
    public async Task<int> Search_WithSizeFilter()
    {
        if (!_isIndexed) return 0;

        var query = new SearchQuery
        {
            SearchText = "*",
            MinSize = 1024,
            MaxSize = 1024 * 1024,
            MaxResults = 100
        };

        var result = await _searchEngine.SearchAsync(query);
        return await result.Files.CountAsync();
    }

    [Benchmark]
    public int SearchQuery_Creation()
    {
        var count = 0;
        for (int i = 0; i < 1000; i++)
        {
            var query = new SearchQuery
            {
                BasePath = _testDirectory,
                SearchText = $"search{i}",
                IncludeSubdirectories = true,
                MaxResults = 100
            };
            count++;
        }
        return count;
    }
}
