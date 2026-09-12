using FastFind.Models;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// A stored path reads back exactly as it was given, on Windows too.
/// </summary>
/// <remarks>
/// <c>StringPool.InternPath</c> folded <c>/</c> to <c>\</c> on a Windows host, so an item built from
/// a forward-slash-spelled directory read back with a spelling its caller never used. Identity that
/// ignores the difference is the comparers' job — these tests hold the value side of that split, the
/// last of the four path-normalization mistakes recorded in the repository guide.
/// </remarks>
[Trait("Category", "Functional")]
public class PathStorageFidelityTests
{
    [Theory]
    [InlineData(@"C:\data\proj")]
    [InlineData("C:/data/proj")]
    [InlineData(@"C:/data\proj")]
    [InlineData("/home/user/docs")]
    public void An_Interned_Path_Should_Read_Back_Verbatim(string path)
    {
        StringPool.GetString(StringPool.InternPath(path)).Should().Be(path);
    }

    [Fact]
    public void Two_Spellings_Of_One_Directory_Should_Stay_Two_Distinct_Values()
    {
        var forward = StringPool.InternPath("C:/data/distinct-spellings");
        var backward = StringPool.InternPath(@"C:\data\distinct-spellings");

        StringPool.GetString(forward).Should().Be("C:/data/distinct-spellings");
        StringPool.GetString(backward).Should().Be(@"C:\data\distinct-spellings");
    }

    [Fact]
    public void A_Forward_Slash_Directory_Should_Survive_A_FastFileItem()
    {
        var item = new FastFileItem(
            "C:/data/proj/readme.md",
            "readme.md",
            "C:/data/proj",
            ".md",
            12,
            DateTime.Now,
            DateTime.Now,
            DateTime.Now,
            FileAttributes.Normal,
            'C');

        item.DirectoryPath.Should().Be("C:/data/proj", "the pool stores what it was given");
        item.FullPath.Should().Be("C:/data/proj/readme.md",
            "the path is composed from the stored directory, the separator it was spelled with, "
            + "and the name — none of which is rewritten");
    }
}
