using System.Runtime.CompilerServices;
using FastFind.Interfaces;
using FastFind.Models;
using DriveInfo = FastFind.Interfaces.DriveInfo;

namespace FastFind.Windows.Tests.Helpers;

/// <summary>
/// A file-system provider that does exactly what a test tells it to, so that how an indexing pass ends is
/// a fact of the script and not of the timing.
/// </summary>
/// <remarks>
/// It waits at <see cref="Gate"/> before yielding anything (<see cref="Release"/> opens it), yields
/// <see cref="Items"/>, can throw after them, and reports <see cref="Failures"/> the way a provider that
/// could not read a location does.
/// </remarks>
internal sealed class ScriptedFileSystemProvider : IFileSystemProvider
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedFileSystemProvider(bool startOpen = true)
    {
        if (startOpen) _gate.TrySetResult();
    }

    /// <summary>What the enumeration yields, in order.</summary>
    public List<FileItem> Items { get; } = new();

    /// <summary>What <see cref="EnumerationFailures"/> reports.</summary>
    public List<string> Failures { get; } = new();

    /// <summary>When set, the enumeration throws this after yielding every item.</summary>
    public Exception? ThrowAfterItems { get; set; }

    /// <summary>Lets a held enumeration begin.</summary>
    public void Release() => _gate.TrySetResult();

    public IReadOnlyCollection<string> EnumerationFailures => Failures;

    public PlatformType SupportedPlatform => PlatformType.Windows;
    public bool IsAvailable => true;

    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations, IndexingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var item in Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        if (ThrowAfterItems is not null)
            throw ThrowAfterItems;
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

    /// <summary>A file item at a path that need not exist.</summary>
    public static FileItem File(string fullPath) => new()
    {
        FullPath = fullPath,
        Name = Path.GetFileName(fullPath),
        DirectoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty,
        Extension = Path.GetExtension(fullPath),
        Size = 1,
        CreatedTime = DateTime.UtcNow,
        ModifiedTime = DateTime.UtcNow,
        AccessedTime = DateTime.UtcNow,
        Attributes = FileAttributes.Archive,
        DriveLetter = fullPath[0],
    };
}
