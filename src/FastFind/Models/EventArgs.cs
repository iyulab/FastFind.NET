namespace FastFind.Models;

/// <summary>
/// Event arguments for indexing progress updates
/// </summary>
public class IndexingProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets the location being indexed (drive letter or mount point)
    /// </summary>
    public string Location { get; }

    /// <summary>
    /// Gets the number of processed files
    /// </summary>
    public long ProcessedFiles { get; }

    /// <summary>
    /// Gets the estimated total number of files (may be updated during indexing)
    /// </summary>
    public long EstimatedTotalFiles { get; }

    /// <summary>
    /// Gets the progress percentage (0-100, -1 if unknown)
    /// </summary>
    public double ProgressPercentage { get; }

    /// <summary>
    /// Gets the elapsed time since indexing started
    /// </summary>
    public TimeSpan ElapsedTime { get; }

    /// <summary>
    /// Gets the current file or directory being processed
    /// </summary>
    public string CurrentPath { get; }

    /// <summary>
    /// Gets the indexing speed (files per second)
    /// </summary>
    public double FilesPerSecond { get; }

    /// <summary>
    /// Gets the estimated time remaining
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; }

    /// <summary>
    /// Gets the current indexing phase
    /// </summary>
    public IndexingPhase Phase { get; }

    /// <summary>
    /// Initializes a new instance of IndexingProgressEventArgs
    /// </summary>
    public IndexingProgressEventArgs(
        string location,
        long processedFiles,
        long estimatedTotalFiles,
        TimeSpan elapsedTime,
        string currentPath,
        IndexingPhase phase = IndexingPhase.Scanning)
    {
        Location = location;
        ProcessedFiles = processedFiles;
        EstimatedTotalFiles = estimatedTotalFiles;
        ElapsedTime = elapsedTime;
        CurrentPath = currentPath ?? string.Empty;
        Phase = phase;

        // Calculate derived properties
        ProgressPercentage = estimatedTotalFiles > 0 ? 
            Math.Min(100.0, (double)processedFiles / estimatedTotalFiles * 100.0) : -1.0;
        
        FilesPerSecond = elapsedTime.TotalSeconds > 0 ? 
            processedFiles / elapsedTime.TotalSeconds : 0.0;

        if (ProgressPercentage > 0 && FilesPerSecond > 0)
        {
            var remainingFiles = estimatedTotalFiles - processedFiles;
            var remainingSeconds = remainingFiles / FilesPerSecond;
            EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingSeconds);
        }
    }
}

/// <summary>
/// Event arguments for file system change notifications
/// </summary>
public class FileChangeEventArgs : EventArgs
{
    /// <summary>
    /// Gets the file item that changed
    /// </summary>
    public FileItem? FileItem { get; }

    /// <summary>
    /// Gets the type of change
    /// </summary>
    public FileChangeType ChangeType { get; }

    /// <summary>
    /// Gets the old file path (for move/rename operations)
    /// </summary>
    public string? OldPath { get; }

    /// <summary>
    /// Gets the new file path
    /// </summary>
    public string NewPath { get; }

    /// <summary>
    /// Gets the timestamp when the change occurred
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Initializes a new instance of FileChangeEventArgs
    /// </summary>
    public FileChangeEventArgs(
        FileChangeType changeType,
        string newPath,
        FileItem? fileItem = null,
        string? oldPath = null)
    {
        ChangeType = changeType;
        NewPath = newPath;
        FileItem = fileItem;
        OldPath = oldPath;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// Event arguments for search progress updates
/// </summary>
public class SearchProgressEventArgs : EventArgs
{
    /// <summary>
    /// Gets the search query
    /// </summary>
    public SearchQuery Query { get; }

    /// <summary>
    /// Gets the current number of matches found
    /// </summary>
    public long MatchesFound { get; }

    /// <summary>
    /// Gets the number of files processed so far
    /// </summary>
    public long FilesProcessed { get; }

    /// <summary>
    /// Gets the elapsed search time
    /// </summary>
    public TimeSpan ElapsedTime { get; }

    /// <summary>
    /// Gets whether the search is complete
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>
    /// Gets the current search phase
    /// </summary>
    public SearchPhase Phase { get; }

    /// <summary>
    /// Initializes a new instance of SearchProgressEventArgs
    /// </summary>
    public SearchProgressEventArgs(
        SearchQuery query,
        long matchesFound,
        long filesProcessed,
        TimeSpan elapsedTime,
        bool isComplete,
        SearchPhase phase)
    {
        Query = query;
        MatchesFound = matchesFound;
        FilesProcessed = filesProcessed;
        ElapsedTime = elapsedTime;
        IsComplete = isComplete;
        Phase = phase;
    }
}

/// <summary>
/// Types of file system changes
/// </summary>
public enum FileChangeType
{
    /// <summary>
    /// File or directory was created
    /// </summary>
    Created,

    /// <summary>
    /// File or directory was modified
    /// </summary>
    Modified,

    /// <summary>
    /// File or directory was deleted
    /// </summary>
    Deleted,

    /// <summary>
    /// File or directory was renamed or moved
    /// </summary>
    Renamed
}

/// <summary>
/// Indexing phases
/// </summary>
public enum IndexingPhase
{
    /// <summary>
    /// Initializing the indexing process
    /// </summary>
    Initializing,

    /// <summary>
    /// Scanning the file system for files and directories
    /// </summary>
    Scanning,

    /// <summary>
    /// Building the search index
    /// </summary>
    Indexing,

    /// <summary>
    /// Optimizing the index for better performance
    /// </summary>
    Optimizing,

    /// <summary>
    /// Saving the index to persistent storage
    /// </summary>
    Saving,

    /// <summary>
    /// Monitoring for file system changes
    /// </summary>
    Monitoring,

    /// <summary>
    /// Indexing process completed
    /// </summary>
    Completed,

    /// <summary>
    /// Indexing process failed
    /// </summary>
    Failed
}

/// <summary>
/// Search phases
/// </summary>
public enum SearchPhase
{
    /// <summary>
    /// Initializing the search
    /// </summary>
    Initializing,

    /// <summary>
    /// Validating the search query
    /// </summary>
    Validating,

    /// <summary>
    /// Searching the index
    /// </summary>
    SearchingIndex,

    /// <summary>
    /// Applying filters to results
    /// </summary>
    ApplyingFilters,

    /// <summary>
    /// Sorting results
    /// </summary>
    Sorting,

    /// <summary>
    /// Search completed successfully
    /// </summary>
    Completed,

    /// <summary>
    /// Search was cancelled
    /// </summary>
    Cancelled,

    /// <summary>
    /// Search failed with an error
    /// </summary>
    Failed
}