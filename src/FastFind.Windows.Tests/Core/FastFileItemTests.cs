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
        item.FullPathId.Should().NotBe(0, "FullPath should be interned with valid ID");
        item.NameId.Should().NotBe(0, "Name should be interned with valid ID");
        item.DirectoryId.Should().NotBe(0, "Directory should be interned with valid ID");
        item.ExtensionId.Should().NotBe(0, "Extension should be interned with valid ID");
    }
    
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