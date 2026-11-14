using System.Diagnostics;
using FastFind.Models;
using FastFind.Windows.Implementation;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// Runs alone: it measures the process's open-handle count, which any test running beside it moves.
/// </summary>
[CollectionDefinition("HandleCount", DisableParallelization = true)]
public sealed class HandleCountCollection;

/// <summary>
/// What an indexing pass on the directory-walk provider holds open, and for how long.
/// </summary>
/// <remarks>
/// The walk used to hand one recursive <c>Directory.EnumerateFileSystemEntries</c> to a producer task.
/// That enumerator opens every sub-directory it discovers and keeps the handle until the directory is
/// listed, so the number of open handles followed the width of the tree — tens of thousands on a system
/// volume — and a pass that was stopped early left its producer parked on a full channel with all of
/// them still open, until a garbage collection happened to finalise the enumerator.
/// </remarks>
[Collection("HandleCount")]
[Trait("Category", "Functional")]
public class DirectoryWalkTests : IDisposable
{
    private const int Siblings = 2000;

    private readonly string _root;

    public DirectoryWalkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-walk-" + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < Siblings; i++)
        {
            var dir = Path.Combine(_root, "d" + i.ToString("D4"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static int OpenHandles()
    {
        using var process = Process.GetCurrentProcess();
        return process.HandleCount;
    }

    private static IndexingOptions Options(string root) => new()
    {
        SpecificDirectories = { root },
        ExcludedPaths = new List<string>(),
        ExcludedExtensions = new List<string>(),
        IncludeHidden = true,
        IncludeSystem = true,
    };

    [Fact]
    public async Task A_Wide_Tree_Should_Be_Walked_Holding_A_Bounded_Number_Of_Handles()
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);

        var baseline = OpenHandles();
        var peak = baseline;
        var count = 0;

        await foreach (var _ in provider.EnumerateFilesAsync([_root], Options(_root)))
        {
            if (++count % 25 == 0) peak = Math.Max(peak, OpenHandles());
        }

        count.Should().BeGreaterThanOrEqualTo(Siblings * 2, "every directory and its file is reported");
        (peak - baseline).Should().BeLessThan(300,
            $"the walk should hold a handful of directories open, not one per sibling ({Siblings}); baseline {baseline}, peak {peak}");
    }

    [Fact]
    public async Task A_Pass_Stopped_Early_Should_Leave_No_Handles_Open_When_It_Returns()
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);

        var baseline = OpenHandles();

        // Take a few items and walk away, as a MaxFileCount cap or StopIndexingAsync does. No GC is forced:
        // the handles must be released by ending the pass, not by a finaliser that runs when it likes.
        await using (var items = provider.EnumerateFilesAsync([_root], Options(_root)).GetAsyncEnumerator())
        {
            for (var i = 0; i < 10; i++)
                (await items.MoveNextAsync()).Should().BeTrue();
        }

        var after = OpenHandles();
        (after - baseline).Should().BeLessThan(100,
            $"nothing of the stopped pass should still be running; baseline {baseline}, after {after}");
    }

    [Fact]
    public async Task A_Second_Pass_Should_Start_From_Zero_After_A_Stopped_One()
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);

        var baseline = OpenHandles();
        var peak = baseline;

        for (var pass = 0; pass < 3; pass++)
        {
            await using var items = provider.EnumerateFilesAsync([_root], Options(_root)).GetAsyncEnumerator();
            for (var i = 0; i < 1500; i++)
            {
                if (!await items.MoveNextAsync()) break;
                if (i % 100 == 0) peak = Math.Max(peak, OpenHandles());
            }
        }

        (peak - baseline).Should().BeLessThan(300,
            "three stopped passes should not stack their handles on one another; "
            + $"baseline {baseline}, peak {peak}");
    }

    [Fact]
    public void An_Excluded_Directory_Should_Not_Be_Descended_Into()
    {
        var excluded = Path.Combine(_root, "d0000");
        for (var i = 0; i < 300; i++)
            Directory.CreateDirectory(Path.Combine(excluded, "deep" + i, "deeper"));

        var options = Options(_root);
        options.ExcludedPaths = new List<string> { excluded };

        var walk = new DirectoryWalk(options);
        var paths = walk.Enumerate(_root, CancellationToken.None).ToList();

        paths.Should().NotContain(p => p.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            , "nothing beneath an excluded directory is reported");
        // The root and every sibling directory are listed; the excluded one and its 600 sub-directories are not.
        walk.DirectoriesOpened.Should().Be(1 + (Siblings - 1),
            "an excluded directory is skipped before it is opened, not listed and then filtered");
    }
}
