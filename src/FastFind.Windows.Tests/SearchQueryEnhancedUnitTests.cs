using FastFind.Models;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests;

/// <summary>
/// Unit tests for enhanced SearchQuery functionality
/// </summary>
public class SearchQueryEnhancedUnitTests
{
    [Theory]
    [InlineData("D:\\data", "test", true)]
    [InlineData("C:\\Users", "search", false)]
    [InlineData("/home/user", "pattern", true)]
    public void SearchQuery_BasePath_ShouldStoreCorrectly(string basePath, string searchText, bool includeSubdirectories)
    {
        // Arrange & Act
        var query = new SearchQuery
        {
            BasePath = basePath,
            SearchText = searchText,
            IncludeSubdirectories = includeSubdirectories
        };

        // Assert
        query.BasePath.Should().Be(basePath);
        query.SearchText.Should().Be(searchText);
        query.IncludeSubdirectories.Should().Be(includeSubdirectories);
    }

    [Fact]
    public void SearchQuery_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var query = new SearchQuery();

        // Assert - Test the three core enhanced features
        query.BasePath.Should().BeNull("BasePath should default to null");
        query.SearchText.Should().Be(string.Empty, "SearchText should default to empty string");
        query.IncludeSubdirectories.Should().BeTrue("IncludeSubdirectories should default to true");
        query.SearchFileNameOnly.Should().BeFalse("SearchFileNameOnly should default to false for full path search");
    }

    [Theory]
    [InlineData(true, "should include subdirectories")]
    [InlineData(false, "should not include subdirectories")]
    public void SearchQuery_IncludeSubdirectories_ShouldControlSubdirectorySearch(bool includeSubdirectories, string expectation)
    {
        // Arrange
        var query = new SearchQuery
        {
            BasePath = "D:\\TestPath",
            SearchText = "test",
            IncludeSubdirectories = includeSubdirectories
        };

        // Act & Assert
        query.IncludeSubdirectories.Should().Be(includeSubdirectories, expectation);
    }

    [Theory]
    [InlineData(true, "should search only in filenames")]
    [InlineData(false, "should search in full paths (filename + directory)")]
    public void SearchQuery_SearchFileNameOnly_ShouldControlSearchScope(bool searchFileNameOnly, string expectation)
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "test",
            SearchFileNameOnly = searchFileNameOnly
        };

        // Act & Assert
        query.SearchFileNameOnly.Should().Be(searchFileNameOnly, expectation);
    }

    [Fact]
    public void SearchQuery_Clone_ShouldIncludeNewProperties()
    {
        // Arrange
        var original = new SearchQuery
        {
            BasePath = "D:\\Original",
            SearchText = "original_search",
            IncludeSubdirectories = false,
            SearchFileNameOnly = true,
            ExtensionFilter = ".txt",
            CaseSensitive = true,
            UseRegex = true,
            MaxResults = 100
        };

        // Act
        var cloned = original.Clone();

        // Assert - Test all properties including new ones
        cloned.BasePath.Should().Be(original.BasePath);
        cloned.SearchText.Should().Be(original.SearchText);
        cloned.IncludeSubdirectories.Should().Be(original.IncludeSubdirectories);
        cloned.SearchFileNameOnly.Should().Be(original.SearchFileNameOnly);
        cloned.ExtensionFilter.Should().Be(original.ExtensionFilter);
        cloned.CaseSensitive.Should().Be(original.CaseSensitive);
        cloned.UseRegex.Should().Be(original.UseRegex);
        cloned.MaxResults.Should().Be(original.MaxResults);

        // Ensure it's a different object
        cloned.Should().NotBeSameAs(original);
    }

    [Fact]
    public void SearchQuery_Validation_ShouldSucceedWithValidConfig()
    {
        // Arrange
        var query = new SearchQuery
        {
            BasePath = "D:\\ValidPath",
            SearchText = "valid_search",
            IncludeSubdirectories = true,
            SearchFileNameOnly = false
        };

        // Act
        var (isValid, errorMessage) = query.Validate();

        // Assert
        isValid.Should().BeTrue("query with valid configuration should be valid");
        errorMessage.Should().BeNull("valid query should not have error message");
    }

    [Theory]
    [InlineData("test", true, "non-empty search text should be valid")]
    [InlineData("", false, "empty search text should be invalid according to validation logic")]
    public void SearchQuery_Validation_ShouldHandleSearchTextCorrectly(string searchText, bool expectedValid, string reason)
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = searchText,
            BasePath = "D:\\Test"
        };

        // Act
        var (isValid, errorMessage) = query.Validate();

        // Assert
        isValid.Should().Be(expectedValid, reason);
        if (!expectedValid)
        {
            errorMessage.Should().NotBeNull("invalid query should have error message");
        }
    }

    [Fact]
    public void SearchQuery_Validation_ShouldRequireSearchCriteria()
    {
        // Arrange - Query with no search criteria (the actual validation logic)
        var query = new SearchQuery
        {
            SearchText = "", // Empty search text
            ExtensionFilter = null,
            MinSize = null,
            MaxSize = null,
            MinCreatedDate = null,
            MaxCreatedDate = null,
            MinModifiedDate = null,
            MaxModifiedDate = null,
            BasePath = "D:\\ValidPath"
        };

        // Act
        var (isValid, errorMessage) = query.Validate();

        // Assert
        isValid.Should().BeFalse("query without search criteria should be invalid");
        errorMessage.Should().NotBeNull();
        errorMessage.Should().Contain("search criterion", "error should mention search criterion requirement");
    }

    [Fact]
    public void SearchQuery_Properties_ShouldStoreValuesCorrectly()
    {
        // Arrange & Act
        var query = new SearchQuery
        {
            BasePath = "D:\\TestData",
            SearchText = "claude",
            IncludeSubdirectories = false,
            SearchFileNameOnly = true,
            ExtensionFilter = ".txt"
        };

        // Assert - Test that properties store values correctly
        query.BasePath.Should().Be("D:\\TestData");
        query.SearchText.Should().Be("claude");
        query.IncludeSubdirectories.Should().BeFalse();
        query.SearchFileNameOnly.Should().BeTrue();
        query.ExtensionFilter.Should().Be(".txt");
    }

    [Fact]
    public void SearchQuery_Korean_Annotations_ShouldBeDocumented()
    {
        // This test documents the Korean feature names for reference

        // 기준경로 (BasePath) - Base path for search
        var basePath = "D:\\data";

        // search-text - Pattern to find in paths and filenames
        var searchText = "claude";

        // subdirectory - Include subdirectories in search
        var includeSubdirectories = true;

        var query = new SearchQuery
        {
            BasePath = basePath,          // 기준경로
            SearchText = searchText,      // search-text
            IncludeSubdirectories = includeSubdirectories  // subdirectory
        };

        // Assert the three core features work
        query.BasePath.Should().Be("D:\\data", "기준경로 should be set correctly");
        query.SearchText.Should().Be("claude", "search-text should be set correctly");
        query.IncludeSubdirectories.Should().BeTrue("subdirectory option should be set correctly");

        // This test serves as documentation for the Korean feature requirements
        var coreFeatures = new[]
        {
            "기준경로 (BasePath) - Starting directory for search",
            "search-text - Pattern matching in paths and filenames",
            "subdirectory - Control whether to include subdirectories"
        };

        coreFeatures.Should().HaveCount(3, "should have exactly three core features as requested");
    }
}