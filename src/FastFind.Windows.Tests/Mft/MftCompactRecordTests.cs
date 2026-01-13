using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FastFind.Models;
using FastFind.Windows.Mft;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Tests for MftCompactRecord memory-optimized structure.
/// Phase 1.4: Target 40-byte struct using StringPool IDs instead of string references.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Suite", "MFT")]
[SupportedOSPlatform("windows")]
public class MftCompactRecordTests
{
    private readonly ITestOutputHelper _output;

    public MftCompactRecordTests(ITestOutputHelper output)
    {
        _output = output;
        // Reset StringPool for test isolation
        StringPool.Reset();
    }

    #region Size Verification Tests

    [Fact]
    public void MftCompactRecord_Size_Is40Bytes()
    {
        // This is the critical test - verifying our memory target
        var size = Marshal.SizeOf<MftCompactRecord>();

        _output.WriteLine($"MftCompactRecord size: {size} bytes");

        size.Should().Be(40, "MftCompactRecord must be exactly 40 bytes for memory optimization");
    }

    [Fact]
    public void MftCompactRecord_IsValueType()
    {
        typeof(MftCompactRecord).IsValueType.Should().BeTrue();
    }

    [Fact]
    public void MftCompactRecord_IsReadonly()
    {
        // Verify all public fields are readonly
        var fields = typeof(MftCompactRecord).GetFields();
        foreach (var field in fields)
        {
            if (field.IsPublic)
            {
                field.IsInitOnly.Should().BeTrue($"Field {field.Name} should be readonly");
            }
        }
    }

    #endregion

    #region Field Tests

