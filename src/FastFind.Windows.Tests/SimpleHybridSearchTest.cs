using FastFind.Models;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FastFind.Windows.Tests;

/// <summary>
/// Simple test to verify hybrid search functionality
/// </summary>
public class SimpleHybridSearchTest : IDisposable
{
    private readonly ISearchEngine _searchEngine;
    private string _testRootDir = string.Empty;

    public SimpleHybridSearchTest()
    {
        WindowsRegistration.EnsureRegistered();
        _searchEngine = WindowsSearchEngine.CreateWindowsSearchEngine();
    }

    [Fact]
    public async Task HybridSearch_ShouldFindFilesWithoutIndexing()
    {
        // Arrange - Create test directory structure
        _testRootDir = Path.Combine(Path.GetTempPath(), "HybridTest", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testRootDir);

        var testFile = Path.Combine(_testRootDir, "claude_test.txt");
        await File.WriteAllTextAsync(testFile, "Test file content");

        Console.WriteLine($"Created test file: {testFile}");
        Console.WriteLine($"File exists: {File.Exists(testFile)}");

        // Act - Search WITHOUT starting indexing (should trigger filesystem fallback)
        var query = new SearchQuery
        {
            SearchText = "claude",
            BasePath = _testRootDir,
            IncludeSubdirectories = true,
            SearchFileNameOnly = true,
            CaseSensitive = false,
            MaxResults = 10
        };

        Console.WriteLine($"Searching in: {_testRootDir}");
        Console.WriteLine($"Index count before search: {_searchEngine.TotalIndexedFiles}");

        var result = await _searchEngine.SearchAsync(query);
        var foundFiles = await CollectResults(result);

        // Assert
        Console.WriteLine($"Search completed - found {foundFiles.Count} files");
        foreach (var file in foundFiles)
        {
            Console.WriteLine($"  - Found: {file.Name} at {file.DirectoryPath}");
        }

        foundFiles.Should().NotBeEmpty("hybrid search should find files via filesystem fallback");
        foundFiles.Should().Contain(f => f.Name.Contains("claude", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<FastFileItem>> CollectResults(SearchResult searchResult)
    {
        Console.WriteLine($"[DEBUG] CollectResults - Starting to collect results from SearchResult.Files");
        var results = new List<FastFileItem>();
        var collectedCount = 0;
        await foreach (var result in searchResult.Files)
        {
            collectedCount++;
            Console.WriteLine($"[DEBUG] CollectResults - Collected #{collectedCount}: {result.Name}");
            results.Add(result);
        }
        Console.WriteLine($"[DEBUG] CollectResults - Completed, collected {results.Count} results");
        return results;
    }

    public void Dispose()
    {
        _searchEngine?.Dispose();

        if (!string.IsNullOrEmpty(_testRootDir) && Directory.Exists(_testRootDir))
        {
            try
            {
                Directory.Delete(_testRootDir, true);
                Console.WriteLine($"Cleaned up test directory: {_testRootDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not clean up test directory: {ex.Message}");
            }
        }
    }
}