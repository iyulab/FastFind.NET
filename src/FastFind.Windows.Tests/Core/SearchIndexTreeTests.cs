using FastFind.Interfaces;
using FastFind.Models;
using FastFind.SQLite;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// A directory's deletion or move reaches everything indexed beneath it — on every backend.
/// </summary>
[Trait("Category", "Functional")]
public sealed class SearchIndexTreeTests : IAsyncLifetime
{
    private static readonly DateTime Stamp = new(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fastfind-tree-{Guid.NewGuid():N}.db");
    private SqlitePersistence? _store;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { System.IO.File.Delete(_databasePath + suffix); } catch (IOException) { }
        }
    }

    public static TheoryData<string> Backends => new() { "memory", "sqlite" };

    private async Task<ISearchIndex> IndexAsync(string backend)
    {
        ISearchIndex index;
        if (backend == "memory")
        {
            index = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
        }
        else
        {
            _store = SqlitePersistence.Create(_databasePath);
            await _store.InitializeAsync();
            index = new PersistentSearchIndex(_store);
        }

        await index.AddBatchAsync(
        [
            Directory(@"C:\src\App"),
            File(@"C:\src\App\Program.cs"),
            Directory(@"C:\src\App\Models"),
            File(@"C:\src\App\Models\Item.cs"),
            Directory(@"C:\src\App\Models\Deep"),
            File(@"C:\src\App\Models\Deep\Leaf.cs"),
            Directory(@"C:\src\App.Tests"),               // shares the prefix, not the directory
            File(@"C:\src\App.Tests\ProgramTests.cs"),
            Directory(@"C:\src\a_b"),
            File(@"C:\src\a_b\under.txt"),
            Directory(@"C:\src\axb"),                    // '_' must not act as a LIKE wildcard
            File(@"C:\src\axb\wild.txt"),
        ]);

        return index;
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_Recursive_Directory_Query_Should_Return_Every_Level_And_Nothing_Beside_It(string backend)
    {
        var index = await IndexAsync(backend);

        var under = await Paths(index.GetByDirectoryAsync(@"C:\src\App", recursive: true));

        under.Should().BeEquivalentTo(
            @"C:\src\App\Program.cs", @"C:\src\App\Models", @"C:\src\App\Models\Item.cs",
            @"C:\src\App\Models\Deep", @"C:\src\App\Models\Deep\Leaf.cs");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task An_Underscore_In_A_Directory_Name_Should_Match_Only_Itself(string backend)
    {
        var index = await IndexAsync(backend);

        var under = await Paths(index.GetByDirectoryAsync(@"C:\src\a_b", recursive: true));

        under.Should().BeEquivalentTo(@"C:\src\a_b\under.txt");
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Removing_A_Directory_Should_Remove_Its_Whole_Subtree_And_Nothing_Else(string backend)
    {
        var index = await IndexAsync(backend);

        var removed = await index.RemoveTreeAsync(@"C:\src\App");

        removed.Should().Be(6);
        (await index.ContainsAsync(@"C:\src\App\Models\Deep\Leaf.cs")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\src\App")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\src\App.Tests\ProgramTests.cs")).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Removing_A_File_Should_Remove_Only_That_File(string backend)
    {
        var index = await IndexAsync(backend);

        (await index.RemoveTreeAsync(@"C:\src\App\Program.cs")).Should().Be(1);
        (await index.ContainsAsync(@"C:\src\App\Models\Item.cs")).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Moving_A_Directory_Should_Move_Its_Subtree_Keeping_Metadata(string backend)
    {
        var index = await IndexAsync(backend);

        var moved = await index.MoveTreeAsync(@"C:\src\App", @"C:\archive\OldApp");

        moved.Should().Be(5);
        (await index.ContainsAsync(@"C:\src\App\Models\Item.cs")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\src\App")).Should().BeFalse();

        var leaf = await index.GetAsync(@"C:\archive\OldApp\Models\Deep\Leaf.cs");
        leaf.Should().NotBeNull();
        leaf!.Value.DirectoryPath.Should().Be(@"C:\archive\OldApp\Models\Deep");
        leaf.Value.Name.Should().Be("Leaf.cs");
        leaf.Value.Size.Should().Be(10);

        var root = await index.GetAsync(@"C:\archive\OldApp");
        root!.Value.IsDirectory.Should().BeTrue();
        root.Value.DirectoryPath.Should().Be(@"C:\archive");
        root.Value.Name.Should().Be("OldApp");

        (await index.ContainsAsync(@"C:\src\App.Tests\ProgramTests.cs")).Should().BeTrue();
    }

    private static async Task<List<string>> Paths(IAsyncEnumerable<FastFileItem> items)
    {
        var paths = new List<string>();
        await foreach (var item in items) paths.Add(item.FullPath);
        return paths;
    }

    private static FastFileItem File(string path) => Item(path, FileAttributes.Archive, 10);

    private static FastFileItem Directory(string path) => Item(path, FileAttributes.Directory, 0);

    private static FastFileItem Item(string path, FileAttributes attributes, long size) => new(
        path, Path.GetFileName(path), Path.GetDirectoryName(path)!,
        attributes == FileAttributes.Directory ? string.Empty : Path.GetExtension(path),
        size, Stamp, Stamp, Stamp, attributes, 'C');
}
