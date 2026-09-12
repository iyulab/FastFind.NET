using System.Runtime.Versioning;
using FastFind.Models;
using FastFind.Windows.Mft;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// The change-journal provider leaves out what the exclusion list names, over a real volume.
/// </summary>
/// <remarks>
/// Skipped where journal enumeration is unavailable — it needs administrator rights on NTFS — which
/// is every CI runner this project uses. It is the negative control for the exclusion comparison on
/// this provider: it was a case-insensitive substring of the full path, so an exclusion spelled with
/// forward slashes excluded nothing.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Suite", "MFT")]
[SupportedOSPlatform("windows")]
public class MftExclusionTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public MftExclusionTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "fastfind-mft-exclusions-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "binaries"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        File.WriteAllText(Path.Combine(_root, "bin", "excluded.dll"), "x");
        File.WriteAllText(Path.Combine(_root, "binaries", "kept.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "kept.cs"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private async Task<List<string>> EnumerateAsync(params string[] excludedPaths)
    {
        await using var provider = new MftFileSystemProvider(NullLogger<MftFileSystemProvider>.Instance);

        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = excludedPaths.ToList(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var names = new List<string>();
        await foreach (var item in provider.EnumerateFilesAsync([_root], options, cts.Token))
        {
            if (!item.IsDirectory) names.Add(item.Name);
        }

        return names;
    }

    [Fact]
    public async Task An_Exclusion_Written_With_Forward_Slashes_Should_Be_Honoured()
    {
        if (!MftFileSystemProvider.IsMftAccessAvailable)
        {
            _output.WriteLine("Skipped: journal enumeration needs administrator rights on NTFS.");
            return;
        }

        var baseline = await EnumerateAsync();
        baseline.Should().Contain("excluded.dll", "the probe tree has to be visible before excluding any of it");
        baseline.Should().Contain("kept.txt");

        var names = await EnumerateAsync(_root.Replace('\\', '/') + "/bin");

        names.Should().NotContain("excluded.dll");
        names.Should().Contain("kept.txt", "binaries is a different directory");
        names.Should().Contain("kept.cs");
    }
}
