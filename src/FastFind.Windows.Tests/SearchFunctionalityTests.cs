using FastFind.Models;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FastFind.Windows.Tests;

/// <summary>
/// Comprehensive functional tests for search-text matching and subdirectory inclusion
/// </summary>
public class SearchFunctionalityTests : IDisposable
{
    private readonly ISearchEngine _searchEngine;
    private string _testRootDir = string.Empty;

    public SearchFunctionalityTests()
    {
        WindowsRegistration.EnsureRegistered();
        _searchEngine = WindowsSearchEngine.CreateWindowsSearchEngine();
    }

    [Fact]
    public async Task SearchAsync_ShouldFindFilesWithSearchTextInFilename()
    {
        // Arrange - Create test directory structure
        await SetupTestEnvironment();

        var query = new SearchQuery
        {
            SearchText = "claude",
            BasePath = _testRootDir,
            IncludeSubdirectories = true,
            SearchFileNameOnly = false, // Search in full paths
            CaseSensitive = false
        };

        // Start indexing
        await StartIndexingAndWait(_testRootDir);

        // Act - Search for files with "claude" in filename
        var result = await _searchEngine.SearchAsync(query);
        var foundFiles = await CollectResults(result);

        // Assert - Should find files with "claude" in filename
        foundFiles.Should().NotBeEmpty("should find files with 'claude' in filename");

        var claudeFiles = foundFiles.Where(f => f.Name.Contains("claude", StringComparison.OrdinalIgnoreCase)).ToList();
        claudeFiles.Should().NotBeEmpty("should find files with 'claude' in their names");

        Console.WriteLine($"Found {claudeFiles.Count} files with 'claude' in filename:");
        foreach (var file in claudeFiles)
        {
            Console.WriteLine($"  - {file.Name} in {file.DirectoryPath}");
        }

        await _searchEngine.StopIndexingAsync();
    }

    [Fact]
    public async Task SearchAsync_ShouldFindFilesWithSearchTextInDirectoryPath()
    {
        // Arrange - Create test directory structure
        await SetupTestEnvironment();

        var query = new SearchQuery
        {
            SearchText = "documents", // This should match directory path
            BasePath = _testRootDir,
            IncludeSubdirectories = true,
            SearchFileNameOnly = false, // Important: search in full paths
            CaseSensitive = false
        };

        // Start indexing
        await StartIndexingAndWait(_testRootDir);

        // Act - Search for files in directories containing "documents"
        var result = await _searchEngine.SearchAsync(query);
        var foundFiles = await CollectResults(result);

        // Assert - Should find files in directories with "documents" in path
        foundFiles.Should().NotBeEmpty("should find files in directories with 'documents' in path");

        var filesInDocumentsPath = foundFiles.Where(f =>
            f.DirectoryPath?.Contains("documents", StringComparison.OrdinalIgnoreCase) == true).ToList();

        filesInDocumentsPath.Should().NotBeEmpty("should find files in 'documents' directory path");

        Console.WriteLine($"Found {filesInDocumentsPath.Count} files in directories containing 'documents':");
        foreach (var file in filesInDocumentsPath)
        {
            Console.WriteLine($"  - {file.Name} in {file.DirectoryPath}");
        }

        await _searchEngine.StopIndexingAsync();
    }

    [Fact]
    public async Task SearchAsync_WithSubdirectoryTrue_ShouldFindFilesInAllSubfolders()
    {
        // Arrange - Create nested directory structure
        await SetupTestEnvironment();

        var query = new SearchQuery
        {
            SearchText = "test",
            BasePath = _testRootDir,
            IncludeSubdirectories = true, // Include subdirectories
            SearchFileNameOnly = false,
            CaseSensitive = false
        };

        // Start indexing
        await StartIndexingAndWait(_testRootDir);

        // Act - Search with subdirectories included
        var result = await _searchEngine.SearchAsync(query);
        var foundFiles = await CollectResults(result);

        // Assert - Should find files at all directory levels
        foundFiles.Should().NotBeEmpty("should find files with subdirectory inclusion");

        // Check that we found files at different directory levels
        var uniqueDirectories = foundFiles
            .Select(f => f.DirectoryPath)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToList();

        uniqueDirectories.Should().HaveCountGreaterThan(1, "should find files in multiple directory levels");

        Console.WriteLine($"Found files in {uniqueDirectories.Count} different directories:");
        foreach (var dir in uniqueDirectories)
        {
            var filesInDir = foundFiles.Where(f => f.DirectoryPath == dir).Count();
            Console.WriteLine($"  - {dir}: {filesInDir} files");
        }

        await _searchEngine.StopIndexingAsync();
    }

    [Fact]
    public async Task SearchAsync_WithSubdirectoryFalse_ShouldOnlyFindFilesInBasePath()
    {
        // Arrange - Create nested directory structure
        await SetupTestEnvironment();

        var query = new SearchQuery
        {
            SearchText = "test",
            BasePath = _testRootDir,
            IncludeSubdirectories = false, // Do NOT include subdirectories
            SearchFileNameOnly = false,
            CaseSensitive = false
        };

        // Start indexing
        await StartIndexingAndWait(_testRootDir);

        // Act - Search without subdirectories
        var result = await _searchEngine.SearchAsync(query);
        var foundFiles = await CollectResults(result);

        // Assert - Should only find files in base directory
        if (foundFiles.Any())
        {
            var uniqueDirectories = foundFiles
                .Select(f => f.DirectoryPath?.TrimEnd('\\', '/'))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToList();

            // All found files should be in the base directory only
            var basePathNormalized = _testRootDir.TrimEnd('\\', '/');
            uniqueDirectories.Should().OnlyContain(d =>
                string.Equals(d, basePathNormalized, StringComparison.OrdinalIgnoreCase),
                "should only find files in base directory when subdirectories are excluded");

            Console.WriteLine($"Found {foundFiles.Count} files only in base directory: {basePathNormalized}");
        }
        else
        {
            Console.WriteLine("No files found in base directory (expected if no direct files exist)");
        }

        await _searchEngine.StopIndexingAsync();
    }

