using System.Globalization;
using FastFind.Models;
using AwesomeAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// What <see cref="FastFileItem.SizeFormatted"/> says. It is what the README's Quick Start prints, and it said "11 MB" for an
/// 11,682-byte file: the unit was taken one step too large for every size from a kilobyte up.
/// </summary>
[Trait("Category", "Functional")]
public class SizeFormattingTests
{
    // The text is a display string in the current culture ("1,0 KB" in some); the expectations are the invariant spelling.
    private static void InInvariantCulture(Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { body(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1L, "1 bytes")]
    [InlineData(1023L, "1023 bytes")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(11_682L, "11 KB")]
    [InlineData(14_949L, "15 KB")]
    [InlineData(1_048_575L, "1024 KB")]
    [InlineData(1_048_576L, "1.0 MB")]
    [InlineData(5_242_880L, "5.0 MB")]
    [InlineData(104_857_600L, "100 MB")]
    [InlineData(1_073_741_824L, "1.0 GB")]
    [InlineData(1L << 40, "1.0 TB")]
    [InlineData(1L << 50, "1.0 PB")]
    [InlineData(-1L, "Invalid size")]
    public void A_Size_Is_Written_In_The_Unit_It_Reaches(long bytes, string expected) =>
        InInvariantCulture(() =>
        {
            LazyFormatCache.FormatFileSizeFast(bytes).Should().Be(expected);
            LazyFormatCache.GetSizeFormatted(bytes).Should().Be(expected, "the cached path says the same");
        });

    [Fact]
    public void A_File_Item_Reports_Its_Size_In_The_Unit_It_Reaches() =>
        InInvariantCulture(() =>
        {
            var item = new FastFileItem(
                fullPath: @"C:\data\SearchQueryEvaluator.cs",
                name: "SearchQueryEvaluator.cs",
                directoryPath: @"C:\data",
                extension: ".cs",
                size: 11_682,
                created: DateTime.Now,
                modified: DateTime.Now,
                accessed: DateTime.Now,
                attributes: FileAttributes.Archive,
                driveLetter: 'C');

            item.SizeFormatted.Should().Be("11 KB");
        });

    [Fact]
    public void The_Largest_Size_Stays_In_The_Largest_Unit() =>
        InInvariantCulture(() => LazyFormatCache.FormatFileSizeFast(long.MaxValue).Should().Be("8192 PB"));
}
