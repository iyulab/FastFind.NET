using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Mft;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace FastFind.Windows;

/// <summary>
/// Windows search engine registration helper
/// </summary>
public static class WindowsRegistration
{
    private static volatile bool _isRegistered = false;
    private static readonly object _lock = new object();

    /// <summary>
    /// Module initializer that automatically registers the Windows search engine factory
    /// when the FastFind.Windows assembly is loaded. This ensures that users don't need
    /// to manually call EnsureRegistered() before using FastFinder.CreateWindowsSearchEngine().
    /// </summary>
    /// <remarks>
    /// CA2255 is suppressed because this library intentionally uses ModuleInitializer
    /// to provide transparent factory registration when the assembly is loaded.
    /// </remarks>
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        EnsureRegistered();
    }

    /// <summary>
    /// Ensures the Windows search engine factory is registered
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_isRegistered) return;

        lock (_lock)
        {
            if (_isRegistered) return;

            if (OperatingSystem.IsWindows())
            {
                RegisterWindowsFactory();
                _isRegistered = true;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsFactory()
    {
        FastFinder.RegisterSearchEngineFactory(PlatformType.Windows, options => WindowsSearchEngine.CreateWindowsSearchEngine(options));
    }
}

/// <summary>
/// Windows-specific implementation registration for FastFind with .NET 10 optimizations
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsSearchEngine
{
    // .NET 10: Thread-local storage for better performance
    private static readonly ThreadLocal<WindowsCapabilityCache> _capabilityCache =
        new(() => new WindowsCapabilityCache());

    // Factory registration is handled by WindowsRegistration.EnsureRegistered() via ModuleInitializer.
    // No static constructor needed — avoids duplicate registration.

    /// <summary>
    /// Creates a Windows-optimized search engine with .NET 10 enhancements
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory</param>
    /// <param name="providerMode">File system provider mode (default: Auto for MFT when available)</param>
    /// <returns>Windows search engine instance</returns>
    public static ISearchEngine CreateWindowsSearchEngine(
        ILoggerFactory? loggerFactory = null,
        ProviderMode providerMode = ProviderMode.Auto)
    {
        return CreateWindowsSearchEngine(SearchEngineOptions.ForLogger(loggerFactory), providerMode);
    }

    /// <summary>
    /// Creates a Windows-optimized search engine from a full set of composition options.
    /// </summary>
    /// <param name="options">Composition options, including an optional index or persistence store.</param>
    /// <param name="providerMode">File system provider mode (default: Auto for MFT when available)</param>
    public static ISearchEngine CreateWindowsSearchEngine(
        SearchEngineOptions options,
        ProviderMode providerMode = ProviderMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows search engine can only be used on Windows platforms");
        }

        var loggerFactory = options.LoggerFactory;
        var services = new ServiceCollection();

        // .NET 10: Enhanced logging configuration
        if (loggerFactory != null)
        {
            services.AddSingleton(loggerFactory);
            services.AddLogging();
        }
        else
        {
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        }

        // Register enhanced Windows-specific implementations
        services.AddSingleton<WindowsSearchEngineOptions>(provider => CreateOptimizedOptions());
        // Use HybridFileSystemProvider with specified mode
        services.AddSingleton<IFileSystemProvider>(provider =>
            new HybridFileSystemProvider(providerMode, loggerFactory));
        RegisterSearchIndex(services, options);
        services.AddSingleton<ISearchEngine, WindowsSearchEngineImpl>();

        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<ISearchEngine>();
    }

    /// <summary>
    /// Registers the index the options ask for: a supplied one, one over a store, or the in-memory
    /// default — which is given the store to write through to when that is the mode requested.
    /// </summary>
    private static void RegisterSearchIndex(ServiceCollection services, SearchEngineOptions options)
    {
        var supplied = options.ResolveIndex();
        if (supplied is not null)
        {
            services.AddSingleton(supplied);
            return;
        }

        var persistence = options.Persistence;
        services.AddSingleton<ISearchIndex>(provider => new WindowsSearchIndex(
            provider.GetRequiredService<ILogger<WindowsSearchIndex>>(), persistence));
    }

    /// <summary>
    /// Creates a Windows search engine for testing with Standard mode (no MFT)
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory</param>
    /// <returns>Windows search engine using standard file system enumeration</returns>
    public static ISearchEngine CreateWindowsSearchEngineForTesting(ILoggerFactory? loggerFactory = null)
    {
        return CreateWindowsSearchEngine(loggerFactory, ProviderMode.Standard);
    }

    /// <summary>
    /// Creates a Windows search engine with advanced configuration
    /// </summary>
    /// <param name="configure">Configuration action</param>
    /// <param name="loggerFactory">Optional logger factory</param>
    /// <param name="providerMode">File system provider mode (default: Auto)</param>
    /// <returns>Configured Windows search engine</returns>
    public static ISearchEngine CreateWindowsSearchEngine(
        Action<WindowsSearchEngineOptions> configure,
        ILoggerFactory? loggerFactory = null,
        ProviderMode providerMode = ProviderMode.Auto)
    {
        var options = CreateOptimizedOptions();
        configure(options);

        var services = new ServiceCollection();

        // Register logging
        if (loggerFactory != null)
        {
            services.AddSingleton(loggerFactory);
            services.AddLogging();
        }
        else
        {
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        }

        // Register optimized options
        services.AddSingleton(options);

        // Register Windows-specific implementations
        // Use HybridFileSystemProvider with specified mode
        services.AddSingleton<IFileSystemProvider>(provider =>
            new HybridFileSystemProvider(providerMode, loggerFactory));
        RegisterSearchIndex(services, SearchEngineOptions.ForLogger(loggerFactory));
        services.AddSingleton<ISearchEngine, WindowsSearchEngineImpl>();

        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<ISearchEngine>();
    }

    /// <summary>
    /// .NET 10: Creates optimized default options based on system capabilities
    /// </summary>
    private static WindowsSearchEngineOptions CreateOptimizedOptions()
    {
        var capabilities = GetNtfsCapabilities();
        var processorCount = Environment.ProcessorCount;
        // Memory the GC may use, which is the machine's (or the container's) memory. It read
        // GC.GetTotalMemory — the managed heap in use, near zero at startup — so every branch below
        // took its smallest-system value on every machine.
        var totalMemoryGB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);

        return new WindowsSearchEngineOptions
        {
            // MFT Access optimization
            UseMftAccess = capabilities.CanAccessMft,
            EnableNtfsOptimizations = capabilities.HasNtfsDrives,

            // Concurrency optimization based on system
            MaxConcurrentOperations = Math.Max(2, processorCount * (totalMemoryGB > 8 ? 3 : 2)),

            // Memory optimization
            UseMemoryMappedFiles = totalMemoryGB > 4, // Enable for systems with >4GB RAM
            MetadataCacheSize = totalMemoryGB switch
            {
                > 16 => 50000,  // High-end systems
                > 8 => 25000,   // Mid-range systems  
                > 4 => 15000,   // Entry systems
                _ => 10000      // Minimal systems
            },

            // Buffer optimization
            FileEnumerationBufferSize = processorCount >= 8 ? 128 * 1024 : 64 * 1024,
            FileWatcherBufferSize = processorCount >= 8 ? 16384 : 8192,

            // Advanced features
            EnableRealtimeMonitoring = true,
            EnableIndexCompression = true,
            UseVolumeSnapshotService = capabilities.SupportsAdvancedFeatures,

            // .NET 10 specific optimizations
            UseAdvancedStringOptimizations = true,
            EnableSIMDAcceleration = true,
            UseSearchValues = true,
            OptimizeForLargeDatasets = totalMemoryGB > 8
        };
    }

    /// <summary>
    /// Enhanced NTFS capabilities detection with caching
    /// </summary>
    /// <returns>NTFS capability information</returns>
    public static NtfsCapabilities GetNtfsCapabilities()
    {
        var cache = _capabilityCache.Value!;

        // Check cache first (5-minute expiry)
        if (cache.IsValid)
        {
            return cache.Capabilities;
        }

        var capabilities = new NtfsCapabilities();

        try
        {
            // .NET 10: Parallel capability detection
            var tasks = new[]
            {
                Task.Run(() => capabilities.CanAccessMft = WindowsFileSystemProvider.CanAccessMasterFileTable()),
                Task.Run(() => DetectNtfsDrives(capabilities)),
                Task.Run(() => DetectWindowsFeatures(capabilities)),
                Task.Run(() => DetectAdvancedCapabilities(capabilities))
            };

            Task.WaitAll(tasks, TimeSpan.FromSeconds(5)); // 5-second timeout

            capabilities.IsOptimal = capabilities.CanAccessMft &&
                                   capabilities.HasNtfsDrives &&
                                   capabilities.SupportsAdvancedFeatures;

            // Update cache
            cache.Update(capabilities);
        }
        catch (Exception ex)
        {
            capabilities.ErrorMessage = ex.Message;
            capabilities.IsOptimal = false;
        }

        return capabilities;
    }

    /// <summary>
    /// Detects NTFS drives with enhanced information
    /// </summary>
    private static void DetectNtfsDrives(NtfsCapabilities capabilities)
    {
        try
        {
            var ntfsDrives = System.IO.DriveInfo.GetDrives()
                .AsParallel()
                .Where(d => d.IsReady &&
                           d.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                .Select(d => new NtfsDriveInfo
                {
                    Letter = d.Name[0],
                    TotalSize = d.TotalSize,
                    AvailableSpace = d.AvailableFreeSpace,
                    SupportsCompression = CheckCompressionSupport(d),
                    SupportsEncryption = CheckEncryptionSupport(d)
                })
                .ToArray();

            capabilities.NtfsDrives = ntfsDrives;
            capabilities.AvailableNtfsDrives = ntfsDrives.Select(d => d.Letter).ToArray();
            capabilities.HasNtfsDrives = ntfsDrives.Length > 0;
        }
        catch (Exception ex)
        {
            capabilities.ErrorMessage = $"Drive detection error: {ex.Message}";
        }
    }

    /// <summary>
    /// Detects Windows version and features
    /// </summary>
    private static void DetectWindowsFeatures(NtfsCapabilities capabilities)
    {
        try
        {
            var version = Environment.OSVersion.Version;
            capabilities.WindowsVersion = version;
            capabilities.SupportsAdvancedFeatures = version.Major >= 10; // Windows 10+
            capabilities.SupportsWslIntegration = version.Build >= 18362; // Windows 10 1903+
            capabilities.SupportsContainerFeatures = version.Build >= 17763; // Windows 10 1809+
        }
        catch (Exception ex)
        {
            capabilities.ErrorMessage += $" Windows features: {ex.Message}";
        }
    }

    /// <summary>
    /// Detects advanced system capabilities
    /// </summary>
    private static void DetectAdvancedCapabilities(NtfsCapabilities capabilities)
    {
        try
        {
            // Check for administrative privileges
            capabilities.HasAdministrativePrivileges = CheckAdministrativePrivileges();

            // Check for Volume Shadow Copy Service
            capabilities.SupportsVolumeSnapshotService = CheckVssSupport();

            // Check for SIMD support
            capabilities.SupportsSIMD = System.Numerics.Vector.IsHardwareAccelerated;
            capabilities.SupportsAVX2 = CheckAVX2Support();

            // Check memory configuration
            capabilities.SystemMemoryGB = GC.GetTotalMemory(false) / (1024.0 * 1024.0 * 1024.0);
            capabilities.ProcessorCount = Environment.ProcessorCount;
        }
        catch (Exception ex)
        {
            capabilities.ErrorMessage += $" Advanced capabilities: {ex.Message}";
        }
    }

    // Helper methods for capability detection
    private static bool IsNtfsDrive(System.IO.DriveInfo drive)
    {
        return drive.IsReady &&
               drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    // NTFS natively supports both compression and EFS encryption
    private static bool CheckCompressionSupport(System.IO.DriveInfo drive) => IsNtfsDrive(drive);
    private static bool CheckEncryptionSupport(System.IO.DriveInfo drive) => IsNtfsDrive(drive);

    private static bool CheckAdministrativePrivileges()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckVssSupport()
    {
        // VSS is available on Windows Server and Windows 10+ with admin privileges
        var version = Environment.OSVersion.Version;
        return version.Major >= 10 && CheckAdministrativePrivileges();
    }

    private static bool CheckAVX2Support()
    {
        return Avx2.IsSupported;
    }
}

/// <summary>
/// Enhanced configuration options for Windows search engine with .NET 10 optimizations
/// </summary>
public class WindowsSearchEngineOptions
{
    /// <summary>
    /// Whether to use direct MFT access when available
    /// </summary>
    public bool UseMftAccess { get; set; } = true;

    /// <summary>
    /// Whether to enable NTFS-specific optimizations
    /// </summary>
    public bool EnableNtfsOptimizations { get; set; } = true;

    /// <summary>
    /// Whether to use Windows Search integration (if available)
    /// </summary>
    public bool UseWindowsSearchIntegration { get; set; } = false;

    /// <summary>
    /// Buffer size for file enumeration
    /// </summary>
    public int FileEnumerationBufferSize { get; set; } = 64 * 1024; // 64KB

    /// <summary>
    /// Whether to use memory-mapped files for large indexes
    /// </summary>
    public bool UseMemoryMappedFiles { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent file operations
    /// </summary>
    public int MaxConcurrentOperations { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>
    /// Whether to enable real-time file system monitoring
    /// </summary>
    public bool EnableRealtimeMonitoring { get; set; } = true;

    /// <summary>
    /// File system watcher buffer size
    /// </summary>
    public int FileWatcherBufferSize { get; set; } = 8192;

    /// <summary>
    /// Whether to use Windows volume snapshot service integration
    /// </summary>
    public bool UseVolumeSnapshotService { get; set; } = false;

    /// <summary>
    /// Whether to enable compression for index storage
    /// </summary>
    public bool EnableIndexCompression { get; set; } = true;

    /// <summary>
    /// Cache size for file metadata (number of entries)
    /// </summary>
    public int MetadataCacheSize { get; set; } = 10000;

    /// <summary>
    /// Longest an indexing run may take before it is cut short, or <c>null</c> for no limit
    /// (the default).
    /// </summary>
    /// <remarks>
    /// A run that hits the limit stops where it is and raises
    /// <see cref="IndexingPhase.Failed"/> saying so; what was indexed before it stays searchable.
    /// To stop a run on demand instead, cancel the token passed to
    /// <see cref="ISearchEngine.StartIndexingAsync"/> or call <see cref="ISearchEngine.StopIndexingAsync"/>.
    /// </remarks>
    public TimeSpan? IndexingTimeout { get; set; }

    /// <summary>
    /// Former name of <see cref="IndexingTimeout"/>. Despite the name, it bounded the whole
    /// indexing run, not a file operation.
    /// </summary>
    /// <remarks>
    /// It defaulted to 30 seconds, which cut a whole-drive index short with no event to say so.
    /// Setting it sets <see cref="IndexingTimeout"/>; <see cref="Timeout.InfiniteTimeSpan"/> means
    /// no limit.
    /// </remarks>
    [Obsolete("It bounded the whole indexing run, not a file operation. Use IndexingTimeout, which defaults to no limit.")]
    public TimeSpan FileOperationTimeout
    {
        get => IndexingTimeout ?? Timeout.InfiniteTimeSpan;
        set => IndexingTimeout = value == Timeout.InfiniteTimeSpan ? null : value;
    }

    // .NET 10 specific optimizations

    /// <summary>
    /// Whether to use advanced string optimizations (StringPool, SearchValues)
    /// </summary>
    public bool UseAdvancedStringOptimizations { get; set; } = true;

    /// <summary>
    /// Whether to enable SIMD acceleration for string operations
    /// </summary>
    public bool EnableSIMDAcceleration { get; set; } = true;

    /// <summary>
    /// Whether to use .NET 10 SearchValues for character searching
    /// </summary>
    public bool UseSearchValues { get; set; } = true;

    /// <summary>
    /// Whether to optimize for large datasets (>1M files)
    /// </summary>
    public bool OptimizeForLargeDatasets { get; set; } = false;

    /// <summary>
    /// Buffer pool size for high-throughput operations
    /// </summary>
    public int BufferPoolSize { get; set; } = 100;

    /// <summary>
    /// Whether to use aggressive memory optimization
    /// </summary>
    public bool UseAggressiveMemoryOptimization { get; set; } = false;

    /// <summary>
    /// Whether to enable performance telemetry collection
    /// </summary>
    public bool EnablePerformanceTelemetry { get; set; } = true;

    /// <summary>
    /// Validates and optimizes the configuration
    /// </summary>
    public (bool IsValid, string? ErrorMessage) Validate()
    {
        var errors = new List<string>();

        if (FileEnumerationBufferSize < 4096)
            errors.Add("FileEnumerationBufferSize must be at least 4KB");

        if (MaxConcurrentOperations < 1)
            errors.Add("MaxConcurrentOperations must be at least 1");

        if (MetadataCacheSize < 1000)
            errors.Add("MetadataCacheSize must be at least 1000");

        if (IndexingTimeout is { } timeout && timeout <= TimeSpan.Zero)
            errors.Add("IndexingTimeout must be positive, or null for no limit");

        return (errors.Count == 0, errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    /// <summary>
    /// Creates optimized configuration for specific scenarios
    /// </summary>
    public static WindowsSearchEngineOptions CreateForScenario(OptimizationScenario scenario)
    {
        return scenario switch
        {
            OptimizationScenario.HighPerformance => new WindowsSearchEngineOptions
            {
                MaxConcurrentOperations = Environment.ProcessorCount * 4,
                FileEnumerationBufferSize = 256 * 1024,
                MetadataCacheSize = 100000,
                UseAdvancedStringOptimizations = true,
                EnableSIMDAcceleration = true,
                OptimizeForLargeDatasets = true
            },
            OptimizationScenario.LowMemory => new WindowsSearchEngineOptions
            {
                MaxConcurrentOperations = Math.Max(1, Environment.ProcessorCount / 2),
                FileEnumerationBufferSize = 32 * 1024,
                MetadataCacheSize = 5000,
                UseMemoryMappedFiles = false,
                UseAggressiveMemoryOptimization = true
            },
            OptimizationScenario.Balanced => new WindowsSearchEngineOptions(),
            _ => new WindowsSearchEngineOptions()
        };
    }
}

/// <summary>
/// Optimization scenarios for different use cases
/// </summary>
public enum OptimizationScenario
{
    Balanced,
    HighPerformance,
    LowMemory,
    LargeDatasets,
    RealTime
}

/// <summary>
/// Enhanced information about NTFS capabilities on the current system
/// </summary>
public class NtfsCapabilities
{
    /// <summary>
    /// Whether direct MFT access is available
    /// </summary>
    public bool CanAccessMft { get; set; }

    /// <summary>
    /// Whether the system has NTFS drives
    /// </summary>
    public bool HasNtfsDrives { get; set; }

    /// <summary>
    /// Available NTFS drive letters
    /// </summary>
    public char[] AvailableNtfsDrives { get; set; } = [];

    /// <summary>
    /// Detailed NTFS drive information
    /// </summary>
    public NtfsDriveInfo[] NtfsDrives { get; set; } = [];

    /// <summary>
    /// Whether advanced Windows features are supported
    /// </summary>
    public bool SupportsAdvancedFeatures { get; set; }

    /// <summary>
    /// Windows version information
    /// </summary>
    public Version? WindowsVersion { get; set; }

    /// <summary>
    /// Whether WSL integration is supported
    /// </summary>
    public bool SupportsWslIntegration { get; set; }

    /// <summary>
    /// Whether container features are supported
    /// </summary>
    public bool SupportsContainerFeatures { get; set; }

    /// <summary>
    /// Whether Volume Shadow Copy Service is available
    /// </summary>
    public bool SupportsVolumeSnapshotService { get; set; }

    /// <summary>
    /// Whether the current process has administrative privileges
    /// </summary>
    public bool HasAdministrativePrivileges { get; set; }

    /// <summary>
    /// Whether SIMD instructions are supported
    /// </summary>
    public bool SupportsSIMD { get; set; }

    /// <summary>
    /// Whether AVX2 instructions are supported
    /// </summary>
    public bool SupportsAVX2 { get; set; }

    /// <summary>
    /// System memory in GB
    /// </summary>
    public double SystemMemoryGB { get; set; }

    /// <summary>
    /// Number of processor cores
    /// </summary>
    public int ProcessorCount { get; set; }

    /// <summary>
    /// Whether the configuration is optimal for performance
    /// </summary>
    public bool IsOptimal { get; set; }

    /// <summary>
    /// Error message if capabilities check failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets a comprehensive summary of the capabilities
    /// </summary>
    public string GetSummary()
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
            return $"Error checking capabilities: {ErrorMessage}";

        var features = new List<string>();

        if (CanAccessMft)
            features.Add("Direct MFT access");

        if (HasNtfsDrives)
            features.Add($"NTFS drives: {string.Join(", ", AvailableNtfsDrives)}");

        if (SupportsAdvancedFeatures)
            features.Add($"Windows {WindowsVersion?.Major}.{WindowsVersion?.Minor}");

        if (SupportsSIMD)
            features.Add("SIMD acceleration");

        if (SupportsVolumeSnapshotService)
            features.Add("VSS support");

        features.Add($"{ProcessorCount} cores, {SystemMemoryGB:F1}GB RAM");

        var summary = features.Count > 0 ? string.Join(", ", features) : "Basic file system access only";
        var status = IsOptimal ? "Optimal" : "⚠️ Limited";

        return $"{status} configuration: {summary}";
    }

    /// <summary>
    /// Gets performance recommendations based on capabilities
    /// </summary>
    public string GetPerformanceRecommendations()
    {
        var recommendations = new List<string>();

        if (!CanAccessMft)
            recommendations.Add("Run as administrator for MFT access");

        if (SystemMemoryGB < 8)
            recommendations.Add("Consider adding more RAM for better performance");

        if (ProcessorCount < 4)
            recommendations.Add("Limited CPU cores may impact concurrent operations");

        if (!SupportsSIMD)
            recommendations.Add("SIMD acceleration not available on this system");

        return recommendations.Count > 0
            ? string.Join(", ", recommendations)
            : "System is optimally configured";
    }
}

/// <summary>
/// Detailed information about an NTFS drive
/// </summary>
public class NtfsDriveInfo
{
    public char Letter { get; set; }
    public long TotalSize { get; set; }
    public long AvailableSpace { get; set; }
    public bool SupportsCompression { get; set; }
    public bool SupportsEncryption { get; set; }

    public double UsagePercentage => TotalSize > 0 ? (1.0 - (double)AvailableSpace / TotalSize) * 100 : 0;
    public string TotalSizeFormatted => FormatBytes(TotalSize);
    public string AvailableSpaceFormatted => FormatBytes(AvailableSpace);

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}

/// <summary>
/// Thread-safe capability cache for performance
/// </summary>
internal class WindowsCapabilityCache
{
    private NtfsCapabilities? _capabilities;
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public bool IsValid => _capabilities != null &&
                          DateTime.Now - _lastUpdate < _cacheExpiry;

    public NtfsCapabilities Capabilities => _capabilities ?? new NtfsCapabilities();

    public void Update(NtfsCapabilities capabilities)
    {
        _capabilities = capabilities;
        _lastUpdate = DateTime.Now;
    }
}