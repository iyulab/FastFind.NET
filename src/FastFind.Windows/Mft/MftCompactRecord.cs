using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FastFind.Models;

namespace FastFind.Windows.Mft;

/// <summary>
/// Memory-optimized MFT record structure using StringPool IDs instead of string references.
/// Designed for minimal memory footprint (40 bytes) in high-volume MFT enumeration scenarios.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1.4 optimization: Reduces memory from ~80+ bytes (MftFileRecord + string) to 40 bytes fixed.
/// </para>
/// <para>
/// Memory layout (40 bytes total):
/// <list type="bullet">
/// <item>FileReferenceNumber (ulong): 8 bytes - MFT record number + sequence</item>
/// <item>ParentFileReferenceNumber (ulong): 8 bytes - Parent directory reference</item>
/// <item>FileNameId (uint): 4 bytes - StringPool interned ID</item>
/// <item>Attributes (uint): 4 bytes - File attributes flags</item>
/// <item>FileSize (long): 8 bytes - File size in bytes</item>
/// <item>ModifiedTicks (long): 8 bytes - Modification time as DateTime.Ticks</item>
/// </list>
/// </para>
/// <para>
/// Trade-offs:
/// <list type="bullet">
/// <item>Requires StringPool lookup to retrieve filename (slight overhead on read)</item>
/// <item>Only stores modification time (most common use case)</item>
/// <item>Uses uint for attributes (sufficient for FileAttributes enum)</item>
/// </list>
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SupportedOSPlatform("windows")]
public readonly struct MftCompactRecord
{
    /// <summary>
    /// Unique file reference number (MFT record number + sequence number).
    /// Lower 48 bits: record number, Upper 16 bits: sequence number.
    /// </summary>
    public readonly ulong FileReferenceNumber;

    /// <summary>
    /// Parent directory's file reference number.
    /// </summary>
    public readonly ulong ParentFileReferenceNumber;

    /// <summary>
    /// StringPool ID for the filename.
    /// Use <see cref="GetFileName"/> to retrieve the actual string.
    /// </summary>
    public readonly uint FileNameId;

    /// <summary>
    /// File attributes (directory, hidden, system, etc.) stored as uint.
    /// </summary>
    public readonly uint Attributes;

    /// <summary>
    /// File size in bytes (0 for directories).
    /// </summary>
    public readonly long FileSize;

    /// <summary>
    /// Last modification time stored as DateTime.Ticks (UTC).
    /// </summary>
    public readonly long ModifiedTicks;

    /// <summary>
    /// Creates a new compact MFT record.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MftCompactRecord(
        ulong fileReferenceNumber,
        ulong parentFileReferenceNumber,
        uint fileNameId,
        uint attributes,
        long fileSize,
        long modifiedTicks)
    {
        FileReferenceNumber = fileReferenceNumber;
        ParentFileReferenceNumber = parentFileReferenceNumber;
        FileNameId = fileNameId;
        Attributes = attributes;
        FileSize = fileSize;
        ModifiedTicks = modifiedTicks;
    }

    #region Computed Properties

    /// <summary>
    /// Whether this is a directory.
    /// </summary>
    public bool IsDirectory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & (uint)FileAttributes.Directory) != 0;
    }

    /// <summary>
    /// Whether this is a hidden file.
    /// </summary>
    public bool IsHidden
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & (uint)FileAttributes.Hidden) != 0;
    }

    /// <summary>
    /// Whether this is a system file.
    /// </summary>
    public bool IsSystem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & (uint)FileAttributes.System) != 0;
    }

    /// <summary>
    /// Gets the modification time as DateTime (UTC).
    /// </summary>
    public DateTime ModifiedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(ModifiedTicks, DateTimeKind.Utc);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the MFT record number (lower 48 bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetRecordNumber() => FileReferenceNumber & 0x0000FFFFFFFFFFFF;

    /// <summary>
    /// Gets the sequence number (upper 16 bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetSequenceNumber() => (ushort)(FileReferenceNumber >> 48);

    /// <summary>
    /// Retrieves the filename from StringPool using the stored ID.
    /// </summary>
    /// <returns>The filename string, or empty string if ID is 0.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetFileName() => StringPool.Get((int)FileNameId);

    /// <summary>
    /// Gets the file attributes as FileAttributes enum.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FileAttributes GetFileAttributes() => (FileAttributes)Attributes;

    #endregion

    #region Conversion Methods

    /// <summary>
    /// Creates a compact record from a standard MftFileRecord.
    /// Interns the filename in StringPool and uses modification time only.
    /// </summary>
    /// <param name="record">The source record to convert.</param>
    /// <returns>A new compact record.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MftCompactRecord FromMftFileRecord(in MftFileRecord record)
    {
        var fileNameId = StringPool.InternName(record.FileName);

        return new MftCompactRecord(
            fileReferenceNumber: record.FileReferenceNumber,
            parentFileReferenceNumber: record.ParentFileReferenceNumber,
            fileNameId: (uint)fileNameId,
            attributes: (uint)record.Attributes,
            fileSize: record.FileSize,
            modifiedTicks: record.ModificationTime.Ticks);
    }

    /// <summary>
    /// Creates a compact record from a standard MftFileRecord using Span-based interning.
    /// More efficient for MFT parsing where filename is already a span.
    /// </summary>
    /// <param name="record">The source record to convert.</param>
    /// <param name="fileNameSpan">The filename as a span for zero-allocation interning.</param>
    /// <returns>A new compact record.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MftCompactRecord FromMftFileRecordSpan(
        in MftFileRecord record,
        ReadOnlySpan<char> fileNameSpan)
    {
        var fileNameId = StringPool.InternFromSpan(fileNameSpan);

        return new MftCompactRecord(
            fileReferenceNumber: record.FileReferenceNumber,
            parentFileReferenceNumber: record.ParentFileReferenceNumber,
            fileNameId: (uint)fileNameId,
            attributes: (uint)record.Attributes,
            fileSize: record.FileSize,
            modifiedTicks: record.ModificationTime.Ticks);
    }

    /// <summary>
    /// Converts back to a standard MftFileRecord.
    /// Note: Creation and Access times will be set to ModificationTime.
    /// </summary>
    /// <returns>A new MftFileRecord with data from this compact record.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MftFileRecord ToMftFileRecord()
    {
        var modifiedTime = ModifiedTime;

        return new MftFileRecord(
            fileReferenceNumber: FileReferenceNumber,
            parentFileReferenceNumber: ParentFileReferenceNumber,
            attributes: GetFileAttributes(),
            fileSize: FileSize,
            fileName: GetFileName(),
            creationTime: modifiedTime,     // Lost in compact form
            modificationTime: modifiedTime,
            accessTime: modifiedTime);      // Lost in compact form
    }

    #endregion

    #region Static Helpers

    /// <summary>
    /// Extract record number from a file reference number.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ExtractRecordNumber(ulong fileReferenceNumber)
        => fileReferenceNumber & 0x0000FFFFFFFFFFFF;

    /// <summary>
    /// Extract sequence number from a file reference number.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ExtractSequenceNumber(ulong fileReferenceNumber)
        => (ushort)(fileReferenceNumber >> 48);

    #endregion
}
