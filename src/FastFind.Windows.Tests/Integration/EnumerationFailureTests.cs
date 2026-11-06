using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Mft;
using FastFind.Windows.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// A provider says which locations it could not read at all.
/// </summary>
/// <remarks>
/// The change-journal provider logged "Error enumerating drive" and ended the channel as if the drive had been
/// read; the directory walk returned nothing for a location that was not there. In both cases the index was short of
/// what was asked for and no caller could tell.
/// </remarks>
[Trait("Category", "Functional")]
public class EnumerationFailureTests : IDisposable
{
    private readonly string _root;

    public EnumerationFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-failures-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private IndexingOptions Options() => new()
    {
        SpecificDirectories = { _root },
        ExcludedPaths = new List<string>(),
        ExcludedExtensions = new List<string>(),
        IncludeHidden = true,
        IncludeSystem = true,
    };

    private static async Task DrainAsync(IFileSystemProvider provider, IEnumerable<string> locations, IndexingOptions options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await foreach (var _ in provider.EnumerateFilesAsync(locations, options, timeout.Token)) { }
    }

    [Fact]
    public async Task The_Directory_Walk_Names_A_Location_That_Is_Not_There_And_Not_One_That_Is()
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);
        var missing = Path.Combine(_root, "no-such-directory");

        await DrainAsync(provider, [_root, missing], Options());

        IFileSystemProvider asInterface = provider;
        asInterface.EnumerationFailures.Should().Equal([missing]);
    }

    [Fact]
    public async Task The_Directory_Walk_Starts_Each_Enumeration_With_A_Clean_List()
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);
        var missing = Path.Combine(_root, "no-such-directory");
        IFileSystemProvider asInterface = provider;

        await DrainAsync(provider, [missing], Options());
        asInterface.EnumerationFailures.Should().Equal([missing]);

        await DrainAsync(provider, [_root], Options());
        asInterface.EnumerationFailures.Should().BeEmpty("the second enumeration read everything it was asked for");
    }

    [ElevatedFact]
    public async Task The_Change_Journal_Provider_Names_A_Location_That_Is_Not_There()
    {
        await using var provider = new MftFileSystemProvider(NullLogger<MftFileSystemProvider>.Instance);
        var missing = Path.Combine(_root, "no-such-directory");

        await DrainAsync(provider, [_root, missing], Options());

        IFileSystemProvider asInterface = provider;
        asInterface.EnumerationFailures.Should().Equal([missing]);
    }

    [ElevatedFact]
    public async Task The_Change_Journal_Provider_Names_A_Location_On_A_Drive_It_Cannot_Read()
    {
        await using var provider = new MftFileSystemProvider(NullLogger<MftFileSystemProvider>.Instance);

        // A drive letter that is not mounted: there is no volume to open.
        var unmounted = DriveLetterNotInUse() + @":\data";
        await DrainAsync(provider, [_root, unmounted], Options());

        IFileSystemProvider asInterface = provider;
        asInterface.EnumerationFailures.Should().Equal([unmounted]);
    }

    private static char DriveLetterNotInUse()
    {
        var used = System.IO.DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return Enumerable.Range('A', 26).Select(c => (char)c).Last(c => !used.Contains(c));
    }
}
