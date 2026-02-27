using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Unix.Tests.TestFixtures;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class LinuxFileSystemProviderTests : IClassFixture<TestFileTreeFixture>
{
    private readonly TestFileTreeFixture _fixture;

    public LinuxFileSystemProviderTests(TestFileTreeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldReturnAllFiles()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        // 5 files + 3 directories
        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().HaveCount(5);
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldRespectExcludedExtensions()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.ExcludedExtensions = new List<string> { ".log" };

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        items.Where(i => !i.IsDirectory).Should().NotContain(i => i.Extension == ".log");
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldPopulateFields()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();

        FileItem? found = null;
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            if (item.Name == "file1.txt") { found = item; break; }
        }

        found.Should().NotBeNull();
        found!.Size.Should().Be(100);
        found.Extension.Should().Be(".txt");
        found.FullPath.Should().EndWith("file1.txt");
        found.DriveLetter.Should().Be('/');
        found.IsDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task GetFileInfoAsync_ExistingFile_ShouldReturnInfo()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var filePath = Path.Combine(_fixture.RootPath, "file1.txt");

        var result = await provider.GetFileInfoAsync(filePath);
        result.Should().NotBeNull();
        result!.Name.Should().Be("file1.txt");
    }

    [Fact]
    public async Task GetFileInfoAsync_NonExistent_ShouldReturnNull()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var result = await provider.GetFileInfoAsync("/nonexistent/path/file.txt");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAvailableLocationsAsync_ShouldReturnAtLeastRoot()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var locations = await provider.GetAvailableLocationsAsync();
        locations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetFileSystemTypeAsync_Root_ShouldReturnType()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var fsType = await provider.GetFileSystemTypeAsync("/");
        fsType.Should().NotBe("unknown");
    }

    [Fact]
    public void IsAvailable_OnLinux_ShouldBeTrue()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        provider.IsAvailable.Should().BeTrue();
        provider.SupportedPlatform.Should().Be(PlatformType.Linux);
    }

    private static IFileSystemProvider CreateProvider()
        => new FastFind.Unix.Linux.LinuxFileSystemProvider();

    private IndexingOptions CreateOptions() => new()
    {
        SpecificDirectories = { _fixture.RootPath },
        IncludeHidden = true,
        ExcludedPaths = new List<string>(),
        ExcludedExtensions = new List<string>()
    };
}
