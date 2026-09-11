using System.Runtime.CompilerServices;
using FastFind.Windows.Mft;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Path resolution from journal records, which arrive in file-reference order — not parent
/// before child.
/// </summary>
[Trait("Category", "Functional")]
public class MftPathResolverTests
{
    private const ulong Root = MftPathResolver.RootRecordNumber;

    [Fact]
    public async Task A_File_Enumerated_Before_Its_Directories_Should_Still_Get_Its_Full_Path()
    {
        // readme.md (100) arrives before docs (300), and docs before its parent src (200)
        // arrives — the order that used to root the file at the drive.
        var records = new[]
        {
            File(100, parent: 300, "readme.md"),
            Directory(300, parent: 200, "docs"),
            Directory(200, parent: Root, "src"),
        };

        var paths = await ResolveAsync(records);

        paths.Should().Contain(@"D:\src\docs\readme.md");
        paths.Should().Contain(@"D:\src\docs");
        paths.Should().Contain(@"D:\src");
        paths.Should().NotContain(@"D:\readme.md");
    }

    [Fact]
    public async Task A_Deep_Chain_In_Reverse_Order_Should_Resolve_Every_Level()
    {
        var records = new[]
        {
            File(900, parent: 104, "leaf.txt"),
            Directory(104, parent: 103, "d"),
            Directory(103, parent: 102, "c"),
            Directory(102, parent: 101, "b"),
            Directory(101, parent: Root, "a"),
        };

        var paths = await ResolveAsync(records);

        paths.Should().Contain(@"D:\a\b\c\d\leaf.txt");
        paths.Should().Contain(@"D:\a\b\c\d");
    }

    [Fact]
    public async Task A_File_Whose_Parent_Is_Already_Known_Should_Stream_Before_The_Enumeration_Ends()
    {
        var records = Stream(
            Directory(200, parent: Root, "src"),
            File(201, parent: 200, "early.cs"));

        var first = await MftPathResolver.ResolveAsync(records, @"D:\", new StrongBox<int>()).FirstAsync();

        first.Path.Should().Be(@"D:\src\early.cs");
    }

    [Fact]
    public async Task An_Entry_Whose_Ancestor_Never_Appears_Should_Be_Dropped_Not_Rooted_At_The_Drive()
    {
        // Parent 777 is never enumerated — as under a skipped $-prefixed metadata directory.
        var records = new[]
        {
            File(100, parent: 777, "orphan.txt"),
            Directory(101, parent: 777, "orphaned-dir"),
            File(102, parent: Root, "top.txt"),
        };
        var unresolved = new StrongBox<int>();

        var paths = await ResolveAsync(records, unresolved);

        paths.Should().BeEquivalentTo([@"D:\top.txt"]);
        unresolved.Value.Should().Be(2);
    }

    [Fact]
    public async Task A_Parent_Chain_That_Loops_Should_Be_Dropped()
    {
        var records = new[]
        {
            Directory(301, parent: 302, "x"),
            Directory(302, parent: 301, "y"),
            File(303, parent: 301, "in-loop.txt"),
        };
        var unresolved = new StrongBox<int>();

        var paths = await ResolveAsync(records, unresolved);

        paths.Should().BeEmpty();
        unresolved.Value.Should().Be(3);
    }

    [Fact]
    public async Task The_Root_Record_Should_Resolve_To_The_Volume_Root()
    {
        var paths = await ResolveAsync([Directory(Root, parent: Root, ".")]);

        paths.Should().BeEquivalentTo([@"D:\"]);
    }

    [Fact]
    public async Task Two_Volumes_With_The_Same_Record_Numbers_Should_Not_Share_Paths()
    {
        // Record numbers are per volume; every volume's root is 5.
        var records = new[] { Directory(200, parent: Root, "data"), File(201, parent: 200, "a.txt") };

        var onC = await MftPathResolver.ResolveAsync(Stream(records), @"C:\", new StrongBox<int>()).Select(r => r.Path).ToListAsync();
        var onD = await MftPathResolver.ResolveAsync(Stream(records), @"D:\", new StrongBox<int>()).Select(r => r.Path).ToListAsync();

        onC.Should().BeEquivalentTo([@"C:\data\a.txt", @"C:\data"]);
        onD.Should().BeEquivalentTo([@"D:\data\a.txt", @"D:\data"]);
    }

    // --- helpers ---

    private static async Task<List<string>> ResolveAsync(MftFileRecord[] records, StrongBox<int>? unresolved = null) =>
        await MftPathResolver.ResolveAsync(Stream(records), @"D:\", unresolved ?? new StrongBox<int>())
            .Select(r => r.Path)
            .ToListAsync();

    private static async IAsyncEnumerable<MftFileRecord> Stream(params MftFileRecord[] records)
    {
        foreach (var record in records)
        {
            await Task.Yield();
            yield return record;
        }
    }

    // File reference numbers carry a sequence number in the top 16 bits; resolution must use only
    // the record number beneath it.
    private static ulong Reference(ulong recordNumber) => (1UL << 48) | recordNumber;

    private static MftFileRecord File(ulong recordNumber, ulong parent, string name) =>
        Record(recordNumber, parent, name, FileAttributes.Archive);

    private static MftFileRecord Directory(ulong recordNumber, ulong parent, string name) =>
        Record(recordNumber, parent, name, FileAttributes.Directory);

    private static MftFileRecord Record(ulong recordNumber, ulong parent, string name, FileAttributes attributes) =>
        new(Reference(recordNumber), Reference(parent), attributes, 0, name, default, default, default);
}
