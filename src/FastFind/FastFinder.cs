using FastFind.Interfaces;
using FastFind.Models;
using Microsoft.Extensions.Logging;

namespace FastFind;

/// <summary>
/// Main factory class for creating FastFind search engines
/// </summary>
public static class FastFinder
{
    private static readonly Dictionary<PlatformType, Func<ILoggerFactory?, ISearchEngine>> _factories = new();
    private static readonly Lock _lock = new();

    /// <summary>
    /// Creates a search engine for the current platform
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory</param>
    /// <returns>Platform-optimized search engine</returns>
    public static ISearchEngine CreateSearchEngine(ILoggerFactory? loggerFactory = null)
    {
        var platformType = GetCurrentPlatform();
        return CreateSearchEngine(platformType, loggerFactory);
    }

    /// <summary>
    /// Creates a search engine for a specific platform
    /// </summary>
    /// <param name="platformType">Target platform</param>
    /// <param name="loggerFactory">Optional logger factory</param>
    /// <returns>Platform-specific search engine</returns>
    public static ISearchEngine CreateSearchEngine(PlatformType platformType, ILoggerFactory? loggerFactory = null)
    {
        lock (_lock)
        {
            if (_factories.TryGetValue(platformType, out var factory))
            {
                return factory(loggerFactory);
            }

            // Fallback to cross-platform implementation
            if (_factories.TryGetValue(PlatformType.CrossPlatform, out var fallbackFactory))
            {
                return fallbackFactory(loggerFactory);
            }

            throw new PlatformNotSupportedException($"No search engine implementation available for platform: {platformType}");
        }
    }

    /// <summary>
    /// Registers a search engine factory for a specific platform
    /// </summary>
    /// <param name="platformType">Platform type</param>
    /// <param name="factory">Factory function</param>
    public static void RegisterSearchEngineFactory(PlatformType platformType, Func<ILoggerFactory?, ISearchEngine> factory)
    {
        lock (_lock)
        {
            _factories[platformType] = factory;
        }
    }

    /// <summary>
    /// Gets available platforms with registered factories
    /// </summary>
    /// <returns>Available platform types</returns>
    public static IEnumerable<PlatformType> GetAvailablePlatforms()
    {
        lock (_lock)
        {
            return _factories.Keys.ToArray();
        }
    }

    /// <summary>
    /// Gets the current platform type
    /// </summary>
    /// <returns>Current platform type</returns>
    public static PlatformType GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return PlatformType.Windows;
        
        if (OperatingSystem.IsMacOS())
            return PlatformType.MacOS;
        
        if (OperatingSystem.IsLinux())
            return PlatformType.Linux;
        
        if (OperatingSystem.IsFreeBSD() || OperatingSystem.IsAndroid())
            return PlatformType.Unix;
        
