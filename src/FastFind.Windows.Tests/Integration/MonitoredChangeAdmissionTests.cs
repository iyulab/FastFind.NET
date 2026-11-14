using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// A change the monitor reports meets the same rules as an item the indexing pass read: the options'
/// exclusions and the file-count cap hold after the pass is over, not only during it.
/// </summary>
/// <remarks>
/// The pass applied <c>ExcludedExtensions</c>, <c>IncludeHidden</c>, <c>MaxFileSize</c> and
/// <c>MaxFileCount</c>; a monitored creation went to the index with none of them, so an index capped to
/// bound memory kept growing, and files the options refuse were added.
/// </remarks>
[Trait("Category", "Functional")]
public class MonitoredChangeAdmissionTests
{
    private static (WindowsSearchEngineImpl Engine, WindowsSearchIndex Index) EngineOver(ScriptedFileSystemProvider provider)
    {
        var index = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
        var engine = new WindowsSearchEngineImpl(
            provider, index, new WindowsSearchEngineOptions(), NullLogger<WindowsSearchEngineImpl>.Instance);
        return (engine, index);
    }

    private static IndexingOptions Options(Action<IndexingOptions>? configure = null)
    {
        var options = new IndexingOptions
        {
            SpecificDirectories = { @"C:\scripted" },
            EnableMonitoring = true,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        };
        configure?.Invoke(options);
        return options;
    }

    private static ScriptedFileSystemProvider Provider(int items)
    {
        var provider = new ScriptedFileSystemProvider();
        for (var i = 0; i < items; i++)
        {
            var item = ScriptedFileSystemProvider.File($@"C:\scripted\f{i}.txt");
            provider.Items.Add(item);
            provider.OnDisk[item.FullPath] = item;
        }
        return provider;
    }

    private static async Task PassAsync(WindowsSearchEngineImpl engine, IndexingOptions options)
    {
        await engine.StartIndexingAsync(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing)
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>Reports changes and waits until the engine has applied every one (it raises FileChanged after each).</summary>
    private static async Task ReportAsync(WindowsSearchEngineImpl engine, ScriptedFileSystemProvider provider, params FileChangeEventArgs[] changes)
    {
        var remaining = changes.Length;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FileChangeEventArgs> handler = (_, _) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0) done.TrySetResult();
        };

