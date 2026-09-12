using FastFind.Models;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// Windows semantics of the single exclusion predicate: <c>/</c> and <c>\</c> name the same
/// separator, comparison folds case, and an entry is either a fully qualified root or a run of
/// whole path segments.
/// </summary>
[Trait("Category", "Functional")]
public class PathExclusionTests
{
    private static bool Excluded(string path, params string[] entries) =>
        PathExclusion.IsExcluded(path, entries);

    // --- a fully qualified entry excludes what sits at or under it ---

    [Theory]
    [InlineData(@"C:\proj\bin\app.dll", true)]
    [InlineData(@"C:\proj\bin\debug\app.dll", true)]
    [InlineData(@"C:\proj\bin", true)]              // the excluded directory's own entry
    [InlineData(@"C:\proj\binaries\app.dll", false)] // prefix of a longer segment, not under it
    [InlineData(@"C:\proj\app.dll", false)]
    public void A_Fully_Qualified_Entry_Should_Exclude_Its_Own_Subtree_Only(string path, bool expected)
    {
        Excluded(path, @"C:\proj\bin").Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:/proj/bin", @"C:\proj\bin\app.dll")]   // entry written with forward slashes
    [InlineData(@"C:\proj\bin", @"C:/proj/bin/app.dll")]   // path with forward slashes
    [InlineData(@"C:/proj/bin", @"C:/proj/bin/app.dll")]
    public void Separators_Should_Be_Equivalent_On_Windows(string entry, string path)
    {
        Excluded(path, entry).Should().BeTrue();
    }

    [Fact]
    public void Comparison_Should_Fold_Case()
    {
        Excluded(@"C:\PROJ\Bin\App.dll", @"c:\proj\bin").Should().BeTrue();
        Excluded(@"C:\proj\BIN\app.dll", "bin").Should().BeTrue();
    }

    [Fact]
    public void A_Trailing_Separator_On_The_Entry_Should_Not_Change_The_Result()
    {
        Excluded(@"C:\proj\bin\app.dll", @"C:\proj\bin\").Should().BeTrue();
        Excluded(@"C:\proj\bin\", @"C:\proj\bin").Should().BeTrue();
    }

    // --- a bare name excludes whole segments, never a substring ---

    [Theory]
    [InlineData(@"C:\proj\bin\app.dll", true)]
    [InlineData(@"C:\proj\src\bin", true)]           // the directory itself
    [InlineData(@"C:\proj\binaries\app.dll", false)]
    [InlineData(@"C:\proj\app.bin", false)]
    public void A_Bare_Name_Should_Match_A_Whole_Segment(string path, bool expected)
    {
        Excluded(path, "bin").Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\attempts\notes.txt")]   // contains "temp", is not a temp directory
    [InlineData(@"C:\docs\template.docx")]
    public void A_Bare_Name_Should_Not_Match_Inside_A_Segment(string path)
    {
        // The substring comparison this replaces excluded both of these.
        Excluded(path, "temp").Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\proj\src\bin\app.dll", true)]
    [InlineData(@"C:\proj\bin\app.dll", false)]      // "bin" is there, but not under "src"
    [InlineData(@"C:\proj\mysrc\bin\app.dll", false)]
    public void A_Relative_Entry_Should_Match_A_Run_Of_Whole_Segments(string path, bool expected)
    {
        Excluded(path, @"src\bin").Should().Be(expected);
    }

    [Fact]
    public void A_Rooted_But_Drive_Relative_Entry_Should_Read_As_Segments()
    {
        // "/bin" on Windows is rooted but not fully qualified. Reading it as a segment run is what
        // keeps it from being silently inert, which is the defect class this matcher exists to end.
        Excluded(@"C:\proj\bin\app.dll", "/bin").Should().BeTrue();
        Excluded(@"C:\proj\binaries\app.dll", "/bin").Should().BeFalse();
    }

    // --- what the matcher deliberately does not do ---

    [Theory]
    [InlineData(@"C:\proj\bin\app.dll")]
    [InlineData(@"C:\proj\node_modules\x.js")]
    public void Glob_Patterns_Should_Be_Literal_And_Match_Nothing(string path)
    {
        // There is no wildcard support: these are the defaults that never excluded anything.
        Excluded(path, "**/bin/**", "**/node_modules/**").Should().BeFalse();
    }

    [Fact]
    public void An_Empty_Or_Blank_Entry_Should_Exclude_Nothing()
    {
        Excluded(@"C:\proj\bin\app.dll").Should().BeFalse();
        Excluded(@"C:\proj\bin\app.dll", "", "   ").Should().BeFalse();
    }

    [Fact]
    public void Any_Matching_Entry_Should_Exclude()
    {
        Excluded(@"C:\proj\obj\app.dll", @"C:\other", "obj").Should().BeTrue();
    }
}
