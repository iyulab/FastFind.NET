using FastFind.Indexing;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// The in-memory index's storage: identity follows the Windows file system through comparers, and a
/// subtree is answered from directory keys.
/// </summary>
public class FileEntryTableTests
{
    private static FastFileItem Entry(string fullPath, bool directory = false)
    {
        var trimmed = fullPath.TrimEnd('\\');
        return new FastFileItem(
            fullPath,
            Path.GetFileName(trimmed),
            Path.GetDirectoryName(trimmed) ?? "",
            directory ? "" : Path.GetExtension(trimmed),
            size: 1,
            created: DateTime.UtcNow,
            modified: DateTime.UtcNow,
            accessed: DateTime.UtcNow,
            attributes: directory ? FileAttributes.Directory : FileAttributes.Normal,
            driveLetter: 'C');
    }

    private static FileEntryTable Table(params string[] paths)
    {
        var table = new FileEntryTable();
        foreach (var path in paths) table.TryAdd(Entry(path)).Should().BeTrue();
        return table;
    }

    [Fact]
    public void Adding_The_Same_Path_Twice_Should_Keep_One_Entry()
    {
        var table = Table(@"C:\data\a.txt");

        table.TryAdd(Entry(@"C:\data\a.txt")).Should().BeFalse();
        table.Count.Should().Be(1);
    }

    [Theory]
    [InlineData(@"C:\DATA\A.TXT")]
    [InlineData(@"c:/data/a.txt")]
    [InlineData(@"C:\data\a.txt\")]
    public void A_Lookup_Should_Follow_Windows_Path_Identity(string spelling)
    {
        if (!OperatingSystem.IsWindows()) return;

        var table = Table(@"C:\data\a.txt");

        table.TryGet(spelling, out var found).Should().BeTrue();
        found.FullPath.Should().Be(@"C:\data\a.txt", "the stored spelling is kept verbatim");
        table.TryAdd(Entry(spelling.TrimEnd('\\'))).Should().BeFalse("it names the same file");
    }

    [Fact]
    public void Removing_The_Last_Entry_Of_A_Directory_Should_Drop_The_Directory()
    {
        var table = Table(@"C:\data\a.txt", @"C:\other\b.txt");

        table.TryRemove(@"C:\data\a.txt", out var removed).Should().BeTrue();

        removed.Name.Should().Be("a.txt");
        table.Count.Should().Be(1);
        table.Directories.Should().Equal(@"C:\other");
        table.TryRemove(@"C:\data\a.txt", out _).Should().BeFalse();
    }

    [Fact]
    public void A_Subtree_Should_Reach_Every_Depth_And_No_Prefix_Sibling()
    {
        var table = Table(
            @"C:\proj\top.cs",
            @"C:\proj\src\mid.cs",
            @"C:\proj\src\deep\leaf.cs",
            @"C:\project\sibling.cs",
            @"C:\other\x.cs");

        table.Under(@"C:\proj").Select(e => e.Name)
            .Should().BeEquivalentTo("top.cs", "mid.cs", "leaf.cs");
        table.InDirectory(@"C:\proj\").Select(e => e.Name).Should().Equal("top.cs");
        table.Under(@"C:\missing").Should().BeEmpty();
    }

    [Fact]
    public void Coverage_Should_Hold_For_Ancestors_Of_Indexed_Entries_Only()
    {
        var table = Table(@"C:\proj\src\deep\leaf.cs");

        table.Covers(@"C:\proj").Should().BeTrue();
        table.Covers(@"C:\proj\src\deep").Should().BeTrue();
        table.Covers(@"C:\pro").Should().BeFalse();
        table.Covers(@"C:\proj\src\deep\leaf.cs").Should().BeFalse("a file is not a directory holding entries");
    }

    [Fact]
    public void An_Entry_At_A_Drive_Root_Should_Be_Found_By_Its_Path()
    {
        var table = Table(@"C:\root.txt");

        table.TryGet(@"C:\root.txt", out _).Should().BeTrue();
        table.InDirectory(@"C:\").Should().ContainSingle();
    }

    [Fact]
    public void Replacing_An_Entry_Should_Report_What_It_Replaced()
    {
        var table = Table(@"C:\data\a.txt");
        var newer = new FastFileItem(@"C:\data\a.txt", "a.txt", @"C:\data", ".txt", 99,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C');

        table.Set(newer, out var previous).Should().BeTrue();

        previous.Size.Should().Be(1);
        table.TryGet(@"C:\data\a.txt", out var current).Should().BeTrue();
        current.Size.Should().Be(99);
        table.Count.Should().Be(1);
    }

    [Fact]
    public void An_Entry_Whose_Parts_Disagree_With_Its_Path_Should_Still_Be_Found_By_Its_Path()
    {
        var table = new FileEntryTable();
        var inconsistent = new FastFileItem(@"C:\data\a.txt", "different-name", @"C:\elsewhere", ".txt", 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C');

        table.TryAdd(inconsistent).Should().BeTrue();

        table.Contains(@"C:\data\a.txt").Should().BeTrue();
    }

    [Fact]
    public void Clearing_Should_Remove_Everything()
    {
        var table = Table(@"C:\a\1.txt", @"C:\b\2.txt");

        table.Clear();

        table.Count.Should().Be(0);
        table.All().Should().BeEmpty();
    }
}

/// <summary>
/// The same identity, observed through the index's public surface.
/// </summary>
public class WindowsSearchIndexIdentityTests
{
    [Theory]
    [InlineData(@"C:\DATA\A.TXT")]
    [InlineData(@"C:/data/a.txt")]
    public async Task A_Lookup_Should_Accept_Any_Windows_Spelling_Of_The_Path(string spelling)
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var index = new WindowsSearchIndex(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsSearchIndex>.Instance);
        await index.AddAsync(new FastFileItem(@"C:\data\a.txt", "a.txt", @"C:\data", ".txt", 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C'));

        (await index.ContainsAsync(spelling)).Should().BeTrue();
        (await index.GetAsync(spelling))!.Value.FullPath.Should().Be(@"C:\data\a.txt");
        (await index.RemoveAsync(spelling)).Should().BeTrue();
        index.Count.Should().Be(0);
    }

    [Fact]
    public async Task Overlapping_Locations_Should_Return_Each_Entry_Once()
    {
        await using var index = new WindowsSearchIndex(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsSearchIndex>.Instance);
        await index.AddBatchAsync(Enumerable.Range(0, 20).Select(i =>
            new FastFileItem($@"C:\scope\a\b\file{i}.cs", $"file{i}.cs", @"C:\scope\a\b", ".cs", 1,
                DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C')));

        var query = new SearchQuery { SearchText = "file7.cs", SearchFileNameOnly = true };
        query.SearchLocations.Add(@"C:\scope\a");
        query.SearchLocations.Add(@"C:\scope\a\b");

        var found = new List<string>();
        await foreach (var item in index.SearchAsync(query)) found.Add(item.FullPath);

        found.Should().Equal(@"C:\scope\a\b\file7.cs");
    }
}
