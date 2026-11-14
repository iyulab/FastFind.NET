using System.Diagnostics;
using System.Runtime.Versioning;
using FastFind.Indexing;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Optimization;

/// <summary>
/// What the check that keeps a monitored change from a hidden directory out of the index costs: one attribute read
/// for every directory between the monitored location and the changed item, no cache. The decision whether a cache
/// is needed (a directory's attributes invalidated by the monitor's own directory events) is taken from these
/// figures, not from a guess; see <c>docs/BENCHMARKS.md</c>, "Monitored changes".
/// </summary>
[Trait("Category", "Performance")]
[Trait("Suite", "MonitoredChangeCost")]
[SupportedOSPlatform("windows")]
public class MonitoredChangeCostTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fastfind-burst-" + Guid.NewGuid().ToString("N")[..8]);

    public MonitoredChangeCostTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [PerformanceTestFact]
    public async Task Cost_Of_The_Ancestor_Check_By_Depth()
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);
        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = false,
            IncludeSystem = false,
        };

        // One deep chain; a file at the bottom of every depth we measure.
        var deepest = _root;
        var filesByDepth = new Dictionary<int, string>();
        foreach (var depth in new[] { 1, 3, 6, 10, 20, 30 })
        {
            while (deepest.Count(c => c == Path.DirectorySeparatorChar) - _root.Count(c => c == Path.DirectorySeparatorChar) < depth - 1)
                deepest = Path.Combine(deepest, "d" + Guid.NewGuid().ToString("N")[..4]);
            Directory.CreateDirectory(deepest);
            var file = Path.Combine(deepest, "leaf.txt");
            File.WriteAllText(file, "x");
            filesByDepth[depth] = file;
        }

        Func<string, CancellationToken, Task<FileAttributes?>> attributesOf =
            (directory, ct) => provider.GetAttributesAsync(directory, ct);

        Func<string, CancellationToken, Task<FileAttributes?>> direct =
            (directory, ct) => Task.FromResult<FileAttributes?>(File.GetAttributes(directory));

        const int iterations = 2000;
        _output.WriteLine($"{"reads",5} {"via provider µs",16} {"direct µs",10} {"own stat µs",12}");
        foreach (var (depth, file) in filesByDepth.OrderBy(p => p.Key))
        {
            // warm-up
            for (var i = 0; i < 200; i++)
            {
                await IndexingAdmission.IsBeneathSkippedDirectoryAsync(file, [_root], options, attributesOf);
                await IndexingAdmission.IsBeneathSkippedDirectoryAsync(file, [_root], options, direct);
                await provider.GetFileInfoAsync(file);
            }

            var check = new double[iterations];
            var own = new double[iterations];
            var viaDirect = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                var beneath = await IndexingAdmission.IsBeneathSkippedDirectoryAsync(file, [_root], options, attributesOf);
                var t1 = Stopwatch.GetTimestamp();
                await provider.GetFileInfoAsync(file);
                var t2 = Stopwatch.GetTimestamp();
                var beneathDirect = await IndexingAdmission.IsBeneathSkippedDirectoryAsync(file, [_root], options, direct);
                var t3 = Stopwatch.GetTimestamp();
                beneath.Should().BeFalse();
                beneathDirect.Should().BeFalse();
                viaDirect[i] = (t3 - t2) * 1_000_000.0 / Stopwatch.Frequency;
                check[i] = (t1 - t0) * 1_000_000.0 / Stopwatch.Frequency;
                own[i] = (t2 - t1) * 1_000_000.0 / Stopwatch.Frequency;
            }

            Array.Sort(check);
            Array.Sort(own);
            Array.Sort(viaDirect);
            _output.WriteLine($"{depth - 1,5} {check[iterations / 2],16:F1} {viaDirect[iterations / 2],10:F1} {own[iterations / 2],12:F1}");
        }
    }

    /// <summary>
    /// A burst: many changes in a few directories of a tree <c>depth</c> levels deep (an <c>npm install</c>), checked without
    /// and with the cache, alternately, so that the machine's drift falls on both. Reports microseconds per change.
    /// </summary>
    [PerformanceTestFact]
    public async Task Cost_Of_A_Burst_With_And_Without_The_Cache()
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);
        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = false,
            IncludeSystem = false,
        };

        Func<string, CancellationToken, Task<FileAttributes?>> attributesOf = (d, ct) => provider.GetAttributesAsync(d, ct);
        const int changes = 5000;
        _output.WriteLine($"{"depth",5} {"changes",8} {"no cache µs/change",20} {"cache µs/change",17} {"own file-info µs",18}");

        foreach (var depth in new[] { 3, 9, 20 })
        {
            var directory = _root;
            for (var level = 0; level < depth; level++)
                directory = Path.Combine(directory, "n" + level);
            var leaves = Enumerable.Range(0, 20).Select(i => Path.Combine(directory, "pkg" + i)).ToArray();
            foreach (var leaf in leaves)
                Directory.CreateDirectory(leaf);
            var paths = Enumerable.Range(0, changes).Select(i => Path.Combine(leaves[i % leaves.Length], "f" + i + ".js")).ToArray();
            foreach (var path in paths.Take(1000))
                File.WriteAllText(path, "x"); // so that the change's own file-info call has a file to read

            var withoutCache = new List<double>();
            var withCache = new List<double>();
            var own = new List<double>();
            for (var round = 0; round < 3; round++)
            {
                var t0 = Stopwatch.GetTimestamp();
                foreach (var path in paths)
                    await IndexingAdmission.IsBeneathSkippedDirectoryAsync(path, [_root], options, attributesOf);
                var t1 = Stopwatch.GetTimestamp();

                var cache = new DirectoryAttributeCache();
                foreach (var path in paths)
                    await IndexingAdmission.IsBeneathSkippedDirectoryAsync(path, [_root], options, attributesOf, cache: cache);
                var t2 = Stopwatch.GetTimestamp();

                foreach (var path in paths.Take(1000))
                    await provider.GetFileInfoAsync(path);
                var t3 = Stopwatch.GetTimestamp();

                withoutCache.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency / changes);
                withCache.Add((t2 - t1) * 1_000_000.0 / Stopwatch.Frequency / changes);
                own.Add((t3 - t2) * 1_000_000.0 / Stopwatch.Frequency / 1000);
            }

            withoutCache.Sort();
            withCache.Sort();
            own.Sort();
            _output.WriteLine($"{depth,5} {changes,8} {withoutCache[1],20:F1} {withCache[1],17:F1} {own[1],18:F1}");
        }
    }
}
