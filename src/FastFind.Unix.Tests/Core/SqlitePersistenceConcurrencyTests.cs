using FastFind.Models;
using FastFind.SQLite;
using FluentAssertions;
using System.Collections.Concurrent;
using Xunit;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// <c>SqlitePersistence</c> under concurrent use, on Linux and macOS.
/// </summary>
/// <remarks>
/// The store is a cross-platform component, but its comprehensive suite lives in the Windows test
/// project, so until now every concurrency and transaction test ran on one platform only. That
/// mattered because the provider held a single <c>SqliteConnection</c> shared by every operation
/// until cycle 49: concurrent calls raced on the connection's internal command list, intermittently,
/// and the shape of the fix — a pooled connection per operation, with a transaction ambient to the
/// flow that opened it — is exactly the kind of thing that can behave differently on another
/// platform's file locking.
///
/// Paths here are POSIX. Nothing in these tests is platform-specific otherwise, so they also pass if
/// the suite is run on Windows.
/// </remarks>
[Trait("Category", "Functional")]
public sealed class SqlitePersistenceConcurrencyTests : IDisposable
{
    private readonly string _dir;

    public SqlitePersistenceConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fastfind_sqlite_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string DbPath([System.Runtime.CompilerServices.CallerMemberName] string name = "db")
        => Path.Combine(_dir, name + ".db");

    private static FastFileItem Item(string fullPath, long size = 1024)
    {
        var name = Path.GetFileName(fullPath);
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return new FastFileItem(
            fullPath, name, Path.GetDirectoryName(fullPath) ?? "/", Path.GetExtension(name),
            size, when, when, when, FileAttributes.Normal, '/');
    }

    private static List<FastFileItem> Items(int count, string prefix = "/data/corpus") =>
        Enumerable.Range(0, count).Select(i => Item($"{prefix}/file_{i}.txt", 100 + i)).ToList();

