using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Mft;
using FastFind.Windows.Tests.Helpers;
using AwesomeAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// With administrator rights the Auto provider starts with the directory walk and, if that has not finished within a
/// time budget, continues with the change-journal pass. Driven here by scripted providers and a clock the test moves:
/// no volume, no elevation, no real wait.
/// </summary>
[Trait("Category", "Functional")]
[SupportedOSPlatform("windows")]
public class HybridHandOverTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_Walk_That_Finishes_Within_The_Budget_Should_Be_The_Whole_Pass()
    {
        var clock = new ManualClock();
        var walk = new Scripted((_, _) => Yield(Item(@"D:\proj\a.txt"), Item(@"D:\proj\b.txt")));
        var journal = new Scripted((_, _) => throw new InvalidOperationException("the journal pass must not run"));
        await using var provider = Create(walk, journal, clock);

        var items = await Collect(provider, @"D:\proj");

        items.Select(i => i.FullPath).Should().Equal(@"D:\proj\a.txt", @"D:\proj\b.txt");
        journal.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_Walk_That_Outlasts_The_Budget_Should_Hand_Over_To_The_Journal_For_What_It_Did_Not_Read()
    {
        var clock = new ManualClock();
        Scripted? walkRef = null;
        var walkClosedWhenJournalStarted = false;

        var walk = walkRef = new Scripted((_, _) => SlowWalk(clock));

        var journal = new Scripted((locations, _) =>
        {
            walkClosedWhenJournalStarted = walkRef!.Closed;
            // everything under the location, with one the walk already gave spelled differently
            return Yield(Item(@"d:\PROJ\A.txt"), Item(@"D:\proj\b.txt"), Item(@"D:\proj\c.txt"), Item(@"D:\proj\sub\d.txt"));
        });
        await using var provider = Create(walk, journal, clock);

        var items = await Collect(provider, @"D:\proj");

        items.Select(i => i.FullPath).Should().Equal(
            [@"D:\proj\a.txt", @"D:\proj\b.txt", @"D:\proj\c.txt", @"D:\proj\sub\d.txt"],
            "the two the walk gave are not given again, whatever their case or separators, and the third it never reached comes from the journal");
        journal.LastLocations.Should().Equal([@"D:\proj"]);
        walkClosedWhenJournalStarted.Should().BeTrue("the walk's producer is cancelled and awaited before the journal pass starts");
    }

    [Fact]
    public async Task Walked_Entries_Should_Carry_No_Size_Or_Times_Like_The_Journal_Passes_Own()
    {
        var clock = new ManualClock();
        var walk = new Scripted((_, _) => Yield(Item(@"D:\proj\a.txt", size: 4096)));
        await using var provider = Create(walk, new Scripted((_, _) => Yield()), clock);

        var items = await Collect(provider, @"D:\proj");

        items.Should().ContainSingle().Which.Should().Match<FileItem>(i =>
            i.Size == 0 && i.CreatedTime == default && i.ModifiedTime == default && i.AccessedTime == default,
            "the engine is told once, before the pass, that nothing is collected — an index that is right for half its entries is wrong");
    }

    [Fact]
    public async Task A_Pass_That_May_Hand_Over_Should_Say_That_It_Collects_Nothing()
    {
        // like the real journal pass, which collects nothing unless asked
        await using var provider = Create(
            new Scripted((_, _) => Yield()), new Scripted((_, _) => Yield()) { ProvidesMetadata = false }, new ManualClock());

        ((IFileSystemProvider)provider).ProvidesFileMetadata(new IndexingOptions()).Should().BeFalse();
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("depth")]
    [InlineData("links")]
    public async Task An_Option_The_Journal_Pass_Does_Not_Read_Should_Keep_The_Walk_Alone(string which)
    {
        var options = new IndexingOptions();
        switch (which)
        {
            case "metadata": options.CollectFileMetadata = true; break;
            case "depth": options.MaxDepth = 2; break;
            case "links": options.FollowSymlinks = true; break;
        }

        var clock = new ManualClock();
        var walk = new Scripted((_, _) => Yield(Item(@"D:\proj\a.txt", size: 77)));
        var journal = new Scripted((_, _) => throw new InvalidOperationException("the journal pass must not run"));
        await using var provider = Create(walk, journal, clock);

        var items = await Collect(provider, options, @"D:\proj");

        items.Should().ContainSingle().Which.Size.Should().Be(77, "the walk's items are given as they are");
        ((IFileSystemProvider)provider).ProvidesFileMetadata(options).Should().BeTrue("a walk-only pass collects what the walk reads");
        journal.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_Volume_Root_Should_Go_Straight_To_The_Journal_Pass()
    {
        var clock = new ManualClock();
        var walk = new Scripted((_, _) => throw new InvalidOperationException("a walk of a whole volume is the slow way"));
        var journal = new Scripted((_, _) => Yield(Item(@"D:\x.txt")));
        await using var provider = Create(walk, journal, clock);

        var items = await Collect(provider, @"D:\");

        items.Should().ContainSingle();
        walk.Calls.Should().Be(0);
        journal.LastLocations.Should().Equal([@"D:\"]);
    }

    [Fact]
    public async Task A_Volume_The_Journal_Cannot_Read_Should_Be_Walked_Without_A_Budget()
    {
        var clock = new ManualClock();
        var walk = new Scripted((_, _) =>
        {
            clock.Advance(Budget * 5);
            return Yield(Item(@"E:\share\a.txt", size: 9), Item(@"E:\share\b.txt"));
        });
        var journal = new Scripted((_, _) => throw new InvalidOperationException("the journal cannot read E:"));
        await using var provider = Create(walk, journal, clock, journalCovers: drive => drive == 'D');

        var items = await Collect(provider, @"E:\share");

        items.Select(i => i.FullPath).Should().Equal(@"E:\share\a.txt", @"E:\share\b.txt");
        items.Should().OnlyContain(i => i.Size == 0, "every entry of a pass that may hand over has the same shape");
    }

    [Fact]
    public async Task The_Locations_Of_One_Volume_Should_Share_One_Budget()
    {
        var clock = new ManualClock();
        var walk = new Scripted((locations, _) =>
        {
            // the first location is read to its end and takes all of the budget doing it
            if (locations.Single() == @"D:\a")
                return AllOfTheBudget(clock);

            return Yield(Item(@"D:\b\1.txt"));      // the second has more to read, and the budget is spent
        });
        var journal = new Scripted((_, _) => Yield(Item(@"D:\b\2.txt")));
        await using var provider = Create(walk, journal, clock);

        var items = await Collect(provider, @"D:\a", @"D:\b");

        items.Select(i => i.FullPath).Should().Equal(@"D:\a\1.txt", @"D:\b\2.txt");
        journal.LastLocations.Should().Equal([@"D:\b"], "a location the walk finished is not read again");
    }

    [Fact]
    public async Task Failures_Should_Be_Those_Of_The_Providers_That_Took_Part_In_The_Last_Pass()
    {
        var clock = new ManualClock();
        var walk = new Scripted((_, _) => Yield(Item(@"D:\proj\a.txt"))) { Failures = [@"D:\proj\locked"] };
        var journal = new Scripted((_, _) => Yield()) { Failures = [@"D:\"] };
        await using var provider = Create(walk, journal, clock);

        await Collect(provider, @"D:\proj");
        provider.EnumerationFailures.Should().BeEquivalentTo([@"D:\proj\locked"], "the journal pass did not run; its list is an older pass's");

        await Collect(provider, @"D:\");
        provider.EnumerationFailures.Should().BeEquivalentTo([@"D:\"], "the walk did not run this time");
    }

    [ElevatedFact]
    public async Task On_A_Real_Tree_Every_Route_Should_Give_The_Same_Entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "fastfind-handover-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub", "deeper"));
            Directory.CreateDirectory(Path.Combine(root, ".hid"));
            File.WriteAllText(Path.Combine(root, "a.txt"), "x");
            File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "x");
            File.WriteAllText(Path.Combine(root, "sub", "deeper", "c.txt"), "x");
            File.WriteAllText(Path.Combine(root, ".hid", "d.txt"), "x");
            new DirectoryInfo(Path.Combine(root, ".hid")).Attributes |= FileAttributes.Hidden;

            async Task<SortedSet<string>> PathsAsync(HybridFileSystemProvider provider)
            {
                await using var _ = provider;
                var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                await foreach (var item in provider.EnumerateFilesAsync([root], new IndexingOptions { ExcludedExtensions = [] }))
                    paths.Add(Path.GetRelativePath(root, item.FullPath));
                return paths;
            }

            var walkOnly = await PathsAsync(new HybridFileSystemProvider(ProviderMode.Standard));
            var neverHandsOver = await PathsAsync(new HybridFileSystemProvider(TimeSpan.FromHours(1)));
            var alwaysHandsOver = await PathsAsync(new HybridFileSystemProvider(TimeSpan.Zero));

            walkOnly.Should().BeEquivalentTo(
                ["a.txt", "sub", Path.Combine("sub", "b.txt"), Path.Combine("sub", "deeper"), Path.Combine("sub", "deeper", "c.txt")]);
            neverHandsOver.Should().BeEquivalentTo(walkOnly);
            alwaysHandsOver.Should().BeEquivalentTo(walkOnly, "the journal pass takes over after the first entry and gives the rest, none twice");
        }
        finally
        {
            try
            {
                new DirectoryInfo(Path.Combine(root, ".hid")).Attributes = FileAttributes.Directory;
                Directory.Delete(root, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task The_Forced_Modes_Should_Not_Change()
    {
        // Explicit Standard needs no elevation; it is the walk, every time, with the real metadata.
        await using var provider = new HybridFileSystemProvider(ProviderMode.Standard);
        provider.CurrentMode.Should().Be(ProviderMode.Standard);
        ((IFileSystemProvider)provider).ProvidesFileMetadata(new IndexingOptions()).Should().BeTrue();
    }

    // --- helpers ---

    private static async IAsyncEnumerable<FileItem> SlowWalk(ManualClock clock)
    {
        yield return Item(@"D:\proj\a.txt", size: 100);
        yield return Item(@"D:\proj\b.txt", size: 100);
        clock.Advance(Budget);                  // the budget runs out while the walk still has more to read
        await Task.Yield();
        yield return Item(@"D:\proj\c.txt");    // read, then left to the journal pass: the budget is spent
    }

    private static async IAsyncEnumerable<FileItem> AllOfTheBudget(ManualClock clock)
    {
        await Task.Yield();
        yield return Item(@"D:\a\1.txt");
        clock.Advance(Budget);                  // ...and the walk ends: a finished walk is the pass whatever the clock says
    }

    private static HybridFileSystemProvider Create(
        Scripted walk, Scripted journal, ManualClock clock, Func<char, bool>? journalCovers = null) =>
        new(walk, journal, Budget, clock, journalCovers ?? (_ => true));

    private static Task<List<FileItem>> Collect(HybridFileSystemProvider provider, params string[] locations) =>
        Collect(provider, new IndexingOptions(), locations);

    private static async Task<List<FileItem>> Collect(HybridFileSystemProvider provider, IndexingOptions options, params string[] locations)
    {
        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(locations, options))
            items.Add(item);
        return items;
    }

    private static FileItem Item(string path, long size = 0) => new()
    {
        FullPath = path,
        Name = Path.GetFileName(path),
        DirectoryPath = Path.GetDirectoryName(path)!,
        Extension = Path.GetExtension(path),
        Size = size,
        CreatedTime = size > 0 ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : default,
        ModifiedTime = size > 0 ? new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) : default,
        AccessedTime = size > 0 ? new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) : default,
        Attributes = FileAttributes.Archive,
    };

    private static async IAsyncEnumerable<FileItem> Yield(params FileItem[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    /// <summary>A clock that moves only when the test says so.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A provider whose enumeration is a function of the locations it is asked for.</summary>
    private sealed class Scripted(Func<IReadOnlyList<string>, CancellationToken, IAsyncEnumerable<FileItem>> script) : IFileSystemProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<string> LastLocations { get; private set; } = [];
        public bool Closed { get; private set; }
        public IReadOnlyCollection<string> Failures { get; init; } = [];
        public bool ProvidesMetadata { get; init; } = true;

        public PlatformType SupportedPlatform => PlatformType.Windows;
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> EnumerationFailures => Failures;
        public bool ProvidesFileMetadata(IndexingOptions options) => ProvidesMetadata;

        public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
            IEnumerable<string> locations, IndexingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastLocations = locations.ToList();
            Closed = false;
            try
            {
                await foreach (var item in script(LastLocations, cancellationToken).WithCancellation(cancellationToken))
                    yield return item;
            }
            finally
            {
                Closed = true;
            }
        }

        public Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<FileItem?>(null);

        public Task<IEnumerable<FastFind.Interfaces.DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Empty<FastFind.Interfaces.DriveInfo>());

        public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
            IEnumerable<string> locations, MonitoringOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult("NTFS");
        public ProviderPerformance GetPerformanceInfo() => new();
        public void Dispose() { }
    }
}
