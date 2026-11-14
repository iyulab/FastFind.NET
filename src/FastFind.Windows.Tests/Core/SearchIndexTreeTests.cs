using FastFind.Interfaces;
using FastFind.Models;
using FastFind.SQLite;
using FastFind.Windows.Implementation;
using AwesomeAssertions;
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

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Moving_A_Directory_Beneath_Itself_Should_Be_Refused_And_Leave_The_Index_Unchanged(string backend)
    {
        var index = await IndexAsync(backend);
        var before = await Paths(index.GetByDirectoryAsync(@"C:\src\App", recursive: true));

        var act = () => index.MoveTreeAsync(@"C:\src\App", @"C:\src\App\Models");

        await act.Should().ThrowAsync<ArgumentException>();
        (await Paths(index.GetByDirectoryAsync(@"C:\src\App", recursive: true))).Should().BeEquivalentTo(before);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_Rename_Pair_Reported_Over_And_Over_Should_Not_Grow_Any_Path(string backend)
    {
        // The field defect: the same impossible pair applied each time re-rooted every descendant, so
        // each path gained a block per event. Through ApplyRenameAsync the index must not change at all.
        var index = await IndexAsync(backend);
        var before = await Paths(index.GetByDirectoryAsync(@"C:\src", recursive: true));

        for (var i = 0; i < 5; i++)
        {
            var applied = await index.ApplyRenameAsync(
                @"C:\src\App", @"C:\src\App\Models", (_, _) => Task.FromResult(true));
            applied.Should().BeFalse();
        }

        (await Paths(index.GetByDirectoryAsync(@"C:\src", recursive: true))).Should().BeEquivalentTo(before);
        var longest = (await Paths(index.GetByDirectoryAsync(@"C:\src", recursive: true))).Max(p => p.Length);
        longest.Should().Be(before.Max(p => p.Length));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_Pair_That_Is_Not_A_Move_Should_Drop_The_Old_Tree_Only_When_The_Old_Path_Is_Gone(string backend)
    {
        var index = await IndexAsync(backend);

        // Old path still on disk: the index keeps what it has.
        await index.ApplyRenameAsync(@"C:\src\App", @"C:\src\App\Models", (_, _) => Task.FromResult(true));
        (await index.ContainsAsync(@"C:\src\App\Models\Item.cs")).Should().BeTrue();

        // Old path gone: the stale tree is removed, and nothing is re-rooted.
        var applied = await index.ApplyRenameAsync(@"C:\src\App", @"C:\src\App\Models", (_, _) => Task.FromResult(false));

        applied.Should().BeFalse();
        (await index.ContainsAsync(@"C:\src\App")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\src\App\Models\Item.cs")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\src\App\Models\Models\Item.cs")).Should().BeFalse();
        (await index.ContainsAsync(@"C:\src\App.Tests\ProgramTests.cs")).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task A_Case_Only_Rename_Should_Still_Be_Applied_As_A_Move(string backend)
    {
        var index = await IndexAsync(backend);

        var applied = await index.ApplyRenameAsync(@"C:\src\App", @"C:\src\app", (_, _) => Task.FromResult(true));

        applied.Should().BeTrue();
        var leaf = await index.GetAsync(@"C:\src\app\Models\Deep\Leaf.cs");
        leaf!.Value.DirectoryPath.Should().Be(@"C:\src\app\Models\Deep");
    }

    [Theory]
    [InlineData(@"C:\src\App", @"C:\src\App\Models", false)]
    [InlineData(@"C:\src\App", @"C:/src/App/Models/Deep", false)]      // '/' is a separator on Windows
    [InlineData(@"C:\src\App", @"c:\SRC\app\models", false)]          // case folds on Windows
    [InlineData(@"C:\src\App", @"C:\src\App.Tests", true)]            // a sibling sharing the prefix
    [InlineData(@"C:\src\App", @"C:\src\App", true)]                  // the same path
    [InlineData(@"C:\src\App", @"C:\src\app\", true)]                 // case and trailing separator only
    [InlineData(@"C:\src\App\Models", @"C:\src\App", true)]           // up, not down
    [InlineData(@"C:\src\App", @"C:\archive\OldApp", true)]
    public void IsMove_Should_Refuse_Only_A_New_Path_Strictly_Beneath_The_Old_One(string oldPath, string newPath, bool expected)
    {
        SearchIndexTreeExtensions.IsMove(oldPath, newPath).Should().Be(expected);
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
