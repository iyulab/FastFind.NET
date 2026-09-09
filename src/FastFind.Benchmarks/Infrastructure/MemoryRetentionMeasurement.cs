using System.Diagnostics;
using FastFind.Models;

namespace FastFind.Benchmarks.Infrastructure;

/// <summary>
/// Measures how much managed heap a corpus of indexed items retains, with and without the string
/// interning <see cref="FastFileItem"/> performs.
/// </summary>
/// <remarks>
/// <para>
/// This is not a BenchmarkDotNet benchmark, and deliberately so. BenchmarkDotNet measures elapsed
/// time and allocation <i>rate</i>; the question here is <b>retention</b> — how much heap is still
/// held once the corpus exists — which is read with <see cref="GC.GetTotalMemory(bool)"/> after
/// forced collections, not sampled while running.
/// </para>
/// <para>
/// Each configuration runs in its own process because <c>StringPool</c> is <c>static</c>: a second
/// measurement in the same process would see the first one's pool and report a saving that is really
/// just reuse. The parent process spawns itself once per configuration and collects the results.
/// </para>
/// <para>
/// Why this exists: this project advertised a 60–80% memory reduction from interning for years, and
/// retracted it once someone measured. The measurement that retracted it lived only in prose, so it
/// could not be re-run — only re-derived. This can be run:
/// </para>
/// <code>
/// dotnet run -c Release --project src/FastFind.Benchmarks -- --memory-retention
/// </code>
/// <para>
/// <b>What it compares, and what that leaves out.</b> One side holds four strings per item directly;
/// the other holds a <see cref="FastFileItem"/> per item, which stores interning ids and puts the
/// strings in the pool. That is the choice a consumer actually faces, but the two sides do not hold
/// the same struct: <see cref="FastFileItem"/> is roughly twice the width of four bare references, so
/// some of the difference is the item, not the interning. Read the result as "what does this
/// representation cost", not as "what does interning alone cost".
/// </para>
/// <para>
/// <b>It does not reproduce the earlier figures</b>, which recorded interning as a small saving
/// (480.0 → 454.3 B/item at 100,000). Here it is a cost at both corpus sizes and both corpus shapes.
/// The earlier harness was never committed — that is precisely why this one exists — so the two
/// cannot be diffed, and neither number should be published until they are reconciled.
/// </para>
/// </remarks>
public static class MemoryRetentionMeasurement
{
    private const string ParentSwitch = "--memory-retention";
    private const string ChildSwitch = "--memory-retention-child";

    /// <summary>Corpus sizes, matching the two the recorded figures were taken at.</summary>
    private static readonly int[] CorpusSizes = [100_000, 300_000];

    /// <summary>
    /// How many files share a directory. One means every file has its own — the worst case for
    /// interning, and the shape that shows what its bookkeeping costs when there is nothing to
    /// share. A hundred is what an ordinary tree looks like.
    /// </summary>
    private static readonly int[] FilesPerDirectory = [1, 100];

    /// <summary>
    /// Handles the memory-retention switches if present.
    /// </summary>
    /// <returns><see langword="true"/> if this run was a measurement and nothing else should run.</returns>
    public static bool TryHandle(string[] args)
    {
        if (args.Length > 0 && args[0] == ChildSwitch)
        {
            RunChild(int.Parse(args[1]), args[2], int.Parse(args[3]));
            return true;
        }

        if (args.Length > 0 && args[0] == ParentSwitch)
        {
            RunParent();
            return true;
        }

        return false;
    }

    private static void RunParent()
    {
        Console.WriteLine("Managed heap retained per indexed item");
        Console.WriteLine("=====================================");
        Console.WriteLine();
        Console.WriteLine("Each row is a separate process: StringPool is static, so a second");
        Console.WriteLine("measurement in the same process would measure the first one's pool.");
        Console.WriteLine();
        Console.WriteLine("Two corpus shapes, because the answer depends on them far more than on size.");
        Console.WriteLine("The path and the file name are unique per file either way; what varies is how");
        Console.WriteLine("many files share a directory, which is the only component interning can");
        Console.WriteLine("meaningfully deduplicate.");
        Console.WriteLine();
        Console.WriteLine("| files/dir | corpus  |  no interning | interned | saving |");
        Console.WriteLine("|-----------|---------|---------------|----------|--------|");

        foreach (var filesPerDirectory in FilesPerDirectory)
        {
            foreach (var size in CorpusSizes)
            {
                var raw = Measure(size, "raw", filesPerDirectory);
                var interned = Measure(size, "interned", filesPerDirectory);

                if (raw is null || interned is null)
                {
                    Console.WriteLine($"| {filesPerDirectory,9:N0} | {size,7:N0} | measurement failed |");
                    continue;
                }

                var rawPerItem = raw.Value / (double)size;
                var internedPerItem = interned.Value / (double)size;
                var saving = (rawPerItem - internedPerItem) / rawPerItem * 100;

                Console.WriteLine(
                    $"| {filesPerDirectory,9:N0} | {size,7:N0} | {rawPerItem,11:F1} B | {internedPerItem,6:F1} B | {saving,5:F1}% |");
            }
        }

        Console.WriteLine();
        Console.WriteLine("A negative saving is not a bug in the measurement. Interning stores each");
        Console.WriteLine("distinct string once plus a dictionary entry and an array slot; when the");
        Console.WriteLine("strings are mostly distinct, that bookkeeping costs more than the sharing");
        Console.WriteLine("saves. The full path is unique per file and is the largest of the four");
        Console.WriteLine("strings an item holds, so it can never be deduplicated at all.");
        Console.WriteLine();
        Console.WriteLine("Two caveats before quoting any of this. The two sides do not hold the same");
        Console.WriteLine("struct, so part of the difference is FastFileItem rather than interning.");
        Console.WriteLine("And these numbers do not reproduce the earlier measurement that recorded");
        Console.WriteLine("interning as a small saving; that harness was never committed, so the two");
        Console.WriteLine("cannot be compared. Reconcile them before publishing either.");
    }

