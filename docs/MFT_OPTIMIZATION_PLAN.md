# FastFind.NET MFT Performance Optimization Plan

## Executive Summary

Based on strict performance testing and research of industry-leading implementations (Everything by voidtools), this document outlines a comprehensive optimization plan to achieve 200,000+ records/sec MFT enumeration speed and &lt;100 bytes/record memory efficiency.

### Current Performance
| Metric | Current | Target | Gap |
|--------|---------|--------|-----|
| Enumeration Rate | 144,162 records/sec | 200,000 records/sec | -28% |
| Memory per Record | 209.68 bytes | 100 bytes | +109% |
| Time per 100K | 693.66 ms | 500 ms | +38% |

### Target Baseline
- **Everything (voidtools)**: ~500,000 records/sec
- **Our Target**: 40% of Everything = 200,000 records/sec

---

## Phase 1: Binary Parsing Optimization (Expected: +30% speed)

### 1.1 Replace BitConverter with Span&lt;T&gt; and BinaryPrimitives

**Current Implementation (slow):**
```csharp
recordLength = BitConverter.ToUInt32(buffer, offset);
fileReferenceNumber = BitConverter.ToUInt64(buffer, offset + 8);
```

**Optimized Implementation:**
```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static MftFileRecord? ParseUsnRecordFast(ReadOnlySpan<byte> buffer, ref int offset)
{
    if (buffer.Length < offset + 4) return null;

    var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
    if (recordLength == 0 || buffer.Length < offset + (int)recordLength) return null;

    var recordSpan = buffer.Slice(offset, (int)recordLength);
    offset += (int)recordLength;

    var fileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(recordSpan[8..]);
    var parentFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(recordSpan[16..]);
    // ... etc
}
```

**Benefits:**
- Zero allocations for parsing
- SIMD-friendly memory access patterns
- No bounds checking overhead with slicing

### 1.2 Use Unsafe Pointer Arithmetic for Hot Paths

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static unsafe MftFileRecord? ParseUsnRecordUnsafe(byte* buffer, int length, ref int offset)
{
    if (offset + 60 > length) return null;

    byte* ptr = buffer + offset;
    uint recordLength = *(uint*)ptr;

    if (recordLength == 0 || offset + recordLength > length)
        return null;

    ulong fileRef = *(ulong*)(ptr + 8);
    ulong parentRef = *(ulong*)(ptr + 16);
    // Direct memory access - no bounds checking
}
```

### 1.3 Increase Buffer Size

**Current:** 64KB buffer
**Optimized:** 4MB buffer (matching MFTIndexer approach)

```csharp
private const int MFT_BUFFER_SIZE = 4 * 1024 * 1024; // 4MB for optimal throughput
```

**Rationale:** Larger buffers reduce DeviceIoControl syscall overhead. Each syscall has ~1-2ms overhead; with 4MB buffers we make 4x fewer calls.

---

## Phase 2: Memory Layout Optimization (Expected: -60% memory)

### 2.1 Redesign MftFileRecord as Compact Struct

**Current MftFileRecord (estimated 208+ bytes):**
```csharp
public readonly struct MftFileRecord
{
    public readonly ulong FileReferenceNumber;      // 8 bytes
    public readonly ulong ParentFileReferenceNumber; // 8 bytes
    public readonly FileAttributes Attributes;       // 4 bytes
    public readonly long FileSize;                   // 8 bytes
    public readonly string FileName;                 // 8 bytes (reference) + heap allocation
    public readonly DateTime CreationTime;           // 8 bytes
    public readonly DateTime ModificationTime;       // 8 bytes
    public readonly DateTime AccessTime;             // 8 bytes
    // Total: 60 bytes fixed + ~150 bytes string allocation
}
```

**Optimized MftCompactRecord (target: 48 bytes + pooled string):**
```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct MftCompactRecord
{
    public readonly ulong FileReferenceNumber;       // 8 bytes (includes record# + seq#)
    public readonly ulong ParentFileReferenceNumber; // 8 bytes
    public readonly uint FileNameId;                 // 4 bytes (StringPool ID)
    public readonly uint Attributes;                 // 4 bytes
    public readonly long FileSize;                   // 8 bytes
    public readonly long ModificationTimeTicks;      // 8 bytes (raw ticks, no DateTime)
    public readonly int ExtensionId;                 // 4 bytes (common extensions table)
    // Padding for alignment                         // 4 bytes
    // Total: 48 bytes
}
```

### 2.2 Implement High-Performance String Pool

**Dedicated MftStringPool:**
```csharp
public sealed class MftStringPool
{
    // Pre-allocated chunks for zero-allocation pooling
    private readonly List<char[]> _chunks = new();
    private readonly Dictionary<int, (int ChunkIndex, int Start, int Length)> _strings = new();
    private readonly object _lock = new();

