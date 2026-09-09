using FastFind.Models;
using FastFind.SQLite;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.SQLite;

/// <summary>
/// Holds <see cref="SqlitePersistence.SearchAsync"/> to the query semantics defined by
/// <see cref="SearchQueryEvaluator"/>.
/// </summary>
/// <remarks>
/// The provider narrows candidates in SQL before evaluating them. That narrowing is an
/// optimisation, so for every query the provider's result set must equal the corpus filtered by the
/// evaluator — a narrowing clause that drops a matching row would make a disk-backed search return
/// different results than an in-memory one for the same query, which is the failure mode this file
/// exists to catch.
/// </remarks>
[Trait("Category", "SQLite")]
public class SqliteSearchParityTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _databasePath;
    private SqlitePersistence? _persistence;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly DateTime Created = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public SqliteSearchParityTests(ITestOutputHelper output)
    {
        _output = output;
        _databasePath = Path.Combine(Path.GetTempPath(), $"fastfind-parity-{Guid.NewGuid():N}.db");
    }

    /// <summary>
    /// A corpus chosen so that each query below has both matches and non-matches: mid-token
    /// substrings, case variants, several extensions, two directory subtrees, a hidden and a system
    /// entry, a directory, and a non-ASCII name.
    /// </summary>
    private static readonly (string Path, long Size, FileAttributes Attributes)[] Corpus =
    [
        (@"C:\docs\report.txt", 1_000, FileAttributes.Normal),
        (@"C:\docs\AnnualReport.pdf", 50_000, FileAttributes.Normal),
        (@"C:\docs\my-report-2024.docx", 12_000, FileAttributes.Normal),
        (@"C:\docs\readme.md", 400, FileAttributes.Normal),
        (@"C:\docs\sub\Program.cs", 8_000, FileAttributes.Normal),
        (@"C:\docs\sub\program.tests.cs", 3_000, FileAttributes.Normal),
        (@"C:\docs\sub\nested\deep.txt", 100, FileAttributes.Normal),
        (@"C:\other\report.txt", 2_000, FileAttributes.Normal),
        (@"C:\other\.hidden.txt", 10, FileAttributes.Hidden),
        (@"C:\other\pagefile.sys", 999_999, FileAttributes.System),
        (@"C:\other\folder", 0, FileAttributes.Directory),
        (@"C:\docs\보고서.txt", 700, FileAttributes.Normal),
    ];

    public async Task InitializeAsync()
    {
        _persistence = SqlitePersistence.Create(_databasePath);
        await _persistence.InitializeAsync();
        await _persistence.AddBatchAsync(BuildCorpus());
    }

    public async Task DisposeAsync()
    {
        if (_persistence is not null) await _persistence.DisposeAsync();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); } catch (IOException) { }
        }
    }

    private static List<FastFileItem> BuildCorpus()
    {
        var items = new List<FastFileItem>(Corpus.Length);

        foreach (var (path, size, attributes) in Corpus)
        {
            var name = Path.GetFileName(path);
            var directory = Path.GetDirectoryName(path)!;

            items.Add(new FastFileItem(
                path, name, directory, Path.GetExtension(name), size,
                Created, Modified, Modified, attributes, path[0]));
        }

        return items;
    }

    public static TheoryData<string, SearchQuery> Queries()
    {
        var data = new TheoryData<string, SearchQuery>
        {
            { "no constraints", new SearchQuery() },
            { "substring whole token", new SearchQuery { SearchText = "report" } },
            { "substring inside token", new SearchQuery { SearchText = "port" } },
            { "substring case variant", new SearchQuery { SearchText = "REPORT" } },
            { "substring matching a directory", new SearchQuery { SearchText = "sub" } },
            { "substring with no match", new SearchQuery { SearchText = "zzz" } },
            { "non-ascii substring", new SearchQuery { SearchText = "보고서" } },
            { "like metacharacters in the pattern", new SearchQuery { SearchText = "100%_of" } },
            { "name only", new SearchQuery { SearchText = "docs", SearchFileNameOnly = true } },
            { "full path", new SearchQuery { SearchText = "docs", SearchFileNameOnly = false } },
            { "case sensitive", new SearchQuery { SearchText = "Report", CaseSensitive = true } },
            { "case sensitive lower", new SearchQuery { SearchText = "report", CaseSensitive = true } },
            { "wildcard extension", new SearchQuery { SearchText = "*.cs" } },
            { "wildcard on name only", new SearchQuery { SearchText = "prog*", SearchFileNameOnly = true } },
            { "single-char wildcard", new SearchQuery { SearchText = "*repor?.txt" } },
            { "regex", new SearchQuery { SearchText = @"\\sub\\.*\.cs$", UseRegex = true } },
            { "regex case sensitive", new SearchQuery { SearchText = "Program", UseRegex = true, CaseSensitive = true } },
            { "extension filter with dot", new SearchQuery { ExtensionFilter = ".txt" } },
            { "extension filter without dot", new SearchQuery { ExtensionFilter = "cs" } },
            { "min size", new SearchQuery { MinSize = 10_000 } },
            { "max size", new SearchQuery { MaxSize = 1_000 } },
            { "size range", new SearchQuery { MinSize = 500, MaxSize = 10_000 } },
            { "directories only", new SearchQuery { IncludeFiles = false } },
            { "files only", new SearchQuery { IncludeDirectories = false } },
            { "include hidden", new SearchQuery { IncludeHidden = true } },
            { "include system", new SearchQuery { IncludeSystem = true } },
            { "include everything", new SearchQuery { IncludeHidden = true, IncludeSystem = true } },
            { "created lower bound excludes all", new SearchQuery { MinCreatedDate = Created.AddDays(1) } },
            { "created lower bound includes all", new SearchQuery { MinCreatedDate = Created.AddDays(-1) } },
            { "modified upper bound", new SearchQuery { MaxModifiedDate = Modified.AddDays(1) } },
            { "base path", new SearchQuery { BasePath = @"C:\docs" } },
            { "base path non-recursive", new SearchQuery { BasePath = @"C:\docs", IncludeSubdirectories = false } },
            { "base path shared prefix", new SearchQuery { BasePath = @"C:\doc" } },
            { "search locations", new SearchQuery { SearchLocations = { @"C:\other", @"C:\docs\sub" } } },
            { "excluded path", new SearchQuery { ExcludedPaths = { @"C:\docs\sub" } } },
            { "scope and text", new SearchQuery { BasePath = @"C:\docs", SearchText = "report" } },
            { "scope, text and extension", new SearchQuery { BasePath = @"C:\docs", SearchText = "e", ExtensionFilter = "txt" } },
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Provider_Should_Return_Exactly_What_The_Evaluator_Accepts(string label, SearchQuery query)
    {
        var matcher = SearchQueryEvaluator.CreateTextMatcher(query);

        var expected = BuildCorpus()
            .Where(item => SearchQueryEvaluator.Matches(item, query, matcher))
            .Select(item => item.FullPath)
            .OrderBy(path => path, PathComparer)
            .ToList();

        var actual = new List<string>();
        await foreach (var item in _persistence!.SearchAsync(query))
        {
            actual.Add(item.FullPath);
        }

        actual.Sort(PathComparer);

        _output.WriteLine($"{label}: expected {expected.Count}, got {actual.Count}");

        actual.Should().Equal(expected, (a, b) => PathComparer.Equals(a, b),
            $"the SQL narrowing for '{label}' must not change the result set");
    }

    [Fact]
    public async Task Stored_Paths_Should_Keep_Their_Original_Casing()
    {
        // The store used to lower-case every path on write, which lost the real path on a
        // case-sensitive file system and made CaseSensitive queries unsatisfiable everywhere.
        var mixedCase = Corpus.Select(entry => entry.Path).Where(path => path.Any(char.IsUpper)).ToList();
        mixedCase.Should().NotBeEmpty("the corpus must contain a mixed-case path for this to test anything");

        var returned = new List<string>();
        await foreach (var item in _persistence!.SearchAsync(new SearchQuery { IncludeHidden = true, IncludeSystem = true }))
        {
            returned.Add(item.FullPath);
        }

        returned.Should().Contain(mixedCase, "a stored path must come back exactly as it was written");
    }

    [Fact]
    public async Task Path_Lookup_Should_Ignore_Case_On_Windows_And_Honour_It_Elsewhere()
    {
        var stored = @"C:\docs\AnnualReport.pdf";
        var differentCase = @"c:\docs\annualreport.pdf";

        (await _persistence!.ExistsAsync(stored)).Should().BeTrue("the path was stored verbatim");

        (await _persistence.ExistsAsync(differentCase))
            .Should().Be(OperatingSystem.IsWindows(),
                "path identity follows the host file system, not the storage encoding");
    }

    [Fact]
    public async Task MaxResults_Should_Limit_Matches_Rather_Than_Candidates()
    {
        // A LIMIT applied to candidates instead of matches returns fewer than MaxResults whenever a
        // filter rejects a candidate, even though more matches remain in the store.
        var query = new SearchQuery { ExtensionFilter = "txt", MaxResults = 3 };

        var matching = BuildCorpus().Count(item =>
            SearchQueryEvaluator.Matches(item, query, SearchQueryEvaluator.CreateTextMatcher(query)));
        matching.Should().BeGreaterThan(3, "the corpus must have more matches than the cap for this to test anything");

        var returned = 0;
        await foreach (var _ in _persistence!.SearchAsync(query)) returned++;

        returned.Should().Be(3);
    }
}
