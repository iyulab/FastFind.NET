using FastFind.Models;
using AwesomeAssertions;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// A file created after the pass meets the options the pass applied: the engine's monitored path used to
/// index it with no check at all.
/// </summary>
[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public sealed class MonitoredAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fastfind-admit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [LinuxFact]
    public async Task A_Created_File_Should_Meet_The_Pass_Exclusions()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "f0.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "f1.txt"), "x");

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _root },
            EnableMonitoring = true,
            IncludeHidden = false,
            ExcludedExtensions = new List<string> { ".tmp" },
            ExcludedPaths = new List<string>(),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing) await Task.Delay(20, timeout.Token);
        await Task.Delay(500); // the watcher is armed once the pass has ended

        File.WriteAllText(Path.Combine(_root, "scratch.tmp"), "x");
        File.WriteAllText(Path.Combine(_root, ".secret"), "x");
        File.WriteAllText(Path.Combine(_root, "keep.txt"), "x");

        await WaitUntil(async () => (await Where(engine, "keep.txt")).Count == 1);

        (await Where(engine, "keep.txt")).Should().HaveCount(1, "an admitted file is still indexed");
        (await Where(engine, "scratch.tmp")).Should().BeEmpty("the pass would have refused the extension");
        (await Where(engine, ".secret")).Should().BeEmpty("the pass would have skipped a hidden file");
    }

    [LinuxFact]
    public async Task A_Change_Beneath_A_Hidden_Directory_Should_Not_Be_Indexed_Though_The_File_Is_Not_Hidden()
    {
        // A pass does not enter .cache, so nothing beneath it is read; a file created there is not hidden itself.
        Directory.CreateDirectory(Path.Combine(_root, ".cache", "objects"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "f0.txt"), "x");

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { _root },
            EnableMonitoring = true,
            IncludeHidden = false,
            ExcludedExtensions = new List<string>(),
            ExcludedPaths = new List<string>(),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing) await Task.Delay(20, timeout.Token);
        await Task.Delay(500); // the watcher is armed once the pass has ended

        File.WriteAllText(Path.Combine(_root, ".cache", "objects", "pack.bin"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "keep.cs"), "x");

        await WaitUntil(async () => (await Where(engine, "keep.cs")).Count == 1);
        await Task.Delay(500); // give the other event the same chance to arrive

        (await Where(engine, "keep.cs")).Should().HaveCount(1, "a sibling directory is not hidden");
        (await Where(engine, "pack.bin")).Should().BeEmpty("the pass would not have entered .cache");
    }

    private async Task<List<string>> Where(Interfaces.ISearchEngine engine, string name)
    {
        var result = await engine.SearchAsync(new SearchQuery { SearchText = name, SearchFileNameOnly = true, BasePath = _root });
        var paths = new List<string>();
        await foreach (var item in result.Files) paths.Add(item.FullPath);
        return paths;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition() && !timeout.IsCancellationRequested) await Task.Delay(50);
    }
}