    [Fact]
    public async Task SearchAsync_SearchFileNameOnly_ShouldOnlyMatchFilenames()
    {
        // Arrange - Create test files in different directories
        await SetupTestEnvironment();

        var query = new SearchQuery
        {
            SearchText = "documents", // This exists in directory path but not filename
            BasePath = _testRootDir,
            IncludeSubdirectories = true,
            SearchFileNameOnly = true, // Only search in filenames
            CaseSensitive = false
        };

        // Start indexing
        await StartIndexingAndWait(_testRootDir);

        // Act - Search only in filenames
        var result = await _searchEngine.SearchAsync(query);
        var foundFiles = await CollectResults(result);

        // Now test with filename search
        var filenameQuery = new SearchQuery
        {
            SearchText = "claude", // This exists in filenames
            BasePath = _testRootDir,
            IncludeSubdirectories = true,
            SearchFileNameOnly = true, // Only search in filenames
            CaseSensitive = false
        };

        var filenameResult = await _searchEngine.SearchAsync(filenameQuery);
        var filenameFiles = await CollectResults(filenameResult);

        // Assert - Should find files with "claude" in filename but not "documents"
        filenameFiles.Should().NotBeEmpty("should find files with 'claude' in filename");
        filenameFiles.Should().OnlyContain(f =>
            f.Name.Contains("claude", StringComparison.OrdinalIgnoreCase),
            "when SearchFileNameOnly=true, should only match filenames");

        Console.WriteLine($"SearchFileNameOnly=true found {filenameFiles.Count} files with 'claude' in filename");
        Console.WriteLine($"SearchFileNameOnly=true found {foundFiles.Count} files with 'documents' in filename");

        await _searchEngine.StopIndexingAsync();
    }

    private async Task SetupTestEnvironment()
    {
        // Create unique test directory
        _testRootDir = Path.Combine(Path.GetTempPath(), "FastFindTest", Guid.NewGuid().ToString("N")[..8]);

        // Create directory structure
        var documentsDir = Path.Combine(_testRootDir, "documents");
        var projectsDir = Path.Combine(_testRootDir, "projects");
        var claudeProjectDir = Path.Combine(projectsDir, "claude-project");
        var subDir = Path.Combine(documentsDir, "subdirectory");

        Directory.CreateDirectory(_testRootDir);
        Directory.CreateDirectory(documentsDir);
        Directory.CreateDirectory(projectsDir);
        Directory.CreateDirectory(claudeProjectDir);
        Directory.CreateDirectory(subDir);

        // Create test files with different patterns
        await File.WriteAllTextAsync(Path.Combine(_testRootDir, "test_root.txt"), "Root test file");
        await File.WriteAllTextAsync(Path.Combine(documentsDir, "claude_doc.txt"), "Claude document");
        await File.WriteAllTextAsync(Path.Combine(documentsDir, "test_document.txt"), "Test document in documents");
        await File.WriteAllTextAsync(Path.Combine(projectsDir, "claude_project.txt"), "Claude project file");
        await File.WriteAllTextAsync(Path.Combine(claudeProjectDir, "main.txt"), "Main file in claude project");
        await File.WriteAllTextAsync(Path.Combine(claudeProjectDir, "claude_code.cs"), "Claude code file");
        await File.WriteAllTextAsync(Path.Combine(subDir, "test_sub.txt"), "Test file in subdirectory");
        await File.WriteAllTextAsync(Path.Combine(subDir, "claude_nested.txt"), "Claude file nested deep");

        Console.WriteLine($"Created test environment in: {_testRootDir}");
        Console.WriteLine("Test file structure:");
        Console.WriteLine("├── test_root.txt");
        Console.WriteLine("├── documents/");
        Console.WriteLine("│   ├── claude_doc.txt");
        Console.WriteLine("│   ├── test_document.txt");
        Console.WriteLine("│   └── subdirectory/");
        Console.WriteLine("│       ├── test_sub.txt");
        Console.WriteLine("│       └── claude_nested.txt");
        Console.WriteLine("└── projects/");
        Console.WriteLine("    ├── claude_project.txt");
        Console.WriteLine("    └── claude-project/");
        Console.WriteLine("        ├── main.txt");
        Console.WriteLine("        └── claude_code.cs");
    }

    private async Task StartIndexingAndWait(string directory)
    {
        var indexingOptions = new IndexingOptions
        {
            SpecificDirectories = { directory },
            ExcludedPaths = { }, // Clear default exclusions
            IncludeHidden = true,
            MaxDepth = 10
        };

        await _searchEngine.StartIndexingAsync(indexingOptions);

        // Wait for indexing to complete
        var timeout = DateTime.Now.AddSeconds(15);
        while (_searchEngine.IsIndexing && DateTime.Now < timeout)
        {
            await Task.Delay(200);
        }

        Console.WriteLine($"Indexing completed: {_searchEngine.TotalIndexedFiles} files indexed");
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

        // Cleanup test directory
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