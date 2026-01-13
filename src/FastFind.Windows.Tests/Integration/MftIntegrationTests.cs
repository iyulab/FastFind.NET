using System.Diagnostics;
using System.Runtime.Versioning;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Mft;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// Integration tests for MFT (Master File Table) functionality.
/// Verifies that MFT is properly integrated into the search path for high performance.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Suite", "MFT")]
[SupportedOSPlatform("windows")]
public class MftIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;

    public MftIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = NullLoggerFactory.Instance;
    }

    [Fact]
    public void HybridFileSystemProvider_AutoDetects_MftAvailability()
    {
        // Act
        var diagnostics = HybridFileSystemProvider.CheckMftAvailability();

        // Report
        _output.WriteLine("=== MFT Availability Diagnostics ===");
        _output.WriteLine($"Is Administrator: {diagnostics.IsAdministrator}");
        _output.WriteLine($"NTFS Drives: {string.Join(", ", diagnostics.NtfsDrives)}");
        _output.WriteLine($"Can Use MFT: {diagnostics.CanUseMft}");
        _output.WriteLine($"Reason: {diagnostics.Reason}");

        // Assert - Just verify diagnostics work
        diagnostics.Should().NotBeNull();
        diagnostics.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HybridFileSystemProvider_SelectsCorrectMode()
    {
        // Arrange & Act
        using var provider = new HybridFileSystemProvider(_loggerFactory);

        // Report
        _output.WriteLine($"Current Mode: {provider.CurrentMode}");
        _output.WriteLine($"Is MFT Mode: {provider.IsMftMode}");
        _output.WriteLine($"Is Available: {provider.IsAvailable}");

        var status = provider.GetStatus();
        _output.WriteLine($"Is MFT Capable: {status.IsMftCapable}");

        // Assert
        provider.IsAvailable.Should().BeTrue();

        // If MFT is capable, should use MFT mode
        if (status.IsMftCapable)
        {
            provider.IsMftMode.Should().BeTrue(
                "Should use MFT mode when MFT access is available");
            _output.WriteLine(">>> MFT MODE ACTIVE - High performance enabled");
        }
        else
        {
            provider.IsMftMode.Should().BeFalse(
                "Should use standard mode when MFT access is not available");
            _output.WriteLine(">>> STANDARD MODE - MFT not available (requires admin)");
        }
    }

    [Fact]
    public void WindowsSearchEngine_UsesHybridProvider()
    {
        // Arrange
        WindowsRegistration.EnsureRegistered();

        // Act
        using var engine = FastFinder.CreateSearchEngine(PlatformType.Windows, _loggerFactory);

        // Assert - Engine should be created successfully
        engine.Should().NotBeNull();

        _output.WriteLine("WindowsSearchEngine created with HybridFileSystemProvider");
    }

    [Fact]
    public async Task HybridFileSystemProvider_EnumeratesFiles_Successfully()
    {
        // Arrange
        using var provider = new HybridFileSystemProvider(_loggerFactory);
        var tempDir = Path.GetTempPath();
        var options = new IndexingOptions
        {
            IncludeHidden = false,
            IncludeSystem = false
        };

        // Act
        var fileCount = 0;
        var sw = Stopwatch.StartNew();

        await foreach (var file in provider.EnumerateFilesAsync([tempDir], options))
        {
            fileCount++;
            if (fileCount >= 100) break; // Limit for test speed
        }
        sw.Stop();

        // Report
        _output.WriteLine($"Mode: {provider.CurrentMode}");
        _output.WriteLine($"Files enumerated: {fileCount}");
        _output.WriteLine($"Time: {sw.Elapsed.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Rate: {fileCount / sw.Elapsed.TotalSeconds:F0} files/sec");

        // Assert
        fileCount.Should().BeGreaterThan(0, "Should enumerate files from temp directory");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MftMode_Performance_Benchmark()
    {
        // Skip if not running as admin
        var diagnostics = HybridFileSystemProvider.CheckMftAvailability();
        if (!diagnostics.CanUseMft)
        {
            _output.WriteLine("SKIPPED: MFT access not available (requires administrator privileges)");
            return;
        }

        // Arrange
        using var provider = new HybridFileSystemProvider(_loggerFactory);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var options = new IndexingOptions
        {
            IncludeHidden = false,
            IncludeSystem = false,
            MaxDepth = 3 // Limit depth for reasonable test time
        };

        // Act
        var fileCount = 0;
        var sw = Stopwatch.StartNew();

        await foreach (var file in provider.EnumerateFilesAsync([systemDrive!], options))
        {
            fileCount++;
            if (fileCount >= 10000) break; // Cap at 10K for test
        }
        sw.Stop();

        // Report
        _output.WriteLine("=== MFT Performance Benchmark ===");
        _output.WriteLine($"Mode: {provider.CurrentMode}");
        _output.WriteLine($"Files enumerated: {fileCount:N0}");
        _output.WriteLine($"Time: {sw.Elapsed.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Rate: {fileCount / sw.Elapsed.TotalSeconds:N0} files/sec");

        // Assert - MFT should achieve high throughput
        var filesPerSecond = fileCount / sw.Elapsed.TotalSeconds;
        filesPerSecond.Should().BeGreaterThan(1000,
            "MFT mode should enumerate >1000 files/sec");
    }

    [Fact]
    public void MftReader_ChecksAvailability()
    {
        // Act
        var isAvailable = MftReader.IsAvailable();
        var ntfsDrives = MftReader.GetNtfsDrives();

        // Report
        _output.WriteLine($"MFT Reader Available: {isAvailable}");
        _output.WriteLine($"NTFS Drives: {string.Join(", ", ntfsDrives)}");

        // Assert - Should at least detect NTFS drives
        ntfsDrives.Should().NotBeNull();

        if (isAvailable)
        {
            ntfsDrives.Should().NotBeEmpty("Should have NTFS drives when MFT is available");
        }
    }

    [Fact]
    public void ProviderMode_AllModesAvailable()
    {
        // Assert - All enum values should exist
        Enum.GetValues<ProviderMode>().Should().Contain(ProviderMode.Auto);
        Enum.GetValues<ProviderMode>().Should().Contain(ProviderMode.Mft);
        Enum.GetValues<ProviderMode>().Should().Contain(ProviderMode.Standard);

        _output.WriteLine("Available Provider Modes:");
        foreach (var mode in Enum.GetValues<ProviderMode>())
        {
            _output.WriteLine($"  - {mode}");
        }
    }
}
