namespace FastFind.Interfaces;

/// <summary>
/// The file an index store was asked to open is not a usable index: it is not a database at all, or it
/// has been cut short or otherwise damaged. Raised by opening the store, and also by an operation on a store
/// that was fine when it was opened and has been damaged since (a read, a write or an enumeration that meets
/// a page that is not what it was).
/// </summary>
/// <remarks>
/// <para>
/// An index is derived from the file system, so a damaged store loses nothing that cannot be rebuilt:
/// delete the file (and any <c>-wal</c> / <c>-shm</c> files beside it) and index again, or restore it from
/// a copy. The library does not delete it for you — it is a file the caller named, and whether a damaged
/// copy should be kept for inspection is the caller's call.
/// </para>
/// <para>
/// The store's own driver exception is the <see cref="Exception.InnerException"/>, for diagnosis. It is
/// deliberately not what a caller catches: it belongs to a library that <c>FastFind.Core</c> does not
/// reference, and which one it is depends on the store.
/// </para>
/// </remarks>
public sealed class IndexStoreCorruptedException : IOException
{
    /// <summary>Creates the exception for the store at <paramref name="storePath"/>.</summary>
    /// <param name="storePath">The path of the store that could not be read.</param>
    /// <param name="innerException">The store driver's exception.</param>
    public IndexStoreCorruptedException(string storePath, Exception innerException)
        : base(BuildMessage(storePath, innerException), innerException)
    {
        StorePath = storePath;
    }

    /// <summary>The path of the store that could not be read.</summary>
    public string StorePath { get; }

    private static string BuildMessage(string storePath, Exception innerException) =>
        $"The index store at '{storePath}' is not a usable index ({innerException.Message}). " +
        "An index is derived from the file system: delete the file (and any -wal and -shm files beside it) " +
        "and index again, or restore it from a copy.";
}
