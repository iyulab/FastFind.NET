using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix;
using FluentAssertions;
using Xunit;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// A rename pair whose new path lies beneath its old one is not a move: re-rooting every descendant
/// under it once per report grew each indexed path by a block per event, and nothing freed the strings.
/// </summary>
[Trait("Category", "Functional")]
public sealed class RenameNotAMoveTests
{
    private static readonly DateTime Stamp = new(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private static async Task<ISearchIndex> IndexAsync()
    {
        var index = new MemorySearchIndex();
        await index.AddBatchAsync(
        [
            Directory("/data/a"),
            Directory("/data/a/b"),
            File("/data/a/b/c.txt"),
            Directory("/data/a.old"),
            File("/data/a.old/x.txt"),
        ]);
        return index;
    }

    [Fact]
    public async Task A_Pair_Reported_Repeatedly_Should_Not_Grow_Any_Path()
    {
        var index = await IndexAsync();

        for (var i = 0; i < 5; i++)
        {
            (await index.ApplyRenameAsync("/data/a", "/data/a/b", (_, _) => Task.FromResult(true))).Should().BeFalse();
        }

        var all = new List<string>();
        await foreach (var item in index.GetByDirectoryAsync("/data", recursive: true)) all.Add(item.FullPath);

        all.Should().BeEquivalentTo("/data/a", "/data/a/b", "/data/a/b/c.txt", "/data/a.old", "/data/a.old/x.txt");
    }

    [Fact]
    public async Task A_Pair_That_Is_Not_A_Move_Should_Drop_The_Old_Tree_Only_When_The_Old_Path_Is_Gone()
    {
        var index = await IndexAsync();

        await index.ApplyRenameAsync("/data/a", "/data/a/b", (_, _) => Task.FromResult(true));
        (await index.ContainsAsync("/data/a/b/c.txt")).Should().BeTrue();

        await index.ApplyRenameAsync("/data/a", "/data/a/b", (_, _) => Task.FromResult(false));
        (await index.ContainsAsync("/data/a/b/c.txt")).Should().BeFalse();
        (await index.ContainsAsync("/data/a.old/x.txt")).Should().BeTrue("a sibling sharing the prefix is not beneath it");
    }

    [Fact]
    public async Task A_Real_Move_Should_Still_Be_Applied()
    {
        var index = await IndexAsync();

        (await index.ApplyRenameAsync("/data/a", "/archive/a", (_, _) => Task.FromResult(true))).Should().BeTrue();

        (await index.ContainsAsync("/archive/a/b/c.txt")).Should().BeTrue();
        (await index.ContainsAsync("/data/a/b/c.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData("/data/a", "/data/a/b", false)]
    [InlineData("/data/a", "/data/a/b/c", false)]
    [InlineData("/data/a", "/data/a.old", true)]
    [InlineData("/data/a", "/data/a", true)]
    [InlineData("/data/a/b", "/data/a", true)]
    public void IsMove_Should_Refuse_Only_A_New_Path_Strictly_Beneath_The_Old_One(string oldPath, string newPath, bool expected)
    {
        SearchIndexTreeExtensions.IsMove(oldPath, newPath).Should().Be(expected);
    }

    private static FastFileItem File(string path) => Item(path, FileAttributes.Archive, 10);

    private static FastFileItem Directory(string path) => Item(path, FileAttributes.Directory, 0);

    // Split by hand: Path.GetDirectoryName rewrites '/' to '\' on a Windows host, and these paths are POSIX.
    private static FastFileItem Item(string path, FileAttributes attributes, long size)
    {
        var cut = path.LastIndexOf('/');
        var name = path[(cut + 1)..];
        var dot = name.LastIndexOf('.');
        return new FastFileItem(
            path, name, path[..cut],
            attributes == FileAttributes.Directory || dot < 0 ? string.Empty : name[dot..],
            size, Stamp, Stamp, Stamp, attributes, '/');
    }
}
