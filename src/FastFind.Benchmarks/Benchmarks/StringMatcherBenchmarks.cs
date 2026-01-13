using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastFind.Benchmarks.Infrastructure;

namespace FastFind.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for string matching operations
/// Compares SIMD-accelerated matching with native .NET methods
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[ShortRunJob]
public class StringMatcherBenchmarks
{
    private string[] _testStrings = null!;
    private string _searchPattern = null!;

    [Params(1000, 10000, 50000)]
    public int StringCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _testStrings = TestDataGenerator.GenerateFilePaths(StringCount);
        _searchPattern = "document";
    }

    [Benchmark(Baseline = true)]
    public int Native_StringContains()
    {
        int count = 0;
        foreach (var str in _testStrings)
        {
            if (str.Contains(_searchPattern, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    [Benchmark]
    public int Native_StringContains_Ordinal()
    {
        int count = 0;
        foreach (var str in _testStrings)
        {
            if (str.Contains(_searchPattern, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    [Benchmark]
    public int Span_Contains()
    {
        int count = 0;
        var patternSpan = _searchPattern.AsSpan();
        foreach (var str in _testStrings)
        {
            if (str.AsSpan().Contains(patternSpan, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    [Benchmark]
    public int IndexOf_Ordinal()
    {
        int count = 0;
        foreach (var str in _testStrings)
        {
            if (str.IndexOf(_searchPattern, StringComparison.Ordinal) >= 0)
                count++;
        }
        return count;
    }

    [Benchmark]
    public int IndexOf_OrdinalIgnoreCase()
    {
        int count = 0;
        foreach (var str in _testStrings)
        {
            if (str.IndexOf(_searchPattern, StringComparison.OrdinalIgnoreCase) >= 0)
                count++;
        }
        return count;
    }
}
