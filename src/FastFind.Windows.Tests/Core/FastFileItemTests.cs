using System.Runtime.InteropServices;
using FastFind.Models;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// Tests for FastFileItem memory optimization and performance
/// </summary>
public class FastFileItemTests
{
    [Fact]
    public void FastFileItem_Should_Be_Struct()
    {
        // Arrange & Act
        var type = typeof(FastFileItem);
        
        // Assert
        type.IsValueType.Should().BeTrue("FastFileItem should be a struct for memory optimization");
    }
    
    [Fact]
    public void FastFileItem_Should_Have_Optimal_Size()
    {
        // Arrange & Act
        var size = Marshal.SizeOf<FastFileItem>();
        
        // Assert
        Assert.True(size <= 64, "FastFileItem should be <= 64 bytes for cache line optimization");
    }
    
    [Fact]
    public void FastFileItem_Should_Support_String_Interning()
    {
        // Arrange
        var path = @"C:\Test\File.txt";
        var name = "File.txt";
        var directory = @"C:\Test";
        var extension = ".txt";
        
        // Act
        var item = new FastFileItem(
            path, name, directory, extension,
            1024, DateTime.Now, DateTime.Now, DateTime.Now,
            FileAttributes.Normal, 'C');
        
        // Assert
        item.NameId.Should().NotBe(0, "Name should be interned with valid ID");
        item.DirectoryId.Should().NotBe(0, "Directory should be interned with valid ID");
        item.ExtensionId.Should().NotBe(0, "Extension should be interned with valid ID");
    }

    [Theory]
    [InlineData(@"C:\Test\File.txt", "File.txt", @"C:\Test")]        // directory, \, name
    [InlineData("/home/user/file.txt", "file.txt", "/home/user")]     // directory, /, name
    [InlineData(@"C:\File.txt", "File.txt", @"C:\")]                 // directory ends in a separator
    [InlineData("/file.txt", "file.txt", "/")]
    [InlineData(@"C:\Test\Sub\", "Sub", @"C:\Test")]                  // not a composition: kept as given
    [InlineData(@"C:\Test\File.txt", "Other.txt", @"C:\Elsewhere")]  // parts disagree: kept as given
    [InlineData("", "", "")]
    public void FullPath_Should_Read_Back_As_Given(string fullPath, string name, string directory)
    {
        var item = new FastFileItem(fullPath, name, directory, "", 0,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C');

        // The pool folds '/' to '\' on Windows, and always has; elsewhere nothing is rewritten.
        var expected = OperatingSystem.IsWindows() ? fullPath.Replace('/', '\\') : fullPath;
        item.FullPath.Should().Be(expected);
        item.MatchesPath(expected).Should().BeTrue();
    }

    [Fact]
    public void Items_With_The_Same_Path_Should_Be_Equal_However_It_Is_Stored()
    {
        // One composed from its parts, one kept whole because its parts do not compose to it.
        var composed = new FastFileItem(@"C:\a\b.txt", "b.txt", @"C:\a", ".txt", 0,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C');
        var whole = new FastFileItem(@"C:\a\b.txt", "x", @"C:\z", ".txt", 0,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C');
        var different = new FastFileItem(@"C:\a\c.txt", "c.txt", @"C:\a", ".txt", 0,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, 'C');

        (composed == whole).Should().BeTrue();
        composed.GetHashCode().Should().Be(whole.GetHashCode());
        (composed == different).Should().BeFalse();
        composed.Equals(different).Should().BeFalse();
    }

#pragma warning disable CS0618 // The obsolete member's contract is what is under test.
    [Fact]
    public void The_Obsolete_FullPathId_Should_Still_Name_The_Full_Path()
    {
        var item = CreateTestFastFileItem("Legacy.txt");

        StringPool.Get(item.FullPathId).Should().Be(item.FullPath);
    }
#pragma warning restore CS0618
    
    [Theory]
    [InlineData("test", true)]   // Case-insensitive matching
    [InlineData("TEST", true)]   // Case-insensitive matching
    [InlineData("File", true)]   // Substring match
    [InlineData("xyz", false)]   // No match
    public void MatchesName_Should_Work_Correctly(string pattern, bool expected)
    {
        // Arrange
        var item = CreateTestFastFileItem("TestFile.txt");
        
        // Act
        var result = item.MatchesName(pattern.AsSpan());
        
        // Assert
        result.Should().Be(expected);
    }
    
    [Theory]
    [InlineData("*.txt", true)]
    [InlineData("Test*", true)]
    [InlineData("*.doc", false)]
    [InlineData("*File*", true)]
    public void MatchesWildcard_Should_Work_Correctly(string pattern, bool expected)
    {
        // Arrange
        var item = CreateTestFastFileItem("TestFile.txt");
        
        // Act
        var result = item.MatchesWildcard(pattern.AsSpan());
        
        // Assert
        result.Should().Be(expected);
    }
    
    [Fact]
    public void ToFileItem_Should_Convert_Correctly()
    {
        // Arrange
        var fastItem = CreateTestFastFileItem("File.txt");
        
        // Act
        var fileItem = fastItem.ToFileItem();
        
        // Assert
        fileItem.Should().NotBeNull();
        fileItem.Name.Should().Be("File.txt");
        fileItem.Extension.Should().Be(".txt");
        fileItem.Size.Should().Be(1024);
    }
    
    [Fact]
    public void Equals_Should_Work_For_Identical_Items()
    {
        // Arrange
        var item1 = CreateTestFastFileItem("Test.txt");
        var item2 = CreateTestFastFileItem("Test.txt");
        
        // Act & Assert
        item1.Equals(item2).Should().BeTrue("Identical items should be equal");
        (item1 == item2).Should().BeTrue("== operator should work");
        item1.GetHashCode().Should().Be(item2.GetHashCode(), "Hash codes should match");
    }
    
    private static FastFileItem CreateTestFastFileItem(string fileName)
    {
        var fullPath = $@"C:\Test\{fileName}";
        var extension = Path.GetExtension(fileName);
        var directory = @"C:\Test";
        
        return new FastFileItem(
            fullPath, fileName, directory, extension,
            1024, DateTime.Now, DateTime.Now, DateTime.Now,
            FileAttributes.Normal, 'C');
    }
}