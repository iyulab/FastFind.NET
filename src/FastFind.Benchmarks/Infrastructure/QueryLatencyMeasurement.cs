using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FastFind.Interfaces;
using FastFind.Models;

namespace FastFind.Benchmarks.Infrastructure;

/// <summary>
/// Measures how long a query takes against an in-memory index of a given size, and against an engine that
/// has indexed a real directory tree.
/// </summary>
/// <remarks>
/// <para>
/// The README leads with speed, and until this existed no figure for the operation a user actually
/// performs — typing a query — could be reproduced from the repository. Run it with:
/// </para>
/// <code>
/// dotnet run -c Release --project src/FastFind.Benchmarks -- --query-latency
/// dotnet run -c Release --project src/FastFind.Benchmarks -- --query-latency --tree C:\some\folder
/// </code>
/// <para>
/// <b>Why not BenchmarkDotNet.</b> A query's cost depends on the corpus size and on how many items match,
/// and BenchmarkDotNet's parameterisation does not fit "build a million-item index once, then time seven
/// queries against it, at three sizes, in a process of its own". The numbers it would add (confidence
/// intervals, outlier handling) are less to the point than the median and the 95th percentile of
/// a few hundred real calls, which is what this reports.
/// </para>
/// <para>
/// <b>The corpus is synthetic and deterministic</b> (a fixed seed), so a result can be repeated and two
/// machines compared. Names are drawn from a small vocabulary, extensions follow a skewed distribution,
/// and one word in twenty thousand names and one directory in two thousand carry a rare token, so a
/// "rare" query matches the same <i>fraction</i> of the corpus at every size. The paths do not exist on
/// disk — which matters: an index with fewer than five matches for a query may fall back to scanning the
/// disk, and for this corpus that costs only an existence check. The second mode, <c>--tree</c>, indexes
/// a real folder with the real engine, where that scan is paid for.
/// </para>
/// <para>
/// Each configuration runs in its own process: <c>StringPool</c> is static, and a million-item corpus
/// left in the heap by one run would make the next one's garbage collections slower.
/// </para>
/// </remarks>
public static class QueryLatencyMeasurement
{
    private const string ParentSwitch = "--query-latency";
    private const string ChildSwitch = "--query-latency-child";

    private static readonly int[] CorpusSizes = [100_000, 300_000, 1_000_000];

    private static readonly (string Variant, string Label)[] IndexVariants =
    [
        ("windows-index", "Windows in-memory index"),
        ("unix-index", "Unix engine's own index"),
    ];

    /// <summary>How long to keep timing one query, and the bounds on how many calls that is.</summary>
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(2.5);
    private const int MinIterations = 20;
    private const int MaxIterations = 500;
    private const int Warmup = 5;

    public static bool TryHandle(string[] args)
    {
        if (args.Length > 0 && args[0] == ChildSwitch)
        {
            RunChild(args[1], args[2]).GetAwaiter().GetResult();
            return true;
        }

        if (args.Length > 0 && args[0] == ParentSwitch)
        {
            string? tree = null;
            for (var i = 1; i < args.Length - 1; i++)
                if (args[i] == "--tree") tree = args[i + 1];

            RunParent(tree);
            return true;
        }

        return false;
    }

    // ---- queries ------------------------------------------------------------------------------------

    private sealed record Q(string Name, Func<SearchQuery> Make);

    /// <summary>The synthetic corpus's queries. Match fractions are properties of the generator below.</summary>
    private static Q[] SyntheticQueries() =>
    [
        new("name contains a rare word (~0.005% of files)",
            () => new SearchQuery { SearchText = "quarterly", SearchFileNameOnly = true, MaxResults = 100 }),
        new("name contains a common word (~3% of files)",
            () => new SearchQuery { SearchText = "report", SearchFileNameOnly = true, MaxResults = 100 }),
        new("name contains a word nothing has",
            () => new SearchQuery { SearchText = "zzzzzz", SearchFileNameOnly = true, MaxResults = 100 }),
        new("extension only (.json, ~10% of files)",
            () => new SearchQuery { SearchText = "", ExtensionFilter = ".json", MaxResults = 100 }),
        new("common word + extension (.pdf)",
            () => new SearchQuery { SearchText = "report", ExtensionFilter = ".pdf", SearchFileNameOnly = true, MaxResults = 100 }),
        new("full path contains a rare directory name (~0.05% of dirs)",
            () => new SearchQuery { SearchText = "projectx", SearchFileNameOnly = false, MaxResults = 100 }),
        new("common word, scoped to one directory",
            () => new SearchQuery { BasePath = DirectoryOf(0), SearchText = "report", SearchFileNameOnly = true, MaxResults = 100 }),
    ];

