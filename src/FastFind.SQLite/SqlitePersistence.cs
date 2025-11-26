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

        await using var cmd = CreateInsertCommand(item);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        Interlocked.Increment(ref _count);
    }

    /// <inheritdoc/>
    public async Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReady();

        var count = 0;
        await using var transaction = await _connection!.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested) break;

                await using var cmd = CreateInsertCommand(item);
                cmd.Transaction = (SqliteTransaction)transaction;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                count++;
            }

            await transaction.CommitAsync(cancellationToken);
            Interlocked.Add(ref _count, count);

            _logger?.LogDebug("Batch inserted {Count} items", count);
            return count;
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
        cmd.Parameters.AddWithValue("@full_path", fullPath);

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

                param.Value = path;
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
        cmd.Parameters.AddWithValue("@full_path", fullPath);

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
        cmd.Parameters.AddWithValue("@full_path", fullPath);

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

        await using var cmd = _connection!.CreateCommand();

        if (recursive)
        {
            cmd.CommandText = SqliteSchema.GetByDirectoryRecursive;
            cmd.Parameters.AddWithValue("@directory_pattern", directoryPath.TrimEnd('\\', '/') + "%");
        }
        else
        {
            cmd.CommandText = SqliteSchema.GetByDirectory;
            cmd.Parameters.AddWithValue("@directory_path", directoryPath);
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
        return new SqliteIndexTransaction((SqliteTransaction)transaction);
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
        cmd.Parameters.AddWithValue("@full_path", item.FullPath);
        cmd.Parameters.AddWithValue("@name", item.Name);
        cmd.Parameters.AddWithValue("@directory_path", item.DirectoryPath);
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        _disposed = true;
        _isReady = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        _connection?.Close();
        _connection?.Dispose();

        _disposed = true;
        _isReady = false;
    }
}

/// <summary>
/// SQLite transaction wrapper
/// </summary>
internal sealed class SqliteIndexTransaction : IIndexTransaction
{
    private readonly SqliteTransaction _transaction;
    private bool _completed;

    public SqliteIndexTransaction(SqliteTransaction transaction)
    {
        _transaction = transaction;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _transaction.RollbackAsync();
        }
        await _transaction.DisposeAsync();
    }
}
