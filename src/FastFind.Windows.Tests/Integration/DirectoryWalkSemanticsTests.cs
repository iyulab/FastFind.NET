using System.Diagnostics;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// What the directory walk reports and where it stops, on a small tree.
/// </summary>
[Trait("Category", "Functional")]
public class DirectoryWalkSemanticsTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = new();

    public DirectoryWalkSemanticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-walksem-" + Guid.NewGuid().ToString("N")[..8]);

        // f0.txt, L1\f1.txt, L1\L2\f2.txt, L1\L2\L3\f3.txt, L1\L2\L3\L4\f4.txt
        var dir = _root;
        for (var level = 0; level <= 4; level++)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"f{level}.txt"), "x");
            dir = Path.Combine(dir, $"L{level + 1}");
        }
    }

    public void Dispose()
    {
        // A junction is removed on its own first, so deleting the tree never reaches through it.
        foreach (var link in _links)
        {
            try { Directory.Delete(link); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static IndexingOptions Options(Action<IndexingOptions>? configure = null)
    {
        var options = new IndexingOptions
        {
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            IncludeSystem = true,
        };
        configure?.Invoke(options);
        return options;
    }

    private List<string> Walk(IndexingOptions options, string? root = null)
    {
        root ??= _root;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return new DirectoryWalk(options)
            .Enumerate(root, timeout.Token)
            .Select(p => Path.GetRelativePath(root, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "the test needs to create a junction");
        _links.Add(link);
        return link;
    }

    [Fact]
    public void No_Depth_Limit_Should_Report_Every_Level()
    {
        Walk(Options()).Should().Equal(
            "f0.txt", "L1", @"L1\f1.txt", @"L1\L2", @"L1\L2\f2.txt", @"L1\L2\L3", @"L1\L2\L3\f3.txt",
            @"L1\L2\L3\L4", @"L1\L2\L3\L4\f4.txt");
    }

    [Theory]
    [InlineData(1, new[] { "f0.txt", "L1", @"L1\f1.txt", @"L1\L2" })]
    [InlineData(2, new[] { "f0.txt", "L1", @"L1\f1.txt", @"L1\L2", @"L1\L2\f2.txt", @"L1\L2\L3" })]
    [InlineData(3, new[] { "f0.txt", "L1", @"L1\f1.txt", @"L1\L2", @"L1\L2\f2.txt", @"L1\L2\L3", @"L1\L2\L3\f3.txt", @"L1\L2\L3\L4" })]
    public void MaxDepth_Should_Mean_The_Same_Thing_As_It_Does_On_Unix(int maxDepth, string[] expected)
    {
        // The location is depth 0. A directory deeper than MaxDepth is not entered, so a MaxDepth of n lists
        // the location's own entries and n levels beneath them. The recursive enumerator this walk replaced
        // gave that for 2 and above and only the location's own entries for 1; the Unix providers have
        // always given it, and ProviderConformanceTests holds the two to the same answer.
        Walk(Options(o => o.MaxDepth = maxDepth)).Should().Equal(expected);
    }

    [Fact]
    public void A_Junction_Should_Be_Listed_But_Not_Followed_By_Default()
    {
        // L1\loop -> the root: following it would walk the tree again, beneath itself, without end.
        var link = Junction(Path.Combine(_root, "L1", "loop"), _root);

        var entries = Walk(Options());

        entries.Should().Contain(@"L1\loop", "the link is an entry like any other");
        entries.Should().NotContain(e => e.StartsWith(@"L1\loop\", StringComparison.OrdinalIgnoreCase),
            "nothing beneath it is reported unless links are to be followed");
        entries.Should().Contain(@"L1\L2\L3\L4\f4.txt", "the rest of the tree is untouched");
        Directory.Exists(link).Should().BeTrue();
    }

    [Fact]
    public void A_Followed_Junction_Should_Report_What_It_Points_At()
    {
        var target = Path.Combine(Path.GetTempPath(), "fastfind-walksem-target-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "remote.txt"), "x");
        try
        {
            Junction(Path.Combine(_root, "linked"), target);

            Walk(Options(o => o.FollowSymlinks = true)).Should().Contain(@"linked\remote.txt");
            Walk(Options(o => o.FollowSymlinks = false)).Should().NotContain(@"linked\remote.txt");
        }
        finally
        {
            try { Directory.Delete(Path.Combine(_root, "linked")); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_Followed_Junction_That_Loops_Should_Still_Come_To_An_End()
    {
        Junction(Path.Combine(_root, "L1", "loop"), _root);

        var entries = Walk(Options(o => o.FollowSymlinks = true));

        entries.Should().Contain(@"L1\L2\L3\L4\f4.txt");
        entries.Count.Should().BeLessThan(10_000, "following a cycle is bounded, not unlimited");
    }

    [Fact]
    public void A_File_Whose_Only_Attribute_Is_Normal_Should_Be_Reported()
    {
        // A file with its archive bit cleared (a backup tool, a restore, some file systems) carries
        // FILE_ATTRIBUTE_NORMAL alone. The walk skipped it: the attributes to skip started from Normal.
        var path = Path.Combine(_root, "plain.txt");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.Normal);
        File.GetAttributes(path).Should().Be(FileAttributes.Normal, "the test needs a file with no other attribute");

        Walk(Options(o => { o.IncludeHidden = false; o.IncludeSystem = false; })).Should().Contain("plain.txt");
        Walk(Options()).Should().Contain("plain.txt");
    }
}
