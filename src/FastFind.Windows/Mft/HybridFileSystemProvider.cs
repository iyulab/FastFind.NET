using System.Diagnostics;
using FastFind.Indexing;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace FastFind.Windows.Mft;

/// <summary>
/// Hybrid file system provider that automatically selects the fastest available method.
/// </summary>
/// <remarks>
/// <para>
/// Without administrator rights it is the directory walk. With them, <see cref="ProviderMode.Auto"/> is not simply
/// the change-journal pass: that pass reads the whole volume whatever the location, a cost that is close to a constant
/// per volume (7-13 s on the volumes measured, for a folder of 6 entries as for one of 2.2 million), while the walk costs
/// per entry (about 25-100 microseconds). A folder of a few thousand entries takes 0.03 s to walk and 7 s to read from the
/// journal; a folder of two million takes a minute to walk and 9 s from the journal. The crossover differs by volume and
/// nobody can say in advance how many entries a location holds, so an enumeration <b>starts with the walk and, if it has not
/// finished within a time budget, continues with the journal pass</b> for the locations still unread - at most about
/// twice the better choice, where either fixed choice loses up to 200x or 6x. The entries the walk already gave are not
/// given again.
/// </para>
/// <para>
/// The two read the same entries, with one property of the journal worth knowing: it lists a file <em>once</em>, under one of
/// its names, so a hard-linked file's other names (a few thousand under <c>C:\Program Files</c>, linked from <c>WinSxS</c>)
/// are in the walk's answer and not in the journal pass's. A handed-over pass therefore differs from a walk of the same
/// folder by those names, and by whatever changed while it ran.
/// </para>
/// <para>
/// Only where the two give the same answer: when size or timestamps are collected (the journal pass would read each one
/// anyway), a depth limit is set, or links are followed (the journal pass reads neither option), the walk is used on its
/// own. Items of a pass that may hand over carry no size or times, as the journal pass's do, so the index is the same
/// whichever pass produced an entry. A location that is a volume root, or on a volume the journal cannot read, is not
/// timed. <see cref="ProviderMode.Mft"/> and <see cref="ProviderMode.Standard"/> are forced as before, and monitoring
/// still uses the change journal.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HybridFileSystemProvider : IFileSystemProvider, IAsyncDisposable
{
    private readonly ILogger? _logger;
    private readonly IFileSystemProvider _activeProvider;
    private readonly ProviderMode _mode;
    private bool _disposed;

    // Only in Auto mode where the journal pass is available: the walk that starts an enumeration, and the time it gets.
    private readonly IFileSystemProvider? _walkProvider;
    private readonly TimeSpan _walkBudget = DefaultWalkBudget;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly Func<char, bool> _journalCovers = JournalCoversDrive;
    private volatile bool _walkRan;
    private volatile bool _journalRan;

    /// <summary>How long the walk runs before the journal pass takes over (see the type's remarks).</summary>
    internal static readonly TimeSpan DefaultWalkBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the current provider mode
    /// </summary>
    public ProviderMode CurrentMode => _mode;

    /// <summary>
    /// Gets whether MFT mode is active (maximum performance)
    /// </summary>
    public bool IsMftMode => _mode == ProviderMode.Mft;

    /// <summary>
    /// Creates a new hybrid provider with automatic mode selection
    /// </summary>
    public HybridFileSystemProvider(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<HybridFileSystemProvider>();

        // Automatic mode detection
        if (MftFileSystemProvider.IsMftAccessAvailable)
        {
            _logger?.LogInformation("MFT access available - the walk first, the change-journal provider for what it does not finish");
            _activeProvider = new MftFileSystemProvider(loggerFactory?.CreateLogger<MftFileSystemProvider>());
            _walkProvider = new WindowsFileSystemProvider(
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsFileSystemProvider>());
            _mode = ProviderMode.Mft;
        }
        else
        {
            _logger?.LogInformation("MFT access not available - using standard Windows provider");
            _activeProvider = new WindowsFileSystemProvider(
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsFileSystemProvider>());
            _mode = ProviderMode.Standard;
        }
    }

    /// <summary>
    /// Creates a new hybrid provider with explicit mode
    /// </summary>
    public HybridFileSystemProvider(ProviderMode mode, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<HybridFileSystemProvider>();
        _mode = mode;

        switch (mode)
        {
            case ProviderMode.Mft:
                if (!MftFileSystemProvider.IsMftAccessAvailable)
                {
                    throw new InvalidOperationException(
                        "MFT mode requested but not available. Ensure you have administrator privileges and NTFS drives.");
                }
                _activeProvider = new MftFileSystemProvider(loggerFactory?.CreateLogger<MftFileSystemProvider>());
                break;

            case ProviderMode.Standard:
                _activeProvider = new WindowsFileSystemProvider(
                    (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsFileSystemProvider>());
                break;

            case ProviderMode.Auto:
            default:
                // Recursive call with Auto detection logic
                if (MftFileSystemProvider.IsMftAccessAvailable)
                {
                    _activeProvider = new MftFileSystemProvider(loggerFactory?.CreateLogger<MftFileSystemProvider>());
                    _walkProvider = new WindowsFileSystemProvider(
                        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsFileSystemProvider>());
                    _mode = ProviderMode.Mft;
                }
                else
                {
                    _activeProvider = new WindowsFileSystemProvider(
                        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsFileSystemProvider>());
                    _mode = ProviderMode.Standard;
                }
                break;
        }

        _logger?.LogInformation("HybridFileSystemProvider initialized in {Mode} mode", _mode);
    }

    /// <summary>
    /// Automatic mode over the real providers with a chosen walk budget: zero hands every enumeration over after the first
    /// entry, an hour never does. For the tests that compare what the two routes give on a real tree.
    /// </summary>
    internal HybridFileSystemProvider(TimeSpan walkBudget, ILoggerFactory? loggerFactory = null)
        : this(ProviderMode.Auto, loggerFactory)
    {
        if (_walkProvider is null)
            throw new InvalidOperationException("The hand-over needs the change-journal provider: administrator rights and an NTFS volume.");

        _walkBudget = walkBudget;
    }

    /// <summary>
    /// Automatic mode over the two given providers, a time budget, a clock and the set of drives the journal covers,
    /// so the hand-over can be driven without a volume, elevation or a real wait.
    /// </summary>
    internal HybridFileSystemProvider(
        IFileSystemProvider walk,
        IFileSystemProvider journal,
        TimeSpan walkBudget,
        TimeProvider clock,
        Func<char, bool> journalCovers)
    {
        _walkProvider = walk;
        _activeProvider = journal;
        _mode = ProviderMode.Mft;
        _walkBudget = walkBudget;
        _clock = clock;
        _journalCovers = journalCovers;
    }

    /// <inheritdoc/>
    public PlatformType SupportedPlatform => PlatformType.Windows;

    /// <inheritdoc/>
    public bool IsAvailable => _activeProvider.IsAvailable;

    /// <inheritdoc/>
    /// <remarks>
    /// Asked once before a pass, so it answers for the whole of it: a pass that may hand over reports what the journal pass
    /// reports (nothing is collected, and its walked entries are made to match); a pass that is the walk alone reports the walk.
    /// </remarks>
    public bool ProvidesFileMetadata(IndexingOptions options) =>
        _walkProvider is null || MayHandOver(options)
            ? _activeProvider.ProvidesFileMetadata(options)
            : _walkProvider.ProvidesFileMetadata(options);

    /// <inheritdoc/>
    /// <remarks>Of the providers that took part in the last enumeration; the one that did not run holds an older pass's.</remarks>
    public IReadOnlyCollection<string> EnumerationFailures =>
        _walkProvider is null
            ? _activeProvider.EnumerationFailures
            : (_walkRan ? _walkProvider.EnumerationFailures : Array.Empty<string>())
                .Concat(_journalRan ? _activeProvider.EnumerationFailures : Array.Empty<string>())
                .Distinct()
                .ToArray();

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations,
        IndexingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        _logger?.LogDebug("Enumerating files using {Mode} mode", _mode);
        _walkRan = false;
        _journalRan = false;

        if (_walkProvider is null)
        {
            await foreach (var item in _activeProvider.EnumerateFilesAsync(locations, options, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        // The walk alone where the journal pass would not give the same answer.
        if (!MayHandOver(options))
        {
            _walkRan = true;
            await foreach (var item in _walkProvider.EnumerateFilesAsync(locations, options, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        foreach (var group in locations.Where(l => !string.IsNullOrWhiteSpace(l)).GroupBy(DriveOf))
        {
            var volumeLocations = group.ToList();

            // A volume the journal cannot read (not NTFS, a network share): only the walk can.
            if (group.Key == NoDrive || !_journalCovers(group.Key))
            {
                _walkRan = true;
                await foreach (var item in _walkProvider.EnumerateFilesAsync(volumeLocations, options, cancellationToken))
                {
                    yield return NotCollected(item);
                }

                continue;
            }

            // The whole volume: the journal pass reads exactly that, and a walk of it would be the slow way.
            if (volumeLocations.Any(IsVolumeRoot))
            {
                _journalRan = true;
                await foreach (var item in _activeProvider.EnumerateFilesAsync(volumeLocations, options, cancellationToken))
                {
                    yield return item;
                }

                continue;
            }

            // The walk, for as long as the budget lasts: one budget for all of a volume's locations, since the journal
            // pass's cost is the volume's, not a location's.
            var seen = new HashSet<string>(FileEntryTable.PathKeyComparer.Instance);
            var unread = new List<string>(volumeLocations);
            var started = _clock.GetTimestamp();
            var handedOver = false;
            _walkRan = true;

            foreach (var location in volumeLocations)
            {
                // Disposing the enumerator cancels the walk and waits for its producer to end, before the journal pass starts.
                await using var walk = _walkProvider.EnumerateFilesAsync([location], options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                // A walk that has ended is the pass whatever the clock says; only when there is more to read, and the
                // budget is spent, does the journal pass take over (and give the entry just read, among the rest).
                while (await walk.MoveNextAsync())
                {
                    if (_clock.GetElapsedTime(started) >= _walkBudget)
                    {
                        handedOver = true;
                        break;
                    }

                    var item = NotCollected(walk.Current);
                    seen.Add(item.FullPath);
                    yield return item;
                }

                if (handedOver)
                    break;

                unread.Remove(location);
            }

            if (!handedOver)
                continue;

            _logger?.LogInformation(
                "The walk read {Count} entries in {Elapsed:F1} s and has not finished: continuing with the change-journal pass for {Locations}",
                seen.Count, _clock.GetElapsedTime(started).TotalSeconds, string.Join(", ", unread));

            _journalRan = true;
            await foreach (var item in _activeProvider.EnumerateFilesAsync(unread, options, cancellationToken))
            {
                if (!seen.Contains(item.FullPath))
                    yield return item;
            }
        }
    }

    private const char NoDrive = '\0';

    /// <summary>Options under which the journal pass gives the walk's answer, so an enumeration may switch between them.</summary>
    internal static bool MayHandOver(IndexingOptions options) =>
        !options.CollectFileMetadata && options.MaxDepth is null && !options.FollowSymlinks;

    private static char DriveOf(string location)
    {
        var full = Path.GetFullPath(location);
        return full.Length >= 2 && full[1] == ':' ? char.ToUpperInvariant(full[0]) : NoDrive;
    }

    private static bool IsVolumeRoot(string location)
    {
        var full = Path.GetFullPath(location);
        return MftFileSystemProvider.DirectoryDepth(full, Path.GetPathRoot(full) ?? string.Empty) == 0;
    }

    private static bool JournalCoversDrive(char drive) => MftReader.GetNtfsDrives().Contains(drive);

    /// <summary>
    /// The item as the journal pass would have produced it: no size, no times ("not collected"). An enumeration that may
    /// switch passes gives every entry this form, or a query by size would be right for the walked half of an index and
    /// wrong for the rest.
    /// </summary>
    private static FileItem NotCollected(FileItem item) =>
        item.Size == 0 && item.CreatedTime == default && item.ModifiedTime == default && item.AccessedTime == default
            ? item
            : item with { Size = 0, CreatedTime = default, ModifiedTime = default, AccessedTime = default };

    /// <inheritdoc/>
    public Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _activeProvider.GetFileInfoAsync(filePath, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<FastFind.Interfaces.DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _activeProvider.GetAvailableLocationsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _activeProvider.MonitorChangesAsync(locations, options, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _activeProvider.ExistsAsync(path, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _activeProvider.GetFileSystemTypeAsync(path, cancellationToken);
    }

    /// <inheritdoc/>
    public ProviderPerformance GetPerformanceInfo()
    {
        ThrowIfDisposed();
        return _activeProvider.GetPerformanceInfo();
    }

    /// <summary>
    /// Gets detailed information about the provider status
    /// </summary>
    public ProviderStatus GetStatus()
    {
        return new ProviderStatus
        {
            Mode = _mode,
            IsAvailable = IsAvailable,
            IsMftCapable = MftFileSystemProvider.IsMftAccessAvailable,
            Performance = GetPerformanceInfo()
        };
    }

    /// <summary>
    /// Checks if MFT access is possible and returns diagnostic information
    /// </summary>
    public static MftDiagnostics CheckMftAvailability()
    {
        var isAdmin = MftReader.IsAvailable();
        var ntfsDrives = MftReader.GetNtfsDrives();

        return new MftDiagnostics
        {
            IsAdministrator = isAdmin,
            NtfsDrives = ntfsDrives,
            CanUseMft = isAdmin && ntfsDrives.Length > 0,
            Reason = GetDiagnosticReason(isAdmin, ntfsDrives)
        };
    }

    private static string GetDiagnosticReason(bool isAdmin, char[] ntfsDrives)
    {
        if (!isAdmin)
            return "Administrator privileges required for MFT access";

        if (ntfsDrives.Length == 0)
            return "No NTFS drives found on the system";

        return $"MFT access available for drives: {string.Join(", ", ntfsDrives)}";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HybridFileSystemProvider));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _walkProvider?.Dispose();
            _activeProvider.Dispose();
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_walkProvider is IAsyncDisposable asyncWalk)
                await asyncWalk.DisposeAsync();
            else
                _walkProvider?.Dispose();

            if (_activeProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _activeProvider.Dispose();
            }
            _disposed = true;
        }
    }
}

/// <summary>
/// Provider operation mode
/// </summary>
public enum ProviderMode
{
    /// <summary>
    /// Automatic selection based on available capabilities
    /// </summary>
    Auto,

    /// <summary>
    /// Force MFT mode (requires admin + NTFS)
    /// </summary>
    Mft,

    /// <summary>
    /// Use standard Windows file enumeration
    /// </summary>
    Standard
}

/// <summary>
/// Provider status information
/// </summary>
public record ProviderStatus
{
    /// <summary>
    /// Current operation mode
    /// </summary>
    public required ProviderMode Mode { get; init; }

    /// <summary>
    /// Whether the provider is available
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// Whether MFT access is possible
    /// </summary>
    public required bool IsMftCapable { get; init; }

    /// <summary>
    /// Performance characteristics
    /// </summary>
    public required ProviderPerformance Performance { get; init; }
}

/// <summary>
/// MFT availability diagnostics
/// </summary>
public record MftDiagnostics
{
    /// <summary>
    /// Whether running as administrator
    /// </summary>
    public required bool IsAdministrator { get; init; }

    /// <summary>
    /// Available NTFS drives
    /// </summary>
    public required char[] NtfsDrives { get; init; }

    /// <summary>
    /// Whether MFT can be used
    /// </summary>
    public required bool CanUseMft { get; init; }

    /// <summary>
    /// Diagnostic reason or status message
    /// </summary>
    public required string Reason { get; init; }
}
