using FastFind.Models;
using FluentAssertions;

namespace FastFind.Windows.Tests;

public class IndexingOptionsTests
{
    [Fact]
    public void MaxFileCount_DefaultsToNull()
    {
        var options = new IndexingOptions();
        options.MaxFileCount.Should().BeNull();
    }

    [Fact]
    public void Validate_WithPositiveMaxFileCount_ReturnsValid()
    {
        var options = new IndexingOptions
        {
            SpecificDirectories = { "C:\\temp" },
            MaxFileCount = 50_000
        };
        var (isValid, _) = options.Validate();
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithZeroMaxFileCount_ReturnsInvalid()
    {
        var options = new IndexingOptions
        {
            SpecificDirectories = { "C:\\temp" },
            MaxFileCount = 0
        };
        var (isValid, error) = options.Validate();
        isValid.Should().BeFalse();
        error.Should().Contain("MaxFileCount");
    }

    [Fact]
    public void Validate_WithNegativeMaxFileCount_ReturnsInvalid()
    {
        var options = new IndexingOptions
        {
            SpecificDirectories = { "C:\\temp" },
            MaxFileCount = -1
        };
        var (isValid, error) = options.Validate();
        isValid.Should().BeFalse();
        error.Should().Contain("MaxFileCount");
    }
}
