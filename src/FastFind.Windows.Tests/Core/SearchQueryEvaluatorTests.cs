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

    [Fact]
    public void A_Local_Date_Bound_Should_Mean_The_Same_Instant_As_Its_UTC_Equivalent()
    {
        // Item times are UTC. A bound given in local time names the same instant, so both an
        // inclusive lower and an inclusive upper bound at that instant must match. Compared by raw
        // ticks, the local bound is off by the UTC offset and one of the two fails.
        var item = Item(@"C:\docs\report.txt");
        var localInstant = Modified.ToLocalTime();

        var lower = new SearchQuery { MinModifiedDate = localInstant };
        var upper = new SearchQuery { MaxModifiedDate = localInstant };

        lower.MinModifiedDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
        lower.MinModifiedDate.Value.Should().Be(Modified);
        Matches(item, lower).Should().BeTrue();
        Matches(item, upper).Should().BeTrue();
    }

    [Fact]
    public void An_Uncollected_Timestamp_Should_Satisfy_No_Date_Bound()
    {
        // DateTime.MinValue is "not collected". It must not pass an upper bound just because it is
        // earlier than everything, nor a lower one.
        var item = Item(@"C:\docs\report.txt", created: DateTime.MinValue, modified: DateTime.MinValue);

        item.ModifiedTime.Should().Be(DateTime.MinValue);
        Matches(item, new SearchQuery { MaxModifiedDate = Modified }).Should().BeFalse();
        Matches(item, new SearchQuery { MinModifiedDate = DateTime.MinValue }).Should().BeFalse();
        Matches(item, new SearchQuery { MaxCreatedDate = Created }).Should().BeFalse();
        Matches(item, new SearchQuery()).Should().BeTrue();
    }

    [Fact]
    public void An_Unspecified_MinValue_Should_Stay_Uncollected_Whatever_The_Time_Zone()
    {
        // FileItem's timestamps default to an Unspecified MinValue. Converting that to UTC as local
        // time moves it forward wherever the offset is negative, and it would stop reading as unset.
        var item = new FastFileItem(@"C:\docs\report.txt", "report.txt", @"C:\docs", ".txt", 0,
            default, default, default, FileAttributes.Normal, 'C');

        item.CreatedTicks.Should().Be(0);
        item.ModifiedTicks.Should().Be(0);
        item.AccessedTicks.Should().Be(0);
    }

    [Fact]
    public void A_BasePath_With_Forward_Slashes_Should_Scope_Like_Its_Backslash_Form_On_Windows()
    {
        var inside = Item(@"C:\docs\sub\report.txt");
        var sibling = Item(@"C:\docs2\report.txt");

        Matches(inside, new SearchQuery { BasePath = "C:/docs" }).Should().BeTrue();
        Matches(sibling, new SearchQuery { BasePath = "C:/docs" }).Should().BeFalse();
        Matches(inside, new SearchQuery { SearchLocations = { "C:/docs/" } }).Should().BeTrue();
    }

    [Fact]
    public void RequiresFileMetadata_Should_Detect_Every_Size_And_Date_Bound()
    {
        SearchQueryEvaluator.RequiresFileMetadata(new SearchQuery { SearchText = "*.md" }).Should().BeFalse();
        SearchQueryEvaluator.RequiresFileMetadata(new SearchQuery { MinSize = 1 }).Should().BeTrue();
        SearchQueryEvaluator.RequiresFileMetadata(new SearchQuery { MaxSize = 1 }).Should().BeTrue();
        SearchQueryEvaluator.RequiresFileMetadata(new SearchQuery { MinCreatedDate = Created }).Should().BeTrue();
        SearchQueryEvaluator.RequiresFileMetadata(new SearchQuery { MaxCreatedDate = Created }).Should().BeTrue();
        SearchQueryEvaluator.RequiresFileMetadata(new SearchQuery { MinModifiedDate = Modified }).Should().BeTrue();
        SearchQueryEvaluator.RequiresFileMetadata(new SearchQuery { MaxModifiedDate = Modified }).Should().BeTrue();
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

    [Fact]
    public void RequiredAttributes_Should_Demand_Every_Requested_Flag()
    {
        var readOnlyOnly = Item(@"C:\docs\a.txt", attributes: FileAttributes.ReadOnly);
        var readOnlyArchive = Item(@"C:\docs\b.txt", attributes: FileAttributes.ReadOnly | FileAttributes.Archive);

        var query = new SearchQuery
        {
            RequiredAttributes = FileAttributes.ReadOnly | FileAttributes.Archive,
        };

        Matches(readOnlyOnly, query).Should().BeFalse("only one of the two required flags is present");
        Matches(readOnlyArchive, query).Should().BeTrue();
    }

    [Fact]
    public void ExcludedAttributes_Should_Reject_An_Item_Carrying_Any_One_Of_Them()
    {
        // "must not be present" reads as any, not all. Testing an item with exactly one of the two
        // excluded flags is what separates the two readings: HasFlag would accept it.
        var readOnly = Item(@"C:\docs\a.txt", attributes: FileAttributes.ReadOnly);
        var archive = Item(@"C:\docs\b.txt", attributes: FileAttributes.Archive);
        var neither = Item(@"C:\docs\c.txt", attributes: FileAttributes.Normal);

        var query = new SearchQuery
        {
            ExcludedAttributes = FileAttributes.ReadOnly | FileAttributes.Archive,
        };

        Matches(readOnly, query).Should().BeFalse();
        Matches(archive, query).Should().BeFalse();
        Matches(neither, query).Should().BeTrue();
    }

    [Fact]
    public void Attribute_Filters_Should_Be_Inert_When_Unset()
    {
        var item = Item(@"C:\docs\a.txt", attributes: FileAttributes.ReadOnly | FileAttributes.Archive);

        Matches(item, new SearchQuery()).Should().BeTrue();
    }
}
