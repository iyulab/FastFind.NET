using System.Runtime.CompilerServices;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.SQLite.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FastFind.SQLite;

/// <summary>
/// SQLite-based persistence provider for FastFind.NET
/// Features: WAL mode, batched bulk ingest, queries evaluated through SearchQueryEvaluator
/// </summary>
public sealed class SqlitePersistence : IIndexPersistence
{
    private readonly ILogger<SqlitePersistence>? _logger;
    private readonly PersistenceConfiguration _config;
    private string? _connectionString;
    private bool _disposed;
    private long _count;
    private bool _isReady;

    /// <summary>
    /// Serialises bulk write windows so two of them queue in process rather than contending for
    /// SQLite's single writer and failing on <c>busy_timeout</c>.
    /// </summary>
    private readonly SemaphoreSlim _bulkOperationLock = new(1, 1);

    /// <summary>
    /// The transaction ambient to the current asynchronous flow, if any. Set by
    /// <see cref="BeginTransactionAsync"/> so that operations issued inside the transaction's scope
    /// run on its connection and are covered by it, exactly as they were when every operation shared
    /// one connection.
    /// </summary>
    private readonly AsyncLocal<AmbientTransaction?> _ambient = new();

    /// <inheritdoc/>
    public long Count => _count;

    /// <inheritdoc/>
    public bool IsReady => _isReady;

    /// <inheritdoc/>
    public string StoragePath => _config.StoragePath;

    /// <summary>
    /// Creates a new SQLite persistence provider
    /// </summary>
    public SqlitePersistence(PersistenceConfiguration config, ILogger<SqlitePersistence>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <summary>
    /// Creates a new SQLite persistence provider with a simple path
    /// </summary>
    public static SqlitePersistence Create(string databasePath, ILogger<SqlitePersistence>? logger = null)
    {
        return new SqlitePersistence(PersistenceConfiguration.CreateSQLite(databasePath), logger);
    }

    /// <summary>
    /// Creates a high-performance SQLite persistence provider
    /// </summary>
    public static SqlitePersistence CreateHighPerformance(string databasePath, ILogger<SqlitePersistence>? logger = null)
    {
        return new SqlitePersistence(PersistenceConfiguration.CreateSQLiteHighPerformance(databasePath), logger);
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _config.StoragePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using var connection = await OpenConnectionAsync(cancellationToken);

        // Properties of the database file itself, applied once. Everything that is per-connection
        // is applied by OpenConnectionAsync to every connection this provider opens.
        await ExecuteNonQueryAsync(
            connection,
            SqliteSchema.GetDatabasePragmas(_config.UseWAL, _config.PageSize),
            cancellationToken);

        // Metadata first: it carries the schema version that decides whether the rest can be kept.
        await ExecuteNonQueryAsync(connection, SqliteSchema.CreateMetadataTable, cancellationToken);
        await DiscardStoreIfSchemaChangedAsync(connection, cancellationToken);

        // Create tables
        await ExecuteNonQueryAsync(connection, SqliteSchema.CreateFilesTable, cancellationToken);
        await ExecuteNonQueryAsync(connection, SqliteSchema.CreateStatisticsTable, cancellationToken);

        // Create indexes
        await ExecuteNonQueryAsync(connection, SqliteSchema.CreateIndexes, cancellationToken);

        // Initialize statistics if not exists
        await ExecuteNonQueryAsync(
            connection,
            $"INSERT OR IGNORE INTO statistics (id, created_at, updated_at) VALUES (1, {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, {DateTimeOffset.UtcNow.ToUnixTimeSeconds()})",
            cancellationToken);

        await using (var writeVersion = connection.CreateCommand())
        {
            writeVersion.CommandText =
                "INSERT INTO metadata (key, value) VALUES (@key, @value) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            writeVersion.Parameters.AddWithValue("@key", SqliteSchema.SchemaVersionKey);
            writeVersion.Parameters.AddWithValue("@value", SqliteSchema.CurrentVersion.ToString());
            await writeVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        // Load count
        _count = await GetCountFromDbAsync(connection, cancellationToken);
        _isReady = true;

        _logger?.LogInformation("SQLite persistence initialized at {Path} with {Count} items", _config.StoragePath, _count);
    }

    /// <summary>
    /// Opens a connection to the store and applies the per-connection settings.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Data.Sqlite</c>'s own guidance is to open a connection whenever the database is
    /// needed rather than to share one: <see cref="SqliteConnection"/> and the commands created from
    /// it are not thread-safe, and concurrent use corrupts the connection's internal command list.
    /// Connections are pooled, so opening one is cheap. The pragmas applied here - cache size, temp
    /// store, mmap window and the busy timeout - are properties of a <i>connection</i>, not of the
    /// database, so they have to be set on each one; the database-level pragmas are applied once in
    /// <see cref="InitializeAsync"/>. Setting them unconditionally also means a connection handed
    /// back by the pool cannot carry another operation's settings into this one.
    /// </remarks>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            _connectionString ?? throw new InvalidOperationException(
                "Persistence layer not initialized. Call InitializeAsync first."));

