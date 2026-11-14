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

    private readonly Dictionary<ulong, (ulong Parent, string Name, FileAttributes Attributes)> _directories = new();
    private readonly Dictionary<ulong, string> _resolvedDirectories = new();
    // A directory's depth is the number of segments in its path beneath the volume root (the root is 0).
    // _skipDepth holds, for a directory with a skipped directory at or above it, the depth of the DEEPEST such one.
    private readonly Dictionary<ulong, int> _depth = new();
    private readonly Dictionary<ulong, int> _skipDepth = new();
    private readonly FileAttributes _skipDirectories;
    private readonly Stack<ulong> _pending = new();

    /// <param name="volumeRoot">The path of the volume's root, such as <c>D:\</c>.</param>
    /// <param name="skipDirectories">
    /// Attributes that make a directory one that is not entered (see <c>IndexingAdmission.AttributesToSkip</c>):
    /// <see cref="TryResolve(in MftFileRecord, out string?, out int)"/> reports where the deepest such directory
    /// above an entry lies. Nothing by default.
    /// </param>
    public MftPathResolver(string volumeRoot, FileAttributes skipDirectories = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeRoot);
        _resolvedDirectories[RootRecordNumber] = volumeRoot;
        _depth[RootRecordNumber] = 0;
        _skipDirectories = skipDirectories;
    }

    /// <summary>Makes a directory available as an ancestor of entries resolved from now on.</summary>
    public void AddDirectory(in MftFileRecord record)
    {
        var recordNumber = record.GetRecordNumber();
        if (recordNumber == RootRecordNumber) return;

        _directories[recordNumber] = (MftFileRecord.ExtractRecordNumber(record.ParentFileReferenceNumber), record.FileName, record.Attributes);
    }

    /// <summary>
    /// Resolves an entry's full path if every ancestor up to the volume root is known.
    /// </summary>
    /// <returns>
    /// <c>false</c> when some ancestor has not been seen, or the chain loops. The entry may resolve
    /// later, once more directories have been added.
    /// </returns>
    public bool TryResolve(in MftFileRecord record, [NotNullWhen(true)] out string? path) =>
        TryResolve(record, out path, out _);

    /// <summary>
    /// Resolves an entry's full path and reports the <b>depth of the deepest hidden or system directory above
    /// it</b> (as the constructor was told which attributes to skip), 0 when there is none. Whether that makes the
    /// entry one a pass would not read depends on where the pass started: a directory walk begins in its
    /// location and does not look above it, so only a skipped directory <em>beneath the location</em> counts —
    /// <c>skippedDepth</c> greater than the location's own depth.
    /// </summary>
    public bool TryResolve(in MftFileRecord record, [NotNullWhen(true)] out string? path, out int skippedDepth)
    {
        var recordNumber = record.GetRecordNumber();
        var parent = MftFileRecord.ExtractRecordNumber(record.ParentFileReferenceNumber);

        if (_resolvedDirectories.TryGetValue(recordNumber, out path))
        {
            skippedDepth = _skipDepth.GetValueOrDefault(parent);
            return true;
        }

        if (!TryResolveDirectory(parent, recordNumber, out var parentPath))
        {
            path = null;
            skippedDepth = 0;
            return false;
        }

        path = Path.Join(parentPath, record.FileName);
        skippedDepth = _skipDepth.GetValueOrDefault(parent);
        if (record.IsDirectory)
        {
            _resolvedDirectories[recordNumber] = path;
            var depth = _depth[parent] + 1;
            _depth[recordNumber] = depth;
            if ((record.Attributes & _skipDirectories) != 0)
                _skipDepth[recordNumber] = depth;
            else if (skippedDepth > 0)
                _skipDepth[recordNumber] = skippedDepth;
        }

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

        var depth = _depth[current];
        var skipped = _skipDepth.GetValueOrDefault(current);
        while (_pending.TryPop(out var descendant))
        {
            var entry = _directories[descendant];
            path = Path.Join(path, entry.Name);
            _resolvedDirectories[descendant] = path;

            depth++;
            _depth[descendant] = depth;
            if ((entry.Attributes & _skipDirectories) != 0)
                skipped = depth;
            if (skipped > 0)
                _skipDepth[descendant] = skipped;
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
    /// volume under a guessed path. <c>SkippedDepth</c> is what <see cref="TryResolve(in MftFileRecord, out string?, out int)"/>
    /// reports; the caller, which knows the locations, decides what it means.
    /// </remarks>
    internal static async IAsyncEnumerable<(MftFileRecord Record, string Path, int SkippedDepth)> ResolveAsync(
        IAsyncEnumerable<MftFileRecord> records,
        string volumeRoot,
        StrongBox<int> unresolved,
        FileAttributes skipDirectories = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolver = new MftPathResolver(volumeRoot, skipDirectories);
        var directories = new List<MftFileRecord>();
        var waiting = new List<MftFileRecord>();

        await foreach (var record in records.WithCancellation(cancellationToken))
        {
            if (record.IsDirectory)
            {
                resolver.AddDirectory(record);
                directories.Add(record);
            }
            else if (resolver.TryResolve(record, out var path, out var skipped))
            {
                yield return (record, path, skipped);
            }
            else
            {
                waiting.Add(record);
            }
        }

        foreach (var record in waiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolver.TryResolve(record, out var path, out var skipped)) yield return (record, path, skipped);
            else unresolved.Value++;
        }

        foreach (var record in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolver.TryResolve(record, out var path, out var skipped)) yield return (record, path, skipped);
            else unresolved.Value++;
        }
    }
}
