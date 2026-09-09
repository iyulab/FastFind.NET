using FastFind.Interfaces;
using FastFind.Models;
using FastFind.SQLite;
using FluentAssertions;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// The Unix engines can be composed with a persistence store, and say so clearly when they cannot.
/// </summary>
/// <remarks>
/// These engines used to reject a store outright: <c>UnixSearchEngineImpl</c> kept its index in a
/// plain dictionary that was not reachable as an <see cref="ISearchIndex"/>, so Linux and macOS
/// consumers got none of the composition work. It now takes an <see cref="ISearchIndex"/> and routes
/// through it when given one, keeping the dictionary as the default path.
///
/// This class is also the first automated exercise of <c>FastFind.SQLite</c> outside Windows.
/// </remarks>
[Trait("Category", "Functional")]
public class EngineCompositionTests
{
    private static SearchEngineOptions StoreOptions(IIndexPersistence store) => new()
    {
        Persistence = store,
        DisposeSuppliedComponents = false,
    };

    private static async Task<(SqlitePersistence store, string dir)> NewStoreAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fastfind_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var store = new SqlitePersistence(
            PersistenceConfiguration.CreateSQLite(Path.Combine(dir, "index.db")));
        await store.InitializeAsync();

        return (store, dir);
    }

    private static FastFileItem Item(string fullPath, long size = 1024)
    {
        var name = Path.GetFileName(fullPath);
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return new FastFileItem(
            fullPath, name, Path.GetDirectoryName(fullPath) ?? "/", Path.GetExtension(name),
            size, when, when, when, FileAttributes.Normal, '/');
    }

    private static ISearchEngine CreateForThisPlatform(SearchEngineOptions options) =>
        OperatingSystem.IsMacOS()
            ? UnixSearchEngine.CreateMacOSSearchEngine(options)
            : UnixSearchEngine.CreateLinuxSearchEngine(options);

    [Fact]
    public async Task An_Engine_Composed_With_A_Store_Should_Expose_That_Index()
    {
        if (OperatingSystem.IsWindows()) return;

        var (store, dir) = await NewStoreAsync();
        try
        {
            using var engine = CreateForThisPlatform(StoreOptions(store));

            engine.Index.Should().NotBeNull("composing with a store is what ISearchEngine.Index is for");
            engine.Index!.Persistence.Should().BeSameAs(store);
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_Search_Should_Be_Answered_From_The_Store()
    {
        if (OperatingSystem.IsWindows()) return;

        var (store, dir) = await NewStoreAsync();
        try
        {
            using var engine = CreateForThisPlatform(StoreOptions(store));

            await engine.Index!.AddBatchAsync(
            [
                Item("/home/user/docs/report.txt"),
                Item("/home/user/src/main.cs", size: 8192),
            ]);

            var result = await engine.SearchAsync(new SearchQuery
            {
                SearchText = "port",
                SearchFileNameOnly = true,
            });

            var paths = new List<string>();
            await foreach (var file in result.Files)
            {
                paths.Add(file.FullPath);
            }

            // Nothing was ever put in the engine's own dictionary, so a match can only have come
            // from the store.
            paths.Should().ContainSingle().Which.Should().Be("/home/user/docs/report.txt");
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveIndexAsync_Should_Report_The_Item_Count_Rather_Than_Throwing()
    {
        if (OperatingSystem.IsWindows()) return;

        var (store, dir) = await NewStoreAsync();
        try
        {
            using var engine = CreateForThisPlatform(StoreOptions(store));

            await engine.Index!.AddAsync(Item("/home/user/docs/report.txt"));

            var saved = await engine.SaveIndexAsync();
            var loaded = await engine.LoadIndexAsync();

            saved.Should().Be(1);
            loaded.Should().Be(1);
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveIndexAsync_Without_A_Store_Should_Say_How_To_Get_One()
    {
        if (OperatingSystem.IsWindows()) return;

        using var engine = CreateForThisPlatform(SearchEngineOptions.ForLogger(null));

        engine.Index.Should().BeNull("an engine created without a store keeps its index internally");

        // InvalidOperationException, not NotSupportedException: the operation is supported, this
        // engine just has nothing to save to. Same contract as the Windows engine.
        var act = async () => await engine.SaveIndexAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CreateSearchEngine(persistence)*");
    }

    [Fact]
    public void MirrorInMemory_Should_Still_Be_Rejected_Loudly()
    {
        if (OperatingSystem.IsWindows()) return;

        // This mode needs a shared in-memory ISearchIndex to mirror into, and this engine's
        // in-memory path is a dictionary rather than one. Accepting it and quietly not mirroring is
        // the failure mode this project has found in several places.
        var options = new SearchEngineOptions
        {
            Persistence = new UnusableStore(),
            PersistenceMode = PersistenceMode.MirrorInMemory,
        };

        var act = () => CreateForThisPlatform(options);

        act.Should().Throw<NotSupportedException>().WithMessage("*MirrorInMemory*");
    }

    /// <summary>
    /// A store that is never used: the factory must reject the mode before touching anything.
    /// </summary>
    private sealed class UnusableStore : IIndexPersistence
    {
        public long Count => throw new NotImplementedException();
        public bool IsReady => throw new NotImplementedException();
        public string StoragePath => throw new NotImplementedException();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> RemoveBatchAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> UpdateAsync(FastFileItem item, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string fullPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<FastFileItem> GetByExtensionAsync(string extension, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task OptimizeAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IIndexTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task VacuumAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => throw new NotImplementedException();
        public void Dispose() => throw new NotImplementedException();
    }
}
