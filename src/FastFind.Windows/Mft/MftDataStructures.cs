using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FastFind.Windows.Mft;

/// <summary>
/// High-performance MFT file record structure.
/// Optimized for minimal memory footprint and cache-friendly access.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SupportedOSPlatform("windows")]
public readonly struct MftFileRecord
{
    /// <summary>
    /// Unique file reference number (MFT record number + sequence number)
    /// </summary>
    public readonly ulong FileReferenceNumber;

    /// <summary>
    /// Parent directory's file reference number
    /// </summary>
    public readonly ulong ParentFileReferenceNumber;

    /// <summary>
    /// File attributes (directory, hidden, system, etc.)
    /// </summary>
    public readonly FileAttributes Attributes;

    /// <summary>
    /// File size in bytes (0 for directories)
    /// </summary>
    public readonly long FileSize;

    /// <summary>
    /// File name (without path)
    /// </summary>
    public readonly string FileName;

    /// <summary>
    /// Creation time in UTC
    /// </summary>
    public readonly DateTime CreationTime;

    /// <summary>
    /// Last modification time in UTC
    /// </summary>
    public readonly DateTime ModificationTime;

    /// <summary>
    /// Last access time in UTC
    /// </summary>
    public readonly DateTime AccessTime;

    public MftFileRecord(
        ulong fileReferenceNumber,
        ulong parentFileReferenceNumber,
        FileAttributes attributes,
        long fileSize,
        string fileName,
        DateTime creationTime,
        DateTime modificationTime,
        DateTime accessTime)
    {
        FileReferenceNumber = fileReferenceNumber;
        ParentFileReferenceNumber = parentFileReferenceNumber;
        Attributes = attributes;
        FileSize = fileSize;
        FileName = fileName;
        CreationTime = creationTime;
        ModificationTime = modificationTime;
        AccessTime = accessTime;
    }

    /// <summary>
    /// Gets the MFT record number (lower 48 bits)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetRecordNumber() => FileReferenceNumber & 0x0000FFFFFFFFFFFF;

    /// <summary>
    /// Gets the sequence number (upper 16 bits)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetSequenceNumber() => (ushort)(FileReferenceNumber >> 48);

    /// <summary>
    /// Whether this is a directory
    /// </summary>
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>
    /// Whether this is a hidden file
    /// </summary>
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    /// <summary>
    /// Whether this is a system file
    /// </summary>
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    /// <summary>
    /// Extract record number from a file reference number
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ExtractRecordNumber(ulong fileReferenceNumber)
        => fileReferenceNumber & 0x0000FFFFFFFFFFFF;

    /// <summary>
    /// Extract sequence number from a file reference number
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ExtractSequenceNumber(ulong fileReferenceNumber)
        => (ushort)(fileReferenceNumber >> 48);
}

/// <summary>
/// USN change record for real-time file system monitoring
/// </summary>
[SupportedOSPlatform("windows")]
public readonly struct UsnChangeRecord
{
    /// <summary>
    /// USN (Update Sequence Number) of this change
    /// </summary>
    public readonly long Usn;

    /// <summary>
    /// File reference number of the changed file
    /// </summary>
    public readonly ulong FileReferenceNumber;

    /// <summary>
    /// Parent directory's file reference number
    /// </summary>
    public readonly ulong ParentFileReferenceNumber;

    /// <summary>
    /// Reason flags indicating what changed
    /// </summary>
    public readonly UsnReason Reason;

    /// <summary>
    /// File attributes after the change
    /// </summary>
    public readonly FileAttributes Attributes;

    /// <summary>
    /// File name (may be old or new name depending on reason)
    /// </summary>
    public readonly string FileName;

    /// <summary>
    /// Timestamp of the change
    /// </summary>
    public readonly DateTime TimeStamp;

    public UsnChangeRecord(
        long usn,
        ulong fileReferenceNumber,
        ulong parentFileReferenceNumber,
        UsnReason reason,
        FileAttributes attributes,
        string fileName,
        DateTime timeStamp)
    {
        Usn = usn;
        FileReferenceNumber = fileReferenceNumber;
        ParentFileReferenceNumber = parentFileReferenceNumber;
        Reason = reason;
        Attributes = attributes;
        FileName = fileName;
        TimeStamp = timeStamp;
    }

    /// <summary>
    /// Whether this is a file creation event
    /// </summary>
    public bool IsCreated => (Reason & UsnReason.FileCreate) != 0;

    /// <summary>
    /// Whether this is a file deletion event
    /// </summary>
    public bool IsDeleted => (Reason & UsnReason.FileDelete) != 0;

    /// <summary>
    /// Whether this is a rename event (old name)
    /// </summary>
    public bool IsRenamedFrom => (Reason & UsnReason.RenameOldName) != 0;

    /// <summary>
    /// Whether this is a rename event (new name)
    /// </summary>
    public bool IsRenamedTo => (Reason & UsnReason.RenameNewName) != 0;

    /// <summary>
    /// Whether the file data was modified
    /// </summary>
    public bool IsDataModified => (Reason & (UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.DataTruncation)) != 0;

    /// <summary>
    /// Whether this is the final close record
    /// </summary>
    public bool IsClosed => (Reason & UsnReason.Close) != 0;

    /// <summary>
    /// Whether this is a directory
    /// </summary>
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// USN reason flags indicating what type of change occurred
/// </summary>
[Flags]
[SupportedOSPlatform("windows")]
public enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    Close = 0x80000000
}

