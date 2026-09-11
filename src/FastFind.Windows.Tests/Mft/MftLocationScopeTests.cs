using FastFind.Windows.Mft;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Which enumerated entries fall inside the locations being indexed.
/// </summary>
[Trait("Category", "Functional")]
public class MftLocationScopeTests
{
    [Theory]
    [InlineData(@"C:\src\App\Program.cs", true)]
    [InlineData(@"C:\src\App", true)]
    [InlineData(@"C:\src\App.Tests\Program.cs", false)]   // shares the prefix, not the directory
    [InlineData(@"C:\src\Application\Program.cs", false)]
    [InlineData(@"C:\SRC\APP\Program.cs", true)]          // Windows paths compare case-insensitively
    public void A_Location_Should_Cover_Only_Itself_And_What_Lies_Beneath_It(string path, bool expected)
    {
        MftFileSystemProvider.IsPathInLocations(path, [@"C:\src\App"]).Should().Be(expected);
        MftFileSystemProvider.IsPathInLocations(path, [@"C:\src\App\"]).Should().Be(expected);
    }

    [Fact]
    public void A_Drive_Root_Location_Should_Cover_The_Whole_Drive()
    {
        MftFileSystemProvider.IsPathInLocations(@"C:\src\App\Program.cs", [@"C:\"]).Should().BeTrue();
        MftFileSystemProvider.IsPathInLocations(@"D:\src\App\Program.cs", [@"C:\"]).Should().BeFalse();
    }
}
