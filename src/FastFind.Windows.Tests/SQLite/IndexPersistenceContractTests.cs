using FastFind;
using FastFind.Models;
using FastFind.SQLite;
using FastFind.Windows;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.SQLite;

/// <summary>
/// The contract for saving and loading an index, and for a query the library rejects.
/// </summary>
/// <remarks>
/// Both used to fail quietly: <c>SaveIndexAsync</c> logged a warning and returned successfully when
/// no store was configured — which, before an engine could be composed with one, meant it could
/// never do anything — and an invalid query produced an empty result indistinguishable from a query
/// that matched nothing.
/// </remarks>
[Trait("Category", "SQLite")]
public class IndexPersistenceContractTests : IAsyncLifetime
{
    private readonly List<string> _paths = new();

    public Task InitializeAsync()
    {
        WindowsRegistration.EnsureRegistered();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var path in _paths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch (IOException) { }
            }
        }

        return Task.CompletedTask;
    }

    private async Task<SqlitePersistence> NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fastfind-contract-{Guid.NewGuid():N}.db");
        _paths.Add(path);

        var store = SqlitePersistence.Create(path);
        await store.InitializeAsync();
        return store;
    }

    private static FastFileItem Item(string fullPath) => new(
        fullPath, Path.GetFileName(fullPath), Path.GetDirectoryName(fullPath)!,
        Path.GetExtension(fullPath), 128,
        DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, fullPath[0]);

    [Fact]
    public async Task Saving_Without_A_Store_Should_Say_So_Rather_Than_Succeed()
    {
        using var engine = FastFinder.CreateSearchEngine();

        var save = async () => await engine.SaveIndexAsync();
        var load = async () => await engine.LoadIndexAsync();

        (await save.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*CreateSearchEngine(persistence)*", "the message must say how to fix it");
        await load.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Saving_And_Loading_With_A_Store_Should_Report_The_Item_Count()
    {
        await using var store = await NewStore();
        using var engine = FastFinder.CreateSearchEngine(store);

        await engine.Index!.AddBatchAsync([Item(@"C:\c\a.txt"), Item(@"C:\c\b.txt")]);

        (await engine.SaveIndexAsync()).Should().Be(2);
        (await engine.LoadIndexAsync()).Should().Be(2);
    }

    [Fact]
    public async Task An_Invalid_Query_Should_Throw_From_A_Store_Backed_Index()
    {
        await using var store = await NewStore();
        using var engine = FastFinder.CreateSearchEngine(store);

        var invalid = new SearchQuery { MinSize = 500, MaxSize = 5 };
        invalid.Validate().IsValid.Should().BeFalse("the test needs a query the library rejects");

        var act = async () =>
        {
            await foreach (var _ in engine.Index!.SearchAsync(invalid)) { }
        };

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_Invalid_Query_Should_Throw_From_An_In_Memory_Index_Too()
    {
        using var engine = FastFinder.CreateSearchEngine();

        var invalid = new SearchQuery { SearchText = "[", UseRegex = true };
        invalid.Validate().IsValid.Should().BeFalse("an unparseable regex is rejected by validation");

        var act = async () =>
        {
            await foreach (var _ in engine.Index!.SearchAsync(invalid)) { }
        };

        await act.Should().ThrowAsync<ArgumentException>(
            "both backends must answer an invalid query the same way");
    }
}