        try
        {
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqliteSchema.GetConnectionPragmas(
                _config.CacheSize, _config.UseMmap, _config.MmapSize);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Gets a connection to work on: the ambient transaction's connection when one is open on this
    /// flow, otherwise a fresh one that the lease owns and disposes.
    /// </summary>
    private async Task<ConnectionLease> LeaseAsync(CancellationToken cancellationToken)
    {
        var ambient = _ambient.Value;
        if (ambient is { Completed: false, Connection: { } shared })
        {
            return new ConnectionLease(shared, owned: false);
        }

        return new ConnectionLease(await OpenConnectionAsync(cancellationToken), owned: true);
    }

    /// <summary>
    /// A connection borrowed for one operation. Disposing it closes the connection only when the
    /// lease opened it - a connection borrowed from an open transaction outlives the operation.
    /// </summary>
    private readonly struct ConnectionLease(SqliteConnection connection, bool owned) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        /// <summary>True when this operation is running inside a caller's transaction.</summary>
        public bool IsAmbient => !owned;

        public ValueTask DisposeAsync() => owned ? Connection.DisposeAsync() : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The transaction ambient to one asynchronous flow.
    /// </summary>
    /// <remarks>
    /// The holder is written into the <see cref="AsyncLocal{T}"/> synchronously, from the caller's
    /// frame, because a value assigned inside an <c>async</c> method does not flow back out to its
    /// caller. Completion is signalled by mutating this object rather than by clearing the
    /// <see cref="AsyncLocal{T}"/>, for the same reason in reverse.
    /// </remarks>
    private sealed class AmbientTransaction : IAmbientScope
    {
        public SqliteConnection? Connection { get; set; }
        public bool Completed { get; private set; }

        public void Complete()
        {
            Completed = true;
            Connection = null;
        }
    }

    /// <summary>
    /// The half of the ambient scope a transaction needs: the ability to close it.
    /// </summary>
    internal interface IAmbientScope
    {
        void Complete();
    }

    /// <summary>
    /// Drops the stored index when it was written by a different schema version.
    /// </summary>
    /// <remarks>
    /// The store is a cache of the file system, so a rebuild costs one re-index and avoids
    /// carrying migration code for every past shape. A database with no recorded version predates
    /// version tracking and is treated as stale.
    /// </remarks>
    private async Task DiscardStoreIfSchemaChangedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tableExists = await ScalarAsync(
            connection,
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'files' LIMIT 1",
            cancellationToken);

        if (tableExists is null) return; // nothing stored yet

        var recorded = await ScalarAsync(
            connection,
            $"SELECT value FROM metadata WHERE key = '{SqliteSchema.SchemaVersionKey}' LIMIT 1",
            cancellationToken);

        if (recorded is string text
            && int.TryParse(text, out var version)
            && version == SqliteSchema.CurrentVersion)
        {
            return;
        }

        _logger?.LogInformation(
            "Rebuilding the index store at {Path}: schema version {Found} is not {Expected}",
            _config.StoragePath, recorded ?? "(none)", SqliteSchema.CurrentVersion);

        await ExecuteNonQueryAsync(connection, SqliteSchema.DropAll, cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    /// <inheritdoc/>
    public async Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var lease = await LeaseAsync(cancellationToken);

        // The existence check and the upsert decide together whether the count moves, so they have
        // to be one atomic step: two concurrent adds of the same path would otherwise both read
        // "absent" and both increment, leaving the count above the number of rows. The transaction
        // is IMMEDIATE, so it takes the write lock before the check rather than after. Inside a
        // caller's transaction that atomicity is already theirs.
        var transaction = lease.IsAmbient
            ? null
            : (SqliteTransaction)await lease.Connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var existsBefore = await ExistsAsync(lease.Connection, item.FullPath, cancellationToken);

            await using (var cmd = CreateInsertCommand(lease.Connection, item))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            // Only increment count for actual INSERT, not UPDATE (UPSERT)
            if (!existsBefore)
            {
                Interlocked.Increment(ref _count);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(cancellationToken); } catch { /* ignore */ }
            }
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    /// <inheritdoc/>
    public async Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var itemList = items as IList<FastFileItem> ?? items.ToList();
        if (itemList.Count == 0) return 0;

        // Use optimized bulk insert for large batches
        if (itemList.Count >= 100)
        {
            return await AddBulkOptimizedAsync(itemList, cancellationToken);
        }

        // Standard batch insert for smaller batches
        var count = 0;
        await using var lease = await LeaseAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Reuse command with parameters for efficiency
            await using var cmd = lease.Connection.CreateCommand();
            cmd.CommandText = SqliteSchema.InsertFile;
            cmd.Transaction = (SqliteTransaction)transaction;

            var fullPathParam = cmd.Parameters.Add("@full_path", SqliteType.Text);
            var nameParam = cmd.Parameters.Add("@name", SqliteType.Text);
            var dirPathParam = cmd.Parameters.Add("@directory_path", SqliteType.Text);
            var extParam = cmd.Parameters.Add("@extension", SqliteType.Text);
            var sizeParam = cmd.Parameters.Add("@size", SqliteType.Integer);
            var createdParam = cmd.Parameters.Add("@created_time", SqliteType.Integer);
            var modifiedParam = cmd.Parameters.Add("@modified_time", SqliteType.Integer);
            var accessedParam = cmd.Parameters.Add("@accessed_time", SqliteType.Integer);
            var attrsParam = cmd.Parameters.Add("@attributes", SqliteType.Integer);
            var driveParam = cmd.Parameters.Add("@drive_letter", SqliteType.Text);
            var isDirParam = cmd.Parameters.Add("@is_directory", SqliteType.Integer);

            foreach (var item in itemList)
            {
                if (cancellationToken.IsCancellationRequested) break;

                fullPathParam.Value = NormalizePath(item.FullPath);
                nameParam.Value = item.Name;
                dirPathParam.Value = NormalizePath(item.DirectoryPath);
                extParam.Value = item.Extension;
                sizeParam.Value = item.Size;
                createdParam.Value = new DateTimeOffset(item.CreatedTime).ToUnixTimeSeconds();
                modifiedParam.Value = new DateTimeOffset(item.ModifiedTime).ToUnixTimeSeconds();
                accessedParam.Value = new DateTimeOffset(item.AccessedTime).ToUnixTimeSeconds();
                attrsParam.Value = (int)item.Attributes;
                driveParam.Value = item.DriveLetter.ToString();
                isDirParam.Value = item.IsDirectory ? 1 : 0;

                await cmd.ExecuteNonQueryAsync(cancellationToken);
                count++;
            }

            await transaction.CommitAsync(cancellationToken);

            // Refresh count from DB to accurately reflect UPSERT behavior
            Interlocked.Exchange(ref _count, await GetCountFromDbAsync(lease.Connection, cancellationToken));

            _logger?.LogDebug("Batch inserted {Count} items", count);
            return count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// High-performance bulk insert for MFT-level throughput (100K+ items)
    /// Uses multi-value INSERT statements and optimized PRAGMA settings.
    /// </summary>
    public async Task<int> AddBulkOptimizedAsync(IList<FastFileItem> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        if (items.Count == 0) return 0;

        const int batchSize = 500; // SQLite max variables / 11 params per row
        var totalInserted = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // One connection for the whole bulk window. The transaction, the batch inserts and the
        // bulk PRAGMAs all have to run on the same connection: a PRAGMA applies to the connection
        // that issued it, and a statement outside the connection holding the transaction is not
        // covered by it.
        await _bulkOperationLock.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await LeaseAsync(cancellationToken);
            var connection = lease.Connection;

            // Apply bulk loading optimizations
            await ExecuteNonQueryAsync(connection, SqliteSchema.BulkLoadPragmas, cancellationToken);

            // Insert data in transaction. Inside a caller's transaction the batches join it rather
            // than opening a nested one, which SQLite does not support.
            var transaction = lease.IsAmbient
                ? null
                : (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                for (var i = 0; i < items.Count; i += batchSize)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var batch = items.Skip(i).Take(batchSize).ToList();
                    var inserted = await InsertBatchMultiValueAsync(connection, batch, transaction, cancellationToken);
                    totalInserted += inserted;
                }

                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                if (transaction is not null)
                {
                    try { await transaction.RollbackAsync(cancellationToken); } catch { /* ignore */ }
                }
                throw;
            }
            finally
            {
                if (transaction is not null) await transaction.DisposeAsync();
            }

            // Refresh count from DB to accurately reflect UPSERT behavior
            Interlocked.Exchange(ref _count, await GetCountFromDbAsync(connection, cancellationToken));

            stopwatch.Stop();
            var rate = totalInserted / stopwatch.Elapsed.TotalSeconds;
            _logger?.LogInformation(
                "Bulk inserted {Count:N0} items in {Time:F2}s ({Rate:N0} items/sec)",
                totalInserted, stopwatch.Elapsed.TotalSeconds, rate);

            return totalInserted;
        }
        finally
        {
            _bulkOperationLock.Release();
        }
    }

    /// <summary>
    /// Inserts one batch as a single multi-value INSERT.
    /// </summary>
    /// <param name="connection">The connection to issue the statement on.</param>
    /// <param name="batch">The items to insert.</param>
    /// <param name="transaction">
    /// The transaction to enlist in, or <see langword="null"/> to use whichever transaction is
    /// already pending on <paramref name="connection"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task<int> InsertBatchMultiValueAsync(
        SqliteConnection connection,
        List<FastFileItem> batch,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return 0;

        // Build multi-value INSERT statement
        var sb = new System.Text.StringBuilder(SqliteSchema.BulkInsertPrefix);

        await using var cmd = connection.CreateCommand();
        if (transaction is not null) cmd.Transaction = transaction;

        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(',');

            var item = batch[i];
            var prefix = $"@p{i}_";

            sb.Append($"({prefix}fp, {prefix}n, {prefix}dp, {prefix}e, {prefix}s, {prefix}ct, {prefix}mt, {prefix}at, {prefix}a, {prefix}dl, {prefix}id)");

            cmd.Parameters.AddWithValue($"{prefix}fp", NormalizePath(item.FullPath));
            cmd.Parameters.AddWithValue($"{prefix}n", item.Name);
            cmd.Parameters.AddWithValue($"{prefix}dp", NormalizePath(item.DirectoryPath));
            cmd.Parameters.AddWithValue($"{prefix}e", item.Extension);
            cmd.Parameters.AddWithValue($"{prefix}s", item.Size);
            cmd.Parameters.AddWithValue($"{prefix}ct", new DateTimeOffset(item.CreatedTime).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue($"{prefix}mt", new DateTimeOffset(item.ModifiedTime).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue($"{prefix}at", new DateTimeOffset(item.AccessedTime).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue($"{prefix}a", (int)item.Attributes);
            cmd.Parameters.AddWithValue($"{prefix}dl", item.DriveLetter.ToString());
            cmd.Parameters.AddWithValue($"{prefix}id", item.IsDirectory ? 1 : 0);
        }

        sb.AppendLine();
        sb.Append(SqliteSchema.BulkInsertSuffix);

        cmd.CommandText = sb.ToString();
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }


    /// <summary>
    /// Streams data from an async enumerable directly into SQLite with buffered bulk inserts.
    /// Optimized for MFT enumeration integration (500K+ records/sec source).
    /// </summary>
    public async Task<int> AddFromStreamAsync(
        IAsyncEnumerable<FastFileItem> items,
        int bufferSize = 5000,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var buffer = new List<FastFileItem>(bufferSize);
        var totalInserted = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Like AddBulkOptimizedAsync, this is a bulk write window: one connection for its whole
        // duration, and serialised against other bulk writers so they queue rather than contend.
        await _bulkOperationLock.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await LeaseAsync(cancellationToken);
            var connection = lease.Connection;

            // Apply bulk loading optimizations
            await ExecuteNonQueryAsync(connection, SqliteSchema.BulkLoadPragmas, cancellationToken);

            // Stream and insert data
            await foreach (var item in items.WithCancellation(cancellationToken))
            {
                buffer.Add(item);

                if (buffer.Count >= bufferSize)
                {
                    var inserted = await FlushBufferAsync(connection, lease.IsAmbient, buffer, cancellationToken);
                    totalInserted += inserted;
                    progress?.Report(totalInserted);
                    buffer.Clear();
                }
            }

            // Flush remaining items
            if (buffer.Count > 0)
            {
                var inserted = await FlushBufferAsync(connection, lease.IsAmbient, buffer, cancellationToken);
                totalInserted += inserted;
                progress?.Report(totalInserted);
            }

            Interlocked.Add(ref _count, totalInserted);

            stopwatch.Stop();
            var rate = totalInserted / stopwatch.Elapsed.TotalSeconds;
            _logger?.LogInformation(
                "Stream insert completed: {Count:N0} items in {Time:F2}s ({Rate:N0} items/sec)",
                totalInserted, stopwatch.Elapsed.TotalSeconds, rate);

            return totalInserted;
        }
        finally
        {
            _bulkOperationLock.Release();
        }
    }

    private static async Task<int> FlushBufferAsync(
        SqliteConnection connection,
        bool ambient,
        List<FastFileItem> buffer,
        CancellationToken cancellationToken)
    {
        if (buffer.Count == 0) return 0;

        const int batchSize = 500;
        var totalInserted = 0;

        // Inside a caller's transaction the batches join it; SQLite has no nested transactions.
        var transaction = ambient
            ? null
            : (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            for (var i = 0; i < buffer.Count; i += batchSize)
            {
                var batch = buffer.Skip(i).Take(batchSize).ToList();
                totalInserted += await InsertBatchMultiValueAsync(connection, batch, transaction, cancellationToken);
            }

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return totalInserted;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var lease = await LeaseAsync(cancellationToken);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SqliteSchema.DeleteFile;
        cmd.Parameters.AddWithValue("@full_path", NormalizePath(fullPath));

        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected > 0)
        {
            Interlocked.Decrement(ref _count);
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public async Task<int> RemoveBatchAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var count = 0;
        await using var lease = await LeaseAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var cmd = lease.Connection.CreateCommand();
            cmd.CommandText = SqliteSchema.DeleteFile;
            cmd.Transaction = (SqliteTransaction)transaction;
            var param = cmd.Parameters.Add("@full_path", SqliteType.Text);

            foreach (var path in fullPaths)
            {
                if (cancellationToken.IsCancellationRequested) break;

                param.Value = NormalizePath(path);
                var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0) count++;
            }

            await transaction.CommitAsync(cancellationToken);
            Interlocked.Add(ref _count, -count);
            return count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var lease = await LeaseAsync(cancellationToken);
        await using var cmd = CreateInsertCommand(lease.Connection, item);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var lease = await LeaseAsync(cancellationToken);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SqliteSchema.GetByPath;
        cmd.Parameters.AddWithValue("@full_path", NormalizePath(fullPath));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadFileItem(reader);
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var lease = await LeaseAsync(cancellationToken);
        return await ExistsAsync(lease.Connection, fullPath, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(SqliteConnection connection, string fullPath, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE full_path = @full_path LIMIT 1";
        cmd.Parameters.AddWithValue("@full_path", NormalizePath(fullPath));

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        // SearchQueryEvaluator is the authority on what matches. The SQL below only narrows the
        // candidate set, and every narrowing clause must be a provable superset of the evaluator's
        // verdict — a clause that can exclude a matching row would make this provider disagree with
        // the in-memory index for the same query.
        var textMatcher = SearchQueryEvaluator.CreateTextMatcher(query);

        await using var lease = await LeaseAsync(cancellationToken);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = BuildCandidateQuery(query, textMatcher, cmd);

        var remaining = query.MaxResults ?? int.MaxValue;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (remaining > 0 && await reader.ReadAsync(cancellationToken))
        {
            var item = ReadFileItem(reader);

            if (!SearchQueryEvaluator.Matches(item, query, textMatcher))
                continue;

            remaining--;
            yield return item;
        }
    }

    /// <summary>
    /// Builds the candidate SELECT for a query, adding only clauses that cannot exclude a row the
    /// evaluator would accept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SearchQuery.MaxResults"/> is deliberately <b>not</b> translated to SQL
    /// <c>LIMIT</c>. Limiting candidates rather than matches returns fewer results than asked for
    /// whenever any filter rejects a candidate, while more matches remain in the store.
    /// </para>
    /// <para>
    /// Substring text is narrowed with <c>LIKE</c> only when the pattern is ASCII. SQLite's default
    /// <c>LIKE</c> folds case for ASCII only, so a non-ASCII pattern could miss a row that the
    /// evaluator's full-Unicode comparison accepts.
    /// </para>
    /// </remarks>
    private static string BuildCandidateQuery(SearchQuery query, System.Text.RegularExpressions.Regex? textMatcher, SqliteCommand cmd)
    {
        var clauses = new List<string>();

        if (query.MinSize.HasValue)
        {
            clauses.Add("size >= @min_size");
            cmd.Parameters.AddWithValue("@min_size", query.MinSize.Value);
        }

        if (query.MaxSize.HasValue)
        {
            clauses.Add("size <= @max_size");
            cmd.Parameters.AddWithValue("@max_size", query.MaxSize.Value);
        }

        if (query.IncludeFiles != query.IncludeDirectories)
        {
            clauses.Add("is_directory = @is_directory");
            cmd.Parameters.AddWithValue("@is_directory", query.IncludeDirectories ? 1 : 0);
        }

        // Plain substring search over an ASCII pattern: LIKE agrees with the evaluator exactly for
        // a case-insensitive match, and is a superset for a case-sensitive one.
        if (textMatcher is null && !string.IsNullOrEmpty(query.SearchText) && IsAscii(query.SearchText))
        {
            var column = query.SearchFileNameOnly ? "name" : "full_path";
            clauses.Add($"{column} LIKE @text ESCAPE '{LikeEscape}'");
            cmd.Parameters.AddWithValue("@text", $"%{EscapeLikeLiteral(query.SearchText)}%");
        }

        var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : string.Empty;
        return "SELECT * FROM files" + where;
    }

    /// <summary>
    /// Escape character used with SQL <c>LIKE</c>. Not a backslash: a Windows path is full of
    /// those, and doubling them in every pattern is noise for no benefit.
    /// </summary>
    private const char LikeEscape = '!';

    /// <summary>
    /// Escapes the <c>LIKE</c> metacharacters in a literal, for use with <c>ESCAPE '!'</c>.
    /// </summary>
    private static string EscapeLikeLiteral(string value)
    {
        if (value.AsSpan().IndexOfAny('%', '_', LikeEscape) < 0) return value;

        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '%' or '_' or LikeEscape) builder.Append(LikeEscape);
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 127) return false;
        }

        return true;
    }
    /// <inheritdoc/>
    public async IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var normalizedPath = NormalizePath(directoryPath);
        await using var lease = await LeaseAsync(cancellationToken);
        await using var cmd = lease.Connection.CreateCommand();

        if (recursive)
        {
            var root = normalizedPath.TrimEnd('\\', '/');
            cmd.CommandText = SqliteSchema.GetByDirectoryRecursive;
            cmd.Parameters.AddWithValue("@directory_path", root);
            cmd.Parameters.AddWithValue("@directory_pattern", EscapeLikeLiteral(root) + "\\%");
            cmd.Parameters.AddWithValue("@directory_alt_pattern", EscapeLikeLiteral(root) + "/%");
        }
        else
        {
            cmd.CommandText = SqliteSchema.GetByDirectory;
            cmd.Parameters.AddWithValue("@directory_path", normalizedPath);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return ReadFileItem(reader);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FastFileItem> GetByExtensionAsync(string extension, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var normalizedExt = extension.StartsWith('.') ? extension : "." + extension;

        await using var lease = await LeaseAsync(cancellationToken);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SqliteSchema.GetByExtension;
        cmd.Parameters.AddWithValue("@extension", normalizedExt);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return ReadFileItem(reader);
        }
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var lease = await LeaseAsync(cancellationToken);
        await ExecuteNonQueryAsync(lease.Connection, $"DELETE FROM files; UPDATE statistics SET total_items = 0, total_files = 0, total_directories = 0, updated_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} WHERE id = 1;", cancellationToken);

        _count = 0;
        _logger?.LogInformation("Cleared all items from persistence");
    }

    /// <inheritdoc/>
    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        _logger?.LogInformation("Starting optimization...");

        await using var lease = await LeaseAsync(cancellationToken);

        // Analyze tables for query optimization
        await ExecuteNonQueryAsync(lease.Connection, "ANALYZE;", cancellationToken);

        await ExecuteNonQueryAsync(
            lease.Connection,
            $"UPDATE statistics SET last_optimized = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, updated_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} WHERE id = 1",
            cancellationToken);

        _logger?.LogInformation("Optimization complete");
    }

    /// <inheritdoc/>
    public async Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var lease = await LeaseAsync(cancellationToken);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = SqliteSchema.GetStatistics;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var fileInfo = new FileInfo(_config.StoragePath);
            return new PersistenceStatistics
            {
                TotalItems = reader.GetInt64(0),
                TotalFiles = reader.GetInt64(1),
                TotalDirectories = reader.GetInt64(2),
                UniqueExtensions = reader.GetInt32(3),
                StorageSizeBytes = fileInfo.Exists ? fileInfo.Length : 0
            };
        }

        return new PersistenceStatistics
        {
            TotalItems = 0,
            TotalFiles = 0,
            TotalDirectories = 0,
            StorageSizeBytes = 0
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The transaction is <b>ambient to the asynchronous flow that opened it</b>: operations issued
    /// on that flow before it commits or rolls back run on its connection and are covered by it,
    /// while unrelated flows keep getting their own connections. Work fanned out from inside the
    /// scope inherits the flow, and so inherits the connection - do not run concurrent operations
    /// inside an open transaction, for the same reason a <see cref="SqliteConnection"/> cannot be
    /// shared between threads.
    /// <para>
    /// This method is deliberately not <c>async</c>. The ambient scope is published from the
    /// caller's frame, because a value assigned to an <see cref="AsyncLocal{T}"/> inside an
    /// <c>async</c> method does not flow back out to its caller.
    /// </para>
    /// </remarks>
    public Task<IIndexTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var scope = new AmbientTransaction();
        _ambient.Value = scope;
        return BeginTransactionCoreAsync(scope, cancellationToken);
    }

    private async Task<IIndexTransaction> BeginTransactionCoreAsync(
        AmbientTransaction scope,
        CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);

        try
        {
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            scope.Connection = connection;
            return new SqliteIndexTransaction(connection, transaction, scope, this);
        }
        catch
        {
            scope.Complete();
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        _logger?.LogInformation("Starting VACUUM...");

        await using var lease = await LeaseAsync(cancellationToken);

        await ExecuteNonQueryAsync(lease.Connection, "VACUUM;", cancellationToken);

        await ExecuteNonQueryAsync(
            lease.Connection,
            $"UPDATE statistics SET last_vacuumed = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, updated_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} WHERE id = 1",
            cancellationToken);

        _logger?.LogInformation("VACUUM complete");
    }

    private static SqliteCommand CreateInsertCommand(SqliteConnection connection, FastFileItem item)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = SqliteSchema.InsertFile;
        cmd.Parameters.AddWithValue("@full_path", NormalizePath(item.FullPath));
        cmd.Parameters.AddWithValue("@name", item.Name);
        cmd.Parameters.AddWithValue("@directory_path", NormalizePath(item.DirectoryPath));
        cmd.Parameters.AddWithValue("@extension", item.Extension);
        cmd.Parameters.AddWithValue("@size", item.Size);
        cmd.Parameters.AddWithValue("@created_time", new DateTimeOffset(item.CreatedTime).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@modified_time", new DateTimeOffset(item.ModifiedTime).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@accessed_time", new DateTimeOffset(item.AccessedTime).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@attributes", (int)item.Attributes);
        cmd.Parameters.AddWithValue("@drive_letter", item.DriveLetter.ToString());
        cmd.Parameters.AddWithValue("@is_directory", item.IsDirectory ? 1 : 0);
        return cmd;
    }

    private static FastFileItem ReadFileItem(SqliteDataReader reader)
    {
        var fullPath = reader.GetString(reader.GetOrdinal("full_path"));
        var name = reader.GetString(reader.GetOrdinal("name"));
        var directoryPath = reader.GetString(reader.GetOrdinal("directory_path"));
        var extension = reader.GetString(reader.GetOrdinal("extension"));
        var size = reader.GetInt64(reader.GetOrdinal("size"));
        var createdTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("created_time"))).UtcDateTime;
        var modifiedTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("modified_time"))).UtcDateTime;
        var accessedTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("accessed_time"))).UtcDateTime;
        var attributes = (FileAttributes)reader.GetInt32(reader.GetOrdinal("attributes"));
        var driveLetter = reader.GetString(reader.GetOrdinal("drive_letter"))[0];

        return new FastFileItem(
            fullPath, name, directoryPath, extension,
            size, createdTime, modifiedTime, accessedTime,
            attributes, driveLetter
        );
    }

    private static async Task<long> GetCountFromDbAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long l ? l : 0;
    }

    /// <summary>
    /// Refreshes the in-memory count from the database.
    /// Called internally when a transaction completes.
    /// </summary>
    internal async Task RefreshCountAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await LeaseAsync(cancellationToken);
        Interlocked.Exchange(ref _count, await GetCountFromDbAsync(lease.Connection, cancellationToken));
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureReady()
    {
        if (!_isReady)
            throw new InvalidOperationException("Persistence layer not initialized. Call InitializeAsync first.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }


    /// <summary>
    /// Normalizes a file path for consistent storage and lookup.
    /// Converts to lowercase and normalizes directory separators for Windows filesystem compatibility.
    /// </summary>
    /// <summary>
    /// Normalises a path for storage.
    /// </summary>
    /// <remarks>
    /// Casing is preserved. Lower-casing here made a case-sensitive search impossible and, on a
    /// case-sensitive file system, replaced the real path with one that does not exist. Separators
    /// are folded only on Windows, where both are accepted; elsewhere a backslash is an ordinary
    /// filename character. Case-insensitive path identity on Windows comes from the column
    /// collation instead — see <see cref="SqliteSchema.CreateFilesTable"/>.
    /// </remarks>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        return OperatingSystem.IsWindows() && path.Contains('/')
            ? path.Replace('/', '\\')
            : path;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _isReady = false;

        ReleaseConnectionPool();

        try
        {
            _bulkOperationLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Semaphore already disposed
        }
    }

    /// <summary>
    /// Closes the pooled connections for this store.
    /// </summary>
    /// <remarks>
    /// Pooled connections stay open after the operation that used them returns, which keeps the
    /// database file open. Without this, deleting the file after disposing the provider fails on
    /// Windows, where an open handle blocks the delete.
    /// </remarks>
    private void ReleaseConnectionPool()
    {
        if (_connectionString is null) return;

        try
        {
            using var handle = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(handle);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Releasing the SQLite connection pool failed");
        }
    }
}

/// <summary>
/// SQLite transaction wrapper with count synchronization support
/// </summary>
internal sealed class SqliteIndexTransaction : IIndexTransaction
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqlitePersistence.IAmbientScope _scope;
    private readonly SqlitePersistence _persistence;
    private bool _completed;

    internal SqliteIndexTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqlitePersistence.IAmbientScope scope,
        SqlitePersistence persistence)
    {
        _connection = connection;
        _transaction = transaction;
        _scope = scope;
        _persistence = persistence;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.CommitAsync(cancellationToken);
        Complete();
        // Refresh count from DB to ensure accuracy after commit
        await _persistence.RefreshCountAsync(cancellationToken);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.RollbackAsync(cancellationToken);
        Complete();
        // Refresh count from DB to revert any in-memory count changes
        await _persistence.RefreshCountAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _transaction.RollbackAsync();
            Complete();
            // Refresh count from DB to revert any in-memory count changes
            await _persistence.RefreshCountAsync();
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Closes the ambient scope before the count refresh, so that refresh opens its own connection
    /// rather than reusing this transaction's.
    /// </summary>
    private void Complete()
    {
        _completed = true;
        _scope.Complete();
    }
}
