using FastFind.Models;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FastFind.Windows.Tests;

/// <summary>
/// Tests for enhanced search options: BasePath, full path search, and subdirectory support
/// </summary>
public class EnhancedSearchOptionsTests : IDisposable
{
    private readonly ISearchEngine _searchEngine;

    public EnhancedSearchOptionsTests()
    {
        // Ensure Windows registration
        WindowsRegistration.EnsureRegistered();

        // Create search engine using public API
        _searchEngine = WindowsSearchEngine.CreateWindowsSearchEngine();
    }

    [Fact]
    public async Task SearchAsync_WithBasePath_ShouldSearchFromSpecifiedPath()
    {
        // Arrange - Create test directory structure
        var tempDir = Path.GetTempPath();
        var baseDir = Path.Combine(tempDir, "FastFindTest", Guid.NewGuid().ToString("N")[..8]);
        var subDir = Path.Combine(baseDir, "subdirectory");
        var unrelatedDir = Path.Combine(tempDir, "UnrelatedDir");

        try
        {
            Directory.CreateDirectory(baseDir);
            Directory.CreateDirectory(subDir);
            Directory.CreateDirectory(unrelatedDir);

            // Create files
            var fileInBase = Path.Combine(baseDir, "data_file.txt");
            var fileInSub = Path.Combine(subDir, "data_nested.txt");
            var unrelatedFile = Path.Combine(unrelatedDir, "data_unrelated.txt");

            await File.WriteAllTextAsync(fileInBase, "Base directory file");
            await File.WriteAllTextAsync(fileInSub, "Subdirectory file");
            await File.WriteAllTextAsync(unrelatedFile, "Unrelated file");

            // Index the directories
            var indexingOptions = new IndexingOptions
            {
                SpecificDirectories = { baseDir, unrelatedDir },
                ExcludedPaths = { }, // Clear default exclusions
                IncludeHidden = true // Include hidden files for testing
            };
            await _searchEngine.StartIndexingAsync(indexingOptions);

            // Wait for indexing
            await WaitForIndexingComplete();

            // Debug: Check if indexing worked
            Console.WriteLine($"Total indexed files: {_searchEngine.TotalIndexedFiles}");

            // Act - Search with BasePath
            var query = new SearchQuery
            {
                SearchText = "data",
                BasePath = baseDir, // Only search from this base path
                IncludeSubdirectories = true,
                SearchFileNameOnly = false, // Search in full paths
                CaseSensitive = false
            };

            var result = await _searchEngine.SearchAsync(query);
            var results = await CollectResults(result);

            // Assert - Should find files only in baseDir and its subdirectories
            // Note: Results may include directories with 'data' in name, so filter for files only
            var fileResults = results.Where(f => !f.IsDirectory).ToList();
            fileResults.Should().HaveCount(2, "should find files in base directory and subdirectory");
            fileResults.Should().Contain(f => f.FullPath.Contains("data_file.txt"));
            fileResults.Should().Contain(f => f.FullPath.Contains("data_nested.txt"));
            results.Should().NotContain(f => f.FullPath.Contains("data_unrelated.txt"));
        }
        finally
        {
            CleanupDirectory(baseDir);
            CleanupDirectory(unrelatedDir);
        }
    }

    [Fact]
    public async Task SearchAsync_WithoutSubdirectories_ShouldSearchOnlyDirectPath()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var baseDir = Path.Combine(tempDir, "FastFindTest", Guid.NewGuid().ToString("N")[..8]);
        var subDir = Path.Combine(baseDir, "subdirectory");

