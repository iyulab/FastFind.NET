using FastFind.Models;
using FluentAssertions;
using Xunit;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// Unix semantics of the single exclusion predicate: only <c>/</c> separates, comparison is
/// case-sensitive, and a backslash is an ordinary file name character.
/// </summary>
[Trait("Category", "Functional")]
public class PathExclusionTests
{
    private static bool Excluded(string path, params string[] entries) =>
        PathExclusion.IsExcluded(path, entries);

    // --- a fully qualified entry excludes what sits at or under it ---

    [Theory]
    [InlineData("/data/logs/today.txt", true)]
    [InlineData("/data/logs/2026/today.txt", true)]
    [InlineData("/data/logs", true)]                 // the excluded directory's own entry
    [InlineData("/data/logsx/today.txt", false)]     // prefix of a longer segment, not under it
    [InlineData("/data/today.txt", false)]
    public void A_Rooted_Entry_Should_Exclude_Its_Own_Subtree_Only(string path, bool expected)
    {
        Excluded(path, "/data/logs").Should().Be(expected);
    }

    [Fact]
    public void A_Trailing_Separator_On_The_Entry_Should_Not_Change_The_Result()
    {
        Excluded("/data/logs/today.txt", "/data/logs/").Should().BeTrue();
    }

    [Fact]
    public void Comparison_Should_Be_Case_Sensitive()
    {
        Excluded("/data/logs/today.txt", "/data/LOGS").Should().BeFalse();
        Excluded("/srv/app/node_modules/x.js", "Node_Modules").Should().BeFalse();
    }

    // --- a bare name excludes whole segments, never a substring ---

    [Theory]
    [InlineData("/srv/app/node_modules/x.js", true)]
    [InlineData("/srv/app/node_modules", true)]
    [InlineData("/srv/app/node_modules_old/x.js", false)]
    [InlineData("/srv/mynode_modules/x.js", false)]
    public void A_Bare_Name_Should_Match_A_Whole_Segment(string path, bool expected)
    {
        Excluded(path, "node_modules").Should().Be(expected);
    }

    [Theory]
    [InlineData("/proj/src/bin/a.o", true)]
    [InlineData("/proj/bin/a.o", false)]
    [InlineData("/proj/mysrc/bin/a.o", false)]
    public void A_Relative_Entry_Should_Match_A_Run_Of_Whole_Segments(string path, bool expected)
    {
        Excluded(path, "src/bin").Should().Be(expected);
    }

    // --- a backslash is an ordinary character here ---

    [Fact]
    public void A_Backslash_Should_Not_Separate_Segments()
    {
        // "logs\today.txt" is one segment whose name contains a backslash, not two segments.
        Excluded(@"/data/logs\today.txt", "logs").Should().BeFalse();
        Excluded(@"/data/logs\today.txt", @"logs\today.txt").Should().BeTrue();
    }

    [Fact]
    public void A_Windows_Spelled_Entry_Should_Not_Match_A_Posix_Path()
    {
        Excluded("/data/logs/today.txt", @"\data\logs").Should().BeFalse();
    }

    // --- what the matcher deliberately does not do ---

    [Theory]
    [InlineData("/proj/bin/a.o")]
    [InlineData("/srv/app/node_modules/x.js")]
    public void Glob_Patterns_Should_Be_Literal_And_Match_Nothing(string path)
    {
        Excluded(path, "**/bin/**", "**/node_modules/**").Should().BeFalse();
    }

    [Fact]
    public void An_Empty_Or_Blank_Entry_Should_Exclude_Nothing()
    {
        Excluded("/proj/bin/a.o").Should().BeFalse();
        Excluded("/proj/bin/a.o", "", "   ").Should().BeFalse();
    }

    [Fact]
    public void Any_Matching_Entry_Should_Exclude()
    {
        Excluded("/proj/obj/a.o", "/other", "obj").Should().BeTrue();
    }
}
