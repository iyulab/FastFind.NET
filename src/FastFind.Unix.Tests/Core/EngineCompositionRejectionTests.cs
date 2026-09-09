using FastFind.Interfaces;
using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// The Unix engine cannot be given an index or a store, and says so.
/// </summary>
/// <remarks>
/// <see cref="UnixSearchEngineImpl"/> keeps its index in an internal dictionary rather than behind an
/// <see cref="Interfaces.ISearchIndex"/>. Accepting a store and quietly ignoring it would leave a
/// caller believing their index was persisted — the failure mode this project has now found in
/// several places.
/// </remarks>
[Trait("Category", "Functional")]
public class EngineCompositionRejectionTests
{
    [Fact]
    public void Composing_With_A_Store_Should_Throw_Rather_Than_Ignore_It()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix factories reject on any platform, but keep the suite honest

        var options = new SearchEngineOptions { Persistence = new UnusableStore() };

        var act = () => UnixSearchEngine.CreateLinuxSearchEngine(options);

        act.Should().Throw<NotSupportedException>().WithMessage("*cannot be given*");
    }

    [Fact]
    public void An_Engine_Without_Composition_Should_Report_No_Index()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();

        engine.Index.Should().BeNull("this engine holds its index internally");
    }

    /// <summary>
    /// A store that is never used: the factory must reject it before touching anything.
    /// </summary>
    private sealed class UnusableStore : Interfaces.IIndexPersistence
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
        public Task<Interfaces.PersistenceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Interfaces.IIndexTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task VacuumAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => throw new NotImplementedException();
        public void Dispose() => throw new NotImplementedException();
    }
}