    /// <summary>Queries for a real tree, which cannot assume any particular word is in it.</summary>
    private static Q[] TreeQueries() =>
    [
        new("name contains a very common letter",
            () => new SearchQuery { SearchText = "e", SearchFileNameOnly = true, MaxResults = 100 }),
        new("name contains an uncommon word",
            () => new SearchQuery { SearchText = "kernel32", SearchFileNameOnly = true, MaxResults = 100 }),
        new("name contains a word nothing has",
            () => new SearchQuery { SearchText = "zzzzzznomatch", SearchFileNameOnly = true, MaxResults = 100 }),
        new("extension only (.dll)",
            () => new SearchQuery { SearchText = "", ExtensionFilter = ".dll", MaxResults = 100 }),
    ];

    // ---- parent -------------------------------------------------------------------------------------

    private static void RunParent(string? tree)
    {
        Console.WriteLine("Query latency");
        Console.WriteLine("=============");
        Console.WriteLine();
        Console.WriteLine($"Taken {DateTime.UtcNow:yyyy-MM-dd} on {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription},");
        Console.WriteLine($"{Environment.ProcessorCount} logical processors, elevated: {Environment.IsPrivilegedProcess}.");
        Console.WriteLine($"Each cell is median / 95th percentile in milliseconds over at least {(tree is null ? MinIterations : 5)} and at most {MaxIterations} calls ({TimeBudget.TotalSeconds:0.#} s budget), after {Warmup} warm-up calls.");
        Console.WriteLine("Every query asks for at most 100 results.");
        Console.WriteLine();

        if (tree is null)
        {
            foreach (var (variant, label) in IndexVariants)
            {
                var runs = CorpusSizes.Select(size => (size, rows: Spawn(variant, size.ToString(CultureInfo.InvariantCulture)))).ToList();
                PrintTable($"{label} — synthetic corpus", runs.Select(r => (r.size.ToString("N0", CultureInfo.InvariantCulture), r.rows)).ToList());
            }

            Console.WriteLine("The synthetic paths do not exist on disk. A query with fewer than five matches may make the");
            Console.WriteLine("Windows index scan the disk instead (up to five indexed directories); here that costs an");
            Console.WriteLine("existence check. For what it costs on a real tree, run with --tree <folder>.");
        }
        else
        {
            var rows = Spawn("engine-tree", tree);
            PrintTable($"Engine over a real tree: {tree}", [("one tree", rows)]);
            foreach (var note in Notes) Console.WriteLine(note + ".");
            Console.WriteLine();
            Console.WriteLine("Indexed with the engine as this host configures it (the change-journal provider when elevated,");
            Console.WriteLine("the directory walk otherwise), then queried through the engine's SearchAsync.");
        }
    }

    /// <summary>What a child reported besides its rows, such as how big the tree it indexed was.</summary>
    private static readonly List<string> Notes = new();

    private sealed record Row(string Query, double MedianMs, double P95Ms, double MinMs, int Matches, int Iterations);

    private static void PrintTable(string title, List<(string Column, List<Row>? Rows)> columns)
    {
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
        Console.WriteLine();

        Console.WriteLine("| query | " + string.Join(" | ", columns.Select(c => c.Column)) + " |");
        Console.WriteLine("|---|" + string.Join("|", columns.Select(_ => "---:")) + "|");

        var queryNames = columns.Select(c => c.Rows).FirstOrDefault(r => r is not null)?.Select(r => r.Query).ToList() ?? [];
        foreach (var name in queryNames)
        {
            var cells = columns.Select(c =>
            {
                var row = c.Rows?.FirstOrDefault(r => r.Query == name);
                return row is null
                    ? "failed"
                    : $"{row.MedianMs:F2} / {row.P95Ms:F2}";
            });
            Console.WriteLine($"| {name} | {string.Join(" | ", cells)} |");
        }

        Console.WriteLine();
        Console.WriteLine("Matches returned (capped at 100 by the query): " + string.Join("; ", columns.Select(c =>
            c.Column + ": " + (c.Rows is null ? "failed" : string.Join(", ", c.Rows.Select(r => r.Matches))))));
        Console.WriteLine();
    }

