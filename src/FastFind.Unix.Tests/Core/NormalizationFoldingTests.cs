using System.Text;
using FastFind.Models;
using AwesomeAssertions;
using Xunit;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// On macOS a name spelled in normalization form C and the same name in form D are one name. Path and name
/// comparison folds them there, and only there — on Linux and Windows they are two names.
/// </summary>
/// <remarks>
/// The folding is selected by a parameter so that these cases run on any host; the platform decides which
/// value the library passes (<c>PathExclusion.FoldsNormalization</c>).
/// </remarks>
[Trait("Category", "Functional")]
public class NormalizationFoldingTests
{
    private static readonly string Composed = "문서".Normalize(NormalizationForm.FormC);
    private static readonly string Decomposed = "문서".Normalize(NormalizationForm.FormD);

    [Fact]
    public void The_Two_Spellings_Are_Different_Text()
    {
        Composed.Should().NotBe(Decomposed, "the whole point: same name, different code points");
        Decomposed.Length.Should().BeGreaterThan(Composed.Length);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_Scope_Spelled_In_The_Other_Form_Should_Contain_The_Path_Only_When_Folding(bool fold, bool expected)
    {
        var stored = $"/Users/me/{Composed}/scan_1.pdf";
        var scope = $"/Users/me/{Decomposed}";

        PathExclusion.IsAtOrUnder(stored, scope, fold).Should().Be(expected);
        PathExclusion.IsAtOrUnder(scope + "/sub/x", $"/Users/me/{Composed}", fold).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_Same_Folder_In_Either_Form_Should_Be_The_Same_Path_Only_When_Folding(bool fold, bool expected)
    {
        PathExclusion.SamePath($"/Users/me/{Composed}", $"/Users/me/{Decomposed}/", fold).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void An_Exclusion_Spelled_In_The_Other_Form_Should_Match_Only_When_Folding(bool fold, bool expected)
    {
        // A fully qualified entry and a bare name, each against a path spelled the other way.
        PathExclusion.Matches($"/Users/me/{Decomposed}/a.txt", $"/Users/me/{Composed}", fold).Should().Be(expected);
        PathExclusion.Matches($"/Users/me/{Decomposed}/a.txt", Composed, fold).Should().Be(expected);
    }

    [Fact]
    public void A_Sibling_Sharing_The_Prefix_Should_Not_Be_Beneath_It_Even_When_Folding()
    {
        PathExclusion.IsAtOrUnder($"/Users/me/{Decomposed}x/a", $"/Users/me/{Composed}", true).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_Name_Query_Spelled_In_The_Other_Form_Should_Match_Only_When_Folding(bool fold, bool expected)
    {
        SearchQueryEvaluatorProbe.MatchesText($"{Decomposed}_report.pdf", $"{Composed}_report", fold).Should().Be(expected);
        SearchQueryEvaluatorProbe.MatchesText($"{Composed}_report.pdf", $"{Decomposed}_report", fold).Should().Be(expected);
    }
}

/// <summary>Reaches the evaluator's internal text match with the folding chosen by the test.</summary>
internal static class SearchQueryEvaluatorProbe
{
    public static bool MatchesText(string target, string searchText, bool fold) =>
        SearchQueryEvaluator.MatchesText(target.AsSpan(), searchText, caseSensitive: false, textMatcher: null, foldNormalization: fold);
}