    private static long? Measure(int count, string variant, int filesPerDirectory)
    {
        var host = Environment.ProcessPath;
        if (host is null)
        {
            Console.Error.WriteLine("Cannot locate the current executable to spawn a child process.");
            return null;
        }

        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // A framework-dependent apphost takes the arguments directly; `dotnet run` does not reach
        // here, because Environment.ProcessPath is the apphost in both cases.
        startInfo.ArgumentList.Add(ChildSwitch);
        startInfo.ArgumentList.Add(count.ToString());
        startInfo.ArgumentList.Add(variant);
        startInfo.ArgumentList.Add(filesPerDirectory.ToString());

        using var child = Process.Start(startInfo);
        if (child is null) return null;

        var output = child.StandardOutput.ReadToEnd();
        var error = child.StandardError.ReadToEnd();
        child.WaitForExit();

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("BYTES ", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(6).Trim(), out var bytes))
            {
                return bytes;
            }
        }

        Console.Error.WriteLine($"Child ({variant}, {count:N0}) produced no result. stderr: {error}");
        return null;
    }

    private static void RunChild(int count, string variant, int filesPerDirectory)
    {
        var before = Settle();

        object corpus = variant switch
        {
            "raw" => BuildRaw(count, filesPerDirectory),
            "interned" => BuildInterned(count, filesPerDirectory),
            _ => throw new ArgumentException($"Unknown variant '{variant}'.", nameof(variant)),
        };

        var after = Settle();

        // Touch the corpus after the reading so it cannot be collected before it is measured.
        GC.KeepAlive(corpus);

        Console.WriteLine($"BYTES {after - before}");
    }

    private static long Settle()
    {
        // Two forced gen-2 collections: the first can resurrect finalisable objects onto the
        // finalisation queue, so a single pass reads high.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>One item's four strings, held without interning.</summary>
    private readonly record struct RawItem(string FullPath, string Name, string DirectoryPath, string Extension);

    private static RawItem[] BuildRaw(int count, int filesPerDirectory)
    {
        var items = new RawItem[count];
        for (var i = 0; i < count; i++)
        {
            var path = PathFor(i, filesPerDirectory);
            // Fresh instances: this is the no-interning baseline, so nothing may be shared.
            items[i] = new RawItem(
                path,
                new string(Path.GetFileName(path.AsSpan())),
                new string(Path.GetDirectoryName(path.AsSpan())),
                new string(Path.GetExtension(path.AsSpan())));
        }

        return items;
    }

    private static FastFileItem[] BuildInterned(int count, int filesPerDirectory)
    {
        var items = new FastFileItem[count];
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < count; i++)
        {
            var path = PathFor(i, filesPerDirectory);
            items[i] = new FastFileItem(
                path,
                new string(Path.GetFileName(path.AsSpan())),
                new string(Path.GetDirectoryName(path.AsSpan())),
                new string(Path.GetExtension(path.AsSpan())),
                size: 4096,
                created: when,
                modified: when,
                accessed: when,
                attributes: FileAttributes.Normal,
                driveLetter: 'C');
        }

        return items;
    }

    /// <summary>
    /// A 99-character path, unique per index — the shape the recorded figures were taken with.
    /// </summary>
    private static string PathFor(int index, int filesPerDirectory)
    {
        // How many files share a directory is the whole experiment: a directory unique per file
        // leaves interning nothing to deduplicate, while a hundred files to a directory is what a
        // real tree looks like. Both are measured rather than one being picked.
        var bucket = index / filesPerDirectory;
        var directory = $"C:\\corpus\\{bucket / 100:D4}\\{bucket % 100:D2}";
        var name = $"file_{index:D9}";
        var path = $"{directory}\\{name}.txt";

        // Pad the name, not the directory: padding the directory would make it unique per file.
        var shortfall = 99 - path.Length;
        return shortfall > 0
            ? $"{directory}\\{name}{new string('x', shortfall)}.txt"
            : path;
    }
}
