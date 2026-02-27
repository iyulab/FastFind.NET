# FastFind.NET Search Examples

Complete guide to using the search options in FastFind.NET.

## Core Search Options

FastFind.NET provides three essential search options:

1. **BasePath** - Specify the starting directory for your search
2. **SearchText** - Define the pattern to find in paths and filenames
3. **IncludeSubdirectories** - Control whether to search in subdirectories

## Basic Examples

### Example 1: Search from Specific Directory
```csharp
using FastFind;
using FastFind.Windows;

// Ensure Windows support is registered
WindowsRegistration.EnsureRegistered();

// Create search engine
using var searchEngine = FastFinder.CreateWindowsSearchEngine();

// Search for "claude" files starting from C:\Users
var query = new SearchQuery
{
    BasePath = @"C:\Users",              // 기준경로: Start from this directory
    SearchText = "claude",               // search-text: Find files containing "claude"
    IncludeSubdirectories = true,        // subdirectory: Search in all subdirectories
    SearchFileNameOnly = false           // Search in full paths (not just filenames)
};

var results = await searchEngine.SearchAsync(query);
Console.WriteLine($"Found {results.TotalMatches} files containing 'claude'");

// Stream results efficiently
await foreach (var file in results.Files)
{
    Console.WriteLine($"{file.FullPath} ({file.Size:N0} bytes)");
}
```

### Example 2: Filename-Only vs Full-Path Search
```csharp
// Compare searching in filenames vs full paths
var searchText = "config";
var basePath = @"C:\Program Files";

// Search only in filenames
var filenameQuery = new SearchQuery
{
    BasePath = basePath,
    SearchText = searchText,
    SearchFileNameOnly = true,           // Only search in file names
    IncludeSubdirectories = true
};

// Search in full paths (directories + filenames)
var fullPathQuery = new SearchQuery
{
    BasePath = basePath,
    SearchText = searchText,
    SearchFileNameOnly = false,          // Search in full paths
    IncludeSubdirectories = true
};

var filenameResults = await searchEngine.SearchAsync(filenameQuery);
var fullPathResults = await searchEngine.SearchAsync(fullPathQuery);

Console.WriteLine($"Filename search: {filenameResults.TotalMatches} matches");
Console.WriteLine($"Full path search: {fullPathResults.TotalMatches} matches");
// Full path search typically finds more results because it matches directory names too
```

### Example 3: Subdirectory Control
```csharp
var basePath = @"C:\Users\Documents";
var searchText = "project";

// Search only in the base directory (no subdirectories)
var directQuery = new SearchQuery
{
    BasePath = basePath,
    SearchText = searchText,
    IncludeSubdirectories = false,       // Only direct files
    SearchFileNameOnly = false
};

// Search in base directory and all subdirectories
var recursiveQuery = new SearchQuery
{
    BasePath = basePath,
    SearchText = searchText,
    IncludeSubdirectories = true,        // Include all subdirectories
    SearchFileNameOnly = false
};

var directResults = await searchEngine.SearchAsync(directQuery);
var recursiveResults = await searchEngine.SearchAsync(recursiveQuery);

Console.WriteLine($"Direct only: {directResults.TotalMatches} files");
Console.WriteLine($"Recursive: {recursiveResults.TotalMatches} files");
```

## Real-World Scenarios

### Scenario 1: Find Project Files
```csharp
// Find all C# controller files in a web project
var projectQuery = new SearchQuery
{
    BasePath = @"D:\MyProjects\WebApp",
    SearchText = "Controller",
    ExtensionFilter = ".cs",
    IncludeSubdirectories = true,
    SearchFileNameOnly = false,          // Match both filenames and directory names
    CaseSensitive = false
};

var results = await searchEngine.SearchAsync(projectQuery);
Console.WriteLine($"Found {results.TotalMatches} controller files");

await foreach (var file in results.Files)
{
    // Will find files like:
    // - Controllers\HomeController.cs
    // - Controllers\Api\UserController.cs
    // - Areas\Admin\Controllers\DashboardController.cs
    Console.WriteLine($"{file.FullPath}");
}
```

