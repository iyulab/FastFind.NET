using FastFind.Interfaces;
using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class LinuxFileMonitorTests
{
    [Fact]
    public async Task MonitorChangesAsync_ShouldDetectCreatedFile()
    {
        if (!OperatingSystem.IsLinux()) return;

        var testDir = Path.Combine(Path.GetTempPath(), $"fastfind-monitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);

        try
        {
            using var provider = new FastFind.Unix.Linux.LinuxFileSystemProvider();
            var options = new MonitoringOptions { IncludeSubdirectories = false };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            FileChangeEventArgs? detected = null;

            var monitorTask = Task.Run(async () =>
            {
                await foreach (var change in provider.MonitorChangesAsync(new[] { testDir }, options, cts.Token))
                {
                    detected = change;
                    break;
                }
            }, cts.Token);

            await Task.Delay(500);

            var newFile = Path.Combine(testDir, "new-file.txt");
            await File.WriteAllTextAsync(newFile, "test content");

            try { await monitorTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }

            detected.Should().NotBeNull();
            detected!.ChangeType.Should().Be(FileChangeType.Created);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}
