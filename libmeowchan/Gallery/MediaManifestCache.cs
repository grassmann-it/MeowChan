using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Gallery;

/// <summary>
/// A manifest view and, when it could be pinned, the identity of the manifest file whose bytes it was read from
/// (see <see cref="RegularProfileFile.TryGetIdentity(string)"/>): equal identity later means equal bytes, so the view and its
/// generation can be reused without reading and hashing the file again. Null identity: never reused by identity.
/// </summary>
internal sealed record MediaManifestRead(MediaAssetSnapshot Snapshot, RegularProfileFileIdentity? Identity);

/// <summary>
/// The media page's manifest views, shared by thumbnails, the sound check and the index build. A view read in the
/// current round (since the last <see cref="Invalidate"/>) is returned as is, like the page always did. A view from an
/// earlier round is returned again only when its manifest file still has the identity it had while it was read; anything
/// else is read again. Only views the page itself asked for are kept: the index build looks views up but does not add
/// its own reads, so building over 200,000 files does not pin every manifest in memory.
/// Identity checks are only used for the real manifest reader; a custom opener (tests, fakes) gets the earlier "until the
/// next round" behavior and no identity at all. The real reader here is the preview reader
/// (<see cref="MediaAssetReader.OpenForPreviewAsync"/>): the page's tiles and sound check never read a folder source file in
/// full just to learn its identity (W3-02); the gallery and menu actions open manifests themselves.
/// </summary>
internal sealed class MediaManifestCache
{
    private readonly Func<string, CancellationToken, Task<MediaAssetSnapshot>> _open;
    private readonly bool _identity;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private long _round;

    private sealed record Slot(Task<MediaManifestRead> Read, long Round);

    /// <param name="open">Reads one manifest; null means the real (preview) reader with file identity checks.</param>
    /// <param name="lifetime">Ends the reads the cache keeps; a caller's own token only ends its wait.</param>
    /// <param name="identity">Whether file identities validate views; by default only for the real reader.</param>
    internal MediaManifestCache(Func<string, CancellationToken, Task<MediaAssetSnapshot>>? open = null, CancellationToken lifetime = default,
        bool? identity = null)
    {
        _open = open ?? MediaAssetReader.OpenForPreviewAsync;
        _identity = identity ?? open == null;
        _lifetime = lifetime;
    }

    /// <summary>Whether this cache pins views to manifest file identities.</summary>
    internal bool UsesIdentity => _identity;

    /// <summary>Files may have changed: earlier views must prove their identity before they are used again.</summary>
    internal void Invalidate()
    {
        lock (_slots) _round++;
    }

    /// <summary>
    /// The identity the manifest file has right now, or null when it cannot be pinned (missing, not a regular file, an
    /// unsupported platform, a custom opener). Only the current manifest name counts: a folder that still has only the
    /// legacy manifest is always read.
    /// </summary>
    internal RegularProfileFileIdentity? Probe(string directory)
    {
        if (!_identity || string.IsNullOrWhiteSpace(directory)) return null;
        try { return RegularProfileFile.TryGetIdentity(Path.Combine(Path.GetFullPath(directory), MediaStore.ManifestFileName)); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The page's own use (thumbnails, sound check, menus): kept until it is no longer valid.</summary>
    internal async Task<MediaAssetSnapshot> OpenAsync(string directory, CancellationToken cancellationToken = default) =>
        (await GetAsync(directory, keep: true).WaitAsync(cancellationToken).ConfigureAwait(false)).Snapshot;

    /// <summary>The index build's use: a valid kept view or a fresh read, which is not kept.</summary>
    internal Task<MediaManifestRead> ReadAsync(string directory, CancellationToken cancellationToken = default) =>
        GetAsync(directory, keep: false, cancellationToken).WaitAsync(cancellationToken);

    private Task<MediaManifestRead> GetAsync(string directory, bool keep, CancellationToken cancellationToken = default)
    {
        lock (_slots)
        {
            if (_slots.TryGetValue(directory, out Slot? slot))
            {
                if (slot.Round == _round) return slot.Read;
                // An earlier round's view is checked off the caller's thread (a stat can be slow on a network share).
                Slot checkedSlot = new(_identity ? RevalidateAsync(directory, slot.Read) : ReadFreshAsync(directory, _lifetime), _round);
                _slots[directory] = checkedSlot;
                return checkedSlot.Read;
            }
            if (!keep) return ReadFreshAsync(directory, cancellationToken);
            Slot fresh = new(ReadFreshAsync(directory, _lifetime), _round);
            _slots[directory] = fresh;
            return fresh.Read;
        }
    }

    private async Task<MediaManifestRead> RevalidateAsync(string directory, Task<MediaManifestRead> earlier)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // never on the caller's (UI) thread
        if (earlier.IsCompletedSuccessfully && earlier.Result.Identity is { } identity && Probe(directory) == identity) return earlier.Result;
        return await ReadFreshAsync(directory, _lifetime).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the manifest. Its identity is recorded only when the file kept the same identity across the whole read and
    /// the read produced a valid manifest, so a recorded identity always names exactly the bytes behind the view.
    /// </summary>
    internal async Task<MediaManifestRead> ReadFreshAsync(string directory, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // the stat and the read stay off the UI thread
        RegularProfileFileIdentity? before = Probe(directory);
        MediaAssetSnapshot snapshot = await _open(directory, cancellationToken).ConfigureAwait(false);
        RegularProfileFileIdentity? identity = before is { } stable && snapshot is { Status: MediaManifestStatus.Available, Generation: not null }
            && Probe(directory) == stable ? stable : null;
        return new(snapshot, identity);
    }
}