        engine.FileChanged += handler;
        try
        {
            foreach (var change in changes) provider.Report(change);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            engine.FileChanged -= handler;
        }
    }

    private static FileItem Create(ScriptedFileSystemProvider provider, string path, FileAttributes attributes = FileAttributes.Archive)
    {
        var item = ScriptedFileSystemProvider.File(path, attributes);
        provider.OnDisk[path] = item;
        return item;
    }

    private static FileChangeEventArgs Created(string path) => new(FileChangeType.Created, path);

    [Fact]
    public async Task A_Created_File_The_Options_Exclude_Should_Not_Be_Indexed()
    {
        var provider = Provider(3);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.ExcludedExtensions = new List<string> { ".tmp" }));

        Create(provider, @"C:\scripted\scratch.tmp");
        Create(provider, @"C:\scripted\keep.txt");
        await ReportAsync(engine, provider, Created(@"C:\scripted\scratch.tmp"), Created(@"C:\scripted\keep.txt"));

        (await index.ContainsAsync(@"C:\scripted\keep.txt")).Should().BeTrue();
        (await index.ContainsAsync(@"C:\scripted\scratch.tmp")).Should().BeFalse("the pass would have refused it");
    }

    [Fact]
    public async Task A_Created_Hidden_File_Should_Not_Be_Indexed_When_Hidden_Files_Are_Not_Included()
    {
        var provider = Provider(1);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.IncludeHidden = false));

        Create(provider, @"C:\scripted\secret.txt", FileAttributes.Hidden);
        await ReportAsync(engine, provider, Created(@"C:\scripted\secret.txt"));

        (await index.ContainsAsync(@"C:\scripted\secret.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_File_That_Becomes_Inadmissible_Should_Leave_The_Index()
    {
        var provider = Provider(2);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.IncludeHidden = false));
        (await index.ContainsAsync(@"C:\scripted\f0.txt")).Should().BeTrue();

        Create(provider, @"C:\scripted\f0.txt", FileAttributes.Hidden);
        await ReportAsync(engine, provider, new FileChangeEventArgs(FileChangeType.Modified, @"C:\scripted\f0.txt"));

        (await index.ContainsAsync(@"C:\scripted\f0.txt")).Should().BeFalse("the pass would not have admitted it now");
    }

    [Fact]
    public async Task The_File_Count_Cap_Should_Hold_After_The_Pass_Ends()
    {
        var provider = Provider(10);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.MaxFileCount = 5));
        index.Count.Should().Be(5);

        for (var i = 0; i < 3; i++) Create(provider, $@"C:\scripted\new{i}.txt");
        await ReportAsync(engine, provider,
            Created(@"C:\scripted\new0.txt"),
            Created(@"C:\scripted\new1.txt"),
            Created(@"C:\scripted\new2.txt"),
            // An entry the index already holds may still change at the cap.
            new FileChangeEventArgs(FileChangeType.Modified, @"C:\scripted\f0.txt"));

        index.Count.Should().Be(5, "an index capped to bound memory must not keep growing");
        (await index.ContainsAsync(@"C:\scripted\new0.txt")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\scripted\f0.txt")).Should().BeTrue();
        engine.TotalIndexedFiles.Should().Be(index.Count, "the statistic follows the index, not the pass");
    }

    [Fact]
    public async Task A_Deletion_Should_Make_Room_Under_The_Cap()
    {
        var provider = Provider(5);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.MaxFileCount = 5));

        Create(provider, @"C:\scripted\after.txt");
        await ReportAsync(engine, provider,
            new FileChangeEventArgs(FileChangeType.Deleted, @"C:\scripted\f0.txt"),
            Created(@"C:\scripted\after.txt"));

        (await index.ContainsAsync(@"C:\scripted\f0.txt")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\scripted\after.txt")).Should().BeTrue("a deletion freed a place under the cap");
        index.Count.Should().Be(5);
        engine.TotalIndexedFiles.Should().Be(5);
    }

    [Fact]
    public async Task A_Rename_Pair_That_Is_Not_A_Move_Should_Not_Disturb_The_Index_Through_The_Engine()
    {
        var provider = Provider(0);
        provider.Items.Add(ScriptedFileSystemProvider.File(@"C:\scripted\a\b\c.txt"));
        foreach (var item in provider.Items) provider.OnDisk[item.FullPath] = item;
        provider.OnDisk[@"C:\scripted\a"] = ScriptedFileSystemProvider.File(@"C:\scripted\a"); // the old path is still there
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options());

        for (var i = 0; i < 3; i++)
        {
            await ReportAsync(engine, provider,
                new FileChangeEventArgs(FileChangeType.Renamed, @"C:\scripted\a\b", null, @"C:\scripted\a"));
        }

        var paths = new List<string>();
        await foreach (var item in index.GetByDirectoryAsync(@"C:\scripted", recursive: true)) paths.Add(item.FullPath);
        paths.Should().BeEquivalentTo(@"C:\scripted\a\b\c.txt");
    }

    // A pass does not enter a hidden directory, and so reads nothing beneath it, whatever each entry's own attributes are.
    // A monitored change looked only at the item: a file created in .git is not hidden itself, and was indexed.

    private static void AddDirectory(ScriptedFileSystemProvider provider, string path, FileAttributes attributes = default) =>
        provider.OnDisk[path] = ScriptedFileSystemProvider.File(path, FileAttributes.Directory | attributes);

    [Fact]
    public async Task A_Change_Beneath_A_Hidden_Directory_Should_Not_Be_Indexed_Though_The_File_Is_Not_Hidden()
    {
        var provider = Provider(1);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.IncludeHidden = false));

        AddDirectory(provider, @"C:\scripted\.git", FileAttributes.Hidden);
        AddDirectory(provider, @"C:\scripted\.git\objects");
        AddDirectory(provider, @"C:\scripted\src");
        Create(provider, @"C:\scripted\.git\objects\x.pack");
        Create(provider, @"C:\scripted\src\a.cs");
        await ReportAsync(engine, provider, Created(@"C:\scripted\.git\objects\x.pack"), Created(@"C:\scripted\src\a.cs"));

        (await index.ContainsAsync(@"C:\scripted\src\a.cs")).Should().BeTrue("a sibling directory is not hidden");
        (await index.ContainsAsync(@"C:\scripted\.git\objects\x.pack")).Should().BeFalse("a pass would not have entered .git");
    }

    [Fact]
    public async Task A_Change_Beneath_A_Hidden_Directory_Should_Be_Indexed_When_Hidden_Files_Are_Included()
    {
        var provider = Provider(1);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.IncludeHidden = true));

        AddDirectory(provider, @"C:\scripted\.git", FileAttributes.Hidden);
        Create(provider, @"C:\scripted\.git\x.pack");
        await ReportAsync(engine, provider, Created(@"C:\scripted\.git\x.pack"));

        (await index.ContainsAsync(@"C:\scripted\.git\x.pack")).Should().BeTrue();
    }

    [Fact]
    public async Task A_Hidden_Directory_Above_The_Monitored_Location_Should_Not_Hide_What_Is_Beneath_It()
    {
        // The location itself lies under a hidden directory (a temp folder under AppData); a pass starts in the
        // location and does not look above it.
        var provider = new ScriptedFileSystemProvider();
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        AddDirectory(provider, @"C:\scripted\AppData", FileAttributes.Hidden);
        AddDirectory(provider, @"C:\scripted\AppData\Local");
        await PassAsync(engine, Options(o =>
        {
            o.SpecificDirectories.Clear();
            o.SpecificDirectories.Add(@"C:\scripted\AppData\Local");
            o.IncludeHidden = false;
        }));

        Create(provider, @"C:\scripted\AppData\Local\kept.txt");
        await ReportAsync(engine, provider, Created(@"C:\scripted\AppData\Local\kept.txt"));

        (await index.ContainsAsync(@"C:\scripted\AppData\Local\kept.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task A_Rename_Into_A_Hidden_Directory_Should_Leave_Neither_Path_In_The_Index()
    {
        var provider = Provider(0);
        AddDirectory(provider, @"C:\scripted\src");
        AddDirectory(provider, @"C:\scripted\.cache", FileAttributes.Hidden);
        var original = Create(provider, @"C:\scripted\src\a.cs");
        provider.Items.Add(original);
        var (engine, index) = EngineOver(provider);
        using var _ = engine;
        await PassAsync(engine, Options(o => o.IncludeHidden = false));
        (await index.ContainsAsync(@"C:\scripted\src\a.cs")).Should().BeTrue();

        provider.OnDisk.Remove(@"C:\scripted\src\a.cs");
        Create(provider, @"C:\scripted\.cache\a.cs");
        await ReportAsync(engine, provider,
            new FileChangeEventArgs(FileChangeType.Renamed, @"C:\scripted\.cache\a.cs", null, @"C:\scripted\src\a.cs"));

        (await index.ContainsAsync(@"C:\scripted\src\a.cs")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\scripted\.cache\a.cs")).Should().BeFalse("it is now beneath a hidden directory");
    }
}
