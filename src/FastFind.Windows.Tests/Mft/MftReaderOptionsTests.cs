using System.Runtime.Versioning;
using FastFind.Windows.Mft;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// Tests for MftReaderOptions configuration.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Suite", "MFT")]
[SupportedOSPlatform("windows")]
public class MftReaderOptionsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var options = MftReaderOptions.Default;

        options.BufferSize.Should().Be(MftReaderOptions.DefaultBufferSize);
        options.UseStringPooling.Should().BeFalse();
        options.SkipSystemFiles.Should().BeTrue();
    }

    [Fact]
    public void DefaultBufferSize_Is1MB()
    {
        MftReaderOptions.DefaultBufferSize.Should().Be(1024 * 1024);
    }

    [Fact]
    public void MinBufferSize_Is64KB()
    {
        MftReaderOptions.MinBufferSize.Should().Be(64 * 1024);
    }

    [Fact]
    public void MaxBufferSize_Is4MB()
    {
        MftReaderOptions.MaxBufferSize.Should().Be(4 * 1024 * 1024);
    }

    [Theory]
    [InlineData(32 * 1024, 64 * 1024)]      // Below min -> clamped to min
    [InlineData(64 * 1024, 64 * 1024)]      // At min -> unchanged
    [InlineData(1024 * 1024, 1024 * 1024)]  // At default -> unchanged
    [InlineData(4 * 1024 * 1024, 4 * 1024 * 1024)] // At max -> unchanged
    [InlineData(8 * 1024 * 1024, 4 * 1024 * 1024)] // Above max -> clamped to max
    public void Validate_ClampsBufferSizeToValidRange(int input, int expected)
    {
        var options = new MftReaderOptions { BufferSize = input };
        var validated = options.Validate();

        validated.BufferSize.Should().Be(expected);
    }

    [Theory]
    [InlineData(65000, 64 * 1024)]    // Not aligned -> aligned down to 64KB
    [InlineData(100000, 98304)]       // 100000 -> 24 * 4096 = 98304
    [InlineData(1000000, 999424)]     // 1000000 -> 244 * 4096 = 999424
    public void Validate_AlignsBufferSizeTo4KBBoundary(int input, int expected)
    {
        var options = new MftReaderOptions { BufferSize = input };
        var validated = options.Validate();

        // Buffer should be aligned to 4KB boundary
        (validated.BufferSize % 4096).Should().Be(0);
        validated.BufferSize.Should().Be(expected);
    }

    [Fact]
    public void Validate_PreservesOtherOptions()
    {
        var options = new MftReaderOptions
        {
            BufferSize = 512 * 1024,
            UseStringPooling = true,
            SkipSystemFiles = false
        };

        var validated = options.Validate();

        validated.UseStringPooling.Should().BeTrue();
        validated.SkipSystemFiles.Should().BeFalse();
    }

    [Fact]
    public void Validate_ReturnsSameInstanceIfNoChanges()
    {
        var options = new MftReaderOptions { BufferSize = 1024 * 1024 }; // Valid, aligned
        var validated = options.Validate();

        validated.Should().BeSameAs(options);
    }

    [Fact]
    public void CreateOptimal_ReturnsValidOptions()
    {
        var options = MftReaderOptions.CreateOptimal();

        options.BufferSize.Should().BeGreaterThanOrEqualTo(MftReaderOptions.MinBufferSize);
        options.BufferSize.Should().BeLessThanOrEqualTo(MftReaderOptions.MaxBufferSize);
        (options.BufferSize % 4096).Should().Be(0);
    }

    [Fact]
    public void MftReader_DefaultConstructor_UsesDefaultOptions()
    {
        using var reader = new FastFind.Windows.Mft.MftReader();

        reader.Options.Should().NotBeNull();
        reader.Options.BufferSize.Should().Be(MftReaderOptions.DefaultBufferSize);
    }

    [Fact]
    public void MftReader_WithCustomOptions_UsesProvidedOptions()
    {
        var customOptions = new MftReaderOptions { BufferSize = 256 * 1024 };
        using var reader = new FastFind.Windows.Mft.MftReader(customOptions);

        reader.Options.BufferSize.Should().Be(256 * 1024);
    }

    [Fact]
    public void MftReader_WithInvalidOptions_ValidatesOnConstruction()
    {
        var invalidOptions = new MftReaderOptions { BufferSize = 10000 }; // Below min, not aligned
        using var reader = new FastFind.Windows.Mft.MftReader(invalidOptions);

        // Should be validated (clamped to min)
        reader.Options.BufferSize.Should().Be(MftReaderOptions.MinBufferSize);
    }
}