    private static List<Row>? Spawn(string variant, string argument)
    {
        var host = Environment.ProcessPath;
        if (host is null) return null;

        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(ChildSwitch);
        startInfo.ArgumentList.Add(variant);
        startInfo.ArgumentList.Add(argument);

        using var child = Process.Start(startInfo);
        if (child is null) return null;

        var errorTask = child.StandardError.ReadToEndAsync();
        var output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();

        var rows = new List<Row>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length == 2 && parts[0] == "INFO")
                Notes.Add(parts[1]);

            if (parts.Length == 7 && parts[0] == "ROW")
            {
                rows.Add(new Row(
                    parts[1],
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture),
                    double.Parse(parts[4], CultureInfo.InvariantCulture),
                    int.Parse(parts[5], CultureInfo.InvariantCulture),
                    int.Parse(parts[6], CultureInfo.InvariantCulture)));
            }
        }

        if (rows.Count == 0)
            Console.Error.WriteLine($"Child ({variant}, {argument}) produced no rows. stderr: {errorTask.Result}");

        return rows.Count == 0 ? null : rows;
    }

    // ---- child --------------------------------------------------------------------------------------

    private static async Task RunChild(string variant, string argument)
    {
        Func<SearchQuery, Task<int>> run;
        Q[] queries;
        IDisposable? owner = null;

        if (variant == "engine-tree")
        {
            queries = TreeQueries();
            var engine = FastFinder.CreateSearchEngine();
            owner = engine;
            var sw = Stopwatch.StartNew();
            await engine.StartIndexingAsync(new IndexingOptions
            {
                SpecificDirectories = { argument },
                EnableMonitoring = false,
            });
            while (engine.IsIndexing) await Task.Delay(200);
            Console.WriteLine($"INFO\tindexed {engine.TotalIndexedFiles:N0} entries in {sw.Elapsed.TotalSeconds:F1} s");

            run = async query =>
            {
                var result = await engine.SearchAsync(query);
                var n = 0;
                await foreach (var _ in result.Files) n++;
                return n;
            };
        }
        else
        {
            queries = SyntheticQueries();
            var count = int.Parse(argument, CultureInfo.InvariantCulture);
            ISearchIndex index = variant switch
            {
                "windows-index" when OperatingSystem.IsWindows() => new FastFind.Windows.Implementation.WindowsSearchIndex(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<FastFind.Windows.Implementation.WindowsSearchIndex>.Instance),
                "unix-index" => new FastFind.Unix.MemorySearchIndex(),
                _ => throw new ArgumentException($"Unknown variant '{variant}'.", nameof(variant)),
            };
            owner = index as IDisposable;

            await Fill(index, count);

            run = async query =>
            {
                // What both engines do for a capped query (ResultCap): ask the index for one match past the cap, and stop
                // consuming there. Draining the index instead measured the cost of collecting every match — tens of
                // thousands for a query that asked for 100 — which no engine does any longer.
                var cap = query.MaxResults ?? int.MaxValue;
                var probe = query.Clone();
                if (cap != int.MaxValue) probe.MaxResults = cap + 1;

                var n = 0;
                await foreach (var _ in index.SearchAsync(probe))
                {
                    if (++n > cap) break;
                }

                return n;
            };
        }

        // Settle the heap so the first query is not charged for the fill's garbage.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        // A scan of a real folder can take seconds a call, so a tree is timed with fewer repeats.
        var minIterations = variant == "engine-tree" ? 5 : MinIterations;

        foreach (var q in queries)
        {
            var query = q.Make();
            var matches = 0;
            for (var i = 0; i < Warmup; i++) matches = await run(query);

            var samples = new List<double>();
            var clock = Stopwatch.StartNew();
            while (samples.Count < minIterations || (clock.Elapsed < TimeBudget && samples.Count < MaxIterations))
            {
                var t0 = Stopwatch.GetTimestamp();
                matches = await run(query);
                samples.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }

            samples.Sort();
            var median = samples[samples.Count / 2];
            var p95 = samples[(int)Math.Min(samples.Count - 1, Math.Ceiling(samples.Count * 0.95) - 1)];

            Console.WriteLine(string.Join('\t', "ROW", q.Name,
                median.ToString("F4", CultureInfo.InvariantCulture),
                p95.ToString("F4", CultureInfo.InvariantCulture),
                samples[0].ToString("F4", CultureInfo.InvariantCulture),
                matches.ToString(CultureInfo.InvariantCulture),
                samples.Count.ToString(CultureInfo.InvariantCulture)));
        }

        owner?.Dispose();
    }

    // ---- the synthetic corpus ----------------------------------------------------------------------

    private static readonly string[] Words =
    [
        "report", "invoice", "budget", "summary", "draft", "final", "notes", "meeting", "schedule", "design",
        "spec", "review", "backup", "archive", "image", "photo", "video", "music", "config", "settings",
        "readme", "license", "index", "main", "util", "helper", "service", "controller", "model", "view",
        "test", "data", "sample", "export", "import", "contract", "proposal", "plan", "roadmap", "status",
        "log", "trace", "dump", "cache", "temp", "build", "release", "debug", "release", "script",
        "module", "package", "driver", "plugin", "theme", "icon", "font", "layout", "template", "form",
        "table", "chart", "graph", "slide",
    ];

    /// <summary>Extensions with the weights a mixed working folder tends to have.</summary>
    private static readonly (string Ext, int Weight)[] Extensions =
    [
        (".txt", 20), (".cs", 15), (".json", 10), (".dll", 10), (".md", 8), (".png", 8),
        (".log", 7), (".xml", 6), (".pdf", 5), (".zip", 4), (".cfg", 4), (".exe", 3),
    ];

    private static readonly string[] DirNames =
    [
        "src", "docs", "assets", "build", "lib", "tools", "tests", "config", "data", "images", "scripts", "samples",
        "common", "core", "web", "api", "ui", "shared", "legacy", "vendor", "output", "temp", "notes", "archive",
        "reports", "finance", "design", "ops", "infra", "ml", "mobile", "desktop", "server", "client", "admin", "public",
        "private", "staging", "prod", "dev",
    ];

    private const int FilesPerDirectory = 100;

    private static string DirectoryOf(int bucket)
    {
        var a = DirNames[bucket % DirNames.Length];
        var b = DirNames[bucket / DirNames.Length % DirNames.Length];
        var c = DirNames[bucket / (DirNames.Length * DirNames.Length) % DirNames.Length];
        // One directory in two thousand carries the rare path token.
        var leaf = bucket % 2000 == 1 ? "projectx" : $"d{bucket}";
        return $"C:\\corpus\\{c}\\{b}\\{a}\\{leaf}";
    }

    private static readonly DateTime When = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task Fill(ISearchIndex index, int count)
    {
        var random = new Random(20260101);
        var totalWeight = Extensions.Sum(e => e.Weight);

        const int batchSize = 10_000;
        var batch = new FastFileItem[batchSize];

        for (var start = 0; start < count; start += batchSize)
        {
            var n = Math.Min(batchSize, count - start);
            for (var k = 0; k < n; k++)
            {
                var i = start + k;
                var directory = DirectoryOf(i / FilesPerDirectory);

                var w1 = Words[random.Next(Words.Length)];
                var w2 = Words[random.Next(Words.Length)];
                // One name in twenty thousand carries the rare word.
                var stem = i % 20_000 == 7 ? $"quarterly_{w2}" : $"{w1}_{w2}";

                var pick = random.Next(totalWeight);
                var ext = Extensions[0].Ext;
                foreach (var (candidate, weight) in Extensions)
                {
                    if (pick < weight) { ext = candidate; break; }
                    pick -= weight;
                }

                var name = $"{stem}_{i:D7}{ext}";
                batch[k] = new FastFileItem(
                    $"{directory}\\{name}", name, directory, ext,
                    size: 4096, created: When, modified: When, accessed: When,
                    attributes: FileAttributes.Archive, driveLetter: 'C');
            }

            await index.AddBatchAsync(batch.Take(n));
        }
    }
}
