using System.Runtime.Versioning;

namespace FastFind.Windows.Mft;

/// <summary>
/// Configuration options for MFT enumeration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftReaderOptions
{
    /// <summary>
    /// Default buffer size for MFT enumeration (1MB).
    /// Increased from 64KB based on performance research - fewer syscalls = better throughput.
    /// </summary>
    public const int DefaultBufferSize = 1024 * 1024; // 1MB

    /// <summary>
    /// Minimum allowed buffer size (64KB).
    /// Below this, syscall overhead dominates.
    /// </summary>
    public const int MinBufferSize = 64 * 1024;

    /// <summary>
    /// Maximum allowed buffer size (4MB).
    /// Beyond this, memory pressure may impact performance.
    /// </summary>
    public const int MaxBufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// Buffer size for MFT enumeration.
    /// Larger buffers reduce DeviceIoControl syscall overhead (~1.5ms per call).
    /// Default: 1MB (optimal for most systems with 8GB+ RAM).
    /// </summary>
    public int BufferSize { get; init; } = DefaultBufferSize;

    /// <summary>
    /// Whether to use StringPool for filename interning.
    /// Reduces memory for duplicate filenames (e.g., "index.html", "README.md").
    /// Default: false (Phase 1.3 feature).
    /// </summary>
    public bool UseStringPooling { get; init; } = false;

    /// <summary>
    /// Whether to skip system files (names starting with '$').
    /// Default: true.
    /// </summary>
    public bool SkipSystemFiles { get; init; } = true;

    /// <summary>
    /// Validates the options and returns a corrected instance if necessary.
    /// </summary>
    public MftReaderOptions Validate()
    {
        var bufferSize = BufferSize;

        // Clamp buffer size to valid range
        if (bufferSize < MinBufferSize)
            bufferSize = MinBufferSize;
        else if (bufferSize > MaxBufferSize)
            bufferSize = MaxBufferSize;

        // Align buffer size to 4KB boundary (optimal for disk I/O)
        bufferSize = (bufferSize / 4096) * 4096;

        if (bufferSize == BufferSize)
            return this;

        return new MftReaderOptions
        {
            BufferSize = bufferSize,
            UseStringPooling = UseStringPooling,
            SkipSystemFiles = SkipSystemFiles
        };
    }

    /// <summary>
    /// Creates options based on available system resources.
    /// </summary>
    public static MftReaderOptions CreateOptimal()
    {
        var totalMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);

        int bufferSize;
        if (totalMemoryMb >= 16384) // 16GB+
            bufferSize = MaxBufferSize; // 4MB
        else if (totalMemoryMb >= 8192) // 8GB+
            bufferSize = DefaultBufferSize; // 1MB
        else if (totalMemoryMb >= 4096) // 4GB+
            bufferSize = 256 * 1024; // 256KB
        else
            bufferSize = MinBufferSize; // 64KB

        return new MftReaderOptions { BufferSize = bufferSize };
    }

    /// <summary>
    /// Default options instance.
    /// </summary>
    public static MftReaderOptions Default { get; } = new MftReaderOptions();
}
