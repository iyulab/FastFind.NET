using System.Text.RegularExpressions;

namespace FastFind.Models;

/// <summary>
/// Represents a search query with comprehensive filtering options
/// </summary>
public class SearchQuery
{
    /// <summary>
    /// The text to search for (supports wildcards and regex)
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Whether to use regular expressions for matching
    /// </summary>
    public bool UseRegex { get; set; } = false;

    /// <summary>
    /// Whether the search should be case-sensitive
    /// </summary>
    public bool CaseSensitive { get; set; } = false;

    /// <summary>
    /// Whether to search only in file names (true) or in both file names and full paths (false)
    /// When false, search text can match anywhere in the full path (e.g., directory names, file names)
    /// </summary>
    public bool SearchFileNameOnly { get; set; } = false;

    /// <summary>
    /// Single base path to start searching from (takes precedence over SearchLocations if specified)
    /// Example: "D:\data" - will search from this path and optionally include subdirectories
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// File extension filter (e.g., ".txt", ".pdf")
    /// </summary>
    public string? ExtensionFilter { get; set; }

    /// <summary>
    /// Whether to include files in results
    /// </summary>
    public bool IncludeFiles { get; set; } = true;

    /// <summary>
    /// Whether to include directories in results
    /// </summary>
    public bool IncludeDirectories { get; set; } = true;

    /// <summary>
    /// Whether to include hidden files and directories
    /// </summary>
    public bool IncludeHidden { get; set; } = false;

    /// <summary>
    /// Whether to include system files and directories
    /// </summary>
    public bool IncludeSystem { get; set; } = false;

    /// <summary>
    /// Minimum file size in bytes (null for no limit)
    /// </summary>
    public long? MinSize { get; set; }

    /// <summary>
    /// Maximum file size in bytes (null for no limit)
    /// </summary>
    public long? MaxSize { get; set; }

    /// <summary>
    /// Minimum creation date (null for no limit)
    /// </summary>
    public DateTime? MinCreatedDate { get; set; }

    /// <summary>
    /// Maximum creation date (null for no limit)
    /// </summary>
    public DateTime? MaxCreatedDate { get; set; }

    /// <summary>
    /// Minimum modification date (null for no limit)
    /// </summary>
    public DateTime? MinModifiedDate { get; set; }

    /// <summary>
    /// Maximum modification date (null for no limit)
    /// </summary>
    public DateTime? MaxModifiedDate { get; set; }

    /// <summary>
    /// Maximum number of results to return (null for no limit)
    /// </summary>
    public int? MaxResults { get; set; }

    /// <summary>
    /// Specific drives to search (Windows) or mount points (Unix)
    /// </summary>
    public IList<string> SearchLocations { get; set; } = new List<string>();

    /// <summary>
    /// Paths to exclude from search
    /// </summary>
    public IList<string> ExcludedPaths { get; set; } = new List<string>();

    /// <summary>
    /// Whether to include subdirectories when searching in specific locations
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Custom file attributes to match
    /// </summary>
    public System.IO.FileAttributes? RequiredAttributes { get; set; }

    /// <summary>
    /// File attributes that must not be present
    /// </summary>
    public System.IO.FileAttributes? ExcludedAttributes { get; set; }

    /// <summary>
    /// Gets the compiled regex pattern if UseRegex is true
    /// </summary>
    public Regex? GetCompiledRegex()
    {
        if (!UseRegex || string.IsNullOrEmpty(SearchText))
            return null;

        try
        {
            var options = RegexOptions.Compiled;
            if (!CaseSensitive)
                options |= RegexOptions.IgnoreCase;

            return new Regex(SearchText, options);
        }
        catch (ArgumentException)
        {
            // Invalid regex pattern
            return null;
        }
    }

    /// <summary>
    /// Converts wildcard pattern to regex pattern
    /// </summary>
    public Regex? GetWildcardRegex()
    {
        if (UseRegex || string.IsNullOrEmpty(SearchText))
            return null;

        try
        {
            // Escape special regex characters except * and ?
            var escaped = Regex.Escape(SearchText);
            // Convert wildcards to regex
            var pattern = escaped.Replace(@"\*", ".*").Replace(@"\?", ".");
            
            var options = RegexOptions.Compiled;
            if (!CaseSensitive)
                options |= RegexOptions.IgnoreCase;

            return new Regex($"^{pattern}$", options);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates if the query is valid and can be executed
    /// </summary>
    public (bool IsValid, string? ErrorMessage) Validate()
    {
        if (string.IsNullOrWhiteSpace(SearchText) && ExtensionFilter == null && 
            MinSize == null && MaxSize == null && MinCreatedDate == null && 
            MaxCreatedDate == null && MinModifiedDate == null && MaxModifiedDate == null)
        {
            return (false, "At least one search criterion must be specified");
        }

        if (UseRegex && !string.IsNullOrEmpty(SearchText))
        {
            try
            {
                _ = new Regex(SearchText);
            }
            catch (ArgumentException ex)
            {
                return (false, $"Invalid regex pattern: {ex.Message}");
            }
        }

        if (MinSize.HasValue && MaxSize.HasValue && MinSize.Value > MaxSize.Value)
        {
            return (false, "Minimum size cannot be greater than maximum size");
        }

        if (MinCreatedDate.HasValue && MaxCreatedDate.HasValue && MinCreatedDate.Value > MaxCreatedDate.Value)
        {
            return (false, "Minimum creation date cannot be later than maximum creation date");
        }

        if (MinModifiedDate.HasValue && MaxModifiedDate.HasValue && MinModifiedDate.Value > MaxModifiedDate.Value)
        {
            return (false, "Minimum modification date cannot be later than maximum modification date");
        }

        if (MaxResults.HasValue && MaxResults.Value <= 0)
        {
            return (false, "Maximum results must be a positive number");
        }

        return (true, null);
    }

    /// <summary>
    /// Creates a copy of the query with modified parameters
    /// </summary>
    public SearchQuery Clone()
    {
        return new SearchQuery
        {
            SearchText = SearchText,
            UseRegex = UseRegex,
            CaseSensitive = CaseSensitive,
            SearchFileNameOnly = SearchFileNameOnly,
            BasePath = BasePath,
            ExtensionFilter = ExtensionFilter,
            IncludeFiles = IncludeFiles,
            IncludeDirectories = IncludeDirectories,
            IncludeHidden = IncludeHidden,
            IncludeSystem = IncludeSystem,
            MinSize = MinSize,
            MaxSize = MaxSize,
            MinCreatedDate = MinCreatedDate,
            MaxCreatedDate = MaxCreatedDate,
            MinModifiedDate = MinModifiedDate,
            MaxModifiedDate = MaxModifiedDate,
            MaxResults = MaxResults,
            SearchLocations = new List<string>(SearchLocations),
            ExcludedPaths = new List<string>(ExcludedPaths),
            IncludeSubdirectories = IncludeSubdirectories,
            RequiredAttributes = RequiredAttributes,
            ExcludedAttributes = ExcludedAttributes
        };
    }
}