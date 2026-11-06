using System.Diagnostics;
using System.Runtime.CompilerServices;
using FastFind.Indexing;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DriveInfo = FastFind.Interfaces.DriveInfo;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// A query that arrives while the index is still empty is answered from a live scan of the disk, and that
/// scan answers under the rules the index was built with.
/// </summary>
/// <remarks>
/// While an index holds fewer than ten items — the whole of the first batch, which on the change-journal
/// path is the whole enumeration of the volume — a search is answered by walking the directory. That walk
/// read the query's exclusions and nothing of the engine's <see cref="IndexingOptions"/>, so
/// <c>ExcludedPaths</c> appeared to do nothing for as long as indexing took. These tests hold the
/// enumeration open with a provider that yields nothing until released, so the window is not a matter of
/// timing.
/// </remarks>
[Trait("Category", "Functional")]
public class SearchDuringIndexingTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = new();

    public SearchDuringIndexingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-fallback-" + Guid.NewGuid().ToString("N")[..8]);

        Write("report.txt");
        Write(@"docs\notes.txt");
        Write(@"bin\mine.txt");
        Write(@"app\src\code.txt");
        Write(@"node_modules\pkg\dep.txt");
        Write(@"app\node_modules\pkg\nested.txt");
        Write(@"repo\.git\objects\blob.txt");
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

    /// <summary>A provider whose enumeration yields nothing until <see cref="Release"/> is called.</summary>
    private sealed class HeldProvider : IFileSystemProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public PlatformType SupportedPlatform => PlatformType.Windows;
        public bool IsAvailable => true;

        public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
            IEnumerable<string> locations, IndexingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<FileItem?>(null);

        public Task<IEnumerable<DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<DriveInfo>>(Array.Empty<DriveInfo>());

        public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
            IEnumerable<string> locations, MonitoringOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult("NTFS");
        public ProviderPerformance GetPerformanceInfo() => new();
        public void Dispose() { }
    }

    private static async Task<List<string>> SearchAsync(WindowsSearchEngineImpl engine, string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await engine.SearchAsync(
            new SearchQuery { BasePath = root, SearchText = "", ExtensionFilter = ".txt", MaxResults = 1000 },
            timeout.Token);

        var paths = new List<string>();
        await foreach (var file in result.Files)
            paths.Add(Path.GetRelativePath(root, file.FullPath));
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    private async Task<List<string>> SearchWhileIndexingAsync(Action<IndexingOptions> configure)
    {
        var provider = new HeldProvider();
        using var engine = new WindowsSearchEngineImpl(
            provider,
            new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance),
            new WindowsSearchEngineOptions(),
            NullLogger<WindowsSearchEngineImpl>.Instance);

        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        };
        configure(options);

        await engine.StartIndexingAsync(options);
        try
        {
            engine.IsIndexing.Should().BeTrue("the held provider keeps the pass open, so the index stays empty");
            engine.Index!.Count.Should().Be(0);

            return await SearchAsync(engine, _root);
        }
        finally
        {
            provider.Release();
            await engine.StopIndexingAsync();
        }
    }

    [Fact]
    public async Task A_Search_Before_Indexing_Has_Delivered_Anything_Should_Not_Return_What_Indexing_Excludes()
    {
        var found = await SearchWhileIndexingAsync(o =>
            o.ExcludedPaths = new List<string> { "node_modules", ".git", "bin" });

        found.Should().BeEquivalentTo(new[] { "report.txt", @"docs\notes.txt", @"app\src\code.txt" },
            "an excluded path is not in the index, so a live scan standing in for the index must not return it either");
    }

    [Fact]
    public async Task A_Fully_Qualified_Exclusion_Should_Be_Honoured_By_The_Live_Scan_Too()
    {
        var found = await SearchWhileIndexingAsync(o =>
            o.ExcludedPaths = new List<string> { Path.Combine(_root, "node_modules"), Path.Combine(_root, "bin") });

        found.Should().NotContain(p => p.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase)
                                       || p.StartsWith("bin", StringComparison.OrdinalIgnoreCase));
        found.Should().Contain(@"app\node_modules\pkg\nested.txt",
            "a fully qualified entry names that one directory, not every directory of the same name");
    }

    [Fact]
    public async Task The_Live_Scan_Should_Apply_The_Other_Indexing_Limits_Too()
    {
        Write("big.txt", new string('x', 5000));
        Write("debug.log");
        var hidden = Path.Combine(_root, "hidden.txt");
        Write("hidden.txt");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var found = await SearchWhileIndexingAsync(o =>
        {
            o.MaxFileSize = 1000;
            o.ExcludedExtensions = new List<string> { ".log" };
            o.IncludeHidden = false;
        });

        found.Should().NotContain("big.txt", "over MaxFileSize");
        found.Should().NotContain("debug.log", "an excluded extension (and not the extension asked for either)");
        found.Should().NotContain("hidden.txt", "IncludeHidden is off");
        found.Should().Contain("report.txt");
    }

    [Fact]
    public async Task Without_Any_Indexing_Options_The_Live_Scan_Should_Behave_As_Before()
    {
        var found = await SearchWhileIndexingAsync(_ => { });

        found.Should().Contain(@"node_modules\pkg\dep.txt", "nothing was excluded");
        found.Should().Contain("report.txt");
    }

    [Fact]
    public async Task The_Live_Scan_Should_Not_Follow_A_Junction_Back_Into_The_Tree()
    {
        var link = Path.Combine(_root, "app", "loop");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_root}\"")
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

        var stopwatch = Stopwatch.StartNew();
        var found = await SearchWhileIndexingAsync(_ => { });

        found.Should().Contain("report.txt");
        found.Should().NotContain(p => p.Contains(@"app\loop\", StringComparison.OrdinalIgnoreCase),
            "a link is not entered unless the indexing options ask to follow links");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25));
    }
}
