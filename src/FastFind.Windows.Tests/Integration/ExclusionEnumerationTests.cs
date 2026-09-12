using FastFind.Models;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// What the standard Windows provider actually leaves out of an index, over a real directory tree.
/// </summary>
/// <remarks>
/// The comparison used to be a case-insensitive substring of the full path, which both missed an
/// exclusion written with forward slashes and excluded files that merely contained the word —
/// <c>temp</c> took <c>attempts</c> with it.
/// </remarks>
[Trait("Category", "Functional")]
public class ExclusionEnumerationTests : IDisposable
{
    private readonly string _root;

    public ExclusionEnumerationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-exclusions-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "binaries"));
        Directory.CreateDirectory(Path.Combine(_root, "attempts"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        File.WriteAllText(Path.Combine(_root, "bin", "app.dll"), "x");
        File.WriteAllText(Path.Combine(_root, "binaries", "keep.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "attempts", "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "program.cs"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private async Task<List<string>> EnumerateAsync(params string[] excludedPaths)
    {
        using var provider = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);

        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = excludedPaths.ToList(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
        };

        var names = new List<string>();
        await foreach (var item in provider.EnumerateFilesAsync([_root], options))
        {
            if (!item.IsDirectory) names.Add(item.Name);
        }

        return names;
    }

    [Fact]
    public async Task An_Exclusion_Written_With_Forward_Slashes_Should_Be_Honoured()
    {
        var names = await EnumerateAsync(_root.Replace('\\', '/') + "/bin");

        names.Should().NotContain("app.dll");
        names.Should().Contain("keep.txt", "binaries is a different directory");
        names.Should().Contain("program.cs");
    }

    [Fact]
    public async Task A_Bare_Name_Should_Exclude_That_Directory_Only()
    {
        var names = await EnumerateAsync("bin");

        names.Should().NotContain("app.dll");
        names.Should().Contain("keep.txt");
    }

    [Fact]
    public async Task An_Exclusion_Should_Not_Match_Inside_A_Segment()
    {
        // "attempt" is a substring of the "attempts" directory but names no segment of it. The
        // comparison this replaces excluded the file anyway.
        var names = await EnumerateAsync("attempt");

        names.Should().Contain("notes.txt");
    }

    [Fact]
    public async Task Nothing_Should_Be_Excluded_Without_An_Exclusion_List()
    {
        var names = await EnumerateAsync();

        names.Should().Contain(["app.dll", "keep.txt", "notes.txt", "program.cs"]);
    }
}
