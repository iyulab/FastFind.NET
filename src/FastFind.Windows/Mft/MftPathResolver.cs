using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace FastFind.Windows.Mft;

/// <summary>
/// Turns journal records into full paths for one volume, in whatever order the records arrive.
/// </summary>
/// <remarks>
/// <para>
/// A journal record names only its own entry and its parent's file reference. A full path needs
/// every ancestor, and journal enumeration runs in file-reference order, which does not put a
/// directory before what it contains: a directory can carry a higher reference than files beneath
/// it. A path is therefore produced only from a chain that reaches the volume root; an entry whose
/// chain is still incomplete is not guessed at, it waits.
/// </para>
/// <para>
/// One instance per volume. File reference numbers are per volume — every NTFS volume's root is
/// record 5 — so sharing resolved paths between volumes would place one drive's directories under
/// another's.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MftPathResolver
{
    /// <summary>The record number of every NTFS volume's root directory.</summary>
    internal const ulong RootRecordNumber = 5;

    private readonly Dictionary<ulong, (ulong Parent, string Name)> _directories = new();
    private readonly Dictionary<ulong, string> _resolvedDirectories = new();
    private readonly Stack<ulong> _pending = new();

    public MftPathResolver(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);
        _resolvedDirectories[RootRecordNumber] = volumeRoot;
    }

    /// <summary>Makes a directory available as an ancestor of entries resolved from now on.</summary>
    public void AddDirectory(in MftFileRecord record)
    {
        var recordNumber = record.GetRecordNumber();
        if (recordNumber == RootRecordNumber) return;

        _directories[recordNumber] = (MftFileRecord.ExtractRecordNumber(record.ParentFileReferenceNumber), record.FileName);
    }

    /// <summary>
    /// Resolves an entry's full path if every ancestor up to the volume root is known.
    /// </summary>
    /// <returns>
    /// <c>false</c> when some ancestor has not been seen, or the chain loops. The entry may resolve
    /// later, once more directories have been added.
    /// </returns>
    public bool TryResolve(in MftFileRecord record, [NotNullWhen(true)] out string? path)
    {
        var recordNumber = record.GetRecordNumber();
        if (_resolvedDirectories.TryGetValue(recordNumber, out path))
            return true;

        if (!TryResolveDirectory(MftFileRecord.ExtractRecordNumber(record.ParentFileReferenceNumber), recordNumber, out var parentPath))
        {
            path = null;
            return false;
        }

        path = Path.Join(parentPath, record.FileName);
        if (record.IsDirectory)
            _resolvedDirectories[recordNumber] = path;

        return true;
    }

    /// <summary>
    /// Resolves a directory by walking up to the nearest resolved ancestor, then caching every
    /// directory on the way back down so the next entry beneath any of them is one lookup.
    /// </summary>
    private bool TryResolveDirectory(ulong directory, ulong start, [NotNullWhen(true)] out string? path)
    {
        _pending.Clear();
        var current = directory;

        while (!_resolvedDirectories.TryGetValue(current, out path))
        {
            // An unseen ancestor, or a chain that comes back to where it started or exceeds the
            // number of known directories — which can only mean it loops.
            if (current == start
                || _pending.Count > _directories.Count
                || !_directories.TryGetValue(current, out var entry))
            {
                path = null;
                return false;
            }

            _pending.Push(current);
            current = entry.Parent;
        }

        while (_pending.TryPop(out var descendant))
        {
            path = Path.Join(path, _directories[descendant].Name);
            _resolvedDirectories[descendant] = path;
        }

        return true;
    }

    /// <summary>
    /// Pairs each record of one volume with its full path, whatever order the records arrive in.
    /// </summary>
    /// <remarks>
    /// Files whose chain is already complete stream out as they arrive. The rest wait until the
    /// enumeration ends and every directory is known, which costs memory only for those. Directories
    /// come out last, when all of them can be placed. An entry that still cannot be placed — its
    /// ancestor was never enumerated, as for everything beneath a skipped <c>$</c> metadata
    /// directory — is dropped and counted in <paramref name="unresolved"/>, never rooted at the
    /// volume under a guessed path.
    /// </remarks>
    internal static async IAsyncEnumerable<(MftFileRecord Record, string Path)> ResolveAsync(
        IAsyncEnumerable<MftFileRecord> records,
        string volumeRoot,
        StrongBox<int> unresolved,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolver = new MftPathResolver(volumeRoot);
        var directories = new List<MftFileRecord>();
        var waiting = new List<MftFileRecord>();

        await foreach (var record in records.WithCancellation(cancellationToken))
        {
            if (record.IsDirectory)
            {
                resolver.AddDirectory(record);
                directories.Add(record);
            }
            else if (resolver.TryResolve(record, out var path))
            {
                yield return (record, path);
            }
            else
            {
                waiting.Add(record);
            }
        }

        foreach (var record in waiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolver.TryResolve(record, out var path)) yield return (record, path);
            else unresolved.Value++;
        }

        foreach (var record in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolver.TryResolve(record, out var path)) yield return (record, path);
            else unresolved.Value++;
        }
    }
}
