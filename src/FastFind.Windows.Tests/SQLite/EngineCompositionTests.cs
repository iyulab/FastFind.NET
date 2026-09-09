using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.SQLite;
using FastFind.Windows;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.SQLite;

/// <summary>
/// The composition point: giving a <see cref="FastFinder"/>-created engine a persistence store.
/// </summary>
/// <remarks>
/// Before this existed, a consumer could reference the SQLite package, construct a provider, and
/// have no supported way to install it — the engine factory took only a logger factory, and both the
/// index and engine implementations were internal.
/// </remarks>
[Trait("Category", "SQLite")]
public class EngineCompositionTests : IAsyncLifetime
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
        var path = Path.Combine(Path.GetTempPath(), $"fastfind-compose-{Guid.NewGuid():N}.db");
        _paths.Add(path);

        var store = SqlitePersistence.Create(path);
        await store.InitializeAsync();
        return store;
    }

    private static FastFileItem Item(string fullPath) => new(
        fullPath, Path.GetFileName(fullPath), Path.GetDirectoryName(fullPath)!,
        Path.GetExtension(fullPath), 512,
        DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, fullPath[0]);

    [Fact]
    public async Task An_Engine_Given_A_Store_Should_Expose_It_And_Retain_Nothing()
    {
        await using var store = await NewStore();

        using var engine = FastFinder.CreateSearchEngine(store);

        engine.Should().NotBeNull();
        engine.Index.Should().NotBeNull("the engine must expose the index it was composed with");
        engine.Index!.Persistence.Should().BeSameAs(store);
        engine.Index.MemoryUsage.Should().Be(0, "a store-backed index holds no copy");
    }

    [Fact]
    public async Task Items_Added_Through_The_Engine_Should_Be_Searchable_From_The_Store()
    {
        await using var store = await NewStore();
        using var engine = FastFinder.CreateSearchEngine(store);

        await engine.Index!.AddBatchAsync([
            Item(@"C:\composed\alpha.txt"),
            Item(@"C:\composed\Beta.log"),
            Item(@"C:\composed\nested\gamma.txt")
        ]);

        engine.Index.Count.Should().Be(3);

        var hits = new List<string>();
        await foreach (var item in engine.Index.SearchAsync(new SearchQuery { ExtensionFilter = "txt" }))
        {
            hits.Add(item.Name);
        }

        hits.Should().BeEquivalentTo(["alpha.txt", "gamma.txt"]);
    }

    [Fact]
    public async Task A_Store_Should_Survive_The_Engine_That_Used_It()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fastfind-reopen-{Guid.NewGuid():N}.db");
        _paths.Add(path);

        await using (var first = SqlitePersistence.Create(path))
        {
            await first.InitializeAsync();
            using var engine = FastFinder.CreateSearchEngine(first);
            await engine.Index!.AddAsync(Item(@"C:\persisted\kept.txt"));
        }

        // A new provider over the same file, in a new engine: the index is still there.
        await using var second = SqlitePersistence.Create(path);
        await second.InitializeAsync();
        using var reopened = FastFinder.CreateSearchEngine(second);

        reopened.Index!.Count.Should().Be(1);
        (await reopened.Index.ContainsAsync(@"C:\persisted\kept.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task MirrorInMemory_Should_Keep_An_In_Memory_Index_That_Writes_Through()
    {
        await using var store = await NewStore();

        using var engine = FastFinder.CreateSearchEngine(store, mode: PersistenceMode.MirrorInMemory);

        engine.Index!.Persistence.Should().BeSameAs(store);
        engine.Index.Should().NotBeOfType<PersistentSearchIndex>(
            "MirrorInMemory keeps the platform's in-memory index");

        await engine.Index.AddAsync(Item(@"C:\mirrored\file.txt"));

        (await store.ExistsAsync(@"C:\mirrored\file.txt")).Should().BeTrue("writes mirror into the store");
        engine.Index.MemoryUsage.Should().BeGreaterThan(0, "the in-memory copy is the point of this mode");
    }

    [Fact]
    public async Task A_Supplied_Index_Should_Take_Precedence_Over_A_Store()
    {
        await using var store = await NewStore();
        await using var other = await NewStore();
        var supplied = new PersistentSearchIndex(other);

        using var engine = FastFinder.CreateSearchEngine(new SearchEngineOptions
        {
            Persistence = store,
            Index = supplied
        });

        engine.Index.Should().BeSameAs(supplied);
        engine.Index!.Persistence.Should().BeSameAs(other);
    }

    [Fact]
    public void An_Engine_Created_Without_A_Store_Should_Report_No_Persistence()
    {
        using var engine = FastFinder.CreateSearchEngine();

        engine.Index!.Persistence.Should().BeNull();
    }

    [Fact]
    public void Passing_A_Null_Store_Should_Throw()
    {
        var act = () => FastFinder.CreateSearchEngine((IIndexPersistence)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Disposing_The_Engine_Should_Not_Dispose_A_Store_The_Caller_Owns()
    {
        await using var store = await NewStore();

        using (FastFinder.CreateSearchEngine(store))
        {
        }

        store.IsReady.Should().BeTrue("the caller constructed the store and still owns it");
    }
}
