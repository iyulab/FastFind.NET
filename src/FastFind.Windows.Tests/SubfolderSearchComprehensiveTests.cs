using FastFind.Models;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests;

/// <summary>
/// Comprehensive tests for BasePath + IncludeSubdirectories functionality
/// Tests verify that subfolder search works correctly in split panel scenarios
/// </summary>
public class SubfolderSearchComprehensiveTests : IDisposable
{
    private readonly ISearchEngine _searchEngine;
    private readonly ITestOutputHelper _output;
    private readonly string _testRootPath;
    private readonly string _panel1Path;
    private readonly string _panel2Path;
    private readonly List<string> _createdPaths = new();

    public SubfolderSearchComprehensiveTests(ITestOutputHelper output)
    {
        _output = output;

        // Create search engine
        WindowsRegistration.EnsureRegistered();
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _searchEngine = WindowsSearchEngine.CreateWindowsSearchEngine(loggerFactory);

        // Setup test directory structure
        _testRootPath = Path.Combine(Path.GetTempPath(), $"SubfolderSearchTest_{Guid.NewGuid():N}");
        _panel1Path = Path.Combine(_testRootPath, "Panel1");
        _panel2Path = Path.Combine(_testRootPath, "Panel2");

        SetupTestStructure();

        _output.WriteLine($"Test root: {_testRootPath}");
        _output.WriteLine($"Panel1: {_panel1Path}");
        _output.WriteLine($"Panel2: {_panel2Path}");
    }

    private void SetupTestStructure()
    {
        // Create Panel1 structure:
        // Panel1/
        //   test.txt
        //   SubA/
        //     test_a1.txt
        //     test_a2.txt
        //   SubB/
        //     test_b1.txt
        //     Deep/
        //       test_deep.txt
        Directory.CreateDirectory(_panel1Path);
        CreateFile(_panel1Path, "test.txt", "root file");
        CreateFile(_panel1Path, "SubA", "test_a1.txt", "file in SubA");
        CreateFile(_panel1Path, "SubA", "test_a2.txt", "another in SubA");
        CreateFile(_panel1Path, "SubB", "test_b1.txt", "file in SubB");
        CreateFile(_panel1Path, "SubB", "Deep", "test_deep.txt", "deep nested file");

        // Create Panel2 structure:
        // Panel2/
        //   data.txt
        //   Level1/
        //     data_l1.txt
        Directory.CreateDirectory(_panel2Path);
        CreateFile(_panel2Path, "data.txt", "panel2 root");
        CreateFile(_panel2Path, "Level1", "data_l1.txt", "panel2 level1");

        _output.WriteLine($"Created {_createdPaths.Count} test files");
    }

    private void CreateFile(params string[] pathParts)
    {
        var content = pathParts[^1];
        var pathWithoutContent = pathParts[..^1];
        var fullPath = Path.Combine(pathWithoutContent);

        var directory = pathWithoutContent.Length > 1
            ? Path.Combine(pathWithoutContent[..^1])
            : pathWithoutContent[0];

        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(pathWithoutContent);
        File.WriteAllText(filePath, content);
        _createdPaths.Add(filePath);
        _output.WriteLine($"  Created: {filePath}");
    }

