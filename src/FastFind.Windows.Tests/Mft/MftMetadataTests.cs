using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Mft;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Size and timestamps on the journal-enumeration path, which carries neither: they must be read
/// from the file system when asked for, and left visibly unset otherwise — never invented.
/// </summary>
[Trait("Category", "Functional")]
public sealed class MftMetadataTests : IDisposable
{
    private static readonly DateTime KnownWriteTime = new(2021, 5, 4, 3, 2, 1, DateTimeKind.Utc);
    private static readonly DateTime KnownCreationTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly string _root;

    public MftMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-mft-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // --- the USN record carries no file times ---

    [Theory]
    [InlineData(0L)]                          // what FSCTL_ENUM_USN_DATA returns, by specification
    [InlineData(132_000_000_000_000_000L)]    // a journal event time — still not a file time
    public void A_Parsed_Journal_Record_Should_Leave_Size_And_Times_Unset(long timeStamp)
    {
        var buffer = UsnRecordV2("notes.md", timeStamp);
        var offset = 0;

        MftParserV2.TryParseUsnRecord(buffer, ref offset, out var record).Should().BeTrue();

        record.FileName.Should().Be("notes.md");
        record.FileSize.Should().Be(0);
        record.CreationTime.Should().Be(DateTime.MinValue);
        record.ModificationTime.Should().Be(DateTime.MinValue);
        record.AccessTime.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public void A_V3_Record_Should_Be_Rejected_Rather_Than_Read_With_V2_Offsets()
    {
        var buffer = UsnRecordV2("notes.md", 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 3);
        var offset = 0;

        MftParserV2.TryParseUsnRecord(buffer, ref offset, out _).Should().BeFalse();
    }

    // --- the provider reads them when asked ---

    [Fact]
    public void Collecting_Metadata_Should_Fill_Size_And_All_Three_Times_For_A_File()
    {
        var path = CreateFile("report.md", "hello world");

        var item = MftFileSystemProvider.ConvertToFileItem(Record("report.md", isDirectory: false), path, collectMetadata: true);

        item.Size.Should().Be(11);
        item.ModifiedTime.ToUniversalTime().Should().Be(KnownWriteTime);
        item.CreatedTime.ToUniversalTime().Should().Be(KnownCreationTime);
        item.AccessedTime.Should().NotBe(DateTime.MinValue);
    }

    [Fact]
    public void Collecting_Metadata_Should_Fill_Times_For_A_Directory()
    {
        var path = Path.Combine(_root, "folder");
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, KnownWriteTime);

        var item = MftFileSystemProvider.ConvertToFileItem(Record("folder", isDirectory: true), path, collectMetadata: true);

        item.Size.Should().Be(0);
        item.ModifiedTime.ToUniversalTime().Should().Be(KnownWriteTime);
    }

    [Fact]
    public void Without_Metadata_An_Item_Should_Carry_Unset_Values_Not_The_FILETIME_Epoch()
    {
        var path = CreateFile("report.md", "hello world");

        var item = MftFileSystemProvider.ConvertToFileItem(Record("report.md", isDirectory: false), path, collectMetadata: false);

        item.Size.Should().Be(0);
        item.ModifiedTime.Should().Be(DateTime.MinValue);
        item.ToFastFileItem().ModifiedTicks.Should().Be(0);
    }

