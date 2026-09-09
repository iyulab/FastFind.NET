using FastFind.Models;
using FastFind.SQLite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FastFind.Windows.Tests.SQLite;

/// <summary>
/// Verifies that <see cref="PersistenceConfiguration"/> values which are persistent properties of
/// the database file actually reach the file. Connection-scoped pragmas (cache size, mmap window)
/// cannot be observed from a second connection and are not covered here.
/// </summary>
[Trait("Category", "SQLite")]
public class SqliteConfigurationTests : IAsyncLifetime
{
    private readonly List<string> _paths = new();

    public Task InitializeAsync() => Task.CompletedTask;

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

    [Fact]
    public async Task PageSize_Should_Reach_The_File_With_WAL_Enabled()
    {
        // Regression guard: page_size must be emitted before journal_mode. SQLite ignores a
        // page_size change once the database is in WAL mode, and WAL is the default, so emitting
        // it afterwards made the configured page size a silent no-op for every database.
        var config = NewConfig() with { PageSize = 8192, UseWAL = true };

        await Initialize(config);

        (await ReadPragma<long>(config.StoragePath, "page_size")).Should().Be(8192);
        (await ReadPragma<string>(config.StoragePath, "journal_mode")).Should().Be("wal");
    }

    [Fact]
    public async Task PageSize_Should_Reach_The_File_With_WAL_Disabled()
    {
        var config = NewConfig() with { PageSize = 16384, UseWAL = false };

        await Initialize(config);

        (await ReadPragma<long>(config.StoragePath, "page_size")).Should().Be(16384);
    }

    [Fact]
    public async Task Default_Configuration_Should_Use_A_4096_Byte_Page_In_WAL_Mode()
    {
        var config = NewConfig();

        await Initialize(config);

        (await ReadPragma<long>(config.StoragePath, "page_size")).Should().Be(4096);
        (await ReadPragma<string>(config.StoragePath, "journal_mode")).Should().Be("wal");
    }

    private PersistenceConfiguration NewConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fastfind-cfg-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return PersistenceConfiguration.CreateSQLite(path);
    }

    private static async Task Initialize(PersistenceConfiguration config)
    {
        await using var persistence = new SqlitePersistence(config);
        await persistence.InitializeAsync();
    }

    /// <summary>
    /// Reads a pragma on a connection other than the one that created the database, so only
    /// properties actually stored in the file can satisfy the assertion.
    /// </summary>
    private static async Task<T> ReadPragma<T>(string databasePath, string pragma)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";

        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T))!;
    }
}