    [Fact]
    public async Task Readers_And_A_Writer_Should_Not_Corrupt_The_Connection()
    {
        await using var store = SqlitePersistence.Create(DbPath());
        await store.InitializeAsync();
        await store.AddBatchAsync(Items(50));

        var errors = new ConcurrentBag<Exception>();

        // The waits below are generous on purpose: nothing here measures latency, so a budget tight
        // enough to trip on a loaded machine would only add a flake to CI.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var writer = Task.Run(async () =>
        {
            var counter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await store.AddAsync(Item($"/data/live/file_{counter++}.txt"));
                    await Task.Delay(10, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add(ex); }
            }
        });

        var readers = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await store.SearchAsync(new SearchQuery { SearchText = "file" }, cts.Token)
                        .ToListAsync(cts.Token);
                    await Task.Delay(5, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add(ex); }
            }
        }));

        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(120));

        errors.Should().BeEmpty(
            "a shared connection is what corrupted this: {0}",
            string.Join(" | ", errors.Select(e => e.Message)));
    }

    [Fact]
    public async Task Concurrent_Adds_Of_One_Path_Should_Count_One_Row()
    {
        await using var store = SqlitePersistence.Create(DbPath());
        await store.InitializeAsync();

        const string path = "/data/contended/same.txt";
        var errors = new ConcurrentBag<Exception>();

        var adders = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            try { await store.AddAsync(Item(path)); }
            catch (Exception ex) { errors.Add(ex); }
        }));

        await Task.WhenAll(adders).WaitAsync(TimeSpan.FromSeconds(120));

        errors.Should().BeEmpty(string.Join(" | ", errors.Select(e => e.Message)));
        (await store.SearchAsync(new SearchQuery()).ToListAsync()).Should().HaveCount(1);
        store.Count.Should().Be(1, "one path is one row however many writers raced for it");
    }

    [Fact]
    public async Task A_Transaction_Should_Cover_Writes_Issued_Inside_Its_Scope()
    {
        await using var store = SqlitePersistence.Create(DbPath());
        await store.InitializeAsync();
        await store.AddAsync(Item("/data/kept/existing.txt"));

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.AddAsync(Item("/data/rolled-back/one.txt"));
            await store.AddAsync(Item("/data/rolled-back/two.txt"));
            await transaction.RollbackAsync();
        }

        store.Count.Should().Be(1);
        (await store.ExistsAsync("/data/kept/existing.txt")).Should().BeTrue();
        (await store.ExistsAsync("/data/rolled-back/one.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_Transaction_On_One_Flow_Should_Not_Block_Readers_On_Others()
    {
        await using var store = SqlitePersistence.Create(DbPath());
        await store.InitializeAsync();
        await store.AddBatchAsync(Items(50));

        var errors = new ConcurrentBag<Exception>();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readersDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The transaction is ambient to this flow alone; nothing started below inherits it.
        var writer = Task.Run(async () =>
        {
            try
            {
                await using var transaction = await store.BeginTransactionAsync();
                await store.AddAsync(Item("/data/pending/file.txt"));
                opened.SetResult();

                await readersDone.Task.WaitAsync(TimeSpan.FromSeconds(120));
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                opened.TrySetResult();
            }
        });

        await opened.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var readers = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 10; i++)
                {
                    var found = await store.SearchAsync(new SearchQuery { SearchText = "file" }).ToListAsync();
                    found.Should().NotBeEmpty("committed rows stay readable while another flow holds a transaction");
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToArray();

        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(120));
        readersDone.SetResult();
        await writer.WaitAsync(TimeSpan.FromSeconds(150));

        errors.Should().BeEmpty(string.Join(" | ", errors.Select(e => e.Message)));
        (await store.ExistsAsync("/data/pending/file.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Bulk_Insert_Should_Store_Every_Item_Across_Batches()
    {
        await using var store = SqlitePersistence.Create(DbPath());
        await store.InitializeAsync();

        // More than the 500-item batch size, so the window spans several statements.
        var inserted = await store.AddBulkOptimizedAsync(Items(1200));

        inserted.Should().Be(1200);
        store.Count.Should().Be(1200);
        (await store.SearchAsync(new SearchQuery()).ToListAsync()).Should().HaveCount(1200);
    }

    [Fact]
    public async Task Bulk_Insert_Inside_A_Transaction_Should_Roll_Back_With_It()
    {
        await using var store = SqlitePersistence.Create(DbPath());
        await store.InitializeAsync();

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.AddBulkOptimizedAsync(Items(600));
            await transaction.RollbackAsync();
        }

        store.Count.Should().Be(0);
        (await store.SearchAsync(new SearchQuery()).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Stream_Insert_Should_Flush_Every_Buffered_Item()
    {
        await using var store = SqlitePersistence.Create(DbPath());
        await store.InitializeAsync();

        var inserted = await store.AddFromStreamAsync(Stream(Items(750)), bufferSize: 200);

        inserted.Should().Be(750);
        store.Count.Should().Be(750);
        (await store.SearchAsync(new SearchQuery()).ToListAsync()).Should().HaveCount(750);

        static async IAsyncEnumerable<FastFileItem> Stream(IEnumerable<FastFileItem> items)
        {
            foreach (var item in items) yield return item;
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Disposing_The_Store_Should_Release_The_Database_File()
    {
        var path = DbPath();

        await using (var store = SqlitePersistence.Create(path))
        {
            await store.InitializeAsync();
            await store.AddAsync(Item("/data/dispose/file.txt"));
        }

        // Pooled connections keep the file open unless the pool is emptied on dispose. Deleting it
        // is the observable consequence; on Windows an open handle blocks the delete outright, and
        // everywhere it means a caller cannot replace the store.
        var delete = () => File.Delete(path);

        delete.Should().NotThrow();
        File.Exists(path).Should().BeFalse();
    }
}
