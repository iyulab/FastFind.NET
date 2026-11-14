using FastFind.Indexing;
using FastFind.Models;
using FastFind.Unix;
using FluentAssertions;
using Xunit;

namespace FastFind.Unix.Tests.Core;

/// <summary>
/// What an index admits is one definition on every platform and for every path into the index: the
/// directory walk, and a change the monitor reports.
/// </summary>
/// <remarks>
/// The Unix enumerators used to test a leading dot and nothing else, so a macOS folder carrying the BSD
/// hidden flag (<c>~/Library</c>) was walked, and the engine's monitored path tested nothing at all.
/// .NET maps the flag, like the dot-name, to <see cref="FileAttributes.Hidden"/>; the definition reads that.
/// </remarks>
[Trait("Category", "Functional")]
public sealed class UnixAdmissionTests
{
    private static readonly DateTime Stamp = new(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private static FileItem Item(string path, FileAttributes attributes = FileAttributes.Archive, long size = 10)
    {
        var cut = path.LastIndexOf('/');
        var name = path[(cut + 1)..];
        var dot = name.LastIndexOf('.');
        return new FileItem
        {
            FullPath = path,
            Name = name,
            DirectoryPath = path[..cut],
            Extension = attributes.HasFlag(FileAttributes.Directory) || dot < 0 ? string.Empty : name[dot..],
            Size = size,
            CreatedTime = Stamp,
            ModifiedTime = Stamp,
            AccessedTime = Stamp,
            Attributes = attributes,
            DriveLetter = '/',
        };
    }

    private static IndexingOptions Options(Action<IndexingOptions>? configure = null)
    {
        var options = new IndexingOptions { ExcludedPaths = new List<string>(), ExcludedExtensions = new List<string>() };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void A_Directory_With_The_Hidden_Attribute_Should_Be_Refused_Whatever_Its_Name()
    {
        // ~/Library: no leading dot, hidden by the BSD flag, which .NET reports as FileAttributes.Hidden.
        var library = Item("/Users/me/Library", FileAttributes.Directory | FileAttributes.Hidden);

        IndexingAdmission.Admits(library, Options(o => o.IncludeHidden = false)).Should().BeFalse();
        IndexingAdmission.Admits(library, Options(o => o.IncludeHidden = true)).Should().BeTrue();
    }

    [Fact]
    public void A_Visible_Entry_Should_Be_Admitted_And_The_Other_Rules_Still_Apply()
    {
        var options = Options(o =>
        {
            o.ExcludedExtensions = new List<string> { ".tmp" };
            o.ExcludedPaths = new List<string> { "node_modules" };
            o.MaxFileSize = 100;
        });

        IndexingAdmission.Admits(Item("/d/a.txt"), options).Should().BeTrue();
        IndexingAdmission.Admits(Item("/d/a.tmp"), options).Should().BeFalse();
        IndexingAdmission.Admits(Item("/d/node_modules/a.txt"), options).Should().BeFalse();
        IndexingAdmission.Admits(Item("/d/big.bin", size: 101), options).Should().BeFalse();
    }

    [Fact]
    public async Task A_Monitored_Item_Should_Meet_The_Options_And_The_Cap()
    {
        var index = new MemorySearchIndex();
        var options = Options(o => { o.IncludeHidden = false; o.MaxFileCount = 2; });

        (await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/a.txt"), options)).Should().Be(MonitoredOutcome.Indexed);
        (await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/.secret", FileAttributes.Hidden), options)).Should().Be(MonitoredOutcome.NotAdmitted);
        (await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/b.txt"), options)).Should().Be(MonitoredOutcome.Indexed);

        // At the cap: a new item is refused, one the index holds may still change.
        (await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/c.txt"), options)).Should().Be(MonitoredOutcome.CapReached);
        (await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/a.txt", size: 99), options)).Should().Be(MonitoredOutcome.Indexed);
        index.Count.Should().Be(2);
        (await index.ContainsAsync("/d/c.txt")).Should().BeFalse();

        // A deletion makes room.
        await index.RemoveAsync("/d/b.txt");
        (await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/c.txt"), options)).Should().Be(MonitoredOutcome.Indexed);
    }

    [Fact]
    public async Task An_Entry_That_Becomes_Hidden_Should_Leave_The_Index()
    {
        var index = new MemorySearchIndex();
        var options = Options(o => o.IncludeHidden = false);
        await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/a.txt"), options);

        var outcome = await IndexingAdmission.IndexMonitoredAsync(index, Item("/d/a.txt", FileAttributes.Hidden), options);

        outcome.Should().Be(MonitoredOutcome.NotAdmitted);
        (await index.ContainsAsync("/d/a.txt")).Should().BeFalse();
    }
}
