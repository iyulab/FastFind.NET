# FastFind.NET Performance Benchmarks

Comprehensive performance benchmarks for FastFind.NET demonstrating production-ready performance metrics.

## Test Environment

| Component | Specification |
|-----------|---------------|
| **OS** | Windows 11 (Administrator) |
| **Runtime** | .NET 10.0 |
| **CPU** | Multi-core x64 |
| **Storage** | NTFS Drives (C:, D:) |
| **MFT Mode** | Enabled (Auto-detected) |

---

## MFT (Master File Table) Performance

### File Enumeration Benchmark

| Metric | Result | Notes |
|--------|--------|-------|
| **Files Enumerated** | 10,000 | Test sample size |
| **Time** | 321.83 ms | Cold start included |
| **Throughput** | **31,073 files/sec** | Production speed |

### MFT vs Standard Mode Comparison

| Mode | Speed | Use Case |
|------|-------|----------|
| **MFT** | ~31,000 files/sec | Admin privileges available |
| **Standard** | ~500-1,000 files/sec | Non-admin fallback |
| **Improvement** | **30-60x faster** | |

> **Note**: MFT mode automatically activates when running with administrator privileges on NTFS drives.

---

## SIMD String Matching Performance

### Cross-Platform Architecture

SIMDStringMatcher uses a 3-tier fallback strategy with JIT dead-branch elimination:

| Tier | Instruction Set | Platforms | Chars/iteration |
|------|----------------|-----------|-----------------|
| **Vector256** | AVX2 | x86-64 (Intel/AMD) | 16 |
| **Vector128** | SSE2 / NEON | x86-64, ARM64 (Apple Silicon) | 8 |
| **Scalar** | None | All platforms | 1 |

The JIT compiler eliminates unused code paths at runtime based on `Vector256.IsHardwareAccelerated` / `Vector128.IsHardwareAccelerated` checks.

### Hardware-Accelerated Search

| Metric | Result | Target | Status |
|--------|--------|--------|--------|
| **Operations/sec** | 1,877,459 | 1,000,000 | ✅ **87% above target** |
| **SIMD Utilization** | 100% | - | Full hardware acceleration |
| **Memory per Op** | 439 bytes | - | Low GC pressure |

### String Pool Performance

| Metric | Result | Notes |
|--------|--------|-------|
| **Interning Rate** | 6,437 paths/sec | High-throughput |
| **Memory Reduction** | 60-80% | vs. naive string storage |
| **Deduplication** | 100% | Perfect deduplication |

---

## Search Index Performance

### WindowsSearchIndex Benchmarks

| Operation | Performance | Notes |
|-----------|-------------|-------|
| **Indexing Rate** | 243,856 files/sec | 143% above target |
| **Search Rate** | 1,680,631 ops/sec | 68% above target |
| **FastFileItem Creation** | 202,347 items/sec | Ultra-optimized struct |

### Path Trie Index

| Query Type | Time | Files |
|------------|------|-------|
| BasePath Search | 1-2 ms | 100 results |
| Extension Filter | < 200 ms | Full index |
| Text Search | < 100 ms | With Trie optimization |

---

## Memory Efficiency

### FastFileItem Struct

| Metric | Value |
|--------|-------|
| **Struct Size** | 61 bytes |
| **String Interning** | Enabled |
| **GC Pressure** | Minimal |

### Memory Pool Statistics

| Metric | Value |
|--------|-------|
| **Buffer Reuse Rate** | 87% |
| **Non-blocking I/O** | 95% |
| **Channel Throughput** | 1.2M items/sec |

---

## Comparison with Industry Tools

| Tool | Enumeration Speed | Notes |
|------|-------------------|-------|
| **Everything (voidtools)** | ~500,000 files/sec | Industry benchmark |
| **FastFind.NET MFT** | ~31,000 files/sec | 6% of Everything |
| **Windows Search API** | ~1,000 files/sec | Standard API |

> FastFind.NET achieves **30x faster** performance than standard Windows APIs while providing a clean .NET interface.

---

## Running Benchmarks

### Prerequisites
- .NET 10.0 SDK
- Administrator privileges (for MFT benchmarks)
- Windows 10/11 with NTFS drives

### Run Integration Tests
```bash
# Run MFT integration tests
dotnet test src/FastFind.Windows.Tests --filter "MftIntegrationTests"

# Run with detailed output
dotnet test src/FastFind.Windows.Tests --filter "MftIntegrationTests" --logger "trx"
```

### Run BenchmarkDotNet Benchmarks
```bash
# Run all benchmarks
dotnet run --project src/FastFind.Benchmarks -c Release

# Run specific benchmark
dotnet run --project src/FastFind.Benchmarks -c Release -- --filter "*StringMatcher*"
```

---

## Test Results Summary

```
=== MFT Availability Diagnostics ===
Is Administrator: True
NTFS Drives: C, D
Can Use MFT: True
Reason: MFT access available for drives: C, D

>>> MFT MODE ACTIVE - High performance enabled

=== MFT Performance Benchmark ===
Mode: Mft
Files enumerated: 10,000
Time: 321.83ms
Rate: 31,073 files/sec
```

---

## Linux Test Results (WSL)

### Test Environment

| Component | Specification |
|-----------|---------------|
| **OS** | Ubuntu 24.04 (WSL2) |
| **Runtime** | .NET 10.0.103 |
| **File System** | ext4 (via /mnt/d, DrvFs) |

### Test Suite Results

| Test Category | Tests | Status | Time |
|---------------|-------|--------|------|
| Factory Registration | 3 | ✅ All Passed | 444ms |
| LinuxFileSystemProvider | 8 | ✅ All Passed | 508ms |
| LinuxFileMonitor | 1 | ✅ All Passed | 917ms |
| E2E Integration | 3 | ✅ All Passed | 636ms |
| **Total** | **15** | **✅ All Passed** | **4.87s** |

### Linux Enumeration Architecture

LinuxFileSystemProvider uses Channel-based BFS parallel traversal:

| Feature | Implementation |
|---------|---------------|
| **Parallelism** | BoundedChannel(1000) with multiple worker tasks |
| **Depth Strategy** | depth ≤ 2 → queue dispatch, deeper → inline |
| **Change Tracking** | FileSystemWatcher (inotify) with DropOldest backpressure |
| **Mount Discovery** | /proc/mounts parsing, virtual FS filtering |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| v1.0.10 | 2026-01 | MFT integration, HybridFileSystemProvider |
| v1.0.13 | 2026-02 | Capability checks, StringPool improvements |
| v1.0.14 | 2026-02 | Extension normalization, FTS5 recovery, disposal logging |
| v1.0.14+ | 2026-02 | Linux cross-platform support, SIMD Vector128/256 migration |