using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class UnixSearchEngineTests : IClassFixture<TestFileTreeFixture>, IDisposable
{
    private readonly TestFileTreeFixture _fixture;
    private readonly Interfaces.ISearchEngine? _engine;

    public UnixSearchEngineTests(TestFileTreeFixture fixture)
    {
        _fixture = fixture;
        if (OperatingSystem.IsLinux())
            _engine = UnixSearchEngine.CreateLinuxSearchEngine();
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
    public async Task SaveIndexAsync_ShouldThrowNotSupported()
    {
        if (!OperatingSystem.IsLinux()) return;

        var act = () => _engine!.SaveIndexAsync();
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task LoadIndexAsync_ShouldThrowNotSupported()
    {
        if (!OperatingSystem.IsLinux()) return;

        var act = () => _engine!.LoadIndexAsync();
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task OptimizeIndexAsync_ShouldNotThrow()
    {
        if (!OperatingSystem.IsLinux()) return;

        var act = () => _engine!.OptimizeIndexAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RefreshIndexAsync_ShouldReindexLocations()
    {
        if (!OperatingSystem.IsLinux()) return;

        await EnsureIndexedAsync();
        var countBefore = _engine!.TotalIndexedFiles;
        countBefore.Should().BeGreaterThan(0);

        // Refresh should re-index the same location
        await _engine!.RefreshIndexAsync(new[] { _fixture.RootPath });

        _engine!.TotalIndexedFiles.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RefreshIndexAsync_NoLocations_ShouldDoNothing()
    {
        if (!OperatingSystem.IsLinux()) return;

        await EnsureIndexedAsync();
        var countBefore = _engine!.TotalIndexedFiles;

        await _engine!.RefreshIndexAsync();

        _engine!.TotalIndexedFiles.Should().Be(countBefore);
    }

    [Fact]
    public async Task SearchAsync_ByText_ShouldFindMatches()
    {
        if (!OperatingSystem.IsLinux()) return;

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
        if (!OperatingSystem.IsLinux()) return;

        await EnsureIndexedAsync();

        var result = await _engine!.SearchAsync("nonexistent_xyz_12345");
        result.TotalMatches.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_ByExtension_ShouldFilter()
    {
        if (!OperatingSystem.IsLinux()) return;

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
        if (!OperatingSystem.IsLinux()) return;

        await EnsureIndexedAsync();

        var stats = await _engine!.GetIndexingStatisticsAsync();
        stats.TotalFiles.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StopIndexingAsync_ShouldNotThrow()
    {
        if (!OperatingSystem.IsLinux()) return;

        var act = () => _engine!.StopIndexingAsync();
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        _engine?.Dispose();
    }
}
