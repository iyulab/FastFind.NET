namespace FastFind.SQLite.Schema;

/// <summary>
/// SQLite database schema definitions and migration scripts
/// </summary>
internal static class SqliteSchema
{
    /// <summary>
    /// Current schema version
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// SQL to create the main files table
    /// </summary>
    public const string CreateFilesTable = """
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            full_path TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            directory_path TEXT NOT NULL,
            extension TEXT NOT NULL,
            size INTEGER NOT NULL,
            created_time INTEGER NOT NULL,
            modified_time INTEGER NOT NULL,
            accessed_time INTEGER NOT NULL,
            attributes INTEGER NOT NULL,
            drive_letter TEXT NOT NULL,
            is_directory INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// SQL to create the FTS5 virtual table for full-text search
    /// </summary>
    public const string CreateFtsTable = """
        CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
            name,
            full_path,
            content='files',
            content_rowid='id',
            tokenize='unicode61 remove_diacritics 2'
        );
        """;

    /// <summary>
    /// SQL triggers to keep FTS table in sync with main table
    /// </summary>
    public const string CreateFtsTriggers = """
        CREATE TRIGGER IF NOT EXISTS files_ai AFTER INSERT ON files BEGIN
            INSERT INTO files_fts(rowid, name, full_path) VALUES (new.id, new.name, new.full_path);
        END;

        CREATE TRIGGER IF NOT EXISTS files_ad AFTER DELETE ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name, full_path) VALUES('delete', old.id, old.name, old.full_path);
        END;

        CREATE TRIGGER IF NOT EXISTS files_au AFTER UPDATE ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name, full_path) VALUES('delete', old.id, old.name, old.full_path);
            INSERT INTO files_fts(rowid, name, full_path) VALUES (new.id, new.name, new.full_path);
        END;
        """;

    /// <summary>
    /// SQL to create indexes for fast lookups
    /// </summary>
    public const string CreateIndexes = """
        CREATE INDEX IF NOT EXISTS idx_files_directory ON files(directory_path);
        CREATE INDEX IF NOT EXISTS idx_files_extension ON files(extension);
        CREATE INDEX IF NOT EXISTS idx_files_name ON files(name);
        CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified_time);
        CREATE INDEX IF NOT EXISTS idx_files_size ON files(size);
        CREATE INDEX IF NOT EXISTS idx_files_drive ON files(drive_letter);
        """;

    /// <summary>
    /// SQL to create metadata table for tracking schema version and statistics
    /// </summary>
    public const string CreateMetadataTable = """
        CREATE TABLE IF NOT EXISTS metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    /// <summary>
    /// SQL to create statistics table
    /// </summary>
    public const string CreateStatisticsTable = """
        CREATE TABLE IF NOT EXISTS statistics (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            total_items INTEGER DEFAULT 0,
            total_files INTEGER DEFAULT 0,
            total_directories INTEGER DEFAULT 0,
            last_optimized INTEGER,
            last_vacuumed INTEGER,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// PRAGMA settings for optimal performance
    /// </summary>
    public static string GetPragmaSettings(bool useWal = true, int cacheSize = 10000, int pageSize = 4096, bool useMmap = true, long mmapSize = 0)
    {
        var mmap = useMmap ? (mmapSize > 0 ? mmapSize : 268435456) : 0; // Default 256MB
        return $"""
            PRAGMA journal_mode = {(useWal ? "WAL" : "DELETE")};
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -{cacheSize};
            PRAGMA page_size = {pageSize};
            PRAGMA temp_store = MEMORY;
            PRAGMA mmap_size = {mmap};
            PRAGMA busy_timeout = 5000;
            """;
    }

    /// <summary>
    /// SQL for inserting a file
    /// </summary>
    public const string InsertFile = """
        INSERT INTO files (full_path, name, directory_path, extension, size, created_time, modified_time, accessed_time, attributes, drive_letter, is_directory)
        VALUES (@full_path, @name, @directory_path, @extension, @size, @created_time, @modified_time, @accessed_time, @attributes, @drive_letter, @is_directory)
        ON CONFLICT(full_path) DO UPDATE SET
            name = excluded.name,
            directory_path = excluded.directory_path,
            extension = excluded.extension,
            size = excluded.size,
            created_time = excluded.created_time,
            modified_time = excluded.modified_time,
            accessed_time = excluded.accessed_time,
            attributes = excluded.attributes,
            drive_letter = excluded.drive_letter,
            is_directory = excluded.is_directory
        """;

    /// <summary>
    /// SQL for searching by name pattern using FTS5
    /// </summary>
    public const string SearchByNameFts = """
        SELECT f.* FROM files f
        INNER JOIN files_fts fts ON f.id = fts.rowid
        WHERE files_fts MATCH @pattern
        ORDER BY rank
        LIMIT @limit OFFSET @offset
        """;

    /// <summary>
    /// SQL for searching by name pattern using LIKE (fallback)
    /// </summary>
    public const string SearchByNameLike = """
        SELECT * FROM files
        WHERE name LIKE @pattern
        ORDER BY name
        LIMIT @limit OFFSET @offset
        """;

    /// <summary>
    /// SQL for getting files in a directory
    /// </summary>
    public const string GetByDirectory = """
        SELECT * FROM files
        WHERE directory_path = @directory_path
        ORDER BY name
        """;

    /// <summary>
    /// SQL for getting files in a directory recursively
    /// </summary>
    public const string GetByDirectoryRecursive = """
        SELECT * FROM files
        WHERE directory_path LIKE @directory_pattern
        ORDER BY full_path
        """;

    /// <summary>
    /// SQL for getting files by extension
    /// </summary>
    public const string GetByExtension = """
        SELECT * FROM files
        WHERE extension = @extension
        ORDER BY name
        """;

    /// <summary>
    /// SQL for getting a file by path
    /// </summary>
    public const string GetByPath = """
        SELECT * FROM files
        WHERE full_path = @full_path
        LIMIT 1
        """;

    /// <summary>
    /// SQL for deleting a file
    /// </summary>
    public const string DeleteFile = """
        DELETE FROM files WHERE full_path = @full_path
        """;

    /// <summary>
    /// SQL for clearing all files
    /// </summary>
    public const string ClearAll = """
        DELETE FROM files;
        DELETE FROM files_fts;
        UPDATE statistics SET total_items = 0, total_files = 0, total_directories = 0, updated_at = @updated_at WHERE id = 1;
        """;

    /// <summary>
    /// SQL for getting statistics
    /// </summary>
    public const string GetStatistics = """
        SELECT
            (SELECT COUNT(*) FROM files) as total_items,
            (SELECT COUNT(*) FROM files WHERE is_directory = 0) as total_files,
            (SELECT COUNT(*) FROM files WHERE is_directory = 1) as total_directories,
            (SELECT COUNT(DISTINCT extension) FROM files) as unique_extensions
        """;

    /// <summary>
    /// SQL for optimizing FTS index
    /// </summary>
    public const string OptimizeFts = """
        INSERT INTO files_fts(files_fts) VALUES('optimize');
        """;

    /// <summary>
    /// SQL for rebuilding FTS index
    /// </summary>
    public const string RebuildFts = """
        INSERT INTO files_fts(files_fts) VALUES('rebuild');
        """;

    /// <summary>
    /// SQL for bulk inserting files (multi-value INSERT)
    /// Use string.Format or StringBuilder to build the VALUES clause
    /// </summary>
    public const string BulkInsertPrefix = """
        INSERT INTO files (full_path, name, directory_path, extension, size, created_time, modified_time, accessed_time, attributes, drive_letter, is_directory)
        VALUES
        """;

    /// <summary>
    /// SQL suffix for bulk insert with UPSERT behavior
    /// </summary>
    public const string BulkInsertSuffix = """
        ON CONFLICT(full_path) DO UPDATE SET
            name = excluded.name,
            directory_path = excluded.directory_path,
            extension = excluded.extension,
            size = excluded.size,
            created_time = excluded.created_time,
            modified_time = excluded.modified_time,
            accessed_time = excluded.accessed_time,
            attributes = excluded.attributes,
            drive_letter = excluded.drive_letter,
            is_directory = excluded.is_directory
        """;

    /// <summary>
    /// PRAGMA settings for high-performance bulk loading
    /// Disables FTS triggers temporarily for maximum insert speed
    /// </summary>
    public const string BulkLoadPragmas = """
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -32000;
        """;

    /// <summary>
    /// Restore normal PRAGMA settings after bulk loading
    /// Note: journal_mode is not changed here since WAL is already set at initialization
    /// </summary>
    public const string RestoreNormalPragmas = """
        PRAGMA synchronous = NORMAL;
        """;

    /// <summary>
    /// Disable FTS triggers for bulk loading
    /// </summary>
    public const string DisableFtsTriggers = """
        DROP TRIGGER IF EXISTS files_ai;
        DROP TRIGGER IF EXISTS files_ad;
        DROP TRIGGER IF EXISTS files_au;
        """;

    /// <summary>
    /// SQL for bulk FTS rebuild after bulk loading (more efficient than triggers)
    /// </summary>
    public const string BulkRebuildFts = """
        DELETE FROM files_fts;
        INSERT INTO files_fts(rowid, name, full_path) SELECT id, name, full_path FROM files;
        """;
}
