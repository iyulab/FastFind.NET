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

        // Create tables
        await ExecuteNonQueryAsync(SqliteSchema.CreateFilesTable, cancellationToken);
        await ExecuteNonQueryAsync(SqliteSchema.CreateMetadataTable, cancellationToken);
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

        // Load count
        _count = await GetCountFromDbAsync(cancellationToken);
        _isReady = true;

        _logger?.LogInformation("SQLite persistence initialized at {Path} with {Count} items", _config.StoragePath, _count);
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
    /// Uses multi-value INSERT statements and optimized PRAGMA settings
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

            // Use IMMEDIATE transaction to acquire write lock early and prevent conflicts
            await ExecuteNonQueryAsync("BEGIN IMMEDIATE", cancellationToken);

            try
            {
                // Disable FTS triggers within transaction
                if (_config.EnableFullTextSearch)
                {
                    await ExecuteNonQueryAsync(SqliteSchema.DisableFtsTriggers, cancellationToken);
                }

                for (var i = 0; i < items.Count; i += batchSize)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var batch = items.Skip(i).Take(batchSize).ToList();
                    var inserted = await InsertBatchMultiValueInternalAsync(batch, cancellationToken);
                    totalInserted += inserted;
                }

                // Rebuild FTS index within transaction
                if (_config.EnableFullTextSearch)
                {
                    await ExecuteNonQueryAsync(SqliteSchema.BulkRebuildFts, cancellationToken);
                    await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken);
                }

                await ExecuteNonQueryAsync("COMMIT", cancellationToken);
            }
            catch
            {
                await ExecuteNonQueryAsync("ROLLBACK", cancellationToken);

                // Re-enable FTS triggers if they were disabled
                if (_config.EnableFullTextSearch)
                {
                    try
                    {
                        await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken);
                    }
                    catch
                    {
                        // Ignore trigger recreation errors during rollback
                    }
                }
                throw;
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

            if (_config.EnableFullTextSearch)
            {
                await ExecuteNonQueryAsync(SqliteSchema.DisableFtsTriggers, cancellationToken);
            }

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

            // Rebuild FTS index
            if (_config.EnableFullTextSearch)
            {
                _logger?.LogInformation("Rebuilding FTS index...");
                await ExecuteNonQueryAsync(SqliteSchema.BulkRebuildFts, cancellationToken);
                await ExecuteNonQueryAsync(SqliteSchema.CreateFtsTriggers, cancellationToken);
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

        await using var cmd = _connection!.CreateCommand();

        // Use FTS5 for text search if enabled and applicable
        if (_config.EnableFullTextSearch && !string.IsNullOrEmpty(query.SearchText) && !query.UseRegex)
        {
            // Convert wildcards to FTS5 syntax
            var ftsPattern = ConvertToFtsPattern(query.SearchText);
            cmd.CommandText = SqliteSchema.SearchByNameFts;
            cmd.Parameters.AddWithValue("@pattern", ftsPattern);
            cmd.Parameters.AddWithValue("@limit", query.MaxResults ?? 10000);
            cmd.Parameters.AddWithValue("@offset", 0);
        }
        else if (!string.IsNullOrEmpty(query.SearchText))
        {
            // Fallback to LIKE search
            var likePattern = ConvertToLikePattern(query.SearchText);
            cmd.CommandText = SqliteSchema.SearchByNameLike;
            cmd.Parameters.AddWithValue("@pattern", likePattern);
            cmd.Parameters.AddWithValue("@limit", query.MaxResults ?? 10000);
            cmd.Parameters.AddWithValue("@offset", 0);
        }
        else
        {
            // Return all files with limit
            cmd.CommandText = "SELECT * FROM files LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", query.MaxResults ?? 10000);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadFileItem(reader);

            // Apply additional filters that can't be done in SQL
            if (ApplyFilters(item, query))
            {
                yield return item;
            }
        }
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
        var createdTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("created_time"))).DateTime;
        var modifiedTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("modified_time"))).DateTime;
        var accessedTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(reader.GetOrdinal("accessed_time"))).DateTime;
        var attributes = (FileAttributes)reader.GetInt32(reader.GetOrdinal("attributes"));
        var driveLetter = reader.GetString(reader.GetOrdinal("drive_letter"))[0];

        return new FastFileItem(
            fullPath, name, directoryPath, extension,
            size, createdTime, modifiedTime, accessedTime,
            attributes, driveLetter
        );
    }

    private static string ConvertToFtsPattern(string pattern)
    {
        // Convert wildcard pattern to FTS5 query
        // * -> *
        // ? -> handled post-filter
        var ftsPattern = pattern
            .Replace("*", "\"*\"")
            .Replace("?", "*");

        // If no wildcards, add prefix match
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            ftsPattern = $"\"{pattern}\"*";
        }

        return ftsPattern;
    }

    private static string ConvertToLikePattern(string pattern)
    {
        // Convert glob pattern to SQL LIKE pattern
        return pattern
            .Replace("*", "%")
            .Replace("?", "_");
    }

    private static bool ApplyFilters(FastFileItem item, SearchQuery query)
    {
        // Apply extension filter
        if (!string.IsNullOrEmpty(query.ExtensionFilter))
        {
            var ext = item.Extension.TrimStart('.');
            var filterExt = query.ExtensionFilter.TrimStart('.');
            if (!ext.Equals(filterExt, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Apply size filter
        if (query.MinSize.HasValue && item.Size < query.MinSize.Value)
            return false;
        if (query.MaxSize.HasValue && item.Size > query.MaxSize.Value)
            return false;

        // Apply date filter
        if (query.MinModifiedDate.HasValue && item.ModifiedTime < query.MinModifiedDate.Value)
            return false;
        if (query.MaxModifiedDate.HasValue && item.ModifiedTime > query.MaxModifiedDate.Value)
            return false;

        // Apply directory filter
        if (!query.IncludeDirectories && item.IsDirectory)
            return false;
        if (!query.IncludeFiles && !item.IsDirectory)
            return false;

        // Apply hidden filter
        if (!query.IncludeHidden && item.IsHidden)
            return false;

        // Apply system filter
        if (!query.IncludeSystem && item.IsSystem)
            return false;

        return true;
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
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        return path.ToLowerInvariant().Replace('/', '\\');
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