        return PlatformType.CrossPlatform;
    }

    /// <summary>
    /// Creates default indexing options for the current platform
    /// </summary>
    /// <returns>Default indexing options</returns>
    public static IndexingOptions CreateDefaultIndexingOptions()
    {
        return IndexingOptions.CreateDefault();
    }

    /// <summary>
    /// Creates a search query builder
    /// </summary>
    /// <param name="searchText">Initial search text</param>
    /// <returns>Search query builder</returns>
    public static SearchQueryBuilder CreateSearchQuery(string searchText = "")
    {
        return new SearchQueryBuilder(searchText);
    }

    /// <summary>
    /// Validates the current system for FastFind compatibility
    /// </summary>
    /// <returns>Validation result</returns>
    public static SystemValidationResult ValidateSystem()
    {
        try
        {
            var platformType = GetCurrentPlatform();
            var isSupported = _factories.ContainsKey(platformType) || _factories.ContainsKey(PlatformType.CrossPlatform);
            
            // Check .NET version
            var version = Environment.Version;
            var runtimeVersion = version.ToString();
            var isCompatibleRuntime = version.Major >= 6; // .NET 6+ required
            
            // Check available memory
            var workingSet = Environment.WorkingSet;
            var hasSufficientMemory = workingSet > 100 * 1024 * 1024; // 100MB minimum
            
            // Check permissions (basic test)
            bool hasFileSystemAccess;
            try
            {
                var tempPath = Path.GetTempPath();
                var testFile = Path.Combine(tempPath, $"fastfind_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                hasFileSystemAccess = true;
            }
            catch
            {
                hasFileSystemAccess = false;
            }
            
            var isReady = isSupported && isCompatibleRuntime && hasSufficientMemory && hasFileSystemAccess;
            
            return new SystemValidationResult
            {
                SupportedPlatform = platformType,
                IsSupported = isSupported,
                RuntimeVersion = runtimeVersion,
                IsCompatibleRuntime = isCompatibleRuntime,
                AvailableMemory = workingSet,
                HasSufficientMemory = hasSufficientMemory,
                HasFileSystemAccess = hasFileSystemAccess,
                IsReady = isReady
            };
        }
        catch (Exception ex)
        {
            return new SystemValidationResult
            {
                SupportedPlatform = PlatformType.CrossPlatform,
                IsSupported = false,
                RuntimeVersion = "Unknown",
                IsCompatibleRuntime = false,
                AvailableMemory = 0,
                HasSufficientMemory = false,
                HasFileSystemAccess = false,
                IsReady = false,
                ValidationError = ex.Message
            };
        }
    }
}

/// <summary>
/// Builder for creating search queries with fluent API
/// </summary>
public class SearchQueryBuilder
{
    private readonly SearchQuery _query;

    internal SearchQueryBuilder(string searchText)
    {
        _query = new SearchQuery { SearchText = searchText };
    }

    /// <summary>
    /// Sets the search text
    /// </summary>
    /// <param name="searchText">Text to search for</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder WithText(string searchText)
    {
        _query.SearchText = searchText;
        return this;
    }

    /// <summary>
    /// Enables or disables regex matching
    /// </summary>
    /// <param name="useRegex">Whether to use regex</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder UseRegex(bool useRegex = true)
    {
        _query.UseRegex = useRegex;
        return this;
    }

    /// <summary>
    /// Enables or disables case-sensitive matching
    /// </summary>
    /// <param name="caseSensitive">Whether to be case-sensitive</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder CaseSensitive(bool caseSensitive = true)
    {
        _query.CaseSensitive = caseSensitive;
        return this;
    }

    /// <summary>
    /// Sets whether to search only file names or full paths
    /// </summary>
    /// <param name="fileNameOnly">Whether to search file names only</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder FileNameOnly(bool fileNameOnly = true)
    {
        _query.SearchFileNameOnly = fileNameOnly;
        return this;
    }

    /// <summary>
    /// Sets the file extension filter
    /// </summary>
    /// <param name="extension">File extension (e.g., ".txt")</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder WithExtension(string extension)
    {
        _query.ExtensionFilter = extension;
        return this;
    }