    [Fact]
    public void An_Entry_That_Cannot_Be_Read_Should_Keep_Unset_Values()
    {
        // FileSystemInfo reports 1601-01-01 for an entry it could not read; that must not leak.
        var path = Path.Combine(_root, "vanished.md");

        var item = MftFileSystemProvider.ConvertToFileItem(Record("vanished.md", isDirectory: false), path, collectMetadata: true);

        item.ModifiedTime.Should().Be(DateTime.MinValue);
        item.CreatedTime.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public void The_Provider_Should_Report_Metadata_Only_When_Asked_To_Collect_It()
    {
        using var provider = new MftFileSystemProvider();
        IFileSystemProvider asInterface = provider;

        asInterface.ProvidesFileMetadata(new IndexingOptions()).Should().BeFalse();
        asInterface.ProvidesFileMetadata(new IndexingOptions { CollectFileMetadata = true }).Should().BeTrue();
    }

    [Fact]
    public void The_Obsolete_CollectFileSize_Should_Forward_To_CollectFileMetadata()
    {
#pragma warning disable CS0618 // exercising the obsolete alias on purpose
        var options = new IndexingOptions { CollectFileSize = true };
#pragma warning restore CS0618

        options.CollectFileMetadata.Should().BeTrue();
    }

    // --- the engine refuses what it cannot answer ---

    [Fact]
    public async Task A_Date_Bound_Over_An_Index_Without_Metadata_Should_Fail_Rather_Than_Return_Nothing()
    {
        using var engine = Engine(providesMetadata: false);
        await IndexAsync(engine);

        var plain = await engine.SearchAsync(new SearchQuery { SearchText = "*.md" });
        var dated = await engine.SearchAsync(new SearchQuery { SearchText = "*.md", MinModifiedDate = new DateTime(2020, 1, 1) });
        var sized = await engine.SearchAsync(new SearchQuery { SearchText = "*.md", MinSize = 1 });

        plain.HasError.Should().BeFalse();
        plain.TotalMatches.Should().Be(1);
        dated.HasError.Should().BeTrue();
        dated.ErrorMessage.Should().Contain(nameof(IndexingOptions.CollectFileMetadata));
        sized.HasError.Should().BeTrue();
    }

    [Fact]
    public async Task A_Date_Bound_Over_An_Index_With_Metadata_Should_Match()
    {
        using var engine = Engine(providesMetadata: true);
        await IndexAsync(engine);

        var result = await engine.SearchAsync(new SearchQuery { SearchText = "*.md", MinModifiedDate = new DateTime(2020, 1, 1) });

        result.HasError.Should().BeFalse();
        result.TotalMatches.Should().Be(1);
    }

    // --- helpers ---

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, Encoding.ASCII);
        File.SetCreationTimeUtc(path, KnownCreationTime);
        File.SetLastWriteTimeUtc(path, KnownWriteTime);
        return path;
    }

    private static MftFileRecord Record(string name, bool isDirectory) => new(
        fileReferenceNumber: 0x0001000000000123,
        parentFileReferenceNumber: 0x0005000000000005,
        attributes: isDirectory ? FileAttributes.Directory : FileAttributes.Archive,
        fileSize: 0,
        fileName: name,
        creationTime: default,
        modificationTime: default,
        accessTime: default);

    private static byte[] UsnRecordV2(string fileName, long timeStamp)
    {
        var nameBytes = Encoding.Unicode.GetBytes(fileName);
        var length = (60 + nameBytes.Length + 7) & ~7;
        var buffer = new byte[length];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 0x0001000000000123);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 0x0005000000000005);
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], timeStamp);
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], (uint)FileAttributes.Archive);
        BinaryPrimitives.WriteUInt16LittleEndian(span[56..], (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[58..], 60);
        nameBytes.CopyTo(span[60..]);

        return buffer;
    }

    private static WindowsSearchEngineImpl Engine(bool providesMetadata) => new(
        new FixedProvider(providesMetadata),
        new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance),
        new WindowsSearchEngineOptions { EnableRealtimeMonitoring = false },
        NullLogger<WindowsSearchEngineImpl>.Instance);

    private static async Task IndexAsync(ISearchEngine engine)
    {
        await engine.StartIndexingAsync(new IndexingOptions
        {
            SpecificDirectories = { @"C:\docs" },
            EnableMonitoring = false,
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (engine.IsIndexing && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        engine.TotalIndexedFiles.Should().Be(1);
    }

    /// <summary>
    /// Yields one item whose metadata is real or unset, and says which — the two shapes the MFT
    /// provider produces with and without <see cref="IndexingOptions.CollectFileMetadata"/>.
    /// </summary>
    private sealed class FixedProvider(bool providesMetadata) : IFileSystemProvider
    {
        public PlatformType SupportedPlatform => PlatformType.Windows;
        public bool IsAvailable => true;

        public bool ProvidesFileMetadata(IndexingOptions options) => providesMetadata;

        public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
            IEnumerable<string> locations, IndexingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new FileItem
            {
                FullPath = @"C:\docs\readme.md",
                Name = "readme.md",
                DirectoryPath = @"C:\docs",
                Extension = ".md",
                Size = providesMetadata ? 3323 : 0,
                CreatedTime = providesMetadata ? KnownCreationTime : default,
                ModifiedTime = providesMetadata ? KnownWriteTime : default,
                AccessedTime = providesMetadata ? KnownWriteTime : default,
                Attributes = FileAttributes.Archive,
                DriveLetter = 'C',
            };
        }

        public Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<FileItem?>(null);

        public Task<IEnumerable<FastFind.Interfaces.DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Empty<FastFind.Interfaces.DriveInfo>());

        public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
            IEnumerable<string> locations, MonitoringOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult("NTFS");

        public ProviderPerformance GetPerformanceInfo() => new();

        public void Dispose() { }
    }
}
