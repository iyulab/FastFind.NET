using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// Path handling in <see cref="StringPool"/> on POSIX file systems, where a backslash is an
/// ordinary filename character and case is significant. These properties cannot be covered from
/// the Windows test project, which is why they live here.
/// </summary>
[Trait("Category", "Functional")]
public class StringPoolPathTests
{
    [Fact]
    public void InternPath_Should_Return_The_Path_Unchanged()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX-only semantics

        var path = "/var/lib/fastfind/data/report.txt";

        var retrieved = StringPool.GetString(StringPool.InternPath(path));

        retrieved.Should().Be(path, "an interned path must remain usable for I/O");
    }

    [Fact]
    public void InternPath_Should_Treat_Backslash_As_A_Filename_Character()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX-only semantics

        // Legal POSIX filename. Folding it to a separator would rewrite the path to a different,
        // non-existent location.
        var path = "/tmp/fastfind-pool/odd" + '\\' + "name.txt";

        var retrieved = StringPool.GetString(StringPool.InternPath(path));

        retrieved.Should().Be(path);
    }

    [Fact]
    public void InternPath_Should_Keep_Case_Variant_Paths_Distinct()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX-only semantics

        // Two distinct files on a case-sensitive file system.
        var lower = "/tmp/fastfind-pool/alpha.txt";
        var upper = "/tmp/fastfind-pool/ALPHA.TXT";

        var lowerId = StringPool.InternPath(lower);
        var upperId = StringPool.InternPath(upper);

        upperId.Should().NotBe(lowerId);
        StringPool.GetString(lowerId).Should().Be(lower);
        StringPool.GetString(upperId).Should().Be(upper);
    }

    [Fact]
    public void FastFileItem_Should_Expose_Paths_Usable_For_IO()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX-only semantics

        var directory = "/home/user/docs";
        var fullPath = directory + "/File1.txt";

        var item = new FastFileItem(fullPath, "File1.txt", directory, ".txt",
            10, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal, '/');

        item.FullPath.Should().Be(fullPath);
        item.DirectoryPath.Should().Be(directory);
        item.Name.Should().Be("File1.txt");
    }
}
