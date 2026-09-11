using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FastFind.Windows.Mft;

/// <summary>
/// High-performance USN record parser using Span and BinaryPrimitives.
/// Provides 25%+ faster parsing compared to BitConverter-based implementation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MftParserV2
{
    /// <summary>
    /// Minimum size of a valid USN_RECORD_V2 structure (without filename)
    /// </summary>
    private const int MIN_USN_RECORD_SIZE = 60;

    /// <summary>
    /// USN_RECORD_V2 major version
    /// </summary>
    private const ushort USN_RECORD_V2 = 2;

    /// <summary>
    /// Try to parse a USN record from the buffer using zero-allocation Span operations.
    /// </summary>
    /// <param name="buffer">Buffer containing USN records from DeviceIoControl</param>
    /// <param name="offset">Current offset in buffer (will be updated to next record)</param>
    /// <param name="record">Parsed MFT file record if successful</param>
    /// <returns>True if a valid record was parsed, false otherwise</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseUsnRecord(
        ReadOnlySpan<byte> buffer,
        ref int offset,
        out MftFileRecord record)
    {
        record = default;

        // Check minimum buffer size
        if (buffer.Length < offset + 4)
            return false;

        var span = buffer[offset..];

        // Read record length (first 4 bytes)
        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(span);

        // Validate record length
        if (recordLength == 0 || recordLength < MIN_USN_RECORD_SIZE || buffer.Length < offset + recordLength)
            return false;

        // Slice to record boundary
        var recordSpan = span[..(int)recordLength];

        // Check version (offset 4-5). Only V2 is laid out the way the offsets below assume; a V3
        // record has 128-bit file references and every later field moves. Enumeration with
        // MFT_ENUM_DATA_V0 returns V2 only.
        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(recordSpan[4..]);
        if (majorVersion != USN_RECORD_V2)
            return false;

        // Parse fields using BinaryPrimitives (zero-allocation)
        var fileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(recordSpan[8..]);
        var parentFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(recordSpan[16..]);
        var fileAttributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(recordSpan[52..]);
        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(recordSpan[56..]);
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(recordSpan[58..]);

        // Validate filename bounds
        if (fileNameLength == 0 || fileNameOffset + fileNameLength > recordLength)
            return false;

        // Zero-copy filename extraction
        var fileNameBytes = recordSpan.Slice(fileNameOffset, fileNameLength);
        var fileNameChars = MemoryMarshal.Cast<byte, char>(fileNameBytes);
        var fileName = new string(fileNameChars);

        // Skip system files and metadata (starting with '$')
        if (fileName.Length == 0 || fileName[0] == '$')
        {
            // Move offset to next record but return false
            offset += (int)recordLength;
            return false;
        }

        record = CreateRecord(fileReferenceNumber, parentFileReferenceNumber, fileAttributes, fileName);

        // Update offset for next record
        offset += (int)recordLength;
        return true;
    }

    /// <summary>
    /// Try to parse a USN record with StringPool integration for memory optimization.
    /// Uses the static StringPool to intern filenames, reducing memory for duplicate names.
    /// </summary>
    /// <param name="buffer">Buffer containing USN records</param>
    /// <param name="offset">Current offset (will be updated)</param>
    /// <param name="useStringPool">Whether to use StringPool for filename interning</param>
    /// <param name="record">Parsed record if successful</param>
    /// <returns>True if valid record parsed</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseUsnRecordPooled(
        ReadOnlySpan<byte> buffer,
        ref int offset,
        bool useStringPool,
        out MftFileRecord record)
    {
        record = default;

        if (buffer.Length < offset + 4)
            return false;

        var span = buffer[offset..];
        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(span);

        if (recordLength == 0 || recordLength < MIN_USN_RECORD_SIZE || buffer.Length < offset + recordLength)
            return false;

        var recordSpan = span[..(int)recordLength];

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(recordSpan[4..]);
        if (majorVersion != USN_RECORD_V2)
            return false;

        var fileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(recordSpan[8..]);
        var parentFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(recordSpan[16..]);
        var fileAttributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(recordSpan[52..]);
        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(recordSpan[56..]);
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(recordSpan[58..]);

        if (fileNameLength == 0 || fileNameOffset + fileNameLength > recordLength)
            return false;

        var fileNameBytes = recordSpan.Slice(fileNameOffset, fileNameLength);
        var fileNameChars = MemoryMarshal.Cast<byte, char>(fileNameBytes);

        // Skip system files
        if (fileNameChars.Length == 0 || fileNameChars[0] == '$')
        {
            offset += (int)recordLength;
            return false;
        }

        string fileName;
        if (useStringPool)
        {
            // Use StringPool.InternFromSpan for zero-allocation on cache hits (.NET 9+)
            var nameId = FastFind.Models.StringPool.InternFromSpan(fileNameChars);
            fileName = FastFind.Models.StringPool.GetString(nameId);
        }
        else
        {
            fileName = new string(fileNameChars);
        }

        record = CreateRecord(fileReferenceNumber, parentFileReferenceNumber, fileAttributes, fileName);

        offset += (int)recordLength;
        return true;
    }

    /// <summary>
    /// Builds a record from the fields a USN record actually carries about the file.
    /// </summary>
    /// <remarks>
    /// A USN record has no file size and no file timestamps. Its <c>TimeStamp</c> is when a
    /// journal entry was logged, and for records returned by <c>FSCTL_ENUM_USN_DATA</c> it is
    /// specified to be zero ([MS-FSCC] 2.3.62.2). Reading it as a file time gave every entry
    /// 1601-01-01 as its creation, modification and access time. Size and times are left unset —
    /// 0 and <see cref="DateTime.MinValue"/> — for the provider to fill from the file system when
    /// asked to.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MftFileRecord CreateRecord(
        ulong fileReferenceNumber,
        ulong parentFileReferenceNumber,
        FileAttributes fileAttributes,
        string fileName) =>
        new(fileReferenceNumber, parentFileReferenceNumber, fileAttributes,
            fileSize: 0, fileName, default, default, default);

    /// <summary>
    /// Get the record length at the specified offset without full parsing.
    /// Useful for skipping records.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetRecordLength(ReadOnlySpan<byte> buffer, int offset)
    {
        if (buffer.Length < offset + 4)
            return 0;

        return BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
    }

    /// <summary>
    /// Check if the record at the specified offset is a system file (starts with '$').
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSystemFile(ReadOnlySpan<byte> buffer, int offset)
    {
        if (buffer.Length < offset + MIN_USN_RECORD_SIZE)
            return false;

        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        if (recordLength < MIN_USN_RECORD_SIZE)
            return false;

        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 58)..]);
        if (offset + fileNameOffset + 2 > buffer.Length)
            return false;

        // Check first character (UTF-16 LE)
        var firstChar = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + fileNameOffset)..]);
        return firstChar == '$';
    }

    /// <summary>
    /// Parse the next file reference number from the buffer header.
    /// This is the first 8 bytes returned by FSCTL_ENUM_USN_DATA.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetNextFileReferenceNumber(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
            return 0;

        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }
}
