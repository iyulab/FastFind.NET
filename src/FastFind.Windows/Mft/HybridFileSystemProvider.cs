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
/// Uses MFT for maximum performance when admin+NTFS is available, falls back to standard enumeration otherwise.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HybridFileSystemProvider : IFileSystemProvider, IAsyncDisposable
{
    private readonly ILogger? _logger;
    private readonly IFileSystemProvider _activeProvider;
    private readonly ProviderMode _mode;
    private bool _disposed;

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
            _logger?.LogInformation("MFT access available - using high-performance MFT provider");
            _activeProvider = new MftFileSystemProvider(loggerFactory?.CreateLogger<MftFileSystemProvider>());
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

    /// <inheritdoc/>
    public PlatformType SupportedPlatform => PlatformType.Windows;

    /// <inheritdoc/>
    public bool IsAvailable => _activeProvider.IsAvailable;

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations,
        IndexingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        _logger?.LogDebug("Enumerating files using {Mode} mode", _mode);

        await foreach (var item in _activeProvider.EnumerateFilesAsync(locations, options, cancellationToken))
        {
            yield return item;
        }
    }

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
            _activeProvider.Dispose();
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
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