        try
        {
            Directory.CreateDirectory(baseDir);
            Directory.CreateDirectory(subDir);

            var directFile = Path.Combine(baseDir, "target_direct.txt");
            var subFile = Path.Combine(subDir, "target_nested.txt");

            await File.WriteAllTextAsync(directFile, "Direct file");
            await File.WriteAllTextAsync(subFile, "Nested file");

            var indexingOptions = new IndexingOptions
            {
                SpecificDirectories = { baseDir },
                ExcludedPaths = { }, // Clear default exclusions
                IncludeHidden = true // Include hidden files for testing
            };
            await _searchEngine.StartIndexingAsync(indexingOptions);
            await WaitForIndexingComplete();

            // Act - Search without subdirectories
            var query = new SearchQuery
            {
                SearchText = "target",
                BasePath = baseDir,
                IncludeSubdirectories = false, // Do not include subdirectories
                SearchFileNameOnly = false,
                CaseSensitive = false
            };

            var result = await _searchEngine.SearchAsync(query);
            var results = await CollectResults(result);

            // Assert - Should find only the direct file
            results.Should().HaveCount(1, "should find only files directly in base path");
            results.Should().Contain(f => f.FullPath.Contains("target_direct.txt"));
            results.Should().NotContain(f => f.FullPath.Contains("target_nested.txt"));
        }
        finally
        {
            CleanupDirectory(baseDir);
        }
    }

    [Fact]
    public async Task SearchAsync_FileNameOnly_VsFullPath_ShouldProduceDifferentResults()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var baseDir = Path.Combine(tempDir, "FastFindTest", Guid.NewGuid().ToString("N")[..8]);
        var specialDir = Path.Combine(baseDir, "claude_directory");

        try
        {
            Directory.CreateDirectory(baseDir);
            Directory.CreateDirectory(specialDir);

            // File with search term in filename
            var fileWithNameMatch = Path.Combine(baseDir, "claude_file.txt");
            // File with search term only in directory path
            var fileWithPathMatch = Path.Combine(specialDir, "regular_file.txt");
            // Regular file that doesn't match
            var regularFile = Path.Combine(baseDir, "other_file.txt");

            await File.WriteAllTextAsync(fileWithNameMatch, "File name match");
            await File.WriteAllTextAsync(fileWithPathMatch, "Path match");
            await File.WriteAllTextAsync(regularFile, "No match");

            var indexingOptions = new IndexingOptions
            {
                SpecificDirectories = { baseDir },
                ExcludedPaths = { }, // Clear default exclusions
                IncludeHidden = true // Include hidden files for testing
            };
            await _searchEngine.StartIndexingAsync(indexingOptions);
            await WaitForIndexingComplete();

            // Act - Search file names only
            var fileNameOnlyQuery = new SearchQuery
            {
                SearchText = "claude",
                BasePath = baseDir,
                IncludeSubdirectories = true,
                SearchFileNameOnly = true, // Only search in file names
                CaseSensitive = false
            };

            var fileNameResult = await _searchEngine.SearchAsync(fileNameOnlyQuery);
            var fileNameResults = await CollectResults(fileNameResult);

            // Act - Search full paths
            var fullPathQuery = new SearchQuery
            {
                SearchText = "claude",
                BasePath = baseDir,
                IncludeSubdirectories = true,
                SearchFileNameOnly = false, // Search in full paths
                CaseSensitive = false
            };

            var fullPathResult = await _searchEngine.SearchAsync(fullPathQuery);
            var fullPathResults = await CollectResults(fullPathResult);

            // Assert - Filter for files only (exclude directories like 'claude_directory')
            var fileNameFileResults = fileNameResults.Where(f => !f.IsDirectory).ToList();
            fileNameFileResults.Should().HaveCount(1, "filename-only search should find only files with 'claude' in name");
            fileNameFileResults.Should().Contain(f => f.FullPath.Contains("claude_file.txt"));

            var fullPathFileResults = fullPathResults.Where(f => !f.IsDirectory).ToList();
            fullPathFileResults.Should().HaveCount(2, "full-path search should find files with 'claude' in name or path");
            fullPathFileResults.Should().Contain(f => f.FullPath.Contains("claude_file.txt"));
            fullPathFileResults.Should().Contain(f => f.FullPath.Contains("regular_file.txt"));
        }
        finally
        {
            CleanupDirectory(baseDir);
        }
    }

    [Theory]
    [InlineData("D:\\data", "test", true)] // BasePath with subdirectories
    [InlineData("D:\\data", "test", false)] // BasePath without subdirectories
    public async Task SearchQuery_ConfigurationOptions_ShouldWork(string basePath, string searchText, bool includeSubdirectories)
    {
        // Arrange - Create a test query with all the required options
        var query = new SearchQuery
        {
            BasePath = basePath,                    // ✓ 기준경로 기능
            SearchText = searchText,                // ✓ search-text 기능
            IncludeSubdirectories = includeSubdirectories,  // ✓ subdirectory 기능
            SearchFileNameOnly = false,             // Search in both path and filename
            CaseSensitive = false
        };

        // Act & Assert - Verify all properties are set correctly
        query.BasePath.Should().Be(basePath);
        query.SearchText.Should().Be(searchText);
        query.IncludeSubdirectories.Should().Be(includeSubdirectories);
        query.SearchFileNameOnly.Should().BeFalse("should search in full paths by default");

        // Verify query validation
        var validation = query.Validate();
        validation.IsValid.Should().BeTrue("query should be valid");
    }

    private async Task WaitForIndexingComplete()
    {
        var timeout = DateTime.Now.AddSeconds(10);
        while (_searchEngine.IsIndexing && DateTime.Now < timeout)
        {
            await Task.Delay(100);
        }
    }

    private static async Task<List<FastFileItem>> CollectResults(SearchResult searchResult)
    {
        var results = new List<FastFileItem>();
        await foreach (var result in searchResult.Files)
        {
            results.Add(result);
        }
        return results;
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public void Dispose()
    {
        _searchEngine?.Dispose();
    }
}