### Scenario 2: Configuration File Discovery
```csharp
// Find all configuration files in the system
var configQuery = new SearchQuery
{
    SearchText = "config",
    SearchFileNameOnly = false,          // Search in paths to find "config" directories
    ExtensionFilter = ".json",           // Only JSON config files
    IncludeSubdirectories = true,
    MaxResults = 100,                    // Limit results
    ExcludedPaths = { "node_modules", "cache", "temp" }  // Skip common noise
};

// Will find files like:
// - C:\Program Files\App\config\settings.json
// - C:\Users\User\AppData\config\preferences.json
// - D:\Projects\MyApp\config\database.json
```

### Scenario 3: Large File Cleanup
```csharp
// Find large files in Downloads folder (no subdirectories)
var cleanupQuery = new SearchQuery
{
    BasePath = @"C:\Users\%USERNAME%\Downloads",
    MinSize = 100 * 1024 * 1024,        // Files larger than 100MB
    IncludeSubdirectories = false,       // Only check direct downloads
    IncludeFiles = true,
    IncludeDirectories = false,
    MaxResults = 50
};

var largeFiles = await searchEngine.SearchAsync(cleanupQuery);
Console.WriteLine($"Found {largeFiles.TotalMatches} large files to clean up");
```

### Scenario 4: Regex Pattern Search
```csharp
// Find files matching multiple name patterns using regex
var regexQuery = new SearchQuery
{
    BasePath = @"D:\SourceCode\MyProject",
    SearchText = @"Service|Repository|Controller",
    UseRegex = true,                     // Enable regex for multiple patterns
    SearchFileNameOnly = true,           // Match filenames only
    ExtensionFilter = ".cs",
    IncludeSubdirectories = true,
    CaseSensitive = false
};

// Finds files like: UserService.cs, OrderRepository.cs, HomeController.cs
```

## Advanced Filtering

### Date-Based Search
```csharp
// Find recent files modified in the last week
var recentQuery = new SearchQuery
{
    BasePath = @"D:\WorkProjects",
    MinModifiedDate = DateTime.Now.AddDays(-7),
    IncludeSubdirectories = true,
    ExtensionFilter = ".cs",
    SearchFileNameOnly = false
};
```

### Size-Based Search
```csharp
// Find medium-sized image files
var imageQuery = new SearchQuery
{
    BasePath = @"C:\Users\Pictures",
    MinSize = 1024 * 1024,              // At least 1MB
    MaxSize = 50 * 1024 * 1024,         // No more than 50MB
    SearchText = @"\.(jpg|png|gif)$",
    UseRegex = true,
    IncludeSubdirectories = true,
    SearchFileNameOnly = true            // Match file extensions
};
```

### Combined Filters
```csharp
// Complex search: Recent, medium-sized C# files with "Service" in the name
var complexQuery = new SearchQuery
{
    BasePath = @"D:\Development",
    SearchText = "Service",
    ExtensionFilter = ".cs",
    MinModifiedDate = DateTime.Now.AddDays(-30),
    MinSize = 1024,                      // At least 1KB
    MaxSize = 500 * 1024,               // No more than 500KB
    IncludeSubdirectories = true,
    SearchFileNameOnly = false,
    CaseSensitive = false,
    MaxResults = 200
};
```

## Performance Tips

### 1. Use BasePath for Targeted Searches
```csharp
// Good: Targeted search
var targeted = new SearchQuery
{
    BasePath = @"D:\MyProject",          // Start from specific directory
    SearchText = "component",
    IncludeSubdirectories = true
};

// Avoid: System-wide search
var systemWide = new SearchQuery
{
    SearchLocations = { @"C:\", @"D:\" }, // Searches entire drives
    SearchText = "component"
};
```

