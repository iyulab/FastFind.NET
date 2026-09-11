namespace FastFind.SQLite.Schema;

/// <summary>
/// SQLite database schema definitions and migration scripts
/// </summary>
internal static class SqliteSchema
{
    /// <summary>
    /// Current schema version. A database created under a different version is rebuilt on open —
    /// the store is a cache of the file system, so discarding it is cheaper and safer than
    /// migrating it.
    /// </summary>
    /// <remarks>
    /// Version 3 drops the full-text index. No query ever read it, so maintaining it on every
    /// write bought nothing; a version 2 database still carries the table and its triggers and is
    /// rebuilt without them.
    /// Version 2 stores paths with their original casing. Version 1 lower-cased every path on
    /// write, which made a case-sensitive search impossible and lost the true path on
    /// case-sensitive file systems.
    /// </remarks>
    public const int CurrentVersion = 3;

    /// <summary>Metadata key holding the schema version of an existing database.</summary>
    public const string SchemaVersionKey = "schema_version";

    /// <summary>
    /// Collation for path columns: paths compare case-insensitively on Windows and exactly
    /// elsewhere, matching the host file system. Casing is always preserved in storage.
    /// </summary>
    private static string PathCollation => OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;

    /// <summary>
    /// Drops every table and trigger owned by this schema, for a rebuild after a version change.
    /// The full-text table and its triggers are no longer created, but a version 2 database still
    /// has them, so they are dropped here to clean one up.
    /// </summary>
    public const string DropAll = """
        DROP TRIGGER IF EXISTS files_ai;
        DROP TRIGGER IF EXISTS files_ad;
        DROP TRIGGER IF EXISTS files_au;
        DROP TABLE IF EXISTS files_fts;
        DROP TABLE IF EXISTS files;
        DROP TABLE IF EXISTS statistics;
        """;

    /// <summary>
    /// SQL to create the main files table
    /// </summary>
    public static string CreateFilesTable => $"""
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            full_path TEXT NOT NULL UNIQUE{PathCollation},
            name TEXT NOT NULL,
            directory_path TEXT NOT NULL{PathCollation},
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

    /// <summary>Default mmap window when mmap is enabled without an explicit size: 256 MiB.</summary>
    public const long DefaultMmapSize = 268435456;

    /// <summary>
    /// PRAGMA settings applied to a freshly opened connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>page_size</c> is emitted <b>first</b>, and deliberately so. It is a persistent property of
    /// the database file that can only be set before the first table is created, and SQLite ignores
    /// it once the journal mode is WAL. Emitting it after <c>journal_mode</c> made the configured
    /// page size a silent no-op for every database created with WAL enabled — which is the default.
    /// </para>
    /// <para>
    /// <c>cache_size</c> is emitted in its negative form, which SQLite reads as a size in
    /// <b>KiB</b> rather than a page count.
    /// </para>
    /// </remarks>
    public static string GetDatabasePragmas(bool useWal = true, int pageSize = 4096)
    {
        return $"""
            PRAGMA page_size = {pageSize};
            PRAGMA journal_mode = {(useWal ? "WAL" : "DELETE")};
            """;
    }

    /// <summary>
    /// PRAGMA settings that belong to a <i>connection</i> and are therefore applied to every
    /// connection the provider opens, not once when the store is created.
    /// </summary>
    /// <remarks>
    /// A connection handed back by the pool can carry settings a previous operation left on it -
    /// a bulk write window raises <c>cache_size</c>, for instance - so these are set
    /// unconditionally on open rather than restored afterwards.
    /// </remarks>
    public static string GetConnectionPragmas(int cacheSize = 10000, bool useMmap = true, long mmapSize = 0)
    {
        var mmap = useMmap ? (mmapSize > 0 ? mmapSize : DefaultMmapSize) : 0;
        return $"""
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -{cacheSize};
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
    // The directory itself, or anything beneath it at a separator. A bare "dir%" also matched a
    // sibling sharing the prefix (App vs App.Tests), and an unescaped '_' matched any character.
    public const string GetByDirectoryRecursive = """
        SELECT * FROM files
        WHERE directory_path = @directory_path
           OR directory_path LIKE @directory_pattern ESCAPE '!'
           OR directory_path LIKE @directory_alt_pattern ESCAPE '!'
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
    /// </summary>
    public const string BulkLoadPragmas = """
        PRAGMA cache_size = -32000;
        """;

}
