using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using FluentAssertions;

namespace FastFind.Unix.Tests.MacOS;

[Trait("Category", "Functional")]
[Trait("OS", "macOS")]
public class MacOSSearchEngineTests : IClassFixture<TestFileTreeFixture>, IDisposable
{
    private readonly TestFileTreeFixture _fixture;
    private readonly Interfaces.ISearchEngine? _engine;

    public MacOSSearchEngineTests(TestFileTreeFixture fixture)
    {
        _fixture = fixture;
        if (OperatingSystem.IsMacOS())
            _engine = UnixSearchEngine.CreateMacOSSearchEngine();
    }

    private async Task EnsureIndexedAsync()
    {
        if (_engine!.TotalIndexedFiles > 0) return;

        var options = new IndexingOptions
        {
            SpecificDirectories = { _fixture.RootPath },
            IncludeHidden = true,
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };
        await _engine!.StartIndexingAsync(options);
    }

    [Fact]
    public async Task SearchAsync_ByText_ShouldFindMatches()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var result = await _engine!.SearchAsync("file1");
        result.TotalMatches.Should().BeGreaterThan(0);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
            items.Add(item);

        items.Should().Contain(i => i.Name == "file1.txt");
    }

    [Fact]
    public async Task SearchAsync_NoMatch_ShouldReturnEmpty()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var result = await _engine!.SearchAsync("nonexistent_xyz_12345");
        result.TotalMatches.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_ByExtension_ShouldFilter()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var query = new SearchQuery { ExtensionFilter = ".txt" };
        var result = await _engine!.SearchAsync(query);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
            items.Add(item);

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.Extension == ".txt");
    }

    [Fact]
    public async Task GetIndexingStatisticsAsync_AfterIndex_ShouldReturnStats()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await EnsureIndexedAsync();

        var stats = await _engine!.GetIndexingStatisticsAsync();
        stats.TotalFiles.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StopIndexingAsync_ShouldNotThrow()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var act = () => _engine!.StopIndexingAsync();
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        _engine?.Dispose();
    }
}
