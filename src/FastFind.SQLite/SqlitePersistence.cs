using System.Runtime.CompilerServices;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.SQLite.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FastFind.SQLite;

/// <summary>
/// SQLite-based persistence provider for FastFind.NET
/// Features: FTS5 full-text search, WAL mode, optimized queries
/// </summary>
public sealed class SqlitePersistence : IIndexPersistence
{
    private readonly ILogger<SqlitePersistence>? _logger;
    private readonly PersistenceConfiguration _config;
    private SqliteConnection? _connection;
    private bool _disposed;
    private long _count;
    private bool _isReady;
    private readonly object _countSyncLock = new();
    private SqliteIndexTransaction? _activeTransaction;
    private readonly SemaphoreSlim _bulkOperationLock = new(1, 1);

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

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _config.StoragePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);

        // Apply PRAGMA settings
        var pragmas = SqliteSchema.GetPragmaSettings(
            _config.UseWAL,
            _config.CacheSize,
            _config.PageSize,
            _config.UseMmap,
            _config.MmapSize);

        await ExecuteNonQueryAsync(pragmas, cancellationToken);

        // Metadata first: it carries the schema version that decides whether the rest can be kept.
        await ExecuteNonQueryAsync(SqliteSchema.CreateMetadataTable, cancellationToken);
        await DiscardStoreIfSchemaChangedAsync(cancellationToken);

        // Create tables
        await ExecuteNonQueryAsync(SqliteSchema.CreateFilesTable, cancellationToken);
        await ExecuteNonQueryAsync(SqliteSchema.CreateStatisticsTable, cancellationToken);

        // Create FTS if enabled
        if (_config.EnableFullTextSearch)
        {
            await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTable, cancellationToken);
            await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken);
        }

        // Create indexes
        await ExecuteNonQueryAsync(SqliteSchema.CreateIndexes, cancellationToken);

        // Initialize statistics if not exists
        await ExecuteNonQueryAsync(
            $"INSERT OR IGNORE INTO statistics (id, created_at, updated_at) VALUES (1, {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, {DateTimeOffset.UtcNow.ToUnixTimeSeconds()})",
            cancellationToken);

        await using (var writeVersion = _connection.CreateCommand())
        {
            writeVersion.CommandText =
                "INSERT INTO metadata (key, value) VALUES (@key, @value) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            writeVersion.Parameters.AddWithValue("@key", SqliteSchema.SchemaVersionKey);
            writeVersion.Parameters.AddWithValue("@value", SqliteSchema.CurrentVersion.ToString());
            await writeVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        // Load count
        _count = await GetCountFromDbAsync(cancellationToken);
        _isReady = true;

        _logger?.LogInformation("SQLite persistence initialized at {Path} with {Count} items", _config.StoragePath, _count);
    }

    /// <summary>
    /// Drops the stored index when it was written by a different schema version.
    /// </summary>
    /// <remarks>
    /// The store is a cache of the file system, so a rebuild costs one re-index and avoids
    /// carrying migration code for every past shape. A database with no recorded version predates
    /// version tracking and is treated as stale.
    /// </remarks>
    private async Task DiscardStoreIfSchemaChangedAsync(CancellationToken cancellationToken)
    {
        var tableExists = await ScalarAsync(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'files' LIMIT 1",
            cancellationToken);

        if (tableExists is null) return; // nothing stored yet

        var recorded = await ScalarAsync(
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

        await ExecuteNonQueryAsync(SqliteSchema.DropAll, cancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    /// <inheritdoc/>
    public async Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        // Check if item exists before insert to correctly track count for UPSERT
        var existsBefore = await ExistsAsync(item.FullPath, cancellationToken);

        await using var cmd = CreateInsertCommand(item);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Only increment count for actual INSERT, not UPDATE (UPSERT)
        if (!existsBefore)
        {
            Interlocked.Increment(ref _count);
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
        await using var transaction = await _connection!.BeginTransactionAsync(cancellationToken);

        try
        {
            // Reuse command with parameters for efficiency
            await using var cmd = _connection.CreateCommand();
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
            await RefreshCountAsync(cancellationToken);

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
    ///
    /// FTS5 Safety: This method separates data insertion from FTS rebuild to prevent
    /// "database disk image is malformed" errors. The FTS rebuild uses SQLite's
    /// official 'rebuild' command which is atomic and safe.
    /// </summary>
    public async Task<int> AddBulkOptimizedAsync(IList<FastFileItem> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        if (items.Count == 0) return 0;

        const int batchSize = 500; // SQLite max variables / 11 params per row
        var totalInserted = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Acquire exclusive lock for bulk operations to prevent concurrent FTS manipulation
        await _bulkOperationLock.WaitAsync(cancellationToken);
        try
        {
            // Apply bulk loading optimizations
            await ExecuteNonQueryAsync(SqliteSchema.BulkLoadPragmas, cancellationToken);

            // PHASE 1: Disable FTS triggers before bulk insert (outside transaction)
            if (_config.EnableFullTextSearch)
            {
                await ExecuteNonQueryAsync(SqliteSchema.DisableFtsTriggers, cancellationToken);
            }

            // PHASE 2: Insert data in transaction
            await ExecuteNonQueryAsync("BEGIN IMMEDIATE", cancellationToken);
            try
            {
                for (var i = 0; i < items.Count; i += batchSize)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var batch = items.Skip(i).Take(batchSize).ToList();
                    var inserted = await InsertBatchMultiValueInternalAsync(batch, cancellationToken);
                    totalInserted += inserted;
                }

                await ExecuteNonQueryAsync("COMMIT", cancellationToken);
            }
            catch
            {
                try { await ExecuteNonQueryAsync("ROLLBACK", cancellationToken); } catch { /* ignore */ }

                // Re-enable FTS triggers on failure
                if (_config.EnableFullTextSearch)
                {
                    try { await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken); } catch { /* ignore */ }
                }
                throw;
            }

            // PHASE 3: Rebuild FTS index AFTER data commit (separate operation)
            // This prevents FTS corruption by using SQLite's official 'rebuild' command
            if (_config.EnableFullTextSearch)
            {
                try
                {
                    // Use the safe FTS5 'rebuild' command
                    await ExecuteNonQueryAsync(SqliteSchema.BulkRebuildFts, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "FTS rebuild failed, attempting integrity check and recovery");
                    await TryRecoverFtsAsync(cancellationToken);
                }
                finally
                {
                    // Always re-enable triggers
                    try { await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken); } catch { /* ignore */ }
                }
            }

            // Refresh count from DB to accurately reflect UPSERT behavior
            await RefreshCountAsync(cancellationToken);

            stopwatch.Stop();
            var rate = totalInserted / stopwatch.Elapsed.TotalSeconds;
            _logger?.LogInformation(
                "Bulk inserted {Count:N0} items in {Time:F2}s ({Rate:N0} items/sec)",
                totalInserted, stopwatch.Elapsed.TotalSeconds, rate);

            return totalInserted;
        }
        finally
        {
            // Restore normal PRAGMA settings
            try
            {
                await ExecuteNonQueryAsync(SqliteSchema.RestoreNormalPragmas, cancellationToken);
            }
            catch
            {
                // Ignore PRAGMA restore errors
            }
            _bulkOperationLock.Release();
        }
    }

    /// <summary>
    /// Attempts to recover a corrupted FTS5 index by rebuilding it.
    /// This handles the "database disk image is malformed" error for FTS5.
    /// </summary>
    private async Task TryRecoverFtsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Drop and recreate the FTS table, then rebuild from content table
            _logger?.LogWarning("Attempting FTS recovery: DROP → CREATE → REBUILD");
            await ExecuteNonQueryAsync("DROP TABLE IF EXISTS files_fts;", cancellationToken);
            await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTable, cancellationToken);
            await ExecuteNonQueryAsync("INSERT INTO files_fts(files_fts) VALUES('rebuild');", cancellationToken);
            await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken);
            _logger?.LogInformation("FTS index recovered successfully via DROP/CREATE/REBUILD");
        }
        catch (Exception rebuildEx)
        {
            _logger?.LogError(rebuildEx, "FTS recovery failed. FTS search may not work correctly until database is recreated.");
        }
    }

    private async Task<int> InsertBatchMultiValueAsync(
        List<FastFileItem> batch,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return 0;

        // Build multi-value INSERT statement
        var sb = new System.Text.StringBuilder(SqliteSchema.BulkInsertPrefix);

        await using var cmd = _connection!.CreateCommand();
        cmd.Transaction = (SqliteTransaction)transaction;

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
    /// Internal version of InsertBatchMultiValueAsync without explicit transaction parameter.
    /// Used when transaction is controlled externally via BEGIN/COMMIT.
    /// </summary>
    private async Task<int> InsertBatchMultiValueInternalAsync(
        List<FastFileItem> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return 0;

        // Build multi-value INSERT statement
        var sb = new System.Text.StringBuilder(SqliteSchema.BulkInsertPrefix);

        await using var cmd = _connection!.CreateCommand();

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
    ///
    /// FTS5 Safety: Uses separate phases for data insertion and FTS rebuild to prevent
    /// "database disk image is malformed" errors.
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

        try
        {
            // Apply bulk loading optimizations
            await ExecuteNonQueryAsync(SqliteSchema.BulkLoadPragmas, cancellationToken);

            // PHASE 1: Disable FTS triggers before bulk insert
            if (_config.EnableFullTextSearch)
            {
                await ExecuteNonQueryAsync(SqliteSchema.DisableFtsTriggers, cancellationToken);
            }

            // PHASE 2: Stream and insert data
            await foreach (var item in items.WithCancellation(cancellationToken))
            {
                buffer.Add(item);

                if (buffer.Count >= bufferSize)
                {
                    var inserted = await FlushBufferAsync(buffer, cancellationToken);
                    totalInserted += inserted;
                    progress?.Report(totalInserted);
                    buffer.Clear();
                }
            }

            // Flush remaining items
            if (buffer.Count > 0)
            {
                var inserted = await FlushBufferAsync(buffer, cancellationToken);
                totalInserted += inserted;
                progress?.Report(totalInserted);
            }

            // PHASE 3: Rebuild FTS index AFTER all data is committed
            if (_config.EnableFullTextSearch)
            {
                _logger?.LogInformation("Rebuilding FTS index using safe rebuild command...");
                try
                {
                    await ExecuteNonQueryAsync(SqliteSchema.BulkRebuildFts, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "FTS rebuild failed, attempting recovery");
                    await TryRecoverFtsAsync(cancellationToken);
                }
                finally
                {
                    // Always re-enable triggers
                    try { await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken); } catch { /* ignore */ }
                }
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
            await ExecuteNonQueryAsync(SqliteSchema.RestoreNormalPragmas, cancellationToken);
        }
    }

    private async Task<int> FlushBufferAsync(List<FastFileItem> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Count == 0) return 0;

        const int batchSize = 500;
        var totalInserted = 0;

        await using var transaction = await _connection!.BeginTransactionAsync(cancellationToken);

        try
        {
            for (var i = 0; i < buffer.Count; i += batchSize)
            {
                var batch = buffer.Skip(i).Take(batchSize).ToList();
                totalInserted += await InsertBatchMultiValueAsync(batch, transaction, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return totalInserted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var cmd = _connection!.CreateCommand();
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
        await using var transaction = await _connection!.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var cmd = _connection.CreateCommand();
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

        await using var cmd = CreateInsertCommand(item);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var cmd = _connection!.CreateCommand();
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

        await using var cmd = _connection!.CreateCommand();
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

        await using var cmd = _connection!.CreateCommand();
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
        await using var cmd = _connection!.CreateCommand();

        if (recursive)
        {
            cmd.CommandText = SqliteSchema.GetByDirectoryRecursive;
            cmd.Parameters.AddWithValue("@directory_pattern", normalizedPath.TrimEnd('\\', '/') + "%");
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

        await using var cmd = _connection!.CreateCommand();
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

        await ExecuteNonQueryAsync($"DELETE FROM files; UPDATE statistics SET total_items = 0, total_files = 0, total_directories = 0, updated_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} WHERE id = 1;", cancellationToken);

        if (_config.EnableFullTextSearch)
        {
            await ExecuteNonQueryAsync("DELETE FROM files_fts;", cancellationToken);
        }

        _count = 0;
        _logger?.LogInformation("Cleared all items from persistence");
    }

    /// <inheritdoc/>
    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        _logger?.LogInformation("Starting optimization...");

        // Analyze tables for query optimization
        await ExecuteNonQueryAsync("ANALYZE;", cancellationToken);

        // Optimize FTS index if enabled
        if (_config.EnableFullTextSearch)
        {
            await ExecuteNonQueryAsync(SqliteSchema.OptimizeFts, cancellationToken);
        }

        await ExecuteNonQueryAsync(
            $"UPDATE statistics SET last_optimized = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, updated_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} WHERE id = 1",
            cancellationToken);

        _logger?.LogInformation("Optimization complete");
    }

    /// <inheritdoc/>
    public async Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        await using var cmd = _connection!.CreateCommand();
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
    public async Task<IIndexTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var transaction = await _connection!.BeginTransactionAsync(cancellationToken);
        var indexTransaction = new SqliteIndexTransaction((SqliteTransaction)transaction, this);
        _activeTransaction = indexTransaction;
        return indexTransaction;
    }

    /// <inheritdoc/>
    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        _logger?.LogInformation("Starting VACUUM...");

        await ExecuteNonQueryAsync("VACUUM;", cancellationToken);

        await ExecuteNonQueryAsync(
            $"UPDATE statistics SET last_vacuumed = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, updated_at = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} WHERE id = 1",
            cancellationToken);

        _logger?.LogInformation("VACUUM complete");
    }

    private SqliteCommand CreateInsertCommand(FastFileItem item)
    {
        var cmd = _connection!.CreateCommand();
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

    private async Task<long> GetCountFromDbAsync(CancellationToken cancellationToken)
    {
        await using var cmd = _connection!.CreateCommand();
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
        var dbCount = await GetCountFromDbAsync(cancellationToken);
        Interlocked.Exchange(ref _count, dbCount);
    }

    /// <summary>
    /// Clears the active transaction reference.
    /// </summary>
    internal void ClearActiveTransaction()
    {
        _activeTransaction = null;
    }

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var cmd = _connection!.CreateCommand();
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
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (_connection != null)
        {
            try
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
                // Connection already disposed, ignore
            }
            catch (NullReferenceException)
            {
                // Connection internal state issue during concurrent dispose, ignore
            }
        }

        try
        {
            _bulkOperationLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Semaphore already disposed
        }

        _disposed = true;
        _isReady = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _connection?.Close();
            _connection?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Connection already disposed, ignore
        }
        catch (NullReferenceException)
        {
            // Connection internal state issue during concurrent dispose, ignore
        }

        try
        {
            _bulkOperationLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Semaphore already disposed
        }

        _disposed = true;
        _isReady = false;
    }
}

/// <summary>
/// SQLite transaction wrapper with count synchronization support
/// </summary>
internal sealed class SqliteIndexTransaction : IIndexTransaction
{
    private readonly SqliteTransaction _transaction;
    private readonly SqlitePersistence _persistence;
    private bool _completed;

    public SqliteIndexTransaction(SqliteTransaction transaction, SqlitePersistence persistence)
    {
        _transaction = transaction;
        _persistence = persistence;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
        _persistence.ClearActiveTransaction();
        // Refresh count from DB to ensure accuracy after commit
        await _persistence.RefreshCountAsync(cancellationToken);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
        _persistence.ClearActiveTransaction();
        // Refresh count from DB to revert any in-memory count changes
        await _persistence.RefreshCountAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _transaction.RollbackAsync();
            _persistence.ClearActiveTransaction();
            // Refresh count from DB to revert any in-memory count changes
            await _persistence.RefreshCountAsync();
        }
        await _transaction.DisposeAsync();
    }
}
