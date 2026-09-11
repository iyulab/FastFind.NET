using System.Runtime.InteropServices;
using FastFind.Models;
using FastFind.Windows.Mft;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Change-journal records → the change events an index applies.
/// </summary>
[Trait("Category", "Functional")]
public class UsnChangeTranslatorTests
{
    private const ulong Docs = 0x0001_0000_0000_0200;
    private const ulong Archive = 0x0001_0000_0000_0300;
    private const ulong File = 0x0002_0000_0000_0400;

    private sealed class Directories : IDirectoryPathSource
    {
        public Dictionary<ulong, string> Paths { get; } = new()
        {
            [Docs] = @"D:\docs",
            [Archive] = @"D:\archive",
        };

        public int Invalidations { get; private set; }

        public bool TryGetPath(char driveLetter, ulong fileReference, out string path) =>
            Paths.TryGetValue(fileReference, out path!);

        public void Invalidate(char driveLetter) => Invalidations++;
    }

    private static UsnChangeRecord Record(UsnReason reason, ulong parent, string name, bool directory = false) =>
        new(usn: 1, fileReferenceNumber: File, parentFileReferenceNumber: parent, reason,
            directory ? FileAttributes.Directory : FileAttributes.Archive, name, DateTime.UtcNow, 'D');

    [Fact]
    public void A_Creation_Should_Be_Reported_Once_From_The_Closing_Record()
    {
        var translator = new UsnChangeTranslator(new Directories());

        var events = new[]
        {
            translator.Translate(Record(UsnReason.FileCreate, Docs, "new.txt")),
            translator.Translate(Record(UsnReason.FileCreate | UsnReason.DataExtend, Docs, "new.txt")),
            translator.Translate(Record(UsnReason.FileCreate | UsnReason.DataExtend | UsnReason.Close, Docs, "new.txt")),
        }.Where(e => e is not null).ToList();

        events.Should().ContainSingle();
        events[0]!.ChangeType.Should().Be(FileChangeType.Created);
        events[0]!.NewPath.Should().Be(@"D:\docs\new.txt");
    }

    [Fact]
    public void A_Write_To_An_Existing_File_Should_Be_A_Modification()
    {
        var translator = new UsnChangeTranslator(new Directories());

        translator.Translate(Record(UsnReason.DataOverwrite, Docs, "a.txt")).Should().BeNull();
        var change = translator.Translate(Record(UsnReason.DataOverwrite | UsnReason.Close, Docs, "a.txt"));

        change!.ChangeType.Should().Be(FileChangeType.Modified);
    }

    [Fact]
    public void A_Deletion_Should_Carry_The_Path_The_File_Had()
    {
        var translator = new UsnChangeTranslator(new Directories());

        var change = translator.Translate(Record(UsnReason.FileDelete | UsnReason.Close, Docs, "gone.txt"));

        change!.ChangeType.Should().Be(FileChangeType.Deleted);
        change.NewPath.Should().Be(@"D:\docs\gone.txt");
    }

    [Fact]
    public void A_Rename_Should_Pair_The_Old_And_New_Records_Into_One_Event_With_Both_Paths()
    {
        var translator = new UsnChangeTranslator(new Directories());

        translator.Translate(Record(UsnReason.RenameOldName, Docs, "draft.txt")).Should().BeNull();
        var change = translator.Translate(Record(UsnReason.RenameNewName, Archive, "final.txt"));

        change!.ChangeType.Should().Be(FileChangeType.Renamed);
        change.OldPath.Should().Be(@"D:\docs\draft.txt");
        change.NewPath.Should().Be(@"D:\archive\final.txt");
    }

    [Fact]
    public void Renaming_A_Directory_Should_Invalidate_Remembered_Paths()
    {
        var directories = new Directories();
        var translator = new UsnChangeTranslator(directories);

        translator.Translate(Record(UsnReason.RenameOldName, Docs, "sub", directory: true));

        directories.Invalidations.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_Record_Whose_Parent_No_Longer_Exists_Should_Yield_Nothing()
    {
        var translator = new UsnChangeTranslator(new Directories());

        translator.Translate(Record(UsnReason.FileCreate | UsnReason.Close, 0x0009_0000_0000_0999, "x.txt"))
            .Should().BeNull();
    }

    // --- scoping a change to the monitored locations ---

    [Fact]
    public void A_Rename_Out_Of_The_Locations_Should_Read_As_A_Deletion_And_Into_Them_As_A_Creation()
    {
        string[] scope = [@"D:\docs"];

        var outward = MftFileSystemProvider.ScopeToLocations(
            new FileChangeEventArgs(FileChangeType.Renamed, @"D:\archive\a.txt", oldPath: @"D:\docs\a.txt"), scope);
        var inward = MftFileSystemProvider.ScopeToLocations(
            new FileChangeEventArgs(FileChangeType.Renamed, @"D:\docs\a.txt", oldPath: @"D:\archive\a.txt"), scope);
        var elsewhere = MftFileSystemProvider.ScopeToLocations(
            new FileChangeEventArgs(FileChangeType.Created, @"D:\archive\b.txt"), scope);

        outward!.ChangeType.Should().Be(FileChangeType.Deleted);
        outward.NewPath.Should().Be(@"D:\docs\a.txt");
        inward!.ChangeType.Should().Be(FileChangeType.Created);
        inward.NewPath.Should().Be(@"D:\docs\a.txt");
        elsewhere.Should().BeNull();
    }

    // --- resolving a directory by its file reference, for real ---

    [Theory]
    [InlineData(@"\\?\C:\data\x", @"C:\data\x")]
    [InlineData(@"\\?\C:\", @"C:\")]
    [InlineData(@"\\?\UNC\server\share", @"\\?\UNC\server\share")]   // not a drive path: left alone
    public void The_Extended_Length_Prefix_Should_Be_Stripped_From_Drive_Paths(string input, string expected)
    {
        FileIdDirectoryPaths.StripExtendedPrefix(input).Should().Be(expected);
    }

    [Fact]
    public void A_Directory_Should_Be_Found_From_Its_File_Reference()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fastfind-fileid-" + Guid.NewGuid().ToString("N")));
        try
        {
            var reference = FileReferenceOf(directory.FullName);
            using var paths = new FileIdDirectoryPaths();

            paths.TryGetPath(directory.FullName[0], reference, out var path).Should().BeTrue();

            path.Should().BeEquivalentTo(directory.FullName);
        }
        finally
        {
            directory.Delete();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    private static ulong FileReferenceOf(string directory)
    {
        using var handle = CreateFileW(directory, 0x80, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        handle.IsInvalid.Should().BeFalse();
        GetFileInformationByHandle(handle, out var info).Should().BeTrue();
        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }
}
