using FastFind.Indexing;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// What the Windows engine reports about the searches it has answered is measured, not assumed.
/// </summary>
/// <remarks>
/// It summed <c>Stopwatch.ElapsedMilliseconds</c>, an integer, so a search that took half a millisecond added nothing;
/// it never counted matches; and it reported <c>DateTime.Now</c> as the time of the last search whenever it was asked.
/// </remarks>
[Trait("Category", "Functional")]
public class SearchStatisticsTests
{
    private static async Task<WindowsSearchEngineImpl> IndexedEngineAsync()
    {
        var provider = new ScriptedFileSystemProvider();
        for (var i = 0; i < 3; i++)
            provider.Items.Add(ScriptedFileSystemProvider.File($@"C:\scripted\f{i}.txt"));

        var engine = new WindowsSearchEngineImpl(
            provider,
            new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance),
            new WindowsSearchEngineOptions(),
            NullLogger<WindowsSearchEngineImpl>.Instance);

        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { @"C:\scripted" },
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing) await Task.Delay(10, timeout.Token);
        return engine;
    }

    private static async Task SearchAsync(WindowsSearchEngineImpl engine, string text)
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
        stats.TotalMatches.Should().Be(0);
        stats.TotalSearchTime.Should().Be(TimeSpan.Zero);
        stats.AverageSearchTime.Should().Be(TimeSpan.Zero);
        stats.FastestSearchTime.Should().Be(TimeSpan.Zero);
        stats.SlowestSearchTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task After_Searches_The_Figures_Are_What_Was_Measured()
    {
        using var engine = await IndexedEngineAsync();

        await SearchAsync(engine, "f1");    // 1 match
        await SearchAsync(engine, "f");     // 3 matches
        await SearchAsync(engine, "zzzz");  // 0 matches

        var stats = await engine.GetSearchStatisticsAsync();

        stats.TotalSearches.Should().Be(3);
        stats.TotalMatches.Should().Be(4, "1 + 3 + 0");
        stats.TotalSearchTime.Should().BeGreaterThan(TimeSpan.Zero,
            "a search under a millisecond still took time; it used to be rounded to nothing");
        stats.FastestSearchTime.Should().BeGreaterThan(TimeSpan.Zero);
        stats.FastestSearchTime.Should().BeLessThanOrEqualTo(stats.AverageSearchTime);
        stats.AverageSearchTime.Should().BeLessThanOrEqualTo(stats.SlowestSearchTime);
        stats.TotalSearchTime.Should().BeGreaterThanOrEqualTo(stats.SlowestSearchTime);
        stats.LastSearch.Kind.Should().Be(DateTimeKind.Utc);
        stats.LastSearch.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_Time_Of_The_Last_Search_Is_Not_The_Time_Of_The_Question()
    {
        using var engine = await IndexedEngineAsync();
        await SearchAsync(engine, "f1");

        var first = (await engine.GetSearchStatisticsAsync()).LastSearch;
        await Task.Delay(300);
        var second = (await engine.GetSearchStatisticsAsync()).LastSearch;

        second.Should().Be(first, "no search happened in between");
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
    }
}
