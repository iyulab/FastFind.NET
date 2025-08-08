using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastFind.Windows.Tests.Performance;

/// <summary>
/// Performance benchmarks for search operations
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SearchPerformanceBenchmarks
{
    private ISearchEngine? _searchEngine;
    private List<FileItem> _testFiles = [];
    private SearchQuery _simpleQuery = new();
    private SearchQuery _complexQuery = new();
    private SearchQuery _regexQuery = new();
    
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _searchEngine = FastFinder.CreateSearchEngine(NullLogger.Instance);
        _testFiles = GenerateTestFiles(10_000);
        
        // Add test files to search engine
        foreach (var file in _testFiles)
        {
            // Simulate indexing by adding files to internal structures
        }
        
        _simpleQuery = new SearchQuery 
        { 
            SearchText = "test",
            MaxResults = 100
        };
        
        _complexQuery = new SearchQuery
        {
            SearchText = "document",
            ExtensionFilter = ".txt",
            MinSize = 1024,
            MaxSize = 1024 * 1024,
            MaxResults = 50
        };
        
        _regexQuery = new SearchQuery
        {
            SearchText = @"file_\d{3}",
            UseRegex = true,
            MaxResults = 100
        };
    }
    
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _searchEngine?.Dispose();
    }
    
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Search")]
    public async Task<SearchResult> SimpleTextSearch()
    {
        return await _searchEngine!.SearchAsync(_simpleQuery);
    }
    
    [Benchmark]
    [BenchmarkCategory("Search")]
    public async Task<SearchResult> ComplexFilteredSearch()
    {
        return await _searchEngine!.SearchAsync(_complexQuery);
    }
    
    [Benchmark]
    [BenchmarkCategory("Search")]
    public async Task<SearchResult> RegexSearch()
    {
        return await _searchEngine!.SearchAsync(_regexQuery);
    }
    
    [Benchmark]
    [BenchmarkCategory("Search")]
    public async Task<SearchResult> WildcardSearch()
    {
        var query = new SearchQuery { SearchText = "*.cs", MaxResults = 200 };
        return await _searchEngine!.SearchAsync(query);
    }
    
    [Benchmark]
    [BenchmarkCategory("StringMatching")]
    public bool SIMDStringMatching()
    {
        var text = "This is a performance test string with various content for benchmarking";
        var pattern = "performance";
        return SIMDStringMatcher.ContainsVectorized(text.AsSpan(), pattern.AsSpan());
    }
    
    [Benchmark]
    [BenchmarkCategory("StringMatching")]
    public bool NativeStringMatching()
    {
        var text = "This is a performance test string with various content for benchmarking";
        var pattern = "performance";
        return text.Contains(pattern, StringComparison.Ordinal);
    }
    
    [Benchmark]
    [BenchmarkCategory("StringMatching")]
    public bool WildcardMatching()
    {
        var text = "test_file_123.txt";
        var pattern = "test_*.txt";
        return SIMDStringMatcher.MatchesWildcard(text.AsSpan(), pattern.AsSpan());
    }
    
    [Benchmark]
    [BenchmarkCategory("StringPool")]
    public int StringPoolIntern()
    {
        var path = @"C:\Test\Performance\BenchmarkFile.txt";
        return StringPool.InternPath(path);
    }
    
    [Benchmark]
    [BenchmarkCategory("StringPool")]
    public string StringPoolRetrieve()
    {
        return StringPool.GetString(1); // Assuming ID 1 exists from setup
    }
    
    [Benchmark]
    [BenchmarkCategory("FileItem")]
    public FastFileItem CreateFastFileItem()
    {
        return new FastFileItem(
            @"C:\Test\File.txt", "File.txt", @"C:\Test", ".txt",
            1024, DateTime.Now, DateTime.Now, DateTime.Now,
            FileAttributes.Normal, 'C');
    }
    
    [Benchmark]
    [BenchmarkCategory("FileItem")]
    public FileItem CreateRegularFileItem()
    {
        return new FileItem
        {
            FullPath = @"C:\Test\File.txt",
            Name = "File.txt",
            Directory = @"C:\Test",
            Extension = ".txt",
            Size = 1024,
            CreatedTime = DateTime.Now,
            ModifiedTime = DateTime.Now,
            AccessedTime = DateTime.Now,
            Attributes = FileAttributes.Normal
        };
    }
    
    [Benchmark]
    [BenchmarkCategory("FileItem")]
    public bool FastFileItemMatching()
    {
        var item = new FastFileItem(
            @"C:\Test\Document.txt", "Document.txt", @"C:\Test", ".txt",
            2048, DateTime.Now, DateTime.Now, DateTime.Now,
            FileAttributes.Normal, 'C');
        return item.MatchesName("Document".AsSpan());
    }
    
    [Params(100, 1000, 10000)]
    public int FileCount { get; set; }
    
    [Benchmark]
    [BenchmarkCategory("Scalability")]
    public int ProcessFileList()
    {
        var files = GenerateTestFiles(FileCount);
        var matches = 0;
        
        foreach (var file in files)
        {
            if (file.Name.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                matches++;
            }
        }
        
        return matches;
    }
    
    [Benchmark]
    [BenchmarkCategory("Scalability")]
    public int ProcessFastFileList()
    {
        var files = GenerateFastTestFiles(FileCount);
        var matches = 0;
        var pattern = "test".AsSpan();
        
        foreach (var file in files)
        {
            if (file.MatchesName(pattern))
            {
                matches++;
            }
        }
        
        return matches;
    }
    
    [Benchmark]
    [BenchmarkCategory("Memory")]
    public void AllocateFileItems()
    {
        var files = new FileItem[1000];
        for (int i = 0; i < files.Length; i++)
        {
            files[i] = new FileItem
            {
                FullPath = $@"C:\Test\File_{i:D4}.txt",
                Name = $"File_{i:D4}.txt",
                Directory = @"C:\Test",
                Extension = ".txt",
                Size = i * 1024,
                CreatedTime = DateTime.Now,
                ModifiedTime = DateTime.Now,
                AccessedTime = DateTime.Now,
                Attributes = FileAttributes.Normal
            };
        }
        
        // Force memory usage calculation
        GC.KeepAlive(files);
    }
    
    [Benchmark]
    [BenchmarkCategory("Memory")]
    public void AllocateFastFileItems()
    {
        var files = new FastFileItem[1000];
        for (int i = 0; i < files.Length; i++)
        {
            files[i] = new FastFileItem(
                $@"C:\Test\File_{i:D4}.txt", $"File_{i:D4}.txt", @"C:\Test", ".txt",
                i * 1024, DateTime.Now, DateTime.Now, DateTime.Now,
                FileAttributes.Normal, 'C');
        }
        
        // Force memory usage calculation
        GC.KeepAlive(files);
    }
    
    private static List<FileItem> GenerateTestFiles(int count)
    {
        var files = new List<FileItem>(count);
        var random = new Random(42);
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".cs", ".dll" };
        var folders = new[] { @"C:\Documents", @"C:\Projects", @"C:\Test", @"D:\Files" };
        
        for (int i = 0; i < count; i++)
        {
            var folder = folders[random.Next(folders.Length)];
            var extension = extensions[random.Next(extensions.Length)];
            var fileName = i % 10 == 0 ? $"test_file_{i}" : $"file_{i:D4}";
            
            files.Add(new FileItem
            {
                FullPath = $@"{folder}\{fileName}{extension}",
                Name = $"{fileName}{extension}",
                Directory = folder,
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
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".cs", ".dll" };
        var folders = new[] { @"C:\Documents", @"C:\Projects", @"C:\Test", @"D:\Files" };
        
        for (int i = 0; i < count; i++)
        {
            var folder = folders[random.Next(folders.Length)];
            var extension = extensions[random.Next(extensions.Length)];
            var fileName = i % 10 == 0 ? $"test_file_{i}" : $"file_{i:D4}";
            var fullName = $"{fileName}{extension}";
            var fullPath = $@"{folder}\{fullName}";
            
            files.Add(new FastFileItem(
                fullPath, fullName, folder, extension,
                random.Next(1024, 1024 * 1024),
                DateTime.Now.AddDays(-random.Next(365)),
                DateTime.Now.AddDays(-random.Next(30)),
                DateTime.Now.AddDays(-random.Next(7)),
                FileAttributes.Normal,
                folder[0]));
        }
        
        return files;
    }
}

/// <summary>
/// Test runner for performance benchmarks
/// </summary>
public static class BenchmarkRunner
{
    public static void RunSearchBenchmarks()
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);
        
        BenchmarkDotNet.Running.BenchmarkRunner.Run<SearchPerformanceBenchmarks>(config);
    }
}