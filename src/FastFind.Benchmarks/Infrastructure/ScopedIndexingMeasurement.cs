using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using FastFind.Models;
using FastFind.Windows.Mft;

namespace FastFind.Benchmarks.Infrastructure;

/// <summary>
/// Times how long it takes to enumerate one folder with the directory walk and with the change-journal
/// pass, so a choice between them can rest on a measurement.
/// </summary>
/// <remarks>
/// <para>
/// When the process is elevated the engine always uses the change-journal pass, which reads the whole volume
/// and keeps what lies under the requested location; a walk reads only the folder. Which is faster depends on
/// how much of the volume the folder is, and nothing in the repository measured it. Run with:
/// </para>
/// <code>
/// dotnet run -c Release --project src/FastFind.Benchmarks -- --scoped-indexing D:\small D:\medium D:\large
/// </code>
/// <para>
/// Needs an elevated process (the journal pass reads the volume). Each folder is enumerated
/// <see cref="Repeats"/> times with each provider, the two alternating so neither always runs on a colder
/// cache, and the report gives every run, not an average: on a shared machine the same pass varies, and a
/// crossover that exists only in the mean is not one.
/// </para>
/// <para>
/// What is timed is the provider's enumeration alone (items yielded, none stored), which is the part the
/// choice changes. The folder's entry count is what the walk yielded; the journal pass is checked against it
/// and a difference is printed rather than hidden.
/// </para>
/// </remarks>
public static class ScopedIndexingMeasurement
{
    private const string Switch = "--scoped-indexing";
    private const int Repeats = 3;

    public static bool TryHandle(string[] args)
    {
        if (args.Length == 0 || args[0] != Switch) return false;

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The change-journal pass exists on Windows only.");
            return true;
        }

        var folders = args.Skip(1).ToArray();
        if (folders.Length == 0)
        {
            Console.Error.WriteLine($"Usage: {Switch} <folder> [<folder> ...]");
            return true;
        }

        RunAsync(folders).GetAwaiter().GetResult();
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunAsync(string[] folders)
    {
        if (!MftFileSystemProvider.IsMftAccessAvailable)
        {
            Console.Error.WriteLine("The change-journal pass is not available in this process (elevation and an NTFS volume are needed).");
            return;
        }

        Console.WriteLine($"Scoped enumeration, {Repeats} runs per provider, seconds per run, alternating. {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine("| folder | entries (walk / journal) | walk, runs | journal pass, runs |");
        Console.WriteLine("|---|---|---|---|");

        foreach (var folder in folders)
        {
            var walk = new List<double>();
            var journal = new List<double>();
            long walkCount = 0, journalCount = 0;
            HashSet<string>? walkPaths = null, journalPaths = null;

            for (var i = 0; i < Repeats; i++)
            {
                var last = i == Repeats - 1;   // keep the paths of one run only, to say what differs
                (var t, walkCount, walkPaths) = await Time(ProviderMode.Standard, folder, last);
                walk.Add(t);
                (t, journalCount, journalPaths) = await Time(ProviderMode.Mft, folder, last);
                journal.Add(t);
            }

            var entries = walkCount == journalCount
                ? walkCount.ToString("N0", CultureInfo.InvariantCulture)
                : $"{walkCount:N0} / {journalCount:N0}";
            Console.WriteLine($"| {folder} | {entries} | {Runs(walk)} | {Runs(journal)} |");

            if (walkPaths is not null && journalPaths is not null && !walkPaths.SetEquals(journalPaths))
            {
                foreach (var p in journalPaths.Except(walkPaths, StringComparer.OrdinalIgnoreCase).Take(5))
                    Console.WriteLine($"  only the journal pass: {p}");
                foreach (var p in walkPaths.Except(journalPaths, StringComparer.OrdinalIgnoreCase).Take(5))
                    Console.WriteLine($"  only the walk: {p}");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(double Seconds, long Count, HashSet<string>? Paths)> Time(ProviderMode mode, string folder, bool keepPaths)
    {
        await using var provider = new HybridFileSystemProvider(mode);
        var options = new IndexingOptions { SpecificDirectories = { folder } };

        GC.Collect();
        var sw = Stopwatch.StartNew();
        long n = 0;
        var paths = keepPaths ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        await foreach (var item in provider.EnumerateFilesAsync([folder], options))
        {
            n++;
            paths?.Add(item.FullPath);
        }

        return (sw.Elapsed.TotalSeconds, n, paths);
    }

    private static string Runs(List<double> runs) =>
        string.Join(" · ", runs.Select(r => r.ToString("F2", CultureInfo.InvariantCulture)));
}
