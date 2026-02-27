using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("OS", "Linux")]
public class EndToEndTests : IDisposable
{
    private readonly string _testDir;

    public EndToEndTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"fastfind-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        for (int i = 0; i < 100; i++)
        {
            var subDir = Path.Combine(_testDir, $"dir{i % 10}");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, $"test{i}.txt"), $"content {i}");
            File.WriteAllText(Path.Combine(subDir, $"data{i}.cs"), $"class C{i} {{}}");
        }
    }

    [Fact]
    public async Task FullPipeline_Index_Search_ShouldWork()
    {
        if (!OperatingSystem.IsLinux()) return;

        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();

        var options = new IndexingOptions
        {
            SpecificDirectories = { _testDir },
            IncludeHidden = true,
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        await engine.StartIndexingAsync(options);
        engine.TotalIndexedFiles.Should().BeGreaterThan(0);

        var result = await engine.SearchAsync("test");
        result.TotalMatches.Should().BeGreaterThan(0);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
        {
            items.Add(item);
        }
        items.Should().NotBeEmpty();
        items.Should().AllSatisfy(i => i.Name.Should().Contain("test"));
    }

    [Fact]
    public async Task Search_ByExtension_ShouldFilter()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        var options = new IndexingOptions
        {
            SpecificDirectories = { _testDir },
            IncludeHidden = true,
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        await engine.StartIndexingAsync(options);

        var query = new SearchQuery { ExtensionFilter = ".cs" };
        var result = await engine.SearchAsync(query);

        var items = new List<FastFileItem>();
        await foreach (var item in result.Files)
        {
            items.Add(item);
        }
        items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task IndexingStatistics_AfterIndexing_ShouldBePopulated()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        var options = new IndexingOptions
        {
            SpecificDirectories = { _testDir },
            IncludeHidden = true,
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>()
        };

        await engine.StartIndexingAsync(options);

        var stats = await engine.GetIndexingStatisticsAsync();
        stats.TotalFiles.Should().BeGreaterThan(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
