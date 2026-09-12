using FastFind.Interfaces;
using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// Change monitoring leaves out what <see cref="MonitoringOptions.ExcludedPaths"/> names.
/// </summary>
/// <remarks>
/// The Unix providers never read that list: every change under an excluded directory was reported,
/// so an index that deliberately skipped a tree while indexing acquired it again as soon as anything
/// in it moved.
/// </remarks>
[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class MonitoringExclusionTests
{
    [Fact]
    public async Task MonitorChangesAsync_ShouldNotReportChangesUnderAnExcludedPath()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), $"fastfind-monitor-excl-{Guid.NewGuid():N}");
        var excluded = Path.Combine(testDir, "excluded");
        var kept = Path.Combine(testDir, "kept");
        Directory.CreateDirectory(excluded);
        Directory.CreateDirectory(kept);

        try
        {
            using var provider = new FastFind.Unix.Linux.LinuxFileSystemProvider();
            var options = new MonitoringOptions
            {
                IncludeSubdirectories = true,
                ExcludedPaths = new List<string> { "excluded" },
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var seen = new List<FileChangeEventArgs>();

            var monitorTask = Task.Run(async () =>
            {
                await foreach (var change in provider.MonitorChangesAsync(new[] { testDir }, options, cts.Token))
                {
                    seen.Add(change);
                    if (change.NewPath.Contains("keep-me", StringComparison.Ordinal)) break;
                }
            }, cts.Token);

            await Task.Delay(500);

            await File.WriteAllTextAsync(Path.Combine(excluded, "ignore-me.txt"), "x");
            await Task.Delay(200);
            await File.WriteAllTextAsync(Path.Combine(kept, "keep-me.txt"), "x");

            try { await monitorTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }

            seen.Should().Contain(c => c.NewPath.Contains("keep-me", StringComparison.Ordinal),
                "a change outside the excluded directory is still reported");
            seen.Should().NotContain(c => c.NewPath.Contains("ignore-me", StringComparison.Ordinal));
            seen.Should().NotContain(c => c.NewPath.Contains("/excluded/", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}
