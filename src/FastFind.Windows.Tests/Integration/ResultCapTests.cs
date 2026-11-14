using AwesomeAssertions;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// What <c>MaxResults</c> and <c>TotalMatches</c> promise: <c>HasMoreResults</c> is exact, and
/// <c>TotalMatches</c> is the number of matches found before the search stopped — the exact count when nothing
/// lies beyond the cap.
/// </summary>
/// <remarks>
/// The Windows engine used to say "more" when the matches equalled the cap, and the Unix engine drained the
/// whole index to count them; the two meant different things by the same number. The Unix half of this contract
/// is held by the same cases in the Unix project (Linux host).
/// </remarks>
[Trait("Category", "Functional")]
public class ResultCapTests
{
    private static async Task<WindowsSearchEngineImpl> EngineWithAsync(int items)
    {
        var provider = new ScriptedFileSystemProvider();
        for (var i = 0; i < items; i++)
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

    private static async Task<(SearchResult Result, int Returned)> SearchAsync(WindowsSearchEngineImpl engine, int? maxResults)
    {
        var result = await engine.SearchAsync(new SearchQuery { SearchText = "f", SearchFileNameOnly = true, MaxResults = maxResults });
        var returned = 0;
        await foreach (var _ in result.Files) returned++;
        return (result, returned);
    }

    [Fact]
    public async Task More_Matches_Than_The_Cap_Should_Return_The_Cap_And_Say_There_Is_More()
    {
        using var engine = await EngineWithAsync(20);

        var (result, returned) = await SearchAsync(engine, 5);

        returned.Should().Be(5);
        result.HasMoreResults.Should().BeTrue();
        result.TotalMatches.Should().BeGreaterThan(5, "one past the cap was found, which is what proves there is more");
    }

    [Fact]
    public async Task Exactly_The_Cap_Should_Not_Claim_There_Is_More()
    {
        using var engine = await EngineWithAsync(5);

        var (result, returned) = await SearchAsync(engine, 5);

        returned.Should().Be(5);
        result.HasMoreResults.Should().BeFalse("nothing lies beyond the cap");
        result.TotalMatches.Should().Be(5);
    }

    [Fact]
    public async Task Fewer_Matches_Than_The_Cap_Should_Be_Counted_Exactly()
    {
        using var engine = await EngineWithAsync(3);

        var (result, returned) = await SearchAsync(engine, 10);

        returned.Should().Be(3);
        result.HasMoreResults.Should().BeFalse();
        result.TotalMatches.Should().Be(3);
    }

    [Fact]
    public async Task Without_A_Cap_Every_Match_Should_Be_Returned_And_Counted()
    {
        using var engine = await EngineWithAsync(12);

        var (result, returned) = await SearchAsync(engine, null);

        returned.Should().Be(12);
        result.HasMoreResults.Should().BeFalse();
        result.TotalMatches.Should().Be(12);
    }
}
