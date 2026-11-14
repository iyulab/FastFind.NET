using System.Runtime.Versioning;
using FastFind.Models;
using FastFind.Windows.Mft;
using FastFind.Windows.Tests.Helpers;
using AwesomeAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// The change-journal pass and the directory walk read a folder to the same entries. The journal pass admitted each
/// entry by its own attributes, so a file inside <c>.git</c> (not hidden itself) was indexed on an elevated process and
/// not on any other — +1,335 entries in this repository's own folder, +181,177 in a 2.2-million-entry one.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Suite", "MFT")]
[SupportedOSPlatform("windows")]
public class MftHiddenDirectoryTests : IDisposable
{
    private readonly string _root;

    public MftHiddenDirectoryTests()
    {
        // The temp folder lies beneath AppData, which is hidden: the location's own ancestors must not count.
        _root = Path.Combine(Path.GetTempPath(), "fastfind-hidden-dirs-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(Path.Combine(_root, ".hid", "deeper"));
        Directory.CreateDirectory(Path.Combine(_root, "sys"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "x");
        File.WriteAllText(Path.Combine(_root, ".hid", "c.txt"), "x");           // not hidden itself
        File.WriteAllText(Path.Combine(_root, ".hid", "deeper", "d.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "sys", "e.txt"), "x");

        MarkDirectory(Path.Combine(_root, ".hid"), FileAttributes.Hidden);
        MarkDirectory(Path.Combine(_root, "sys"), FileAttributes.System);
    }

    private static void MarkDirectory(string path, FileAttributes attribute) =>
        new DirectoryInfo(path).Attributes |= attribute;

    public void Dispose()
    {
        try
        {
            foreach (var dir in new[] { ".hid", "sys" })
            {
                var path = Path.Combine(_root, dir);
                if (Directory.Exists(path)) new DirectoryInfo(path).Attributes = FileAttributes.Directory;
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private async Task<SortedSet<string>> ReadAsync(ProviderMode mode, Action<IndexingOptions>? configure = null)
    {
        await using var provider = new HybridFileSystemProvider(mode);
        var options = new IndexingOptions { SpecificDirectories = { _root }, ExcludedExtensions = new List<string>() };
        configure?.Invoke(options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var item in provider.EnumerateFilesAsync([_root], options, cts.Token))
            paths.Add(Path.GetRelativePath(_root, item.FullPath));
        return paths;
    }

    [ElevatedFact]
    public async Task Both_Passes_Should_Read_Nothing_From_Inside_A_Hidden_Or_System_Directory()
    {
        var walk = await ReadAsync(ProviderMode.Standard);
        var journal = await ReadAsync(ProviderMode.Mft);

        walk.Should().BeEquivalentTo(["a.txt", "sub", Path.Combine("sub", "b.txt")], "the walk does not enter them (the baseline)");
        journal.Should().BeEquivalentTo(walk, "the journal pass is the same answer, and does not yield the location itself either");
    }

    [ElevatedFact]
    public async Task Both_Passes_Should_Read_Hidden_Content_When_Asked_And_Still_Refuse_System()
    {
        var walk = await ReadAsync(ProviderMode.Standard, o => o.IncludeHidden = true);
        var journal = await ReadAsync(ProviderMode.Mft, o => o.IncludeHidden = true);

        walk.Should().Contain([".hid", Path.Combine(".hid", "c.txt"), Path.Combine(".hid", "deeper", "d.txt")]);
        walk.Should().NotContain(Path.Combine("sys", "e.txt"));
        journal.Should().BeEquivalentTo(walk);
    }

    [Theory]
    [InlineData(@"D:\", 0)]
    [InlineData(@"D:\a", 1)]
    [InlineData(@"D:\a\b\", 2)]
    [InlineData(@"D:/a/b", 2)]
    public void A_Directory_Depth_Should_Count_The_Segments_Beneath_The_Volume_Root(string path, int expected) =>
        MftFileSystemProvider.DirectoryDepth(path, @"D:\").Should().Be(expected);

    [Fact]
    public void The_Deepest_Location_That_Holds_A_Path_Should_Decide_What_Counts_As_Below_It()
    {
        string[] locations = [@"D:\data", @"D:\data\app\logs"];
        int[] depths = [1, 3];

        MftFileSystemProvider.DeepestLocationDepth(@"D:\data\app\logs\x.log", locations, depths).Should().Be(3);
        MftFileSystemProvider.DeepestLocationDepth(@"D:\data\app\x.cs", locations, depths).Should().Be(1);
        MftFileSystemProvider.DeepestLocationDepth(@"D:\other\x", locations, depths).Should().Be(-1);
        MftFileSystemProvider.DeepestLocationDepth(@"D:\any\thing", [], []).Should().Be(0, "no location means the whole volume");
    }

    [Theory]
    [InlineData(@"D:\data\app", true)]
    [InlineData(@"D:\data\app\", true)]
    [InlineData(@"d:\DATA\App", true)]
    [InlineData(@"D:\data\app\x.cs", false)]
    [InlineData(@"D:\data\application", false)]
    public void A_Pass_Should_Not_Yield_The_Location_Itself(string path, bool isLocation) =>
        MftFileSystemProvider.IsLocationItself(path, [@"D:\data\app"]).Should().Be(isLocation);
}
