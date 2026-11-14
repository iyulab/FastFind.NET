using AwesomeAssertions;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// When a query is also answered from the file system: only for a location the index does not cover.
/// </summary>
/// <remarks>
/// It used to be answered from the disk too whenever the index held fewer than ten items or the query found fewer
/// than five matches, so the same query returned different things depending on how many matches there happened to
/// be — and what a covered location returned while it was still being indexed was whatever a second mechanism
/// found. How few matches there are is no reason to scan the disk.
/// </remarks>
[Trait("Category", "Functional")]
public class FallbackRuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fastfind-fallbackrule-" + Guid.NewGuid().ToString("N")[..8]);

    public FallbackRuleTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "onlyondisk_1.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "onlyondisk_2.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static FastFileItem Indexed(string directory, string name) => new(
        Path.Combine(directory, name), name, directory, Path.GetExtension(name),
        size: 10, created: DateTime.UtcNow, modified: DateTime.UtcNow, accessed: DateTime.UtcNow,
        attributes: FileAttributes.Archive, driveLetter: directory[0]);

    private static async Task<WindowsSearchIndex> IndexWithAsync(params FastFileItem[] items)
    {
        var index = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
        await index.AddBatchAsync(items);
        return index;
    }

    private static async Task<List<string>> SearchAsync(WindowsSearchIndex index, SearchQuery query)
    {
        var found = new List<string>();
        await foreach (var item in index.SearchAsync(query)) found.Add(item.FullPath);
        return found;
    }

    [Fact]
    public async Task A_Covered_Location_With_Few_Matches_Should_Not_Be_Scanned()
    {
        var index = await IndexWithAsync(Indexed(_root, "indexed_1.txt"), Indexed(_root, "indexed_2.txt"));
        var query = new SearchQuery { BasePath = _root, SearchText = "onlyondisk", SearchFileNameOnly = true, MaxResults = 100 };

        var found = await SearchAsync(index, query);

        found.Should().BeEmpty("the index covers the location; what it does not hold is not what a query returns");
        index.FilesystemScans.Should().Be(0);
    }

    [Fact]
    public async Task A_Small_Unscoped_Index_Should_Answer_From_Itself()
    {
        var index = await IndexWithAsync(Indexed(_root, "indexed_1.txt"));
        var query = new SearchQuery { SearchText = "onlyondisk", SearchFileNameOnly = true, MaxResults = 100 };

        (await SearchAsync(index, query)).Should().BeEmpty();
        index.FilesystemScans.Should().Be(0, "a small index is no reason to scan the disk");
    }

    [Fact]
    public async Task A_Location_The_Index_Does_Not_Cover_Should_Be_Scanned()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "fastfind-fallbackrule-elsewhere");
        var index = await IndexWithAsync(Indexed(elsewhere, "indexed_1.txt"));
        var query = new SearchQuery { BasePath = _root, SearchText = "onlyondisk", SearchFileNameOnly = true, MaxResults = 100 };

        var found = await SearchAsync(index, query);

        found.Should().HaveCount(2).And.OnlyContain(p => p.Contains("onlyondisk"));
        index.FilesystemScans.Should().Be(1);
    }

    [Fact]
    public async Task An_Empty_Index_Should_Cover_Nothing_And_Fall_Back()
    {
        var index = await IndexWithAsync();
        var query = new SearchQuery { BasePath = _root, SearchText = "onlyondisk", SearchFileNameOnly = true, MaxResults = 100 };

        (await SearchAsync(index, query)).Should().HaveCount(2);
        index.FilesystemScans.Should().Be(1);
    }

    [Fact]
    public async Task The_Engines_Statistics_Should_Count_The_Searches_The_File_System_Answered()
    {
        var provider = new ScriptedFileSystemProvider();
        provider.Items.Add(ScriptedFileSystemProvider.File(@"C:\scripted\a.txt"));
        var index = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
        using var engine = new WindowsSearchEngineImpl(
            provider, index, new WindowsSearchEngineOptions(), NullLogger<WindowsSearchEngineImpl>.Instance);
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { @"C:\scripted" },
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing) await Task.Delay(10, timeout.Token);

        await engine.SearchAsync(new SearchQuery { SearchText = "a", SearchFileNameOnly = true });                          // the index answers
        await engine.SearchAsync(new SearchQuery { BasePath = _root, SearchText = "onlyondisk", SearchFileNameOnly = true }); // an uncovered scope

        var stats = await engine.GetSearchStatisticsAsync();
        stats.TotalSearches.Should().Be(2);
        stats.FileSystemScans.Should().Be(1);
        stats.IndexHits.Should().Be(1, "a search the file system also answered is not an index hit");
    }
}
