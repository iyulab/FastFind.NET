using FastFind.Models;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FastFind.Windows.Tests;

/// <summary>
/// Tests for subdirectory search functionality
/// </summary>
public class SubdirectorySearchTests : IDisposable
{
    private readonly ISearchEngine _searchEngine;

    public SubdirectorySearchTests()
    {
        // Ensure Windows registration
        WindowsRegistration.EnsureRegistered();

        // Create search engine using public API
        _searchEngine = WindowsSearchEngine.CreateWindowsSearchEngine();
    }

    [Fact]
    public async Task SearchAsync_WithSubdirectorySearch_ShouldWorkWithExistingFiles()
    {
        // Arrange - Use real existing directories
        var tempDir = Path.GetTempPath();
        var searchDir = Path.Combine(tempDir, "FastFindTest", Guid.NewGuid().ToString("N")[..8]);
        var subDir1 = Path.Combine(searchDir, "subdir1");
        var subDir2 = Path.Combine(searchDir, "subdir2", "nested");

        try
        {
            // Create test directory structure
            Directory.CreateDirectory(searchDir);
            Directory.CreateDirectory(subDir1);
            Directory.CreateDirectory(subDir2);

            // Create test files with "claude" in the name
            var file1 = Path.Combine(searchDir, "claude_direct.txt");
            var file2 = Path.Combine(subDir1, "claude_sub1.txt");
            var file3 = Path.Combine(subDir2, "claude_nested.txt");

            await File.WriteAllTextAsync(file1, "Direct file");
            await File.WriteAllTextAsync(file2, "Subdirectory 1 file");
            await File.WriteAllTextAsync(file3, "Nested subdirectory file");

            // Start indexing
            var indexingOptions = new IndexingOptions
            {
                SpecificDirectories = { searchDir },
                ExcludedPaths = { }, // Clear default exclusions
                IncludeHidden = true // Include hidden files for testing
            };
            await _searchEngine.StartIndexingAsync(indexingOptions);

            // Wait for indexing
            var timeout = DateTime.Now.AddSeconds(10);
            while (_searchEngine.IsIndexing && DateTime.Now < timeout)
            {
                await Task.Delay(100);
            }

            // Act - Search with subdirectory inclusion
            var queryWithSubdirs = new SearchQuery
            {
                SearchText = "claude",
                SearchLocations = { searchDir },
                IncludeSubdirectories = true,
                CaseSensitive = false
            };

            var resultWithSubdirs = await _searchEngine.SearchAsync(queryWithSubdirs);
            var resultsWithSubdirs = new List<FastFileItem>();
            await foreach (var result in resultWithSubdirs.Files)
            {
                resultsWithSubdirs.Add(result);
            }

            // Act - Search without subdirectory inclusion
            var queryNoSubdirs = new SearchQuery
            {
                SearchText = "claude",
                SearchLocations = { searchDir },
                IncludeSubdirectories = false,
                CaseSensitive = false
            };

            var resultNoSubdirs = await _searchEngine.SearchAsync(queryNoSubdirs);
            var resultsNoSubdirs = new List<FastFileItem>();
            await foreach (var result in resultNoSubdirs.Files)
            {
                resultsNoSubdirs.Add(result);
            }

            // Assert
            resultsWithSubdirs.Should().HaveCount(3, "should find all files including subdirectories");
            resultsNoSubdirs.Should().HaveCount(1, "should find only direct files when subdirectories are excluded");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(searchDir))
            {
                Directory.Delete(searchDir, true);
            }
        }
    }

    public void Dispose()
    {
        _searchEngine?.Dispose();
    }
}