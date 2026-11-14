namespace FastFind.Indexing;

/// <summary>
/// A path's attributes read with one file-system call, for a caller that needs nothing else.
/// </summary>
internal static class DirectAttributes
{
    /// <summary>
    /// The attributes of <paramref name="path"/> (a file or a directory), or <c>null</c> when they cannot be read:
    /// it is gone, the path is not valid on this system, or the account may not look.
    /// </summary>
    public static FileAttributes? Read(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
