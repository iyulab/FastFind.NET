using System.Runtime.Versioning;
using FastFind.Models;
using FastFind.Windows.Mft;
using FastFind.Windows.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// A pass over the change journal ends with the consumer that was reading it.
/// </summary>
/// <remarks>
/// Each drive's producer used to be started with the caller's token alone. A consumer that stopped
/// reading — a file cap, a stop request, disposal — left every producer parked on the full channel,
/// holding its volume handle and read buffers, with nothing to cancel it.
/// </remarks>
[Collection("HandleCount")]
[Trait("Category", "Integration")]
[Trait("Suite", "MFT")]
[SupportedOSPlatform("windows")]
public class MftPassLifetimeTests
{
    [ElevatedFact]
    public async Task A_Pass_Stopped_Early_Should_Have_Ended_Its_Producers_When_It_Returns()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        await using var provider = new MftFileSystemProvider(NullLogger<MftFileSystemProvider>.Instance);

        var options = new IndexingOptions
        {
            SpecificDirectories = { driveRoot },
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            IncludeSystem = true,
        };

        // The drive root is the location, so items arrive as soon as the journal is read.
        await using (var items = provider.EnumerateFilesAsync([driveRoot], options).GetAsyncEnumerator())
        {
            for (var i = 0; i < 10; i++)
                (await items.MoveNextAsync()).Should().BeTrue();

            provider.ActiveProducers.Should().BeGreaterThan(0, "the pass is still running while it is being read");
        }

        provider.ActiveProducers.Should().Be(0,
            "disposing the pass has to end its producers, not leave them parked on a full channel");
    }
}
