using System.Diagnostics;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// What the directory walk reports and where it stops, on a small tree.
/// </summary>
[Trait("Category", "Functional")]
public class DirectoryWalkSemanticsTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = new();

    public DirectoryWalkSemanticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-walksem-" + Guid.NewGuid().ToString("N")[..8]);

        // f0.txt, L1\f1.txt, L1\L2\f2.txt, L1\L2\L3\f3.txt, L1\L2\L3\L4\f4.txt
        var dir = _root;
        for (var level = 0; level <= 4; level++)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"f{level}.txt"), "x");
            dir = Path.Combine(dir, $"L{level + 1}");
        }
    }

    public void Dispose()
    {
        // A junction is removed on its own first, so deleting the tree never reaches through it.
        foreach (var link in _links)
        {
            try { Directory.Delete(link); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static IndexingOptions Options(Action<IndexingOptions>? configure = null)
    {
        var options = new IndexingOptions
        {
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            IncludeSystem = true,
        };
        configure?.Invoke(options);
        return options;
    }

    private List<string> Walk(IndexingOptions options, string? root = null)
    {
        root ??= _root;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return new DirectoryWalk(options)
            .Enumerate(root, timeout.Token)
            .Select(p => Path.GetRelativePath(root, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "the test needs to create a junction");
        _links.Add(link);
        return link;
    }

    [Fact]
    public void A_File_Whose_Only_Attribute_Is_Normal_Should_Be_Reported()
    {
        // A file with its archive bit cleared (a backup tool, a restore, some file systems) carries
        // FILE_ATTRIBUTE_NORMAL alone. The walk skipped it: the attributes to skip started from Normal.
        var path = Path.Combine(_root, "plain.txt");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.Normal);
        File.GetAttributes(path).Should().Be(FileAttributes.Normal, "the test needs a file with no other attribute");

        Walk(Options(o => { o.IncludeHidden = false; o.IncludeSystem = false; })).Should().Contain("plain.txt");
        Walk(Options()).Should().Contain("plain.txt");
    }
}
