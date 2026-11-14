using FastFind.Indexing;
using FastFind.Models;
using AwesomeAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// <see cref="DirectoryAttributeCache"/>: what a monitored burst remembers of the directories above its changes, and what makes
/// it forget — a report of a change to that path, the passing of time, or running out of room — never a failed read.
/// </summary>
[Trait("Category", "Functional")]
public class DirectoryAttributeCacheTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void An_Entry_Is_Believed_For_Its_Lifetime_And_Not_Beyond()
    {
        var clock = new ManualClock();
        var cache = new DirectoryAttributeCache(clock, TimeSpan.FromSeconds(5));
        cache.Set(@"C:\data\a", FileAttributes.Directory | FileAttributes.Hidden);

        cache.TryGet(@"C:\data\a", out var attributes).Should().BeTrue();
        attributes.Should().Be(FileAttributes.Directory | FileAttributes.Hidden);

        clock.Advance(TimeSpan.FromSeconds(4.9));
        cache.TryGet(@"C:\data\a", out _).Should().BeTrue("it has not yet expired");

        clock.Advance(TimeSpan.FromSeconds(0.2));
        cache.TryGet(@"C:\data\a", out _).Should().BeFalse("a monitor that does not report attribute changes is believed for at most the lifetime");
    }

    [Fact]
    public void A_Report_Of_A_Change_Forgets_That_Path_And_Nothing_Beneath_It()
    {
        var cache = new DirectoryAttributeCache();
        cache.Set(@"C:\data\a", FileAttributes.Directory);
        cache.Set(@"C:\data\a\b", FileAttributes.Directory);

        cache.Invalidate(@"C:\data\a");

        cache.TryGet(@"C:\data\a", out _).Should().BeFalse();
        cache.TryGet(@"C:\data\a\b", out _).Should().BeTrue("a directory's own attributes do not change when its parent's do");
    }

    [Fact]
    public void Paths_Are_Compared_As_The_File_System_Compares_Them()
    {
        var cache = new DirectoryAttributeCache();
        cache.Set(@"C:\Data\A", FileAttributes.Directory);

        if (OperatingSystem.IsWindows())
        {
            cache.TryGet(@"c:/data/a", out _).Should().BeTrue("case and separators fold on Windows");
            cache.Invalidate(@"c:/DATA/a");
            cache.TryGet(@"C:\Data\A", out _).Should().BeFalse();
        }
        else
        {
            cache.TryGet(@"C:\Data\a", out _).Should().BeFalse("two spellings are two names elsewhere");
        }
    }

    [Fact]
    public void A_Null_Or_Empty_Path_Is_Ignored()
    {
        var cache = new DirectoryAttributeCache();
        cache.Set(@"C:\data", FileAttributes.Directory);

        cache.Invalidate(null);
        cache.Invalidate("");

        cache.Count.Should().Be(1);
    }

    [Fact]
    public void A_Full_Cache_Is_Emptied_To_Make_Room()
    {
        var cache = new DirectoryAttributeCache(capacity: 3);
        cache.Set(@"C:\a", FileAttributes.Directory);
        cache.Set(@"C:\b", FileAttributes.Directory);
        cache.Set(@"C:\c", FileAttributes.Directory);

        cache.Set(@"C:\b", FileAttributes.Directory | FileAttributes.Hidden);
        cache.Count.Should().Be(3, "replacing an entry needs no room");

        cache.Set(@"C:\d", FileAttributes.Directory);

        cache.Count.Should().Be(1);
        cache.TryGet(@"C:\d", out _).Should().BeTrue();
        cache.TryGet(@"C:\a", out _).Should().BeFalse();
    }

    private static IndexingOptions Options() => new()
    {
        SpecificDirectories = { @"C:\data" },
        ExcludedPaths = new List<string>(),
        ExcludedExtensions = new List<string>(),
        IncludeHidden = false,
    };

    [Fact]
    public async Task The_Check_Reads_Each_Directory_Once_Across_A_Burst()
    {
        var cache = new DirectoryAttributeCache();
        var reads = new List<string>();
        Task<FileAttributes?> Read(string directory, CancellationToken _)
        {
            reads.Add(directory);
            return Task.FromResult<FileAttributes?>(FileAttributes.Directory);
        }

        for (var i = 0; i < 10; i++)
        {
            (await IndexingAdmission.IsBeneathSkippedDirectoryAsync(
                $@"C:\data\a\b\c\f{i}.txt", [@"C:\data"], Options(), Read, cache: cache)).Should().BeFalse();
        }

        reads.Should().BeEquivalentTo([@"C:\data\a\b\c", @"C:\data\a\b", @"C:\data\a"]);
    }

    [Fact]
    public async Task Without_A_Cache_Every_Change_Reads_Every_Directory()
    {
        var reads = 0;
        Task<FileAttributes?> Read(string directory, CancellationToken _)
        {
            reads++;
            return Task.FromResult<FileAttributes?>(FileAttributes.Directory);
        }

        for (var i = 0; i < 4; i++)
            await IndexingAdmission.IsBeneathSkippedDirectoryAsync($@"C:\data\a\b\f{i}.txt", [@"C:\data"], Options(), Read);

        reads.Should().Be(8);
    }

    [Fact]
    public async Task A_Read_That_Failed_Is_Not_Remembered()
    {
        var cache = new DirectoryAttributeCache();
        var reads = 0;
        Task<FileAttributes?> Read(string directory, CancellationToken _)
        {
            reads++;
            return Task.FromResult<FileAttributes?>(null);
        }

        await IndexingAdmission.IsBeneathSkippedDirectoryAsync(@"C:\data\a\f.txt", [@"C:\data"], Options(), Read, cache: cache);
        await IndexingAdmission.IsBeneathSkippedDirectoryAsync(@"C:\data\a\g.txt", [@"C:\data"], Options(), Read, cache: cache);

        reads.Should().Be(2, "a directory that could not be read may be readable a moment later");
        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_Remembered_Hidden_Directory_Still_Hides_What_Is_Beneath_It_And_A_Change_Of_Options_Is_Honoured()
    {
        var cache = new DirectoryAttributeCache();
        Task<FileAttributes?> Read(string directory, CancellationToken _) =>
            Task.FromResult<FileAttributes?>(directory.EndsWith(".git") ? FileAttributes.Directory | FileAttributes.Hidden : FileAttributes.Directory);

        var hidden = await IndexingAdmission.IsBeneathSkippedDirectoryAsync(@"C:\data\.git\x.pack", [@"C:\data"], Options(), Read, cache: cache);
        var again = await IndexingAdmission.IsBeneathSkippedDirectoryAsync(@"C:\data\.git\y.pack", [@"C:\data"], Options(), Read, cache: cache);
        var includeHidden = Options();
        includeHidden.IncludeHidden = true;
        var withHiddenIncluded = await IndexingAdmission.IsBeneathSkippedDirectoryAsync(@"C:\data\.git\z.pack", [@"C:\data"], includeHidden, Read, cache: cache);

        hidden.Should().BeTrue();
        again.Should().BeTrue("the remembered attributes say hidden");
        withHiddenIncluded.Should().BeFalse("an entry is attributes, not a verdict: the options are asked at the moment of the check");
    }
}
