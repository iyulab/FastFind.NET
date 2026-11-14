extern alias UnixPackage;

using System.Diagnostics;
using FastFind.Interfaces;
using FastFind.Models;
using UnixLinux = UnixPackage::FastFind.Unix.Linux;
using FastFind.Windows.Implementation;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// One tree and one set of <see cref="IndexingOptions"/>, run through the Windows directory-walk provider
/// and the Linux provider: for every rule whose meaning is shared, both must leave out the same things.
/// </summary>
/// <remarks>
/// The providers are written separately and the Windows one used to carry its own copy of the rules, so a
/// rule fixed in one place stayed broken in another — an exclusion that did nothing, a depth that counted
/// differently. This is the check that notices. It does not compare the rules whose meaning differs by
/// design: <c>IncludeHidden</c> is an attribute on Windows and a leading dot on Linux, and
/// <c>IncludeSystem</c> has no Linux meaning. The Linux provider runs on this host as it does in the
/// project's own Unix tests.
/// </remarks>
[Trait("Category", "Functional")]
public class ProviderConformanceTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = new();

    public ProviderConformanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-conform-" + Guid.NewGuid().ToString("N")[..8]);

        Write("report.txt");
        Write("data.log");
        Write("UPPER.TXT");
        Write("big.bin", new string('x', 5000));
        Write(@"src\code.cs");
        Write(@"src\deep\deeper\deepest\x.txt");
        Write(@"bin\app.dll");
        Write(@"binaries\keep.txt");
        Write(@"attempts\notes.txt");
        Write(@"node_modules\pkg\dep.txt");
        Write(@"app\node_modules\pkg\nested.txt");
    }

    public void Dispose()
    {
        foreach (var link in _links)
        {
            try { Directory.Delete(link); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private IndexingOptions Options(Action<IndexingOptions>? configure = null)
    {
        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            IncludeSystem = true,
            MaxFileSize = null,
        };
        configure?.Invoke(options);
        return options;
    }

    private async Task<List<string>> EntriesAsync(IFileSystemProvider provider, IndexingOptions options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var entries = new List<string>();
        await foreach (var item in provider.EnumerateFilesAsync([_root], options, timeout.Token))
            entries.Add(Path.GetRelativePath(_root, item.FullPath));
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    private async Task AssertAgreeAsync(IndexingOptions options, string because)
    {
        using var windows = new WindowsFileSystemProvider(NullLogger<WindowsFileSystemProvider>.Instance);
        using var linux = new UnixLinux.LinuxFileSystemProvider();

        var fromWindows = await EntriesAsync(windows, options);
        var fromLinux = await EntriesAsync(linux, options);

        fromLinux.Should().Equal(fromWindows,
            $"both providers should leave out the same things: {because}. Windows: [{string.Join(", ", fromWindows)}]");
    }

    [Fact]
    public Task With_No_Limits_Both_Report_The_Same_Entries()
        => AssertAgreeAsync(Options(), "nothing is excluded");

    [Fact]
    public Task A_Bare_Directory_Name_Is_Excluded_At_Any_Depth_By_Both()
        => AssertAgreeAsync(Options(o => o.ExcludedPaths = new List<string> { "node_modules" }), "a bare name is a run of whole segments");

    [Fact]
    public Task A_Bare_Name_Does_Not_Match_Inside_A_Segment_On_Either()
        => AssertAgreeAsync(Options(o => o.ExcludedPaths = new List<string> { "attempt" }), "'attempt' names no segment of 'attempts'");

    [Fact]
    public Task A_Fully_Qualified_Exclusion_Takes_Only_What_Is_At_Or_Under_It_On_Both()
        => AssertAgreeAsync(Options(o => o.ExcludedPaths = new List<string> { Path.Combine(_root, "bin") }), "'bin' is not 'binaries'");

    [Fact]
    public Task An_Exclusion_Spelled_With_Forward_Slashes_Is_Honoured_By_Both()
        => AssertAgreeAsync(Options(o => o.ExcludedPaths = new List<string> { Path.Combine(_root, "bin").Replace('\\', '/') }), "separators fold on Windows");

    [Fact]
    public Task Excluded_Extensions_Are_Matched_Without_Regard_To_Case_By_Both()
        => AssertAgreeAsync(Options(o => o.ExcludedExtensions = new List<string> { ".log", ".txt" }), "'UPPER.TXT' carries an excluded extension");

    [Fact]
    public Task The_Size_Limit_Leaves_Out_The_Same_Files_On_Both()
        => AssertAgreeAsync(Options(o => o.MaxFileSize = 1000), "big.bin is 5000 bytes");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task MaxDepth_Reaches_The_Same_Level_On_Both(int maxDepth)
        => AssertAgreeAsync(Options(o => o.MaxDepth = maxDepth), $"MaxDepth {maxDepth}");

    [Fact]
    public async Task A_Junction_Is_Not_Entered_By_Either_Unless_Links_Are_Followed()
    {
        var link = Path.Combine(_root, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(_root, "src")}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using (var process = Process.Start(psi)!)
        {
            process.WaitForExit();
            process.ExitCode.Should().Be(0, "the test needs a junction");
        }
        _links.Add(link);

        await AssertAgreeAsync(Options(), "FollowSymlinks is false by default");
    }
}
