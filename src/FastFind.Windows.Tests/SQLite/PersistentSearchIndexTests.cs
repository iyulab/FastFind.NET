using FastFind.Interfaces;
using FastFind.Models;
using FastFind.SQLite;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.SQLite;

/// <summary>
/// Tests for the index that answers queries from a persistence store instead of from memory.
/// </summary>
[Trait("Category", "SQLite")]
public class PersistentSearchIndexTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _databasePath;
    private SqlitePersistence? _persistence;
    private PersistentSearchIndex? _index;

    private static readonly DateTime Timestamp = new(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    public PersistentSearchIndexTests(ITestOutputHelper output)
    {
        _output = output;
        _databasePath = Path.Combine(Path.GetTempPath(), $"fastfind-pidx-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _persistence = SqlitePersistence.Create(_databasePath);
        await _persistence.InitializeAsync();
        _index = new PersistentSearchIndex(_persistence);
        await _index.AddBatchAsync(Corpus());
    }

    public async Task DisposeAsync()
    {
        if (_index is not null) await _index.DisposeAsync();
        if (_persistence is not null) await _persistence.DisposeAsync();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); } catch (IOException) { }
        }
    }

    private static List<FastFileItem> Corpus() =>
    [
        Item(@"C:\work\notes.txt", 100),
        Item(@"C:\work\Report.docx", 5_000),
        Item(@"C:\work\sub\build.log", 900),
        Item(@"C:\work\sub\Program.cs", 2_000),
        Item(@"C:\temp\notes.txt", 50),
    ];

    private static FastFileItem Item(string fullPath, long size)
    {
        var name = Path.GetFileName(fullPath);
        return new FastFileItem(
            fullPath, name, Path.GetDirectoryName(fullPath)!, Path.GetExtension(name),
            size, Timestamp, Timestamp, Timestamp, FileAttributes.Normal, fullPath[0]);
    }

    [Fact]
    public void The_Index_Should_Retain_Nothing_Itself()
    {
        // The reason a store-backed index exists: resident cost does not grow with the corpus.
        _index!.MemoryUsage.Should().Be(0);
    }

    [Fact]
    public void Count_And_Readiness_Should_Come_From_The_Store()
    {
        _index!.Count.Should().Be(5);
        _index.IsReady.Should().BeTrue();
        _index.Persistence.Should().BeSameAs(_persistence);
    }

    [Fact]
    public async Task Statistics_Should_Report_Persistence_As_Enabled_And_No_Managed_Footprint()
    {
        var stats = await _index!.GetStatisticsAsync();

        stats.TotalItems.Should().Be(5);
        stats.PersistenceEnabled.Should().BeTrue();
        stats.MemoryUsageBytes.Should().Be(0);
    }

    [Fact]
    public async Task Search_Should_Return_Exactly_What_The_Evaluator_Accepts()
    {
        // Same invariant the provider is held to, asserted through the index that consumers see.
        var queries = new SearchQuery[]
        {
            new() { SearchText = "notes" },
            new() { SearchText = "port", SearchFileNameOnly = true },
            new() { SearchText = "Report", CaseSensitive = true },
            new() { SearchText = "*.cs" },
            new() { ExtensionFilter = "txt" },
            new() { BasePath = @"C:\work", IncludeSubdirectories = false },
            new() { MinSize = 1_000 },
            new() { ExcludedPaths = { @"C:\temp" } },
        };

        foreach (var query in queries)
        {
            var matcher = SearchQueryEvaluator.CreateTextMatcher(query);
            var expected = Corpus()
                .Where(item => SearchQueryEvaluator.Matches(item, query, matcher))
                .Select(item => item.FullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var actual = new List<string>();
            await foreach (var item in _index!.SearchAsync(query)) actual.Add(item.FullPath);
            actual.Sort(StringComparer.OrdinalIgnoreCase);

            _output.WriteLine($"'{query.SearchText}' + filters -> {actual.Count}");
            actual.Should().Equal(expected, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Get_Contains_And_GetByDirectory_Should_Read_Through_To_The_Store()
    {
        (await _index!.GetAsync(@"C:\work\notes.txt")).Should().NotBeNull();
        (await _index.GetAsync(@"C:\work\absent.txt")).Should().BeNull();

        (await _index.ContainsAsync(@"C:\work\Report.docx")).Should().BeTrue();
        (await _index.ContainsAsync(@"C:\work\absent.txt")).Should().BeFalse();

        var direct = new List<string>();
        await foreach (var item in _index.GetByDirectoryAsync(@"C:\work")) direct.Add(item.Name);
        direct.Should().BeEquivalentTo(["notes.txt", "Report.docx"]);

        var recursive = new List<string>();
        await foreach (var item in _index.GetByDirectoryAsync(@"C:\work", recursive: true)) recursive.Add(item.Name);
        recursive.Should().HaveCount(4);
    }

    [Fact]
    public async Task Writes_Should_Reach_The_Store_Immediately()
    {
        await _index!.AddAsync(Item(@"C:\work\added.txt", 1));
        _index.Count.Should().Be(6);
        (await _persistence!.ExistsAsync(@"C:\work\added.txt")).Should().BeTrue();

        (await _index.RemoveAsync(@"C:\work\added.txt")).Should().BeTrue();
        (await _persistence.ExistsAsync(@"C:\work\added.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Load_And_Save_Should_Be_No_Ops_That_Report_The_Stored_Count()
    {
        // Nothing to transfer in either direction: reads and writes both go through the store.
        (await _index!.LoadFromPersistenceAsync()).Should().Be(5);
        (await _index.SaveToPersistenceAsync()).Should().Be(5);
        _index.Count.Should().Be(5);
    }

    [Fact]
    public async Task An_Invalid_Query_Should_Throw()
    {
        // Both backends reject an invalid query the same way. They used to return an empty result,
        // which a caller could not tell apart from a query that simply matched nothing.
        var invalid = new SearchQuery { MinSize = 100, MaxSize = 10 };
        invalid.Validate().IsValid.Should().BeFalse("the test needs a query the library rejects");

        var act = async () =>
        {
            await foreach (var _ in _index!.SearchAsync(invalid)) { }
        };

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*Minimum size cannot be greater than maximum size*");
    }

    [Fact]
    public async Task Disposing_Should_Not_Dispose_A_Store_It_Does_Not_Own()
    {
        var index = new PersistentSearchIndex(_persistence!, ownsPersistence: false);

        await index.DisposeAsync();

        _persistence!.IsReady.Should().BeTrue("the caller still owns the store");
        index.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task Disposing_Should_Dispose_A_Store_It_Owns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fastfind-owned-{Guid.NewGuid():N}.db");
        var owned = SqlitePersistence.Create(path);
        await owned.InitializeAsync();

        var index = new PersistentSearchIndex(owned, ownsPersistence: true);
        await index.DisposeAsync();

        owned.IsReady.Should().BeFalse();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Use_After_Dispose_Should_Throw()
    {
        var index = new PersistentSearchIndex(_persistence!);
        await index.DisposeAsync();

        var act = () => index.GetAsync(@"C:\work\notes.txt");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Construction_Should_Require_A_Store()
    {
        var act = () => new PersistentSearchIndex(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