    private const int CHUNK_SIZE = 16 * 1024 * 1024; // 16MB chunks
    private int _currentChunk = 0;
    private int _currentOffset = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetOrAdd(ReadOnlySpan<char> value)
    {
        var hash = GetHashCode(value);

        // Lock-free read path for existing strings
        if (_strings.TryGetValue(hash, out var location))
        {
            // Verify it's actually the same string
            var stored = GetSpan(location);
            if (stored.SequenceEqual(value))
                return (uint)hash;
        }

        // Write path with lock
        return AddNew(value, hash);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetString(uint id) => GetSpan(_strings[(int)id]);
}
```

### 2.3 Common Extensions Table

Pre-define common file extensions to avoid string allocations:

```csharp
private static readonly Dictionary<string, int> CommonExtensions = new()
{
    { ".txt", 1 }, { ".doc", 2 }, { ".docx", 3 }, { ".pdf", 4 },
    { ".jpg", 5 }, { ".jpeg", 6 }, { ".png", 7 }, { ".gif", 8 },
    { ".mp3", 9 }, { ".mp4", 10 }, { ".avi", 11 }, { ".mkv", 12 },
    { ".exe", 13 }, { ".dll", 14 }, { ".cs", 15 }, { ".js", 16 },
    { ".html", 17 }, { ".css", 18 }, { ".json", 19 }, { ".xml", 20 },
    // ... 100+ common extensions
};
```

---

## Phase 3: I/O and Concurrency Optimization (Expected: +20% speed)

### 3.1 Overlapped I/O with Completion Ports

Replace synchronous DeviceIoControl with async IOCP:

```csharp
private async Task<int> ReadMftBufferAsync(
    SafeFileHandle handle,
    Memory<byte> buffer,
    MFT_ENUM_DATA_V0 enumData)
{
    var overlapped = new NativeOverlapped();
    var completionSource = new TaskCompletionSource<int>();

    // Use IOCP for non-blocking I/O
    ThreadPool.BindHandle(handle);

    // Queue async read
    return await completionSource.Task;
}
```

### 3.2 Parallel Buffer Processing

Process multiple buffers concurrently while reading the next:

```csharp
public async IAsyncEnumerable<MftCompactRecord> EnumerateFilesParallelAsync(char driveLetter)
{
    var bufferPool = new ConcurrentQueue<byte[]>();
    var resultChannel = Channel.CreateBounded<MftCompactRecord[]>(16);

    // Producer: Read MFT data
    var readerTask = Task.Run(async () =>
    {
        while (hasMoreData)
        {
            var buffer = await ReadNextBufferAsync();
            await bufferPool.EnqueueAsync(buffer);
        }
    });

    // Consumers: Parse buffers in parallel
    var parserTasks = Enumerable.Range(0, Environment.ProcessorCount)
        .Select(_ => Task.Run(async () =>
        {
            while (await bufferPool.TryDequeueAsync(out var buffer))
            {
                var records = ParseBuffer(buffer);
                await resultChannel.Writer.WriteAsync(records);
            }
        }));

    // Yield results
    await foreach (var batch in resultChannel.Reader.ReadAllAsync())
    {
        foreach (var record in batch)
            yield return record;
    }
}
```

### 3.3 NUMA-Aware Memory Allocation

For multi-socket systems:

```csharp
[DllImport("kernel32.dll")]
private static extern IntPtr VirtualAllocExNuma(
    IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize,
    uint flAllocationType, uint flProtect, uint nndPreferred);
```

---

## Phase 4: Search Query Compilation (Expected: +50% search speed)

### 4.1 Compile Search Patterns to Bytecode

Like Everything, compile search queries to optimized bytecode:

```csharp
public sealed class CompiledSearchQuery
{
    private readonly byte[] _bytecode;
    private readonly SearchOpCode[] _operations;

    public enum SearchOpCode : byte
    {
        MatchExact = 0x01,
        MatchPrefix = 0x02,
        MatchSuffix = 0x03,
        MatchContains = 0x04,
        MatchRegex = 0x05,
        FilterExtension = 0x10,
        FilterSize = 0x11,
        FilterDate = 0x12,
        LogicalAnd = 0x20,
        LogicalOr = 0x21,
        LogicalNot = 0x22,
    }

