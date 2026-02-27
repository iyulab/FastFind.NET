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

        // 6 files (file1.txt, file2.cs, .hidden, sub1/file3.txt, sub1/sub1a/file4.log, sub2/file5.pdf)
        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().HaveCount(6);
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
    public async Task EnumerateFilesAsync_ShouldExcludeHiddenWhenNotRequested()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.IncludeHidden = false;

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        // .hidden file should be excluded
        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().HaveCount(5);
        files.Should().NotContain(i => i.Name.StartsWith('.'));
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldReturnDirectories()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        // 3 directories (sub1, sub1a, sub2)
        var dirs = items.Where(i => i.IsDirectory).ToList();
        dirs.Should().HaveCount(3);
        dirs.Select(d => d.Name).Should().Contain(new[] { "sub1", "sub1a", "sub2" });
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldRespectExcludedPaths()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.ExcludedPaths = new List<string> { Path.Combine(_fixture.RootPath, "sub1") };

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        // sub1 and sub1a should be excluded, so no file3.txt or file4.log
        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().NotContain(i => i.Name == "file3.txt");
        files.Should().NotContain(i => i.Name == "file4.log");
    }

    [Fact]
    public async Task EnumerateFilesAsync_ShouldRespectMaxFileSize()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var provider = CreateProvider();
        var options = CreateOptions();
        options.MaxFileSize = 200; // bytes

        var items = new List<FileItem>();
        await foreach (var item in provider.EnumerateFilesAsync(new[] { _fixture.RootPath }, options))
        {
            items.Add(item);
        }

        // Only files <= 200 bytes: file1.txt(100), file2.cs(200), .hidden(50), file3.txt(150)
        var files = items.Where(i => !i.IsDirectory).ToList();
        files.Should().NotContain(i => i.Name == "file4.log"); // 300 bytes
        files.Should().NotContain(i => i.Name == "file5.pdf"); // 500 bytes
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
