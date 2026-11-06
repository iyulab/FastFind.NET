using FastFind.Indexing;
using FastFind.Models;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// What an index built with a given <see cref="IndexingOptions"/> admits — the one definition every
/// provider and the live-scan fallback share.
/// </summary>
[Trait("Category", "Functional")]
public class IndexingAdmissionTests
{
    private static FileItem Item(string path, long size = 10, FileAttributes attributes = FileAttributes.Archive)
    {
        var name = Path.GetFileName(path);
        return new FileItem
        {
            FullPath = path,
            Name = name,
            DirectoryPath = Path.GetDirectoryName(path) ?? string.Empty,
            Extension = Path.GetExtension(path),
            Size = size,
            CreatedTime = DateTime.UtcNow,
            ModifiedTime = DateTime.UtcNow,
            AccessedTime = DateTime.UtcNow,
            Attributes = attributes,
            DriveLetter = path[0],
        };
    }

    private static IndexingOptions Options(Action<IndexingOptions>? configure = null)
    {
        var options = new IndexingOptions
        {
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            IncludeSystem = true,
            MaxFileSize = null,
        };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void Everything_Is_Admitted_Without_Limits()
        => IndexingAdmission.Admits(Item(@"C:\data\a.txt"), Options()).Should().BeTrue();

    [Fact]
    public void A_Hidden_Item_Is_Admitted_Only_When_Hidden_Items_Are_Included()
    {
        var hidden = Item(@"C:\data\.h.txt", attributes: FileAttributes.Hidden);

        IndexingAdmission.Admits(hidden, Options(o => o.IncludeHidden = false)).Should().BeFalse();
        IndexingAdmission.Admits(hidden, Options(o => o.IncludeHidden = true)).Should().BeTrue();
    }

    [Fact]
    public void A_System_Item_Is_Admitted_Only_When_System_Items_Are_Included()
    {
        var system = Item(@"C:\data\s.sys", attributes: FileAttributes.System);

        IndexingAdmission.Admits(system, Options(o => o.IncludeSystem = false)).Should().BeFalse();
        IndexingAdmission.Admits(system, Options(o => o.IncludeSystem = true)).Should().BeTrue();
    }

    [Fact]
    public void An_Item_Over_The_Size_Limit_Is_Not_Admitted_And_One_At_The_Limit_Is()
    {
        var options = Options(o => o.MaxFileSize = 100);

        IndexingAdmission.Admits(Item(@"C:\data\ok.bin", size: 100), options).Should().BeTrue();
        IndexingAdmission.Admits(Item(@"C:\data\big.bin", size: 101), options).Should().BeFalse();
    }

    [Fact]
    public void An_Excluded_Path_Excludes_What_Is_At_Or_Beneath_It_And_Nothing_Beside_It()
    {
        var options = Options(o => o.ExcludedPaths = new List<string> { @"C:\data\bin" });

        IndexingAdmission.Admits(Item(@"C:\data\bin\app.dll"), options).Should().BeFalse();
        IndexingAdmission.Admits(Item(@"C:\data\binaries\keep.txt"), options).Should().BeTrue("a different directory");
    }

    [Fact]
    public void An_Excluded_Extension_Is_Matched_Without_Regard_To_Case()
    {
        var options = Options(o => o.ExcludedExtensions = new List<string> { ".log" });

        IndexingAdmission.Admits(Item(@"C:\data\a.LOG"), options).Should().BeFalse();
        IndexingAdmission.Admits(Item(@"C:\data\a.txt"), options).Should().BeTrue();
    }
}
