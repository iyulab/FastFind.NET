using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// A monitored directory's rename or deletion reaches everything indexed beneath it. The watcher
/// reports each once, for the directory.
/// </summary>
[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public sealed class DirectoryTreeMonitoringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fastfind-tree-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Renaming_And_Deleting_Directories_Should_Carry_Their_Subtrees()
    {
        if (!OperatingSystem.IsLinux()) return;

        Directory.CreateDirectory(Path.Combine(_root, "proj", "src", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "junk", "sub"));
        File.WriteAllText(Path.Combine(_root, "proj", "src", "deep", "leaf.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "proj", "top.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "junk", "sub", "old.log"), "x");

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _root },
            EnableMonitoring = true,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        });
        await WaitUntil(() => !engine.IsIndexing);
        (await Where(engine, "leaf.cs")).Should().Equal(Path.Combine(_root, "proj", "src", "deep", "leaf.cs"));

        Directory.Move(Path.Combine(_root, "proj"), Path.Combine(_root, "renamed"));
        Directory.Delete(Path.Combine(_root, "junk"), recursive: true);

        var movedLeaf = Path.Combine(_root, "renamed", "src", "deep", "leaf.cs");
        await WaitUntilAsync(async () => (await Where(engine, "leaf.cs")).SequenceEqual([movedLeaf]));

        (await Where(engine, "leaf.cs")).Should().Equal(movedLeaf);
        (await Where(engine, "top.cs")).Should().Equal(Path.Combine(_root, "renamed", "top.cs"));
        (await Where(engine, "old.log")).Should().BeEmpty();
        (await Where(engine, "sub")).Should().BeEmpty();
    }

    private async Task<List<string>> Where(Interfaces.ISearchEngine engine, string name)
    {
        var result = await engine.SearchAsync(new SearchQuery { SearchText = name, SearchFileNameOnly = true, BasePath = _root });
        var paths = new List<string>();
        await foreach (var item in result.Files) paths.Add(item.FullPath);
        return paths;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }
}