    public static CompiledSearchQuery Compile(string query)
    {
        var tokens = Tokenize(query);
        var ast = Parse(tokens);
        var bytecode = GenerateBytecode(ast);
        return new CompiledSearchQuery(bytecode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Execute(ref MftCompactRecord record, MftStringPool pool)
    {
        // Interpret bytecode against record
        return InterpretBytecode(ref record, pool);
    }
}
```

### 4.2 SIMD-Accelerated String Matching

Leverage AVX2 for pattern matching:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static bool ContainsAvx2(ReadOnlySpan<char> source, ReadOnlySpan<char> pattern)
{
    if (!Avx2.IsSupported || pattern.Length < 16)
        return source.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    fixed (char* pSource = source)
    fixed (char* pPattern = pattern)
    {
        var firstChar = Vector256.Create(pattern[0]);
        var lastChar = Vector256.Create(pattern[^1]);

        for (int i = 0; i <= source.Length - pattern.Length; i += 16)
        {
            var block = Avx2.LoadVector256((short*)(pSource + i));
            var matchFirst = Avx2.CompareEqual(block, firstChar);

            // Use SIMD to find potential matches, then verify
            var mask = Avx2.MoveMask(matchFirst.AsByte());
            while (mask != 0)
            {
                int pos = BitOperations.TrailingZeroCount(mask) / 2;
                if (VerifyMatch(pSource + i + pos, pPattern, pattern.Length))
                    return true;
                mask &= mask - 1;
            }
        }
    }
    return false;
}
```

---

## Phase 5: USN Journal Integration (Real-time Updates)

### 5.1 Monitor USN Journal for Changes

```csharp
public sealed class UsnJournalMonitor : IDisposable
{
    private readonly SafeFileHandle _volumeHandle;
    private readonly CancellationTokenSource _cts = new();

    public event EventHandler<MftChangeEventArgs>? FileCreated;
    public event EventHandler<MftChangeEventArgs>? FileDeleted;
    public event EventHandler<MftChangeEventArgs>? FileRenamed;

    public async Task StartMonitoringAsync()
    {
        var buffer = new byte[64 * 1024];
        var readData = new READ_USN_JOURNAL_DATA_V1
        {
            StartUsn = GetCurrentUsn(),
            ReasonMask = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE |
                         USN_REASON_RENAME_NEW_NAME | USN_REASON_RENAME_OLD_NAME,
            ReturnOnlyOnClose = false,
            Timeout = 0
        };

        while (!_cts.Token.IsCancellationRequested)
        {
            var result = await ReadUsnJournalAsync(buffer, readData);
            ProcessUsnRecords(buffer, result.BytesReturned);
            readData.StartUsn = result.NextUsn;
        }
    }
}
```

---

## Phase 6: Memory-Mapped Index (Persistence)

### 6.1 Memory-Mapped File Index

Store index in memory-mapped file for instant startup:

```csharp
public sealed class MftPersistentIndex : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    // Header: Magic (4) + Version (4) + RecordCount (8) + StringPoolOffset (8)
    private const int HEADER_SIZE = 24;

    public static MftPersistentIndex Create(string path, long estimatedRecords)
    {
        var fileSize = HEADER_SIZE + (estimatedRecords * 48) + (estimatedRecords * 64); // records + strings
        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Create, null, fileSize);
        return new MftPersistentIndex(mmf);
    }

    public void WriteRecord(int index, ref MftCompactRecord record)
    {
        var offset = HEADER_SIZE + (index * 48);
        _accessor.Write(offset, ref record);
    }

    public MftCompactRecord ReadRecord(int index)
    {
        var offset = HEADER_SIZE + (index * 48);
        _accessor.Read(offset, out MftCompactRecord record);
        return record;
    }
}
```

---

## Implementation Roadmap

### Sprint 1 (Week 1-2): Binary Parsing Optimization
- [ ] Replace BitConverter with BinaryPrimitives
- [ ] Implement Span-based parsing
- [ ] Increase buffer size to 4MB
- [ ] Add benchmarks for parsing speed

### Sprint 2 (Week 3-4): Memory Optimization
- [ ] Design MftCompactRecord struct
- [ ] Implement MftStringPool
- [ ] Create common extensions table
- [ ] Migrate existing code to new structures

### Sprint 3 (Week 5-6): I/O Optimization
- [ ] Implement parallel buffer processing
- [ ] Add producer-consumer pattern
- [ ] Optimize for multi-core systems
- [ ] Add NUMA awareness (optional)

### Sprint 4 (Week 7-8): Search Optimization
- [ ] Design query bytecode format
- [ ] Implement query compiler
- [ ] Add SIMD string matching
- [ ] Integrate with existing search API

### Sprint 5 (Week 9-10): Real-time Updates & Persistence
- [ ] Implement USN Journal monitoring
- [ ] Add memory-mapped index
- [ ] Implement incremental updates
- [ ] Final performance validation

---

## Expected Results

| Metric | Current | After Phase 1-2 | Final Target |
|--------|---------|-----------------|--------------|
| Enumeration Rate | 144K/sec | 250K/sec | 300K+/sec |
| Memory per Record | 209 bytes | 80 bytes | 60 bytes |
| Time per 100K | 694 ms | 400 ms | 300 ms |
| Startup Time | N/A | N/A | &lt;1 sec (with persistence) |

---

## References

- [Everything (voidtools) FAQ](https://www.voidtools.com/faq/)
- [How Everything is so fast](https://www.voidtools.com/forum/viewtopic.php?t=9407)
- [MFTIndexer - C++ Implementation](https://github.com/Laxyny/MFTIndexer)
- [Tutorial: Parsing the MFT](https://handmade.network/wiki/7002-tutorial_parsing_the_mft)
- [Span&lt;T&gt; Performance - Adam Sitnik](https://adamsitnik.com/Span/)
- [StringPool - .NET Community Toolkit](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/high-performance/stringpool)
- [SIMD String Matching - StringZilla](https://github.com/ashvardanian/StringZilla)
- [.NET 2025 Performance Review](https://medium.com/@vikpoca/the-2025-year-end-performance-review-for-net-cb7d259368ba)
