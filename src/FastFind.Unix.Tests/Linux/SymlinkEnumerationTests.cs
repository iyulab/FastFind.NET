using FastFind.Indexing;
using FastFind.Models;
using AwesomeAssertions;
using FastFind.Unix.Linux;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// A test that needs to create a symbolic link: reported as skipped, with the reason, where the account cannot.
/// </summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        var probe = Path.Combine(Path.GetTempPath(), "fastfind-symlink-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(probe);
            File.WriteAllText(Path.Combine(probe, "target"), "x");
            File.CreateSymbolicLink(Path.Combine(probe, "link"), Path.Combine(probe, "target"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip = "This account cannot create symbolic links (on Windows that needs developer mode or an elevated process).";
        }
        finally
        {
            try { Directory.Delete(probe, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// What the Linux provider does with a symbolic link: lists it, and enters it only when asked to follow links.
/// </summary>
/// <remarks>
/// It used to leave a link out altogether, so a file reachable under a second name - <c>python3</c> pointing at
/// <c>python3.12</c> - could not be found by the name it is used under. "Not following" means not going in;
/// it does not mean not saying the link is there.
/// </remarks>
[Trait("Category", "Functional")]
public class SymlinkEnumerationTests : IDisposable
{
    private readonly string _root;

    public SymlinkEnumerationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-symlinks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        File.WriteAllText(Path.Combine(_root, "real", "inside.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "plain.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private async Task<List<string>> EntriesAsync(bool followSymlinks)
    {
        using var provider = new LinuxFileSystemProvider();
        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            MaxFileSize = null,
            FollowSymlinks = followSymlinks,
        };

        var entries = new List<string>();
        await foreach (var item in provider.EnumerateFilesAsync([_root], options))
            entries.Add(Path.GetRelativePath(_root, item.FullPath).Replace('\\', '/'));
        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private void Link()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "linkdir"), Path.Combine(_root, "real"));
        File.CreateSymbolicLink(Path.Combine(_root, "linkfile.txt"), Path.Combine(_root, "plain.txt"));
    }

    [SymlinkFact]
    public async Task A_Link_Should_Be_Listed_But_Not_Entered_By_Default()
    {
        Link();

        var entries = await EntriesAsync(followSymlinks: false);

        entries.Should().Contain(["linkdir", "linkfile.txt"], "a link is an entry, findable by its own name");
        entries.Should().Contain("real/inside.txt", "the target is indexed where it really is");
        entries.Should().NotContain("linkdir/inside.txt", "what a link points at is not entered unless links are followed");
    }

    [SymlinkFact]
    public async Task A_Followed_Link_Should_Be_Entered()
    {
        Link();

        var entries = await EntriesAsync(followSymlinks: true);

        entries.Should().Contain(["linkdir", "linkdir/inside.txt", "linkfile.txt"]);
    }

    [SymlinkFact]
    public async Task A_Followed_Link_Back_To_An_Ancestor_Is_Entered_A_Bounded_Number_Of_Times()
    {
        // real/up -> the root: followed without a limit, the tree is walked again beneath itself until the
        // operating system refuses the path (about forty crossings), forty times the work and as many duplicates.
        Directory.CreateSymbolicLink(Path.Combine(_root, "real", "up"), _root);

        var entries = await EntriesAsync(followSymlinks: true);

        var deepest = entries.Max(e => e.Split('/').Count(segment => segment == "up"));
        deepest.Should().Be(IndexingAdmission.MaxLinkHops + 1,
            "the chain is listed one link past the limit and not entered beyond it");
        entries.Should().OnlyHaveUniqueItems();
        entries.Should().Contain("real/inside.txt", "what is not behind a link is untouched by the limit");
    }

    [SymlinkFact]
    public async Task A_Link_Back_To_An_Ancestor_Is_Listed_Once_When_Links_Are_Not_Followed()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "real", "up"), _root);

        var entries = await EntriesAsync(followSymlinks: false);

        entries.Should().Contain("real/up");
        entries.Count(e => e.Contains("up")).Should().Be(1, "a link that is not followed is only listed");
    }
}
