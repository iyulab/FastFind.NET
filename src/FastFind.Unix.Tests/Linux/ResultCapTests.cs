using AwesomeAssertions;
using FastFind.Models;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// The Unix half of the <c>MaxResults</c> / <c>TotalMatches</c> contract (the Windows half is
/// <c>ResultCapTests</c> in the Windows project): <c>HasMoreResults</c> is exact and <c>TotalMatches</c> is
/// what was found before the search stopped, never a count of the whole corpus.
/// </summary>
[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public sealed class ResultCapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fastfind-cap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<Interfaces.ISearchEngine> EngineWithAsync(int files)
    {
        Directory.CreateDirectory(_root);
        for (var i = 0; i < files; i++) File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "x");

        var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _root },
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing) await Task.Delay(10, timeout.Token);
        return engine;
    }

    private static async Task<(SearchResult Result, int Returned)> SearchAsync(Interfaces.ISearchEngine engine, int? maxResults)
    {
        var result = await engine.SearchAsync(new SearchQuery { SearchText = ".txt", SearchFileNameOnly = true, MaxResults = maxResults });
        var returned = 0;
        await foreach (var _ in result.Files) returned++;
        return (result, returned);
    }

    [LinuxFact]
    public async Task More_Matches_Than_The_Cap_Should_Return_The_Cap_And_Not_Count_The_Rest()
    {
        using var engine = await EngineWithAsync(20);

        var (result, returned) = await SearchAsync(engine, 5);

        returned.Should().Be(5);
        result.HasMoreResults.Should().BeTrue();
        result.TotalMatches.Should().BeGreaterThan(5).And.BeLessThanOrEqualTo(6, "the search stops one past the cap");
    }

    [LinuxFact]
    public async Task Exactly_The_Cap_Should_Not_Claim_There_Is_More()
    {
        using var engine = await EngineWithAsync(5);

        var (result, returned) = await SearchAsync(engine, 5);

        returned.Should().Be(5);
        result.HasMoreResults.Should().BeFalse();
        result.TotalMatches.Should().Be(5);
    }

    [LinuxFact]
    public async Task Without_A_Cap_Every_Match_Should_Be_Counted()
    {
        using var engine = await EngineWithAsync(12);

        var (result, returned) = await SearchAsync(engine, null);

        returned.Should().Be(12);
        result.HasMoreResults.Should().BeFalse();
        result.TotalMatches.Should().Be(12);
    }
}
