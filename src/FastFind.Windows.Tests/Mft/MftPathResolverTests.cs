using System.Runtime.CompilerServices;
using FastFind.Windows.Mft;
using AwesomeAssertions;
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

    [Fact]
    public async Task Everything_Beneath_A_Hidden_Directory_Should_Be_Dropped_Whatever_Its_Own_Attributes_Or_Arrival_Order()
    {
        // .git is hidden; the files in it are not. A directory walk never enters it, so neither may this pass.
        var records = new[]
        {
            File(500, parent: 400, "HEAD"),              // before its directories
            File(501, parent: 402, "deep.pack"),
            Directory(402, parent: 400, "objects"),      // not hidden itself, beneath one that is
            Record(400, Root, ".git", FileAttributes.Directory | FileAttributes.Hidden),
            Directory(200, parent: Root, "src"),
            File(201, parent: 200, "kept.cs"),
        };

        var skipped = await ResolveAsync(records, skipDirectories: FileAttributes.Hidden);
        var everything = await ResolveAsync(records);

        skipped.Should().BeEquivalentTo([@"D:\.git", @"D:\src", @"D:\src\kept.cs"],
            "the hidden directory is the pass's own entry to refuse (admission does); nothing beneath it is read");
        everything.Should().Contain([@"D:\.git\HEAD", @"D:\.git\objects", @"D:\.git\objects\deep.pack"],
            "with hidden entries wanted the same records are all kept");
    }

    [Fact]
    public async Task A_Directory_Is_Skipped_Only_For_The_Attributes_The_Options_Refuse()
    {
        var records = new[]
        {
            Record(300, Root, "System Volume Information", FileAttributes.Directory | FileAttributes.System),
            File(301, parent: 300, "tracking.log"),
            Record(310, Root, ".cache", FileAttributes.Directory | FileAttributes.Hidden),
            File(311, parent: 310, "a.bin"),
        };

        var onlySystem = await ResolveAsync(records, skipDirectories: FileAttributes.System);
        onlySystem.Should().NotContain(@"D:\System Volume Information\tracking.log");
        onlySystem.Should().Contain(@"D:\.cache\a.bin", "hidden entries were asked for");

        var onlyHidden = await ResolveAsync(records, skipDirectories: FileAttributes.Hidden);
        onlyHidden.Should().Contain(@"D:\System Volume Information\tracking.log");
        onlyHidden.Should().NotContain(@"D:\.cache\a.bin");
    }

    [Fact]
    public async Task A_Hidden_Directory_Above_The_Location_Should_Not_Hide_The_Location()
    {
        // D:\Users\me\AppData (hidden)\Local\Foo is where a temp folder lives. A walk of Foo does not look above Foo; the
        // hidden directory counts only for what lies beneath the location.
        var records = new[]
        {
            Directory(200, parent: Root, "Users"),
            Directory(201, parent: 200, "me"),
            Record(202, 201, "AppData", FileAttributes.Directory | FileAttributes.Hidden),
            Directory(203, parent: 202, "Local"),
            Directory(204, parent: 203, "Foo"),
            File(205, parent: 204, "a.txt"),
            Record(206, 204, ".git", FileAttributes.Directory | FileAttributes.Hidden),
            File(207, parent: 206, "HEAD"),
        };

        var fooIsTheLocation = await ResolveAsync(records, skipDirectories: FileAttributes.Hidden, locationDepth: 5);
        fooIsTheLocation.Should().Contain(@"D:\Users\me\AppData\Local\Foo\a.txt");
        fooIsTheLocation.Should().NotContain(@"D:\Users\me\AppData\Local\Foo\.git\HEAD", "that one is beneath a hidden directory below the location");

        var appDataIsTheLocation = await ResolveAsync(records, skipDirectories: FileAttributes.Hidden, locationDepth: 3);
        appDataIsTheLocation.Should().Contain(@"D:\Users\me\AppData\Local\Foo\a.txt", "a location may itself be hidden");

        var wholeVolume = await ResolveAsync(records, skipDirectories: FileAttributes.Hidden, locationDepth: 0);
        wholeVolume.Should().NotContain(@"D:\Users\me\AppData\Local\Foo\a.txt");
    }

    // --- helpers ---

    /// <summary>
    /// The paths a pass whose location lies <paramref name="locationDepth"/> segments below the volume root would read:
    /// an entry beneath a skipped directory is read only if that directory is at or above the location.
    /// </summary>
    private static async Task<List<string>> ResolveAsync(
        MftFileRecord[] records, StrongBox<int>? unresolved = null, FileAttributes skipDirectories = default, int locationDepth = 0) =>
        await MftPathResolver.ResolveAsync(Stream(records), @"D:\", unresolved ?? new StrongBox<int>(), skipDirectories)
            .Where(r => r.SkippedDepth <= locationDepth)
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
