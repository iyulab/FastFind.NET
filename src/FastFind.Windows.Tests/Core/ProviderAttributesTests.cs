extern alias UnixPackage;

using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using UnixLinux = UnixPackage::FastFind.Unix.Linux;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// <see cref="IFileSystemProvider.GetAttributesAsync"/>: a path's attributes and nothing else, read with one file-system
/// call by the real providers, answered through <c>GetFileInfoAsync</c> by a provider that has nothing cheaper.
/// </summary>
[Trait("Category", "Functional")]
public class ProviderAttributesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fastfind-attrs-" + Guid.NewGuid().ToString("N")[..8]);

    public ProviderAttributesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static IEnumerable<IFileSystemProvider> Providers()
    {
        yield return new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);
        yield return new UnixLinux.LinuxFileSystemProvider();
    }

    [Fact]
    public async Task A_File_And_A_Directory_Report_Their_Own_Attributes()
    {
        var file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "x");
        var directory = Path.Combine(_root, "dir");
        Directory.CreateDirectory(directory);
        File.SetAttributes(directory, FileAttributes.Directory | FileAttributes.Hidden);

        foreach (var provider in Providers())
        {
            using (provider as IDisposable)
            {
                (await provider.GetAttributesAsync(file))!.Value.HasFlag(FileAttributes.Directory).Should().BeFalse(provider.GetType().Name);
                var attributes = await provider.GetAttributesAsync(directory);
                attributes.Should().NotBeNull(provider.GetType().Name);
                attributes!.Value.HasFlag(FileAttributes.Directory).Should().BeTrue(provider.GetType().Name);
                attributes.Value.HasFlag(FileAttributes.Hidden).Should().BeTrue(provider.GetType().Name);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-such-file-anywhere.txt")]
    [InlineData("Z:\\no\\such\\drive\\at\\all")]
    public async Task A_Path_That_Cannot_Be_Read_Reports_Nothing(string relative)
    {
        var path = relative.Length == 0 ? relative : relative.Contains(':') ? relative : Path.Combine(_root, relative);

        foreach (var provider in Providers())
        {
            using (provider as IDisposable)
            {
                (await provider.GetAttributesAsync(path)).Should().BeNull(provider.GetType().Name);
            }
        }
    }

    [Fact]
    public async Task A_Provider_Without_A_Cheaper_Way_Answers_From_Its_File_Info()
    {
        var provider = new ScriptedFileSystemProvider();
        var hiddenDirectory = new FileItem
        {
            FullPath = @"C:\data\.git",
            Name = ".git",
            DirectoryPath = @"C:\data",
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        };
        provider.OnDisk[hiddenDirectory.FullPath] = hiddenDirectory;

        (await ((IFileSystemProvider)provider).GetAttributesAsync(@"C:\data\.git"))
            .Should().Be(FileAttributes.Directory | FileAttributes.Hidden);
        (await ((IFileSystemProvider)provider).GetAttributesAsync(@"C:\data\gone")).Should().BeNull();
    }
}
