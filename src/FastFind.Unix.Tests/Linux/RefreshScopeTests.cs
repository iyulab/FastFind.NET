using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// Refreshing a location replaces what is indexed beneath it and nothing else.
/// </summary>
[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public sealed class RefreshScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fastfind-refresh-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Refreshing_A_Directory_Should_Leave_A_Sibling_That_Shares_Its_Prefix()
    {
        if (!OperatingSystem.IsLinux()) return;

        Directory.CreateDirectory(Path.Combine(_root, "app"));
        Directory.CreateDirectory(Path.Combine(_root, "app-tests"));
        File.WriteAllText(Path.Combine(_root, "app", "main.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "app-tests", "main-tests.cs"), "x");

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        });

        await engine.RefreshIndexAsync([Path.Combine(_root, "app")]);

        (await Names(engine)).Should().Contain(["main.cs", "main-tests.cs"]);
    }

    [Fact]
    public async Task Overlapping_Locations_Should_Return_Each_Entry_Once()
    {
        if (!OperatingSystem.IsLinux()) return;

        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllText(Path.Combine(_root, "a", "b", "leaf.cs"), "x");

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        });

        var query = new SearchQuery { SearchText = "leaf.cs", SearchFileNameOnly = true };
        query.SearchLocations.Add(Path.Combine(_root, "a"));
        query.SearchLocations.Add(Path.Combine(_root, "a", "b"));
        var result = await engine.SearchAsync(query);

        (await result.Files.Select(f => f.FullPath).ToListAsync()).Should().ContainSingle();
    }

    private async Task<List<string>> Names(Interfaces.ISearchEngine engine)
    {
        var result = await engine.SearchAsync(new SearchQuery { BasePath = _root, SearchText = "main", SearchFileNameOnly = true });
        return await result.Files.Select(f => f.Name).ToListAsync();
    }
}