    [Fact]
    public void MftCompactRecord_StoresFileReferenceNumber()
    {
        var record = new MftCompactRecord(
            fileReferenceNumber: 0x0001000000000001,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.FileReferenceNumber.Should().Be(0x0001000000000001);
    }

    [Fact]
    public void MftCompactRecord_StoresParentFileReferenceNumber()
    {
        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 0x0002000000000005,
            fileNameId: 100,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.ParentFileReferenceNumber.Should().Be(0x0002000000000005);
    }

    [Fact]
    public void MftCompactRecord_StoresFileNameId()
    {
        var nameId = StringPool.Intern("TestFile.txt");

        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: (uint)nameId,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.FileNameId.Should().Be((uint)nameId);
    }

    [Fact]
    public void MftCompactRecord_StoresAttributes()
    {
        var attributes = (uint)(FileAttributes.Directory | FileAttributes.Hidden);

        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: attributes,
            fileSize: 0,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.Attributes.Should().Be(attributes);
    }

    [Fact]
    public void MftCompactRecord_StoresFileSize()
    {
        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: 0,
            fileSize: 1_234_567_890L,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.FileSize.Should().Be(1_234_567_890L);
    }

    [Fact]
    public void MftCompactRecord_StoresModifiedTicks()
    {
        var now = DateTime.UtcNow;

        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: now.Ticks);

        record.ModifiedTicks.Should().Be(now.Ticks);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void IsDirectory_ReturnsTrueForDirectoryAttribute()
    {
        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: (uint)FileAttributes.Directory,
            fileSize: 0,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void IsDirectory_ReturnsFalseForFileAttribute()
    {
        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: (uint)FileAttributes.Normal,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.IsDirectory.Should().BeFalse();
    }

    [Fact]
    public void ModifiedTime_ReturnsCorrectDateTime()
    {
        var expected = new DateTime(2025, 1, 13, 12, 0, 0, DateTimeKind.Utc);

        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: expected.Ticks);

        record.ModifiedTime.Should().Be(expected);
    }

    [Fact]
    public void GetRecordNumber_ExtractsLower48Bits()
    {
        // File reference number: sequence (16 bits) + record number (48 bits)
        var fileRef = 0x0001_0000_0000_0001UL; // seq=1, record=1

        var record = new MftCompactRecord(
            fileReferenceNumber: fileRef,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.GetRecordNumber().Should().Be(1UL);
    }

    [Fact]
    public void GetSequenceNumber_ExtractsUpper16Bits()
    {
        var fileRef = 0x0005_0000_0000_0001UL; // seq=5, record=1

        var record = new MftCompactRecord(
            fileReferenceNumber: fileRef,
            parentFileReferenceNumber: 5,
            fileNameId: 100,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.GetSequenceNumber().Should().Be(5);
    }

    #endregion

    #region StringPool Integration Tests

    [Fact]
    public void GetFileName_ResolvesFromStringPool()
    {
        var fileName = "Document.docx";
        var nameId = StringPool.InternName(fileName);

        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: (uint)nameId,
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.GetFileName().Should().Be(fileName);
    }

    [Fact]
    public void GetFileName_ReturnsEmptyForZeroId()
    {
        var record = new MftCompactRecord(
            fileReferenceNumber: 1,
            parentFileReferenceNumber: 5,
            fileNameId: 0, // Zero ID = empty
            attributes: 0,
            fileSize: 1024,
            modifiedTicks: DateTime.UtcNow.Ticks);

        record.GetFileName().Should().BeEmpty();
    }

    #endregion

    #region Conversion Tests

    [Fact]
    public void FromMftFileRecord_ConvertsAllFields()
    {
        var fileName = "ConversionTest.txt";
        var modifiedTime = new DateTime(2025, 1, 13, 12, 0, 0, DateTimeKind.Utc);

        var original = new MftFileRecord(
            fileReferenceNumber: 12345,
            parentFileReferenceNumber: 5,
            attributes: FileAttributes.Normal,
            fileSize: 1024,
            fileName: fileName,
            creationTime: modifiedTime.AddDays(-10),
            modificationTime: modifiedTime,
            accessTime: modifiedTime.AddHours(-1));

        var compact = MftCompactRecord.FromMftFileRecord(original);

        compact.FileReferenceNumber.Should().Be(12345);
        compact.ParentFileReferenceNumber.Should().Be(5);
        compact.Attributes.Should().Be((uint)FileAttributes.Normal);
        compact.FileSize.Should().Be(1024);
        compact.GetFileName().Should().Be(fileName);
        compact.ModifiedTime.Should().Be(modifiedTime);
    }

    [Fact]
    public void ToMftFileRecord_ConvertsBack()
    {
        var fileName = "RoundTrip.pdf";
        var nameId = StringPool.InternName(fileName);
        var modifiedTicks = new DateTime(2025, 1, 13, 12, 0, 0, DateTimeKind.Utc).Ticks;

        var compact = new MftCompactRecord(
            fileReferenceNumber: 99999,
            parentFileReferenceNumber: 5,
            fileNameId: (uint)nameId,
            attributes: (uint)FileAttributes.Archive,
            fileSize: 2048,
            modifiedTicks: modifiedTicks);

        var restored = compact.ToMftFileRecord();

        restored.FileReferenceNumber.Should().Be(99999);
        restored.ParentFileReferenceNumber.Should().Be(5);
        restored.Attributes.Should().Be(FileAttributes.Archive);
        restored.FileSize.Should().Be(2048);
        restored.FileName.Should().Be(fileName);
        // Note: Creation and Access time are set to ModificationTime in compact form
        restored.ModificationTime.Ticks.Should().Be(modifiedTicks);
    }

    #endregion

    #region Memory Comparison Tests

    [Fact]
    public void MftCompactRecord_IsSmallerThanMftFileRecord()
    {
        var compactSize = Marshal.SizeOf<MftCompactRecord>();
        var originalSize = Marshal.SizeOf<MftFileRecord>();

        _output.WriteLine($"MftCompactRecord: {compactSize} bytes");
        _output.WriteLine($"MftFileRecord: {originalSize} bytes (struct only, excludes string heap)");
        _output.WriteLine($"Savings: {originalSize - compactSize} bytes per record");

        compactSize.Should().BeLessThan(originalSize,
            "Compact record should be smaller than original");
    }

    [Fact]
    public void MftCompactRecord_ArrayMemory_MeasureSavings()
    {
        const int recordCount = 100_000;

        var compactSize = Marshal.SizeOf<MftCompactRecord>() * recordCount;
        var originalStructSize = Marshal.SizeOf<MftFileRecord>() * recordCount;

        // Estimate average filename overhead (string object header + chars)
        const int avgFileNameLength = 20;
        const int stringOverhead = 26; // Object header (16) + length (4) + null terminator (2) + 4 alignment
        var avgStringSize = stringOverhead + avgFileNameLength * 2;
        var originalTotalSize = originalStructSize + avgStringSize * recordCount;

        var savings = originalTotalSize - compactSize;
        var savingsPercent = (double)savings / originalTotalSize * 100;

        _output.WriteLine($"=== Memory Comparison for {recordCount:N0} records ===");
        _output.WriteLine($"MftCompactRecord array: {compactSize / (1024.0 * 1024.0):F2} MB");
        _output.WriteLine($"MftFileRecord array (with strings): {originalTotalSize / (1024.0 * 1024.0):F2} MB");
        _output.WriteLine($"Memory savings: {savings / (1024.0 * 1024.0):F2} MB ({savingsPercent:F1}%)");

        savingsPercent.Should().BeGreaterThan(30, "Should save at least 30% memory");
    }

    #endregion

    #region Bulk Operation Tests

    [Fact(Skip = "Performance test - run manually")]
    [Trait("Category", "Performance")]
    public void MftCompactRecord_BulkCreation_Performance()
    {
        const int recordCount = 100_000;
        var records = new MftCompactRecord[recordCount];
        var random = new Random(42);

        // Pre-intern some common filenames
        var commonNames = new[]
        {
            "desktop.ini", "thumbs.db", "ntuser.dat",
            "index.html", "styles.css", "app.js"
        };
        var nameIds = commonNames.Select(n => (uint)StringPool.InternName(n)).ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < recordCount; i++)
        {
            records[i] = new MftCompactRecord(
                fileReferenceNumber: (ulong)i,
                parentFileReferenceNumber: 5,
                fileNameId: nameIds[random.Next(nameIds.Length)],
                attributes: (uint)FileAttributes.Normal,
                fileSize: random.Next(1, 1_000_000),
                modifiedTicks: DateTime.UtcNow.Ticks);
        }

        sw.Stop();
        var rate = recordCount / sw.Elapsed.TotalSeconds;

        _output.WriteLine($"Created {recordCount:N0} records in {sw.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"Rate: {rate:N0} records/sec");

        // Should be very fast - no heap allocations
        rate.Should().BeGreaterThan(1_000_000, "Bulk creation should exceed 1M records/sec");
    }

    #endregion
}