    [Fact]
    public async Task BasePath_WithSubdirectories_ShouldFindAllFiles()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "test",
            BasePath = _panel1Path,
            IncludeSubdirectories = true,
            SearchFileNameOnly = true,
            CaseSensitive = false,
            IncludeFiles = true,
            IncludeDirectories = false
        };

        // Act
        _output.WriteLine($"\n[TEST] Searching for '{query.SearchText}' in {query.BasePath} (IncludeSubdirectories={query.IncludeSubdirectories})");
        var result = await _searchEngine.SearchAsync(query);
        var files = await CollectFiles(result);

        // Assert
        _output.WriteLine($"Found {files.Count} files:");
        foreach (var file in files)
        {
            _output.WriteLine($"  - {file.FullPath}");
        }

        files.Should().NotBeEmpty("should find test files in Panel1");
        files.Should().HaveCountGreaterThanOrEqualTo(5, "should find all 5 test files in Panel1 and subdirectories");

        // Verify all found files are in Panel1 path
        files.Should().OnlyContain(f => f.FullPath.StartsWith(_panel1Path, StringComparison.OrdinalIgnoreCase),
            "all found files should be within Panel1 path");

        // Verify files from subdirectories are found
        files.Should().Contain(f => f.Name.Contains("test_a1"), "should find file in SubA");
        files.Should().Contain(f => f.Name.Contains("test_b1"), "should find file in SubB");
        files.Should().Contain(f => f.Name.Contains("test_deep"), "should find deeply nested file");
    }

    [Fact]
    public async Task BasePath_WithoutSubdirectories_ShouldFindOnlyRootFiles()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "test",
            BasePath = _panel1Path,
            IncludeSubdirectories = false,  // Only root level
            SearchFileNameOnly = true,
            CaseSensitive = false,
            IncludeFiles = true,
            IncludeDirectories = false
        };

        // Act
        _output.WriteLine($"\n[TEST] Searching for '{query.SearchText}' in {query.BasePath} (IncludeSubdirectories={query.IncludeSubdirectories})");
        var result = await _searchEngine.SearchAsync(query);
        var files = await CollectFiles(result);

        // Assert
        _output.WriteLine($"Found {files.Count} files:");
        foreach (var file in files)
        {
            _output.WriteLine($"  - {file.FullPath}");
        }

        files.Should().NotBeEmpty("should find test.txt in Panel1 root");
        files.Should().HaveCount(1, "should only find the root-level test.txt");

        // Verify all files are in root directory (not in subdirectories)
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file.FullPath);
            directory.Should().NotBeNull();
            directory!.ToLowerInvariant().Should().Be(_panel1Path.ToLowerInvariant(),
                "file should be directly in Panel1 root, not in subdirectories");
        }

        files.Should().Contain(f => f.Name.Equals("test.txt", StringComparison.OrdinalIgnoreCase), "should find test.txt");
    }

    [Fact]
    public async Task TwoPanels_ShouldReturnSeparateResults()
    {
        // Arrange - Panel1 query
        var panel1Query = new SearchQuery
        {
            SearchText = "test",
            BasePath = _panel1Path,
            IncludeSubdirectories = true,
            SearchFileNameOnly = true,
            CaseSensitive = false,
            IncludeFiles = true
        };

        // Arrange - Panel2 query
        var panel2Query = new SearchQuery
        {
            SearchText = "data",
            BasePath = _panel2Path,
            IncludeSubdirectories = true,
            SearchFileNameOnly = true,
            CaseSensitive = false,
            IncludeFiles = true
        };

        // Act
        _output.WriteLine($"\n[TEST] Panel1 search for '{panel1Query.SearchText}':");
        var panel1Result = await _searchEngine.SearchAsync(panel1Query);
        var panel1Files = await CollectFiles(panel1Result);

        _output.WriteLine($"\n[TEST] Panel2 search for '{panel2Query.SearchText}':");
        var panel2Result = await _searchEngine.SearchAsync(panel2Query);
        var panel2Files = await CollectFiles(panel2Result);

        // Assert
        _output.WriteLine($"\nPanel1 found {panel1Files.Count} files:");
        foreach (var file in panel1Files)
        {
            _output.WriteLine($"  - {file.FullPath}");
        }

        _output.WriteLine($"\nPanel2 found {panel2Files.Count} files:");
        foreach (var file in panel2Files)
        {
            _output.WriteLine($"  - {file.FullPath}");
        }

        // Panel1 assertions
        panel1Files.Should().NotBeEmpty("Panel1 should find test files");
        panel1Files.Should().OnlyContain(f => f.FullPath.StartsWith(_panel1Path, StringComparison.OrdinalIgnoreCase),
            "Panel1 results should only contain files from Panel1 path");
        panel1Files.Should().NotContain(f => f.Name.Contains("data"),
            "Panel1 should not return Panel2 files");

        // Panel2 assertions
        panel2Files.Should().NotBeEmpty("Panel2 should find data files");
        panel2Files.Should().OnlyContain(f => f.FullPath.StartsWith(_panel2Path, StringComparison.OrdinalIgnoreCase),
            "Panel2 results should only contain files from Panel2 path");
        panel2Files.Should().NotContain(f => f.Name.Contains("test"),
            "Panel2 should not return Panel1 files");

        // No overlap
        var allPaths = panel1Files.Select(f => f.FullPath).Concat(panel2Files.Select(f => f.FullPath));
        allPaths.Should().OnlyHaveUniqueItems("Panel1 and Panel2 should have no overlapping results");
    }

    [Fact]
    public async Task SearchWithFullPathMatching_ShouldMatchDirectoryNames()
    {
        // Arrange - Search for "SubA" which appears in directory path
        var query = new SearchQuery
        {
            SearchText = "SubA",
            BasePath = _panel1Path,
            IncludeSubdirectories = true,
            SearchFileNameOnly = false,  // Search in full path
            CaseSensitive = false,
            IncludeFiles = true,
            IncludeDirectories = false
        };

        // Act
        _output.WriteLine($"\n[TEST] Full path search for '{query.SearchText}':");
        var result = await _searchEngine.SearchAsync(query);
        var files = await CollectFiles(result);

        // Assert
        _output.WriteLine($"Found {files.Count} files:");
        foreach (var file in files)
        {
            _output.WriteLine($"  - {file.FullPath}");
        }

        files.Should().NotBeEmpty("should find files in SubA directory");
        files.Should().OnlyContain(f => f.FullPath.Contains("SubA", StringComparison.OrdinalIgnoreCase),
            "all files should have 'SubA' in their full path");
        files.Should().Contain(f => f.Name.Contains("test_a1"), "should find test_a1.txt in SubA");
        files.Should().Contain(f => f.Name.Contains("test_a2"), "should find test_a2.txt in SubA");
    }

    [Fact]
    public async Task DeepNesting_ShouldFindFilesAtAnyDepth()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "deep",
            BasePath = _panel1Path,
            IncludeSubdirectories = true,
            SearchFileNameOnly = true,
            CaseSensitive = false,
            IncludeFiles = true
        };

        // Act
        _output.WriteLine($"\n[TEST] Deep nesting search for '{query.SearchText}':");
        var result = await _searchEngine.SearchAsync(query);
        var files = await CollectFiles(result);

        // Assert
        _output.WriteLine($"Found {files.Count} files:");
        foreach (var file in files)
        {
            _output.WriteLine($"  - {file.FullPath}");
        }

        files.Should().NotBeEmpty("should find deeply nested file");
        files.Should().Contain(f => f.Name.Contains("test_deep"),
            "should find test_deep.txt in SubB/Deep/");

        // Verify the deep file is actually nested
        var deepFile = files.FirstOrDefault(f => f.Name.Contains("test_deep"));
        deepFile.Should().NotBeNull();
        deepFile!.FullPath.ToLowerInvariant().Should().Contain("subb")
            .And.Contain("deep", "file should be in SubB/Deep subdirectory");
    }

    [Fact]
    public async Task InvalidBasePath_ShouldReturnEmptyResults()
    {
        // Arrange
        var query = new SearchQuery
        {
            SearchText = "test",
            BasePath = Path.Combine(_testRootPath, "NonExistent"),
            IncludeSubdirectories = true,
            SearchFileNameOnly = true,
            CaseSensitive = false,
            IncludeFiles = true
        };

        // Act
        _output.WriteLine($"\n[TEST] Invalid path search in: {query.BasePath}");
        var result = await _searchEngine.SearchAsync(query);
        var files = await CollectFiles(result);

        // Assert
        _output.WriteLine($"Found {files.Count} files (expected 0)");
        files.Should().BeEmpty("non-existent path should return no results");
    }

    private async Task<List<FastFileItem>> CollectFiles(SearchResult result)
    {
        var files = new List<FastFileItem>();
        await foreach (var file in result.Files)
        {
            files.Add(file);
        }
        return files;
    }

    public void Dispose()
    {
        try
        {
            _searchEngine?.Dispose();

            if (Directory.Exists(_testRootPath))
            {
                Directory.Delete(_testRootPath, true);
                _output.WriteLine($"\nCleaned up test directory: {_testRootPath}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"\nCleanup error: {ex.Message}");
        }
    }
}
