using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastFind.Benchmarks.Infrastructure;

namespace FastFind.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for string pooling operations
/// Measures interning performance and memory efficiency
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[ShortRunJob]
public class StringPoolBenchmarks
{
    private string[] _uniquePaths = null!;
    private string[] _duplicatePaths = null!;

    [Params(1000, 5000, 10000)]
    public int PathCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _uniquePaths = TestDataGenerator.GenerateFilePaths(PathCount);

        // Create duplicate paths (same path repeated multiple times)
        var basePaths = TestDataGenerator.GenerateFilePaths(PathCount / 10);
        _duplicatePaths = new string[PathCount];
        for (int i = 0; i < PathCount; i++)
        {
            _duplicatePaths[i] = basePaths[i % basePaths.Length];
        }
    }

    [Benchmark(Baseline = true)]
    public int Dictionary_Interning_Unique()
    {
        var dict = new Dictionary<string, int>();
        int nextId = 0;

        foreach (var path in _uniquePaths)
        {
            if (!dict.TryGetValue(path, out var id))
            {
                id = nextId++;
                dict[path] = id;
            }
        }

        return dict.Count;
    }

    [Benchmark]
    public int Dictionary_Interning_Duplicate()
    {
        var dict = new Dictionary<string, int>();
        int nextId = 0;

        foreach (var path in _duplicatePaths)
        {
            if (!dict.TryGetValue(path, out var id))
            {
                id = nextId++;
                dict[path] = id;
            }
        }

        return dict.Count;
    }

    [Benchmark]
    public int StringIntern_Unique()
    {
        var internedStrings = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in _uniquePaths)
        {
            var interned = string.Intern(path);
            internedStrings.Add(interned);
        }

        return internedStrings.Count;
    }

    [Benchmark]
    public int StringIntern_Duplicate()
    {
        var internedStrings = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in _duplicatePaths)
        {
            var interned = string.Intern(path);
            internedStrings.Add(interned);
        }

        return internedStrings.Count;
    }

    [Benchmark]
    public int ConcurrentDictionary_Interning()
    {
        var dict = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int nextId = 0;

        foreach (var path in _uniquePaths)
        {
            dict.GetOrAdd(path, _ => Interlocked.Increment(ref nextId));
        }

        return dict.Count;
    }
}
