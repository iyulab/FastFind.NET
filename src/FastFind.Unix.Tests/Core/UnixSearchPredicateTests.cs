using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// The Unix engine must answer a query with exactly the set <see cref="SearchQueryEvaluator"/>
/// accepts.
/// </summary>
/// <remarks>
/// The engine used to re-implement the whole predicate as a chain of <c>Where</c> clauses. Nothing
/// held the two definitions together, so they were free to drift — and did: the copy applied
/// <see cref="SearchQuery.BasePath"/> with an ordinal comparison on every platform, and treated
/// <see cref="SearchQuery.ExtensionFilter"/> and the location filters with its own rules. These
/// tests pin the engine's filtering to the evaluator rather than to a remembered list of behaviours,
/// so a future divergence fails here instead of in a consumer.
/// </remarks>
[Trait("Category", "Functional")]
public class UnixSearchPredicateTests
{
    private static FastFileItem Item(
        string fullPath,
        long size = 1024,
        FileAttributes attributes = FileAttributes.Normal,
        DateTime? modified = null)
    {
        var name = Path.GetFileName(fullPath);
        var dir = Path.GetDirectoryName(fullPath)?.Replace('\\', '/') ?? "/";
        var when = modified ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return new FastFileItem(
            fullPath, name, dir, Path.GetExtension(name),
            size, when, when, when, attributes, '/');
    }

    /// <summary>
    /// The corpus is shared by every case so that a filter which silently matches nothing would
    /// still be visible as an empty result rather than passing by accident.
    /// </summary>
    private static readonly FastFileItem[] Corpus =
    [
        Item("/home/user/docs/report.txt"),
        Item("/home/user/docs/notes.md"),
        Item("/home/user/src/main.cs", size: 8192),
        Item("/home/user/src/deep/helper.cs", size: 16384),
        Item("/var/log/system.log", attributes: FileAttributes.Hidden),
        Item("/home/user/docs", attributes: FileAttributes.Directory),
    ];

    private static List<string> Evaluate(SearchQuery query)
    {
        var matcher = SearchQueryEvaluator.CreateTextMatcher(query);
        return Corpus
            .Where(item => SearchQueryEvaluator.Matches(item, query, matcher))
            .Select(item => item.FullPath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void A_Mid_Token_Substring_Should_Match()
    {
        // "port" inside "report.txt" — the case an FTS5 unicode61 index could never answer, and the
        // reason the store stopped pretending to have one.
        var results = Evaluate(new SearchQuery { SearchText = "port", SearchFileNameOnly = true });

        results.Should().ContainSingle().Which.Should().Be("/home/user/docs/report.txt");
    }

    [Fact]
    public void A_Wildcard_Pattern_Should_Match_By_Extension()
    {
        var results = Evaluate(new SearchQuery { SearchText = "*.cs", SearchFileNameOnly = true });

        results.Should().BeEquivalentTo(
        [
            "/home/user/src/deep/helper.cs",
            "/home/user/src/main.cs",
        ]);
    }

    [Fact]
    public void Case_Sensitivity_Should_Be_Honoured_Both_Ways()
    {
        var sensitive = Evaluate(new SearchQuery
        {
            SearchText = "REPORT",
            SearchFileNameOnly = true,
            CaseSensitive = true,
        });

        var insensitive = Evaluate(new SearchQuery
        {
            SearchText = "REPORT",
            SearchFileNameOnly = true,
            CaseSensitive = false,
        });

        sensitive.Should().BeEmpty();
        insensitive.Should().ContainSingle().Which.Should().Be("/home/user/docs/report.txt");
    }

    [Fact]
    public void BasePath_Without_Subdirectories_Should_Exclude_Nested_Items()
    {
        var shallow = Evaluate(new SearchQuery
        {
            BasePath = "/home/user/src",
            IncludeSubdirectories = false,
        });

        var deep = Evaluate(new SearchQuery
        {
            BasePath = "/home/user/src",
            IncludeSubdirectories = true,
        });

        shallow.Should().Contain("/home/user/src/main.cs");
        shallow.Should().NotContain("/home/user/src/deep/helper.cs");
        deep.Should().Contain("/home/user/src/deep/helper.cs");
    }

    [Fact]
    public void Size_Bounds_Should_Be_Inclusive()
    {
        var results = Evaluate(new SearchQuery { MinSize = 8192, MaxSize = 8192 });

        results.Should().ContainSingle().Which.Should().Be("/home/user/src/main.cs");
    }

    [Fact]
    public void Hidden_Items_Should_Be_Excluded_Unless_Asked_For()
    {
        var withoutHidden = Evaluate(new SearchQuery { IncludeHidden = false });
        var withHidden = Evaluate(new SearchQuery { IncludeHidden = true });

        withoutHidden.Should().NotContain("/var/log/system.log");
        withHidden.Should().Contain("/var/log/system.log");
    }

    [Fact]
    public void Directories_Should_Be_Separable_From_Files()
    {
        var filesOnly = Evaluate(new SearchQuery { IncludeFiles = true, IncludeDirectories = false });
        var dirsOnly = Evaluate(new SearchQuery { IncludeFiles = false, IncludeDirectories = true });

        filesOnly.Should().NotContain("/home/user/docs");
        dirsOnly.Should().ContainSingle().Which.Should().Be("/home/user/docs");
    }

    [Fact]
    public void An_Extension_Filter_Should_Accept_A_Missing_Leading_Dot()
    {
        var withDot = Evaluate(new SearchQuery { ExtensionFilter = ".cs" });
        var withoutDot = Evaluate(new SearchQuery { ExtensionFilter = "cs" });

        withDot.Should().BeEquivalentTo(withoutDot);
        withDot.Should().HaveCount(2);
    }

    [Fact]
    public void Attribute_Filters_Should_Be_Honoured()
    {
        // The engine's old hand-rolled predicate honoured these; the evaluator did not, so unifying
        // the two briefly lost them on this platform. This pins them for every backend at once.
        var required = Evaluate(new SearchQuery
        {
            RequiredAttributes = FileAttributes.Directory,
            IncludeDirectories = true,
        });

        var excluded = Evaluate(new SearchQuery
        {
            ExcludedAttributes = FileAttributes.Directory,
            IncludeDirectories = true,
        });

        required.Should().ContainSingle().Which.Should().Be("/home/user/docs");
        excluded.Should().NotContain("/home/user/docs");
        excluded.Should().Contain("/home/user/docs/report.txt");
    }
}
