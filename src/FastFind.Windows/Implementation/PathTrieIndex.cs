using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace FastFind.Windows.Implementation;

/// <summary>
/// High-performance trie-based index for hierarchical path lookups.
/// Enables O(log n) path-based queries instead of O(n) full scans.
/// Thread-safe for concurrent read/write operations.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PathTrieIndex
{
    private readonly TrieNode _root = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private long _totalPaths;

    /// <summary>
    /// Total number of paths indexed
    /// </summary>
    public long Count => Interlocked.Read(ref _totalPaths);

    /// <summary>
    /// Adds a file path to the trie index.
    /// </summary>
    /// <param name="fullPath">The full file path to index</param>
    /// <param name="fileKey">The lowercase key used in the file index dictionary</param>
    /// <returns>True if the path was added, false if it already existed</returns>
    public bool Add(string fullPath, string fileKey)
    {
        if (string.IsNullOrEmpty(fullPath))
            return false;

        var segments = GetPathSegments(fullPath);
        if (segments.Length == 0)
            return false;

        _lock.EnterWriteLock();
        try
        {
            var node = _root;
            foreach (var segment in segments)
            {
                node = node.GetOrAddChild(segment);
            }

            // Add the file key to the leaf node
            if (node.FileKeys.Add(fileKey))
            {
                Interlocked.Increment(ref _totalPaths);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a file path from the trie index.
    /// </summary>
    /// <param name="fullPath">The full file path to remove</param>
    /// <param name="fileKey">The lowercase key used in the file index dictionary</param>
    /// <returns>True if the path was removed</returns>
    public bool Remove(string fullPath, string fileKey)
    {
        if (string.IsNullOrEmpty(fullPath))
            return false;

        var segments = GetPathSegments(fullPath);
        if (segments.Length == 0)
            return false;

        _lock.EnterWriteLock();
        try
        {
            var node = _root;
            var path = new List<(TrieNode parent, string segment, TrieNode child)>();

            // Navigate to the leaf
            foreach (var segment in segments)
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    return false;
                path.Add((node, segment, child));
                node = child;
            }

            // Remove the file key
            if (!node.FileKeys.Remove(fileKey))
                return false;

            Interlocked.Decrement(ref _totalPaths);

            // Clean up empty nodes (from leaf to root)
            for (int i = path.Count - 1; i >= 0; i--)
            {
                var (parent, segment, child) = path[i];
                if (child.FileKeys.Count == 0 && child.Children.IsEmpty)
                {
                    parent.Children.TryRemove(segment, out _);
                }
                else
                {
                    break; // Node still has children or files, stop cleanup
                }
            }

            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets all file keys under the specified path (including subdirectories).
    /// This is the key operation that enables O(k) lookups where k = files under path.
    /// </summary>
    /// <param name="basePath">The base path to search under</param>
    /// <returns>Enumerable of file keys under the path</returns>
    public IEnumerable<string> GetFileKeysUnderPath(string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            yield break;

        var segments = GetPathSegments(basePath);
        if (segments.Length == 0)
            yield break;

        _lock.EnterReadLock();
        try
        {
            // Navigate to the target node
            var node = _root;
            foreach (var segment in segments)
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    yield break; // Path not found
                node = child;
            }

            // Collect all file keys from this node and all descendants
            foreach (var fileKey in GetAllDescendantFileKeys(node))
            {
                yield return fileKey;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets file keys in the exact directory (not including subdirectories).
    /// </summary>
    /// <param name="directoryPath">The directory path</param>
    /// <returns>Enumerable of file keys in the directory</returns>
    public IEnumerable<string> GetFileKeysInDirectory(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
            yield break;

        var segments = GetPathSegments(directoryPath);
        if (segments.Length == 0)
            yield break;

        _lock.EnterReadLock();
        try
        {
            // Navigate to the target node
            var node = _root;
            foreach (var segment in segments)
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    yield break; // Path not found
                node = child;
            }

            // Return only files at this exact level
            foreach (var fileKey in node.FileKeys)
            {
                yield return fileKey;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Checks if the trie contains any files under the specified path.
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>True if the path has indexed files</returns>
    public bool ContainsPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var segments = GetPathSegments(path);
        if (segments.Length == 0)
            return false;

        _lock.EnterReadLock();
        try
        {
            var node = _root;
            foreach (var segment in segments)
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    return false;
                node = child;
            }

            // Path exists if node has files or children
            return node.FileKeys.Count > 0 || !node.Children.IsEmpty;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the count of files under the specified path.
    /// </summary>
    /// <param name="basePath">The base path</param>
    /// <returns>Number of files under the path</returns>
    public int GetFileCountUnderPath(string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return 0;

        var segments = GetPathSegments(basePath);
        if (segments.Length == 0)
            return 0;

        _lock.EnterReadLock();
        try
        {
            var node = _root;
            foreach (var segment in segments)
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    return 0;
                node = child;
            }

            return CountDescendantFiles(node);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Clears all entries from the trie.
    /// </summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _root.Children.Clear();
            _root.FileKeys.Clear();
            Interlocked.Exchange(ref _totalPaths, 0);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Recursively collects all file keys from a node and its descendants.
    /// </summary>
    private static IEnumerable<string> GetAllDescendantFileKeys(TrieNode node)
    {
        // Yield files at this node
        foreach (var fileKey in node.FileKeys)
        {
            yield return fileKey;
        }

        // Recursively yield files from children
        foreach (var child in node.Children.Values)
        {
            foreach (var fileKey in GetAllDescendantFileKeys(child))
            {
                yield return fileKey;
            }
        }
    }

    /// <summary>
    /// Counts total files in a node and all descendants.
    /// </summary>
    private static int CountDescendantFiles(TrieNode node)
    {
        var count = node.FileKeys.Count;
        foreach (var child in node.Children.Values)
        {
            count += CountDescendantFiles(child);
        }
        return count;
    }

    /// <summary>
    /// Splits a path into normalized segments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string[] GetPathSegments(string path)
    {
        // Normalize and split
        var normalized = Path.GetFullPath(path).ToLowerInvariant();
        return normalized.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <summary>
    /// Trie node for storing path segments and file keys.
    /// Uses concurrent collections for thread-safe operations.
    /// </summary>
    private sealed class TrieNode
    {
        /// <summary>
        /// Child nodes keyed by path segment (case-insensitive)
        /// </summary>
        public ConcurrentDictionary<string, TrieNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// File keys stored at this node (files in this directory)
        /// </summary>
        public HashSet<string> FileKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or creates a child node for the given segment.
        /// </summary>
        public TrieNode GetOrAddChild(string segment)
        {
            return Children.GetOrAdd(segment, _ => new TrieNode());
        }
    }
}