    /// <summary>
    /// Sets the minimum file size
    /// </summary>
    /// <param name="minSize">Minimum size in bytes</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder MinSize(long minSize)
    {
        _query.MinSize = minSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum file size
    /// </summary>
    /// <param name="maxSize">Maximum size in bytes</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder MaxSize(long maxSize)
    {
        _query.MaxSize = maxSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of results
    /// </summary>
    /// <param name="maxResults">Maximum number of results</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder MaxResults(int maxResults)
    {
        _query.MaxResults = maxResults;
        return this;
    }

    /// <summary>
    /// Includes or excludes files in results
    /// </summary>
    /// <param name="include">Whether to include files</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder IncludeFiles(bool include = true)
    {
        _query.IncludeFiles = include;
        return this;
    }

    /// <summary>
    /// Includes or excludes directories in results
    /// </summary>
    /// <param name="include">Whether to include directories</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder IncludeDirectories(bool include = true)
    {
        _query.IncludeDirectories = include;
        return this;
    }

    /// <summary>
    /// Includes or excludes hidden items
    /// </summary>
    /// <param name="include">Whether to include hidden items</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder IncludeHidden(bool include = true)
    {
        _query.IncludeHidden = include;
        return this;
    }

    /// <summary>
    /// Adds a location to search in
    /// </summary>
    /// <param name="location">Location to search</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder InLocation(string location)
    {
        _query.SearchLocations.Add(location);
        return this;
    }

    /// <summary>
    /// Excludes a path from search
    /// </summary>
    /// <param name="path">Path to exclude</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder ExcludePath(string path)
    {
        _query.ExcludedPaths.Add(path);
        return this;
    }

    /// <summary>
    /// Sets the date range for file creation
    /// </summary>
    /// <param name="from">Start date</param>
    /// <param name="to">End date</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder CreatedBetween(DateTime from, DateTime to)
    {
        _query.MinCreatedDate = from;
        _query.MaxCreatedDate = to;
        return this;
    }

    /// <summary>
    /// Sets the date range for file modification
    /// </summary>
    /// <param name="from">Start date</param>
    /// <param name="to">End date</param>
    /// <returns>Builder instance</returns>
    public SearchQueryBuilder ModifiedBetween(DateTime from, DateTime to)
    {
        _query.MinModifiedDate = from;
        _query.MaxModifiedDate = to;
        return this;
    }

    /// <summary>
    /// Builds the search query
    /// </summary>
    /// <returns>Configured search query</returns>
    public SearchQuery Build()
    {
        return _query.Clone();
    }

    /// <summary>
    /// Implicitly converts the builder to a search query
    /// </summary>
    /// <param name="builder">Builder instance</param>
    public static implicit operator SearchQuery(SearchQueryBuilder builder)
    {
        return builder.Build();
    }
}

/// <summary>
/// System validation result
/// </summary>
public record SystemValidationResult
{
    /// <summary>
    /// Whether the system is supported
    /// </summary>
    public bool IsSupported { get; init; }

    /// <summary>
    /// Whether the system is ready to use FastFind
    /// </summary>
    public bool IsReady { get; init; }

    /// <summary>
    /// Detected platform type
    /// </summary>
    public PlatformType SupportedPlatform { get; init; }

    /// <summary>
    /// .NET runtime version
    /// </summary>
    public string RuntimeVersion { get; init; } = string.Empty;

    /// <summary>
    /// Whether the runtime is compatible
    /// </summary>
    public bool IsCompatibleRuntime { get; init; }

    /// <summary>
    /// Available memory in bytes
    /// </summary>
    public long AvailableMemory { get; init; }

    /// <summary>
    /// Whether there's sufficient memory
    /// </summary>
    public bool HasSufficientMemory { get; init; }

    /// <summary>
    /// Whether file system access is available
    /// </summary>
    public bool HasFileSystemAccess { get; init; }

    /// <summary>
    /// Validation error message (if any)
    /// </summary>
    public string? ValidationError { get; init; }

    /// <summary>
    /// Gets a summary of the validation results
    /// </summary>
    public string GetSummary()
    {
        if (IsReady)
            return "System is ready for FastFind.NET";

        var issues = new List<string>();
        
        if (!IsSupported)
            issues.Add($"Platform {SupportedPlatform} is not supported");
        
        if (!IsCompatibleRuntime)
            issues.Add($"Runtime version {RuntimeVersion} is not compatible (requires .NET 6+)");
        
        if (!HasSufficientMemory)
            issues.Add($"Insufficient memory ({AvailableMemory / (1024 * 1024)}MB available, 100MB required)");
        
        if (!HasFileSystemAccess)
            issues.Add("File system access is not available");
        
        if (!string.IsNullOrEmpty(ValidationError))
            issues.Add($"Validation error: {ValidationError}");

        return $"System is not ready: {string.Join(", ", issues)}";
    }
}