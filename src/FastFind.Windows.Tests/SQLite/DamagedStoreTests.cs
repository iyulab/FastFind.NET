using FastFind.Interfaces;
using FastFind.SQLite;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FastFind.Windows.Tests.SQLite;

/// <summary>
/// What opening a store does with a file that is not a usable index.
/// </summary>
/// <remarks>
/// A store whose schema version differs is dropped and rebuilt on open. A file that is not a database, or a
/// database cut short, used to surface as the driver's own exception — error 26 or error 11, the second being
/// the message a user once reported as a bug — so a caller of <see cref="IIndexPersistence"/> needed a reference
/// to <c>Microsoft.Data.Sqlite</c> to catch it, and the message said nothing of what to do next.
/// </remarks>
[Trait("Category", "SQLite")]
public class DamagedStoreTests : IDisposable
{
    private readonly string _dir;

    public DamagedStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fastfind-damaged-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private static async Task<Exception?> OpenAsync(string path)
    {
        try
        {
            await using var store = SqlitePersistence.Create(path);
            await store.InitializeAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task A_File_That_Is_Not_A_Database_Should_Raise_The_Librarys_Own_Exception()
    {
        var path = Path_("garbage.db");
        File.WriteAllBytes(path, new byte[4096].Select((_, i) => (byte)(i * 31 + 7)).ToArray());

        var exception = await OpenAsync(path);

        var damaged = exception.Should().BeOfType<IndexStoreCorruptedException>().Subject;
        damaged.StorePath.Should().Be(path);
        damaged.Message.Should().Contain(path, "the message names the file")
            .And.Contain("delete the file", "and says what to do next");
        damaged.InnerException.Should().BeOfType<SqliteException>("the driver's exception is kept for diagnosis");
        damaged.Should().BeAssignableTo<IOException>("a caller handling I/O failures already handles it");
    }

    [Fact]
    public async Task A_Truncated_Database_Should_Raise_The_Same_Exception()
    {
        var good = Path_("good.db");
        {
            await using var store = SqlitePersistence.Create(good);
            await store.InitializeAsync();
        }
        SqliteConnection.ClearAllPools();

        var bytes = File.ReadAllBytes(good);
        var truncated = Path_("truncated.db");
        File.WriteAllBytes(truncated, bytes.Take(bytes.Length / 2).ToArray());

        var exception = await OpenAsync(truncated);

        exception.Should().BeOfType<IndexStoreCorruptedException>()
            .Which.StorePath.Should().Be(truncated);
    }

    [Fact]
    public async Task A_Healthy_Store_Should_Reopen_Without_Complaint()
    {
        var path = Path_("healthy.db");
        (await OpenAsync(path)).Should().BeNull();
        SqliteConnection.ClearAllPools();

        (await OpenAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task An_Empty_File_Should_Open_As_An_Empty_Store()
    {
        var path = Path_("empty.db");
        File.WriteAllBytes(path, Array.Empty<byte>());

        (await OpenAsync(path)).Should().BeNull("SQLite treats a zero-byte file as a new database");
    }

    [Fact]
    public async Task A_Failure_That_Is_Not_Damage_Should_Not_Be_Called_Damage()
    {
        // The directory does not exist: the file cannot be opened at all, which says nothing about its contents.
        var path = Path.Combine(_dir, "no-such-directory", "index.db");

        var exception = await OpenAsync(path);

        exception.Should().NotBeNull();
        exception.Should().NotBeOfType<IndexStoreCorruptedException>();
    }
}