### 2. Control Subdirectory Traversal
```csharp
// Good: Limit scope when possible
var limited = new SearchQuery
{
    BasePath = @"C:\Users\Downloads",
    SearchText = "installer",
    IncludeSubdirectories = false        // Only direct files
};

// Use carefully: Deep recursion
var recursive = new SearchQuery
{
    BasePath = @"C:\",
    SearchText = "installer",
    IncludeSubdirectories = true         // Can be slow on large drives
};
```

### 3. Use Appropriate Search Scope
```csharp
// For filename-only searches (faster)
var filenameSearch = new SearchQuery
{
    SearchText = "readme",
    SearchFileNameOnly = true,           // Only search filenames
    ExtensionFilter = ".md"
};

// For path-based searches (more comprehensive)
var pathSearch = new SearchQuery
{
    SearchText = "documentation",
    SearchFileNameOnly = false,          // Search full paths
    IncludeSubdirectories = true
};
```

## Result Processing

### Streaming Processing (Memory Efficient)
```csharp
var query = new SearchQuery
{
    BasePath = @"D:\LargeDataset",
    SearchText = "data",
    IncludeSubdirectories = true
};

var results = await searchEngine.SearchAsync(query);

// Process results as they arrive (low memory usage)
await foreach (var file in results.Files)
{
    // Process immediately
    await ProcessFileAsync(file);

    // No need to store all results in memory
}
```

### Batch Collection (When You Need All Results)
```csharp
var query = new SearchQuery
{
    BasePath = @"D:\MyProject",
    SearchText = "test",
    MaxResults = 1000                    // Limit for memory management
};

var results = await searchEngine.SearchAsync(query);

// Collect all results
var allFiles = new List<FastFileItem>();
await foreach (var file in results.Files)
{
    allFiles.Add(file);
}

// Now process the complete collection
ProcessAllFiles(allFiles);
```

## Migration from Old API

If you're upgrading from an older version, here's how the API has evolved:

### Old Way (Before Enhancement)
```csharp
var oldQuery = new SearchQuery
{
    SearchText = "config",
    SearchLocations = { @"C:\Program Files" },
    SearchFileNameOnly = true
};
```

### New Way (Enhanced API)
```csharp
var newQuery = new SearchQuery
{
    BasePath = @"C:\Program Files",      // Clearer single base path
    SearchText = "config",
    IncludeSubdirectories = true,        // Explicit subdirectory control
    SearchFileNameOnly = false           // Default changed to search full paths
};
```

The new API provides:
- Clearer intent with `BasePath` vs `SearchLocations`
- Explicit subdirectory control
- Full path search by default (more useful)
- Better performance with targeted searches

## Linux Examples

### Setup
```csharp
using FastFind;
using FastFind.Unix;

// Create Linux search engine (auto-registered via ModuleInitializer)
using var searchEngine = UnixSearchEngine.CreateLinuxSearchEngine();

// Index mount points
await searchEngine.StartIndexingAsync(new IndexingOptions
{
    MountPoints = ["/home", "/opt"],
    ExcludedPaths = ["node_modules", ".git", "__pycache__", ".cache", "venv"],
    CollectFileSize = true
});

while (searchEngine.IsIndexing) await Task.Delay(500);
```

### Find Config Files
```csharp
var results = await searchEngine.SearchAsync(new SearchQuery
{
    BasePath = "/etc",
    SearchText = "nginx",
    IncludeSubdirectories = true,
    ExtensionFilter = ".conf"
});

await foreach (var file in results.Files)
{
    Console.WriteLine($"{file.FullPath}");
}
```

### Search Home Directory
```csharp
var results = await searchEngine.SearchAsync(new SearchQuery
{
    BasePath = "/home/user/projects",
    SearchText = "Dockerfile",
    SearchFileNameOnly = true,
    IncludeSubdirectories = true,
    MaxResults = 50
});
```

### Find Large Log Files
```csharp
var results = await searchEngine.SearchAsync(new SearchQuery
{
    BasePath = "/var/log",
    MinSize = 100 * 1024 * 1024,  // > 100MB
    ExtensionFilter = ".log",
    IncludeSubdirectories = true
});
```