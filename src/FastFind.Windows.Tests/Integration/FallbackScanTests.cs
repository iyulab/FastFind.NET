using System.Diagnostics;
using FastFind.Indexing;
using FastFind.Models;
using FastFind.Windows.Implementation;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// The live scan an index falls back to for a location it does not cover: how long it takes to notice there is
/// nothing more to scan, and that it scans all there is.
/// </summary>
/// <remarks>
/// Its workers used to decide the scan was over by polling: with an empty queue each slept 10 ms, ten
/// times, and only then left. So the scan never took less than about 100 ms, whatever it had to do —
/// every query that found nothing paid that, on an index that had answered in 20 ms. It also made the
/// scan end when the queue was <i>momentarily</i> empty, which is wrong while another worker is still
/// listing a directory whose children it has yet to queue.
/// </remarks>
[Trait("Category", "Functional")]
public class FallbackScanTests : IDisposable
{
    private readonly string _root;

    public FallbackScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-fallbackscan-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static WindowsSearchIndex IndexOf(IEnumerable<string> directories, int filesPerDirectory = 3)
    {
        var index = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
        var items = new List<FastFileItem>();
        foreach (var directory in directories)
        {
            for (var i = 0; i < filesPerDirectory; i++)
            {
                var name = $"indexed_{i}.txt";
                items.Add(new FastFileItem(
                    Path.Combine(directory, name), name, directory, ".txt",
                    size: 10, created: DateTime.UtcNow, modified: DateTime.UtcNow, accessed: DateTime.UtcNow,
                    attributes: FileAttributes.Archive, driveLetter: directory[0]));
            }
        }

        index.AddBatchAsync(items).GetAwaiter().GetResult();
        return index;
    }

    private static async Task<List<string>> SearchAsync(WindowsSearchIndex index, SearchQuery query)
    {
        var found = new List<string>();
        await foreach (var item in index.SearchAsync(query))
            found.Add(item.FullPath);
        return found;
    }

    [Fact]
    public async Task A_Fallback_With_Nowhere_To_Look_Should_Not_Spend_Time_Waiting_To_Be_Sure()
    {
        // An index of 30 items whose directories do not exist on disk, and a query scoped to a location it does
        // not cover: the scan finds nothing to scan, and should say so at once.
        var directories = Enumerable.Range(0, 10).Select(i => $@"X:\fastfind-does-not-exist\d{i}").ToList();
        var index = IndexOf(directories);
        var query = new SearchQuery { BasePath = @"X:\fastfind-does-not-exist-either", SearchText = "zzzznomatch", SearchFileNameOnly = true, MaxResults = 100 };

        await SearchAsync(index, query); // warm-up: JIT, first-use costs

        // The old polling cannot finish in under 100 ms (ten sleeps of at least 10 ms); take the best of a few
        // so a busy machine cannot fail the test by being slow once.
        var best = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            (await SearchAsync(index, query)).Should().BeEmpty();
            best = stopwatch.Elapsed < best ? stopwatch.Elapsed : best;
        }

        best.Should().BeLessThan(TimeSpan.FromMilliseconds(90),
            "a scan with nothing to scan should end as soon as the last worker is idle, not after a polling timeout");
    }

    [Fact]
    public async Task The_Fallback_Should_Find_Every_File_In_A_Deep_Tree_Exactly_Once()
    {
        // Three branches, six levels, two files at each directory: a scan that stops while another worker is
        // still queueing children would miss some.
        var expected = new List<string>();
        void Grow(string dir, int depth)
        {
            Directory.CreateDirectory(dir);
            for (var f = 0; f < 2; f++)
            {
                var file = Path.Combine(dir, $"needle_{depth}_{f}.dat");
                File.WriteAllText(file, "x");
                expected.Add(file);
            }

            if (depth == 0) return;
            for (var b = 0; b < 3; b++) Grow(Path.Combine(dir, $"b{b}"), depth - 1);
        }
        Grow(_root, 5);

        // The index holds items elsewhere and nothing under the base path: a location it does not cover, which is
        // what sends a query to the disk.
        var index = IndexOf([Path.Combine(Path.GetTempPath(), "fastfind-fallbackscan-elsewhere")], filesPerDirectory: 3);
        var query = new SearchQuery { BasePath = _root, SearchText = "needle", SearchFileNameOnly = true, IncludeSubdirectories = true, MaxResults = 100_000 };

        var found = await SearchAsync(index, query);

        found.Should().HaveCount(expected.Count);
        found.Should().OnlyHaveUniqueItems();
        found.Should().BeEquivalentTo(expected);
    }
}
