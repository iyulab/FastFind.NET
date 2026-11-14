using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using AwesomeAssertions;

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

    /// <remarks>
    /// InvalidOperationException, not NotSupportedException: saving an index is supported on this
    /// platform now, and this engine simply was not given a store. The distinction matters to a
    /// caller — one says "never possible here", the other says "compose it differently" — and the
    /// message names the fix. Composing with a store is covered in
    /// <see cref="Core.EngineCompositionTests"/>.
    /// </remarks>
    [LinuxFact]
    public async Task SaveIndexAsync_Without_A_Store_ShouldThrowInvalidOperation()
    {
        var act = () => _engine!.SaveIndexAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no persistence store*");
    }

    /// <inheritdoc cref="SaveIndexAsync_Without_A_Store_ShouldThrowInvalidOperation"/>
    [LinuxFact]
    public async Task LoadIndexAsync_Without_A_Store_ShouldThrowInvalidOperation()
    {
        var act = () => _engine!.LoadIndexAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no persistence store*");
    }

    [LinuxFact]
    public async Task OptimizeIndexAsync_ShouldNotThrow()
    {
        var act = () => _engine!.OptimizeIndexAsync();
        await act.Should().NotThrowAsync();
    }

    [LinuxFact]
    public async Task RefreshIndexAsync_ShouldReindexLocations()
    {
        await EnsureIndexedAsync();
        var countBefore = _engine!.TotalIndexedFiles;
        countBefore.Should().BeGreaterThan(0);

        // Refresh should re-index the same location
        await _engine!.RefreshIndexAsync(new[] { _fixture.RootPath });

        _engine!.TotalIndexedFiles.Should().BeGreaterThan(0);
    }

    [LinuxFact]
    public async Task RefreshIndexAsync_NoLocations_ShouldDoNothing()
    {
        await EnsureIndexedAsync();
        var countBefore = _engine!.TotalIndexedFiles;

        await _engine!.RefreshIndexAsync();

        _engine!.TotalIndexedFiles.Should().Be(countBefore);
    }

    [LinuxFact]
    public async Task SearchAsync_ByText_ShouldFindMatches()
    {
        await EnsureIndexedAsync();

        var result = await _engine!.SearchAsync("file1");
        result.TotalMatches.Should().BeGreaterThan(0);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
            items.Add(item);

        items.Should().Contain(i => i.Name == "file1.txt");
    }

    [LinuxFact]
    public async Task SearchAsync_NoMatch_ShouldReturnEmpty()
    {
        await EnsureIndexedAsync();

        var result = await _engine!.SearchAsync("nonexistent_xyz_12345");
        result.TotalMatches.Should().Be(0);
    }

    [LinuxFact]
    public async Task SearchAsync_ByExtension_ShouldFilter()
    {
        await EnsureIndexedAsync();

        var query = new SearchQuery { ExtensionFilter = ".txt" };
        var result = await _engine!.SearchAsync(query);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
            items.Add(item);

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.Extension == ".txt");
    }

    [LinuxFact]
    public async Task GetIndexingStatisticsAsync_AfterIndex_ShouldReturnStats()
    {
        await EnsureIndexedAsync();

        var stats = await _engine!.GetIndexingStatisticsAsync();
        stats.TotalFiles.Should().BeGreaterThan(0);
    }

    [LinuxFact]
    public async Task StopIndexingAsync_ShouldNotThrow()
    {
        var act = () => _engine!.StopIndexingAsync();
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        _engine?.Dispose();
    }
}
