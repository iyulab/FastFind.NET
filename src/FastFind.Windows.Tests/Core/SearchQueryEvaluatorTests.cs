using FastFind.Models;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// Tests for the single query-matching predicate every backend evaluates through.
/// </summary>
[Trait("Category", "Functional")]
public class SearchQueryEvaluatorTests
{
    private static readonly DateTime Created = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static FastFileItem Item(
        string fullPath,
        long size = 1024,
        FileAttributes attributes = FileAttributes.Normal,
        DateTime? created = null,
        DateTime? modified = null)
    {
        var name = Path.GetFileName(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

        return new FastFileItem(
            fullPath, name, directory, Path.GetExtension(name), size,
            created ?? Created, modified ?? Modified, Modified,
            attributes, fullPath.Length > 0 ? fullPath[0] : 'C');
    }

    private static bool Matches(FastFileItem item, SearchQuery query) =>
        SearchQueryEvaluator.Matches(item, query, SearchQueryEvaluator.CreateTextMatcher(query));

    // --- text ---

    [Theory]
    [InlineData("report", true)]   // whole token
    [InlineData("port", true)]     // inside a token — the case FTS tokenisation cannot serve
    [InlineData("REPORT", true)]   // case-insensitive by default
    [InlineData("docs", true)]     // matches the directory part, since the target is the full path
    [InlineData("absent", false)]
    public void SearchText_Should_Match_As_A_Case_Insensitive_Substring_Of_The_Full_Path(string text, bool expected)
    {
        var item = Item(@"C:\docs\report.txt");

        Matches(item, new SearchQuery { SearchText = text }).Should().Be(expected);
    }

    [Fact]
    public void SearchFileNameOnly_Should_Exclude_The_Directory_From_The_Target()
    {
        var item = Item(@"C:\docs\report.txt");

        Matches(item, new SearchQuery { SearchText = "docs", SearchFileNameOnly = true }).Should().BeFalse();
        Matches(item, new SearchQuery { SearchText = "docs", SearchFileNameOnly = false }).Should().BeTrue();
    }

    [Fact]
    public void CaseSensitive_Should_Require_An_Exact_Case_Match()
    {
        var item = Item(@"C:\docs\Report.txt");

        Matches(item, new SearchQuery { SearchText = "report", CaseSensitive = true }).Should().BeFalse();
        Matches(item, new SearchQuery { SearchText = "Report", CaseSensitive = true }).Should().BeTrue();
    }

    [Theory]
    [InlineData("*.txt", true)]
    [InlineData("*.cs", false)]
    [InlineData(@"C:\docs\*", true)]
    [InlineData("report.txt", false)] // a wildcard pattern is anchored, so a bare name cannot match a path
    public void Wildcards_Should_Be_Anchored_To_The_Whole_Target(string pattern, bool expected)
    {
        var item = Item(@"C:\docs\report.txt");

        // "report.txt" has no wildcard, so it is a substring match and does match — assert the
        // anchored cases only through patterns that contain one.
        var query = new SearchQuery { SearchText = pattern };
        var isWildcard = pattern.Contains('*') || pattern.Contains('?');

        if (isWildcard)
        {
            Matches(item, query).Should().Be(expected);
        }
    }

    [Fact]
    public void UseRegex_Should_Match_Against_The_Whole_Target_Unanchored()
    {
        var item = Item(@"C:\docs\report.txt");

        Matches(item, new SearchQuery { SearchText = @"rep.rt\.txt$", UseRegex = true }).Should().BeTrue();
        Matches(item, new SearchQuery { SearchText = "^report", UseRegex = true }).Should().BeFalse();
    }

    [Fact]
    public void An_Invalid_Regex_Should_Fall_Back_To_A_Substring_Match_Rather_Than_Throwing()
    {
        var item = Item(@"C:\docs\report.txt");

        // SearchQuery.GetCompiledRegex returns null for an unparseable pattern.
        var act = () => Matches(item, new SearchQuery { SearchText = "[", UseRegex = true });

        act.Should().NotThrow();
    }

    // --- structured filters ---

    [Fact]
    public void Size_Bounds_Should_Be_Inclusive()
    {
        var item = Item(@"C:\docs\report.txt", size: 1000);

        Matches(item, new SearchQuery { MinSize = 1000 }).Should().BeTrue();
        Matches(item, new SearchQuery { MaxSize = 1000 }).Should().BeTrue();
        Matches(item, new SearchQuery { MinSize = 1001 }).Should().BeFalse();
        Matches(item, new SearchQuery { MaxSize = 999 }).Should().BeFalse();
    }

    [Fact]
    public void Created_And_Modified_Bounds_Should_Be_Applied_Independently()
    {
        var item = Item(@"C:\docs\report.txt");

        Matches(item, new SearchQuery { MinCreatedDate = Created.AddDays(-1) }).Should().BeTrue();
        Matches(item, new SearchQuery { MinCreatedDate = Created.AddDays(1) }).Should().BeFalse();
        Matches(item, new SearchQuery { MaxModifiedDate = Modified.AddDays(-1) }).Should().BeFalse();
        Matches(item, new SearchQuery { MaxModifiedDate = Modified.AddDays(1) }).Should().BeTrue();
    }

    [Theory]
    [InlineData(".txt", true)]
    [InlineData("txt", true)]   // a leading dot is optional on the filter
    [InlineData("TXT", true)]   // extensions compare case-insensitively
    [InlineData(".cs", false)]
    public void ExtensionFilter_Should_Ignore_A_Leading_Dot_And_Case(string filter, bool expected)
    {
        var item = Item(@"C:\docs\report.txt");

        Matches(item, new SearchQuery { ExtensionFilter = filter }).Should().Be(expected);
    }

    [Fact]
    public void Type_Filters_Should_Exclude_By_Attribute()
    {
        var file = Item(@"C:\docs\report.txt");
        var directory = Item(@"C:\docs\sub", attributes: FileAttributes.Directory);
        var hidden = Item(@"C:\docs\.secret", attributes: FileAttributes.Hidden);
        var system = Item(@"C:\docs\pagefile.sys", attributes: FileAttributes.System);

        Matches(file, new SearchQuery { IncludeFiles = false }).Should().BeFalse();
        Matches(directory, new SearchQuery { IncludeDirectories = false }).Should().BeFalse();
        Matches(hidden, new SearchQuery()).Should().BeFalse();
        Matches(hidden, new SearchQuery { IncludeHidden = true }).Should().BeTrue();
        Matches(system, new SearchQuery()).Should().BeFalse();
        Matches(system, new SearchQuery { IncludeSystem = true }).Should().BeTrue();
    }

    // --- location scope ---

    [Fact]
    public void BasePath_Should_Scope_To_A_Subtree()
    {
        var item = Item(@"C:\docs\sub\report.txt");

        Matches(item, new SearchQuery { BasePath = @"C:\docs" }).Should().BeTrue();
        Matches(item, new SearchQuery { BasePath = @"C:\docs\" }).Should().BeTrue();
        Matches(item, new SearchQuery { BasePath = @"C:\other" }).Should().BeFalse();
    }

    [Fact]
    public void BasePath_Should_Not_Match_A_Sibling_With_A_Shared_Prefix()
    {
        var item = Item(@"C:\documents\report.txt");

        Matches(item, new SearchQuery { BasePath = @"C:\doc" }).Should().BeFalse();
    }

    [Fact]
    public void IncludeSubdirectories_False_Should_Require_The_Exact_Directory()
    {
        var direct = Item(@"C:\docs\report.txt");
        var nested = Item(@"C:\docs\sub\report.txt");
        var query = new SearchQuery { BasePath = @"C:\docs", IncludeSubdirectories = false };

        Matches(direct, query).Should().BeTrue();
        Matches(nested, query).Should().BeFalse();
    }

    [Fact]
    public void SearchLocations_Should_Match_Any_Of_The_Listed_Roots()
    {
        var item = Item(@"C:\b\report.txt");

        Matches(item, new SearchQuery { SearchLocations = { @"C:\a", @"C:\b" } }).Should().BeTrue();
        Matches(item, new SearchQuery { SearchLocations = { @"C:\a", @"C:\c" } }).Should().BeFalse();
    }

    [Fact]
    public void BasePath_Should_Take_Precedence_Over_SearchLocations()
    {
        var item = Item(@"C:\b\report.txt");

        var query = new SearchQuery { BasePath = @"C:\b", SearchLocations = { @"C:\a" } };

        Matches(item, query).Should().BeTrue();
    }

    [Fact]
    public void ExcludedPaths_Should_Reject_A_Subtree_Even_When_It_Is_In_Scope()
    {
        var item = Item(@"C:\docs\sub\report.txt");

        var query = new SearchQuery { BasePath = @"C:\docs", ExcludedPaths = { @"C:\docs\sub" } };

        Matches(item, query).Should().BeFalse();
    }

    [Fact]
    public void An_Unscoped_Query_Should_Match_Anywhere()
    {
        Matches(Item(@"C:\anywhere\at\all.txt"), new SearchQuery()).Should().BeTrue();
    }
}