/// <summary>
/// NTFS volume information
/// </summary>
[SupportedOSPlatform("windows")]
public readonly struct NtfsVolumeInfo
{
    /// <summary>
    /// Drive letter (e.g., 'C')
    /// </summary>
    public readonly char DriveLetter;

    /// <summary>
    /// Volume serial number
    /// </summary>
    public readonly long VolumeSerialNumber;

    /// <summary>
    /// Total number of clusters on the volume
    /// </summary>
    public readonly long TotalClusters;

    /// <summary>
    /// Number of free clusters
    /// </summary>
    public readonly long FreeClusters;

    /// <summary>
    /// Bytes per sector
    /// </summary>
    public readonly uint BytesPerSector;

    /// <summary>
    /// Bytes per cluster
    /// </summary>
    public readonly uint BytesPerCluster;

    /// <summary>
    /// Bytes per MFT file record segment
    /// </summary>
    public readonly uint BytesPerFileRecordSegment;

    /// <summary>
    /// Starting LCN of the MFT
    /// </summary>
    public readonly long MftStartLcn;

    /// <summary>
    /// Valid data length of the MFT
    /// </summary>
    public readonly long MftValidDataLength;

    /// <summary>
    /// Estimated number of MFT records
    /// </summary>
    public long EstimatedMftRecordCount => BytesPerFileRecordSegment > 0
        ? MftValidDataLength / BytesPerFileRecordSegment
        : 0;

    public NtfsVolumeInfo(
        char driveLetter,
        long volumeSerialNumber,
        long totalClusters,
        long freeClusters,
        uint bytesPerSector,
        uint bytesPerCluster,
        uint bytesPerFileRecordSegment,
        long mftStartLcn,
        long mftValidDataLength)
    {
        DriveLetter = driveLetter;
        VolumeSerialNumber = volumeSerialNumber;
        TotalClusters = totalClusters;
        FreeClusters = freeClusters;
        BytesPerSector = bytesPerSector;
        BytesPerCluster = bytesPerCluster;
        BytesPerFileRecordSegment = bytesPerFileRecordSegment;
        MftStartLcn = mftStartLcn;
        MftValidDataLength = mftValidDataLength;
    }

    /// <summary>
    /// Total volume size in bytes
    /// </summary>
    public long TotalSizeBytes => TotalClusters * BytesPerCluster;

    /// <summary>
    /// Free space in bytes
    /// </summary>
    public long FreeSizeBytes => FreeClusters * BytesPerCluster;
}

/// <summary>
/// Result of MFT enumeration operation
/// </summary>
[SupportedOSPlatform("windows")]
public readonly struct MftEnumerationResult
{
    /// <summary>
    /// Drive letter that was enumerated
    /// </summary>
    public readonly char DriveLetter;

    /// <summary>
    /// Total number of records enumerated
    /// </summary>
    public readonly long TotalRecords;

    /// <summary>
    /// Number of file records (not directories)
    /// </summary>
    public readonly long FileCount;

    /// <summary>
    /// Number of directory records
    /// </summary>
    public readonly long DirectoryCount;

    /// <summary>
    /// Time taken for enumeration
    /// </summary>
    public readonly TimeSpan ElapsedTime;

    /// <summary>
    /// Whether enumeration completed successfully
    /// </summary>
    public readonly bool IsSuccess;

    /// <summary>
    /// Error message if enumeration failed
    /// </summary>
    public readonly string? ErrorMessage;

    public MftEnumerationResult(
        char driveLetter,
        long totalRecords,
        long fileCount,
        long directoryCount,
        TimeSpan elapsedTime,
        bool isSuccess,
        string? errorMessage = null)
    {
        DriveLetter = driveLetter;
        TotalRecords = totalRecords;
        FileCount = fileCount;
        DirectoryCount = directoryCount;
        ElapsedTime = elapsedTime;
        IsSuccess = isSuccess;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Records per second throughput
    /// </summary>
    public double RecordsPerSecond => ElapsedTime.TotalSeconds > 0
        ? TotalRecords / ElapsedTime.TotalSeconds
        : 0;

    public static MftEnumerationResult Success(
        char driveLetter, long totalRecords, long fileCount, long directoryCount, TimeSpan elapsedTime)
        => new(driveLetter, totalRecords, fileCount, directoryCount, elapsedTime, true);

    public static MftEnumerationResult Failure(char driveLetter, string errorMessage)
        => new(driveLetter, 0, 0, 0, TimeSpan.Zero, false, errorMessage);
}
