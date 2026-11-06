using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// What the Unix engine reports about the searches it has answered is measured, not assumed.
/// </summary>
/// <remarks>
/// It reported a 10 ms average, a 1 ms fastest and a 100 ms slowest as constants - before a single search, and
/// whatever the searches had actually taken.
/// </remarks>
[Trait("Category", "Functional")]
public class SearchStatisticsTests : IDisposable
{
    private readonly string _root;

    public SearchStatisticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-searchstats-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static UnixSearchEngineImpl NewEngine() => new(new FastFind.Unix.Linux.LinuxFileSystemProvider());

    private async Task<UnixSearchEngineImpl> IndexedEngineAsync()
    {
        var engine = NewEngine();
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _root },
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            MaxFileSize = null,
        });
        return engine;
    }

    private static async Task SearchAsync(UnixSearchEngineImpl engine, string text)
    {
        var result = await engine.SearchAsync(new SearchQuery { SearchText = text, SearchFileNameOnly = true, MaxResults = 100 });
        await foreach (var _ in result.Files) { }
    }

    [Fact]
    public async Task Before_Any_Search_The_Times_Are_Zero_Not_Estimates()
    {
        using var engine = await IndexedEngineAsync();

        var stats = await engine.GetSearchStatisticsAsync();

        stats.TotalSearches.Should().Be(0);
        stats.AverageSearchTime.Should().Be(TimeSpan.Zero, "it said 10 ms");
        stats.FastestSearchTime.Should().Be(TimeSpan.Zero, "it said 1 ms");
        stats.SlowestSearchTime.Should().Be(TimeSpan.Zero, "it said 100 ms");
        stats.TotalSearchTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task After_Searches_The_Figures_Are_What_Was_Measured()
    {
        using var engine = await IndexedEngineAsync();

        await SearchAsync(engine, "f1");
        await SearchAsync(engine, "f");
        await SearchAsync(engine, "zzzz");

        var stats = await engine.GetSearchStatisticsAsync();

        stats.TotalSearches.Should().Be(3);
        stats.TotalMatches.Should().Be(4, "1 + 3 + 0");
        stats.TotalSearchTime.Should().BeGreaterThan(TimeSpan.Zero);
        stats.FastestSearchTime.Should().BeGreaterThan(TimeSpan.Zero);
        stats.FastestSearchTime.Should().BeLessThanOrEqualTo(stats.AverageSearchTime);
        stats.AverageSearchTime.Should().BeLessThanOrEqualTo(stats.SlowestSearchTime);
        stats.TotalSearchTime.Should().BeGreaterThanOrEqualTo(stats.SlowestSearchTime);
        stats.AverageSearchTime.Should().NotBe(TimeSpan.FromMilliseconds(10), "the old constant");
        stats.LastSearch.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Clearing_The_Cache_Forgets_The_Measurements()
    {
        using var engine = await IndexedEngineAsync();
        await SearchAsync(engine, "f");

        await engine.ClearCacheAsync();

        var stats = await engine.GetSearchStatisticsAsync();
        stats.TotalSearches.Should().Be(0);
        stats.TotalSearchTime.Should().Be(TimeSpan.Zero);
        stats.SlowestSearchTime.Should().Be(TimeSpan.Zero);
    }
}
