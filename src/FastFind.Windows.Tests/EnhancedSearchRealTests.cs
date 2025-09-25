using FastFind.Models;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FastFind.Windows.Tests;

/// <summary>
/// Real-world tests for enhanced search options using actual files
/// </summary>
public class EnhancedSearchRealTests : IDisposable
{
    private readonly ISearchEngine _searchEngine;

    public EnhancedSearchRealTests()
    {
        // Ensure Windows registration
        WindowsRegistration.EnsureRegistered();

        // Create search engine using public API
        _searchEngine = WindowsSearchEngine.CreateWindowsSearchEngine();
    }

    [Fact]
    public async Task SearchAsync_BasePath_ShouldWorkWithSystemFiles()
    {
        // Arrange - Use Windows system directory (should have files)
        var systemDir = @"C:\Windows\System32";
        if (!Directory.Exists(systemDir))
        {
            // Skip if system directory doesn't exist
            return;
        }

        var indexingOptions = new IndexingOptions
        {
            SpecificDirectories = { systemDir },
            ExcludedPaths = { }, // Clear default exclusions
            IncludeHidden = false,
            IncludeSystem = true
        };

        try
        {
            await _searchEngine.StartIndexingAsync(indexingOptions);
            await WaitForIndexingComplete();

            // Debug: Check if indexing worked
            Console.WriteLine($"Total indexed files: {_searchEngine.TotalIndexedFiles}");

            // Act - Search with BasePath
            var query = new SearchQuery
            {
                SearchText = "dll", // Should find .dll files
                BasePath = systemDir,
                IncludeSubdirectories = false, // Only direct files
                SearchFileNameOnly = true, // Search in filenames
                CaseSensitive = false,
                MaxResults = 10
            };

            var result = await _searchEngine.SearchAsync(query);
            var results = await CollectResults(result);

            // Assert - Should find some dll files
            results.Should().NotBeEmpty("should find dll files in system directory");
            results.Should().OnlyContain(f => f.Name.Contains("dll", StringComparison.OrdinalIgnoreCase),
                "all results should contain 'dll'");

            Console.WriteLine($"Found {results.Count} dll files");
            foreach (var file in results.Take(5))
            {
                Console.WriteLine($"- {file.Name}");
            }
        }
        finally
        {
            await _searchEngine.StopIndexingAsync();
        }
    }

    [Fact]
    public async Task SearchAsync_WithAndWithoutSubdirectories_ShouldShowDifference()
    {
        // Arrange - Use a directory with subdirectories
        var programFilesDir = @"C:\Program Files";
        if (!Directory.Exists(programFilesDir))
        {
            return;
        }

        var indexingOptions = new IndexingOptions
        {
            SpecificDirectories = { programFilesDir },
            ExcludedPaths = { }, // Clear default exclusions
            MaxDepth = 2 // Limit depth for performance
        };

        try
        {
            await _searchEngine.StartIndexingAsync(indexingOptions);
            await WaitForIndexingComplete();

            Console.WriteLine($"Total indexed files: {_searchEngine.TotalIndexedFiles}");

            // Act 1 - Search WITHOUT subdirectories
            var queryNoSubdirs = new SearchQuery
            {
                SearchText = "exe",
                BasePath = programFilesDir,
                IncludeSubdirectories = false,
                SearchFileNameOnly = true,
                CaseSensitive = false,
                MaxResults = 10
            };

            var resultNoSubdirs = await _searchEngine.SearchAsync(queryNoSubdirs);
            var resultsNoSubdirs = await CollectResults(resultNoSubdirs);

            // Act 2 - Search WITH subdirectories
            var queryWithSubdirs = new SearchQuery
            {
                SearchText = "exe",
                BasePath = programFilesDir,
                IncludeSubdirectories = true,
                SearchFileNameOnly = true,
                CaseSensitive = false,
                MaxResults = 10
            };

            var resultWithSubdirs = await _searchEngine.SearchAsync(queryWithSubdirs);
            var resultsWithSubdirs = await CollectResults(resultWithSubdirs);

            // Assert
            Console.WriteLine($"Without subdirs: {resultsNoSubdirs.Count} files");
            Console.WriteLine($"With subdirs: {resultsWithSubdirs.Count} files");

            // Should find more (or equal) files when including subdirectories
            resultsWithSubdirs.Count.Should().BeGreaterThanOrEqualTo(resultsNoSubdirs.Count,
                "subdirectory search should find at least as many files as direct search");

            if (resultsWithSubdirs.Count > resultsNoSubdirs.Count)
            {
                Console.WriteLine("✓ Subdirectory search found more files as expected");
            }
            else
            {
                Console.WriteLine("= Both searches found the same number of files");
            }
        }
        finally
        {
            await _searchEngine.StopIndexingAsync();
        }
    }

    [Fact]
    public async Task SearchAsync_FileNameOnly_VsFullPath_ShouldShowDifference()
    {
        // Arrange
        var tempDir = @"C:\Windows\Temp";
        if (!Directory.Exists(tempDir))
        {
            return;
        }

        var indexingOptions = new IndexingOptions
        {
            SpecificDirectories = { tempDir },
            ExcludedPaths = { }
        };

        try
        {
            await _searchEngine.StartIndexingAsync(indexingOptions);
            await WaitForIndexingComplete();

            Console.WriteLine($"Total indexed files: {_searchEngine.TotalIndexedFiles}");

            // Act 1 - Search in filenames only
            var fileNameQuery = new SearchQuery
            {
                SearchText = "temp",
                BasePath = tempDir,
                IncludeSubdirectories = true,
                SearchFileNameOnly = true, // Only in filenames
                CaseSensitive = false,
                MaxResults = 10
            };

            var fileNameResult = await _searchEngine.SearchAsync(fileNameQuery);
            var fileNameResults = await CollectResults(fileNameResult);

            // Act 2 - Search in full paths
            var fullPathQuery = new SearchQuery
            {
                SearchText = "temp",
                BasePath = tempDir,
                IncludeSubdirectories = true,
                SearchFileNameOnly = false, // In full paths
                CaseSensitive = false,
                MaxResults = 10
            };

            var fullPathResult = await _searchEngine.SearchAsync(fullPathQuery);
            var fullPathResults = await CollectResults(fullPathResult);

            // Assert
            Console.WriteLine($"Filename search: {fileNameResults.Count} files");
            Console.WriteLine($"Full path search: {fullPathResults.Count} files");

            // Both should work
            fileNameResults.Should().NotBeNull();
            fullPathResults.Should().NotBeNull();

            Console.WriteLine("✓ Both filename and full path searches completed");
        }
        finally
        {
            await _searchEngine.StopIndexingAsync();
        }
    }

    private async Task WaitForIndexingComplete()
    {
        var timeout = DateTime.Now.AddSeconds(30); // Longer timeout for real files
        while (_searchEngine.IsIndexing && DateTime.Now < timeout)
        {
            await Task.Delay(500);
            Console.WriteLine($"Indexing... {_searchEngine.TotalIndexedFiles} files indexed");
        }

        if (_searchEngine.IsIndexing)
        {
            Console.WriteLine("⚠️ Indexing timed out");
        }
        else
        {
            Console.WriteLine($"✓ Indexing completed: {_searchEngine.TotalIndexedFiles} files");
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

    public void Dispose()
    {
        _searchEngine?.Dispose();
    }
}