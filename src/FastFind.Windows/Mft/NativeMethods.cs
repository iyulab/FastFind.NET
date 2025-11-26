using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Windows.Mft;

/// <summary>
/// Native Windows API methods for MFT and USN Journal access.
/// Provides direct access to NTFS structures for maximum performance.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    #region Constants

    // File access constants
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // FSCTL codes for DeviceIoControl
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;

    // USN reasons (file change types)
    public const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    public const uint USN_REASON_DATA_EXTEND = 0x00000002;
    public const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
    public const uint USN_REASON_NAMED_DATA_OVERWRITE = 0x00000010;
    public const uint USN_REASON_NAMED_DATA_EXTEND = 0x00000020;
    public const uint USN_REASON_NAMED_DATA_TRUNCATION = 0x00000040;
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_EA_CHANGE = 0x00000400;
    public const uint USN_REASON_SECURITY_CHANGE = 0x00000800;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_INDEXABLE_CHANGE = 0x00004000;
    public const uint USN_REASON_BASIC_INFO_CHANGE = 0x00008000;
    public const uint USN_REASON_HARD_LINK_CHANGE = 0x00010000;
    public const uint USN_REASON_COMPRESSION_CHANGE = 0x00020000;
    public const uint USN_REASON_ENCRYPTION_CHANGE = 0x00040000;
    public const uint USN_REASON_OBJECT_ID_CHANGE = 0x00080000;
    public const uint USN_REASON_REPARSE_POINT_CHANGE = 0x00100000;
    public const uint USN_REASON_STREAM_CHANGE = 0x00200000;
    public const uint USN_REASON_CLOSE = 0x80000000;

    #endregion

    #region P/Invoke Declarations

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MFT_ENUM_DATA_V0 lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref READ_USN_JOURNAL_DATA_V0 lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveTypeW(string lpRootPathName);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformationW(
        string lpRootPathName,
        nint lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        nint lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    #endregion

    #region Drive Type Constants

    public const uint DRIVE_UNKNOWN = 0;
    public const uint DRIVE_NO_ROOT_DIR = 1;
    public const uint DRIVE_REMOVABLE = 2;
    public const uint DRIVE_FIXED = 3;
    public const uint DRIVE_REMOTE = 4;
    public const uint DRIVE_CDROM = 5;
    public const uint DRIVE_RAMDISK = 6;

    #endregion
}

#region Native Structures

/// <summary>
/// NTFS volume data structure returned by FSCTL_GET_NTFS_VOLUME_DATA
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NTFS_VOLUME_DATA_BUFFER
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

/// <summary>
/// Input structure for FSCTL_ENUM_USN_DATA
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_ENUM_DATA_V0
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
}

/// <summary>
/// USN record header (version-independent portion)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct USN_RECORD_V2
{
    public uint RecordLength;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public ulong FileReferenceNumber;
    public ulong ParentFileReferenceNumber;
    public long Usn;
    public long TimeStamp;
    public uint Reason;
    public uint SourceInfo;
    public uint SecurityId;
    public uint FileAttributes;
    public ushort FileNameLength;
    public ushort FileNameOffset;
    // FileName follows immediately after this structure
}

/// <summary>
/// USN Journal data structure returned by FSCTL_QUERY_USN_JOURNAL
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct USN_JOURNAL_DATA_V0
{
    public ulong UsnJournalID;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>
/// Input structure for FSCTL_READ_USN_JOURNAL
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct READ_USN_JOURNAL_DATA_V0
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalID;
}

/// <summary>
/// Input structure for FSCTL_CREATE_USN_JOURNAL
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CREATE_USN_JOURNAL_DATA
{
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

#endregion
