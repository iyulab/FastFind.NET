using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// A refresh re-indexes a location the way the index was built, exclusions included.
/// </summary>
/// <remarks>
/// <c>RefreshIndexAsync</c> built its own <see cref="IndexingOptions"/> with an empty exclusion list
/// and hidden files forced on, because the engine kept none of the options it indexed with. Every
/// refresh therefore pulled back exactly what the index had been told to leave out.
/// </remarks>
[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class RefreshExclusionTests : IClassFixture<TestFileTreeFixture>
{
    private readonly TestFileTreeFixture _fixture;

    public RefreshExclusionTests(TestFileTreeFixture fixture) => _fixture = fixture;

    private static async Task<List<string>> NamesAsync(Interfaces.ISearchEngine engine)
    {
        var result = await engine.SearchAsync(new SearchQuery { SearchText = "file" });

        var names = new List<string>();
        await foreach (var item in result.Files) names.Add(item.Name);
        return names;
    }

    [Fact]
    public async Task RefreshIndexAsync_ShouldKeepHonouringTheExclusionsTheIndexWasBuiltWith()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();

        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _fixture.RootPath },
            IncludeHidden = false,
            EnableMonitoring = false,
            ExcludedPaths = new List<string> { "sub1" },
            ExcludedExtensions = new List<string>(),
        });

        var afterIndexing = await NamesAsync(engine);
        afterIndexing.Should().NotContain("file3.txt", "sub1 was excluded");
        afterIndexing.Should().Contain("file1.txt");

        await engine.RefreshIndexAsync(new[] { _fixture.RootPath });

        var afterRefresh = await NamesAsync(engine);
        afterRefresh.Should().NotContain("file3.txt", "a refresh must not re-index an excluded subtree");
        afterRefresh.Should().NotContain("file4.log");
        afterRefresh.Should().Contain("file1.txt", "the rest of the tree is still there");
    }
}
