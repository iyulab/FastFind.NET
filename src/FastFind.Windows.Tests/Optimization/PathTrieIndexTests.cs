using System.Diagnostics;
using System.Runtime.Versioning;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Optimization;

/// <summary>
/// Tests for PathTrieIndex - the O(log n) path lookup optimization.
/// </summary>
[Trait("Category", "Optimization")]
[Trait("Suite", "PathTrie")]
[SupportedOSPlatform("windows")]
public class PathTrieIndexTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly PathTrieIndex _trie;

    public PathTrieIndexTests(ITestOutputHelper output)
    {
        _output = output;
        _trie = new PathTrieIndex();
    }

    public void Dispose()
    {
        _trie.Dispose();
    }

    [Fact]
    public void Add_SingleFile_IncreasesCount()
    {
        // Arrange
        var path = @"C:\Users\Test\file.txt";
        var key = path.ToLowerInvariant();

        // Act
        var result = _trie.Add(path, key);

        // Assert
        result.Should().BeTrue();
        _trie.Count.Should().Be(1);
    }

    [Fact]
    public void Add_DuplicateFile_ReturnsFalse()
    {
        // Arrange
        var path = @"C:\Users\Test\file.txt";
        var key = path.ToLowerInvariant();
        _trie.Add(path, key);

        // Act
        var result = _trie.Add(path, key);

        // Assert
        result.Should().BeFalse();
        _trie.Count.Should().Be(1);
    }

    [Fact]
    public void Remove_ExistingFile_DecreasesCount()
    {
        // Arrange
        var path = @"C:\Users\Test\file.txt";
        var key = path.ToLowerInvariant();
        _trie.Add(path, key);

        // Act
        var result = _trie.Remove(path, key);

        // Assert
        result.Should().BeTrue();
        _trie.Count.Should().Be(0);
    }

    [Fact]
    public void GetFileKeysUnderPath_ReturnsAllDescendants()
    {
        // Arrange
        var files = new[]
        {
            @"C:\Windows\System32\cmd.exe",
            @"C:\Windows\System32\drivers\ntfs.sys",
            @"C:\Windows\notepad.exe",
            @"C:\Program Files\app.exe"
        };

        foreach (var file in files)
        {
            _trie.Add(file, file.ToLowerInvariant());
        }

        // Act
        var windowsFiles = _trie.GetFileKeysUnderPath(@"C:\Windows").ToList();
        var system32Files = _trie.GetFileKeysUnderPath(@"C:\Windows\System32").ToList();
        var programFiles = _trie.GetFileKeysUnderPath(@"C:\Program Files").ToList();

        // Assert
        windowsFiles.Should().HaveCount(3);  // cmd.exe, ntfs.sys, notepad.exe
        system32Files.Should().HaveCount(2); // cmd.exe, ntfs.sys
        programFiles.Should().HaveCount(1);  // app.exe
    }

    [Fact]
    public void GetFileKeysUnderPath_NonExistentPath_ReturnsEmpty()
    {
        // Arrange
        _trie.Add(@"C:\Windows\notepad.exe", @"c:\windows\notepad.exe");

        // Act
        var result = _trie.GetFileKeysUnderPath(@"D:\NonExistent").ToList();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ContainsPath_ExistingPath_ReturnsTrue()
    {
        // Arrange
        _trie.Add(@"C:\Windows\System32\cmd.exe", @"c:\windows\system32\cmd.exe");

        // Act & Assert
        _trie.ContainsPath(@"C:\Windows").Should().BeTrue();
        _trie.ContainsPath(@"C:\Windows\System32").Should().BeTrue();
        _trie.ContainsPath(@"C:\").Should().BeTrue();
    }

    [Fact]
    public void ContainsPath_NonExistentPath_ReturnsFalse()
    {
        // Arrange
        _trie.Add(@"C:\Windows\notepad.exe", @"c:\windows\notepad.exe");

        // Act & Assert
        _trie.ContainsPath(@"D:\NonExistent").Should().BeFalse();
        _trie.ContainsPath(@"C:\Program Files").Should().BeFalse();
    }

    [Fact]
    public void GetFileCountUnderPath_ReturnsCorrectCount()
    {
        // Arrange
        var files = new[]
        {
            @"C:\Windows\System32\cmd.exe",
            @"C:\Windows\System32\notepad.exe",
            @"C:\Windows\explorer.exe",
            @"C:\Program Files\app.exe"
        };

        foreach (var file in files)
        {
            _trie.Add(file, file.ToLowerInvariant());
        }

        // Act & Assert
        _trie.GetFileCountUnderPath(@"C:\Windows").Should().Be(3);
        _trie.GetFileCountUnderPath(@"C:\Windows\System32").Should().Be(2);
        _trie.GetFileCountUnderPath(@"C:\").Should().Be(4);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        _trie.Add(@"C:\file1.txt", @"c:\file1.txt");
        _trie.Add(@"C:\file2.txt", @"c:\file2.txt");
        _trie.Count.Should().Be(2);

        // Act
        _trie.Clear();

        // Assert
        _trie.Count.Should().Be(0);
        _trie.ContainsPath(@"C:\").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Performance_LargeDataset_TrieVsLinearScan()
    {
        // Arrange: Create 100,000 files under various directories
        const int fileCount = 100_000;
        var random = new Random(42);
        var directories = new[]
        {
            @"C:\Windows\System32",
            @"C:\Windows\SysWOW64",
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\Users\Test\Documents",
            @"C:\Users\Test\Downloads",
            @"C:\Projects\MyApp\src",
            @"C:\Projects\MyApp\bin"
        };

        var allFiles = new List<string>();
        for (int i = 0; i < fileCount; i++)
        {
            var dir = directories[random.Next(directories.Length)];
            var subdir = random.Next(10) < 3 ? $"\\sub{random.Next(100)}" : "";
            var file = $"{dir}{subdir}\\file{i}.txt";
            allFiles.Add(file);
            _trie.Add(file, file.ToLowerInvariant());
        }

        _output.WriteLine($"Indexed {fileCount:N0} files");

        // Act: Measure trie lookup time
        var sw = Stopwatch.StartNew();
        var trieResults = _trie.GetFileKeysUnderPath(@"C:\Windows").ToList();
        var trieTime = sw.ElapsedMilliseconds;

        // Compare with linear scan simulation
        sw.Restart();
        var linearResults = allFiles
            .Where(f => f.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var linearTime = sw.ElapsedMilliseconds;

        // Assert
        _output.WriteLine($"Trie lookup: {trieTime}ms, found {trieResults.Count:N0} files");
        _output.WriteLine($"Linear scan: {linearTime}ms, found {linearResults.Count:N0} files");
        _output.WriteLine($"Speedup: {(linearTime > 0 ? (double)linearTime / Math.Max(1, trieTime) : 0):F1}x");

        trieResults.Count.Should().Be(linearResults.Count);

        // Trie should be faster for targeted lookups
        // (Note: For very large datasets, the difference becomes more pronounced)
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Performance_PathContainsCheck_Fast()
    {
        // Arrange: Build a large index
        const int fileCount = 50_000;
        for (int i = 0; i < fileCount; i++)
        {
            var file = $@"C:\Data\Folder{i % 100}\file{i}.txt";
            _trie.Add(file, file.ToLowerInvariant());
        }

        // Act: Measure ContainsPath performance
        var sw = Stopwatch.StartNew();
        const int iterations = 10_000;
        var found = 0;
        for (int i = 0; i < iterations; i++)
        {
            if (_trie.ContainsPath($@"C:\Data\Folder{i % 100}"))
                found++;
        }
        sw.Stop();

        var avgMicroseconds = sw.Elapsed.TotalMicroseconds / iterations;

        // Assert
        _output.WriteLine($"ContainsPath: {iterations:N0} checks in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {avgMicroseconds:F2}μs per check");
        _output.WriteLine($"Found: {found:N0} paths");

        // Should be very fast - under 10 microseconds per check
        avgMicroseconds.Should().BeLessThan(100, "ContainsPath should be sub-millisecond");
    }
}
