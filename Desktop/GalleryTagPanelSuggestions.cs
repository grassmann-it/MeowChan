using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Plugs the offline context ranking of TP0 (<see cref="TagContextSuggestions.Rank"/>) into the suggestion strip of the quick
/// tag surface (<see cref="IQuickTagSuggestionSource"/>), mapping <see cref="RankedTagSuggestion"/> onto
/// <see cref="QuickTagSuggestion"/> and <see cref="TagSuggestionReason"/> onto <see cref="QuickTagReason"/>.
/// <para>
/// The surface asks on the UI thread after every change and medium switch. The per-snapshot indexes behind the ranking take
/// about 190 ms to build for a large store, so the adapter never builds them there: <see cref="Warm"/> builds them on a worker
/// (cancelled with <see cref="Dispose"/> when the panel closes), and until a snapshot is warm <see cref="Suggest"/> answers
/// from the last ranking (minus what is carried now) or, before any ranking exists, from the plain frequency source.
/// <see cref="Ready"/> tells the host to refresh the strip when a warm-up finished.
/// </para>
/// </summary>
internal sealed class GalleryTagPanelSuggestions : IQuickTagSuggestionSource, IDisposable
{
    private readonly object _gate = new();
    private readonly IQuickTagSuggestionSource _fallback;
    private readonly Action<PrivateTagStoreSnapshot, CancellationToken> _build;
    private TagContextOptions _options;
    private TagContext _context = TagContext.Empty;
    private Func<IReadOnlyList<TagId>>? _recent;
    private WeakReference<PrivateTagStoreSnapshot>? _warm;
    private WeakReference<PrivateTagStoreSnapshot>? _pending;
    // Cancellation authority only. Each worker owns and disposes its source.
    private Action? _cancelWarming;
    private IReadOnlyList<QuickTagSuggestion> _last = Array.Empty<QuickTagSuggestion>();
    private bool _disposed;

    /// <param name="options">Limits of the ranking (the settings of TP5 bind to them).</param>
    /// <param name="fallback">Answer while nothing was ranked yet; the frequency source by default.</param>
    /// <param name="build">The warm-up work; a seam for tests. Default: the context index and the two per-snapshot name indexes.</param>
    internal GalleryTagPanelSuggestions(TagContextOptions? options = null, IQuickTagSuggestionSource? fallback = null,
        Action<PrivateTagStoreSnapshot, CancellationToken>? build = null)
    {
        _options = options ?? TagContextOptions.Default;
        _fallback = fallback ?? FrequentQuickTagSuggestions.Instance;
        _build = build ?? BuildIndexes;
    }

    /// <summary>Raised on a worker thread when a warm-up finished or the context changed asynchronously: the strip should be asked again.</summary>
    internal event Action? Ready;

    /// <summary>What is known about the medium now shown; replaced at every medium switch (immutable, any thread may read).</summary>
    internal TagContext Context
    {
        get => Volatile.Read(ref _context);
        set => Volatile.Write(ref _context, value ?? TagContext.Empty);
    }

    internal TagContextOptions Options
    {
        get { lock (_gate) return _options; }
        set { lock (_gate) _options = value ?? TagContextOptions.Default; }
    }

    /// <summary>The recently used tags, most recent first, read at every ranking so a change just made counts at once.</summary>
    internal Func<IReadOnlyList<TagId>>? Recent
    {
        get { lock (_gate) return _recent; }
        set { lock (_gate) _recent = value; }
    }

    /// <summary>The managed thread that ran the last warm-up (tests and diagnostics); 0 before any.</summary>
    internal int WarmThreadId { get; private set; }
    internal Task WarmTask { get; private set; } = Task.CompletedTask;

    internal bool IsWarm(PrivateTagStoreSnapshot snapshot)
    {
        lock (_gate) return _warm != null && _warm.TryGetTarget(out PrivateTagStoreSnapshot? known) && ReferenceEquals(known, snapshot);
    }

    /// <summary>Starts building the indexes of <paramref name="snapshot"/> on a worker; a newer snapshot cancels an older warm-up.</summary>
    internal void Warm(PrivateTagStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_disposed) return;
            if (_warm != null && _warm.TryGetTarget(out PrivateTagStoreSnapshot? done) && ReferenceEquals(done, snapshot)) return;
            if (_pending != null && _pending.TryGetTarget(out PrivateTagStoreSnapshot? running) && ReferenceEquals(running, snapshot)) return;
            _cancelWarming?.Invoke();
            WarmTask = WarmAsync(snapshot);
        }
    }

    // Called under _gate; admission runs synchronously, disposal spans the actual worker.
    private async Task WarmAsync(PrivateTagStoreSnapshot snapshot)
    {
        using CancellationTokenSource warming = new();
        Action cancel = warming.Cancel;
        _cancelWarming = cancel;
        _pending = new WeakReference<PrivateTagStoreSnapshot>(snapshot);
        CancellationToken token = warming.Token;
        try { await Task.Run(() => RunWarm(snapshot, token), CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancelWarming, cancel)) _cancelWarming = null;
            }
        }
    }

    private void RunWarm(PrivateTagStoreSnapshot snapshot, CancellationToken token)
    {
        try
        {
            WarmThreadId = Environment.CurrentManagedThreadId;
            token.ThrowIfCancellationRequested();
            _build(snapshot, token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The strip keeps answering from the cold path; nothing here may take the app down.
            lock (_gate) { if (_pending != null && _pending.TryGetTarget(out PrivateTagStoreSnapshot? failed) && ReferenceEquals(failed, snapshot)) _pending = null; }
            Serilog.Log.Warning(error, "Warming the tag context index failed; suggestions stay on the frequency list.");
            return;
        }
        lock (_gate)
        {
            if (_disposed || token.IsCancellationRequested) return;
            _warm = new WeakReference<PrivateTagStoreSnapshot>(snapshot);
            _pending = null;
        }
        Ready?.Invoke();
    }

    /// <summary>Tells the strip to ask again (the neighbour subjects finished filling in the background).</summary>
    internal void NotifyReady()
    {
        lock (_gate) { if (_disposed) return; }
        Ready?.Invoke();
    }

    private static void BuildIndexes(PrivateTagStoreSnapshot snapshot, CancellationToken token)
    {
        TagContextIndex.For(snapshot, token);
        token.ThrowIfCancellationRequested();
        TagSuggestionIndex.UsesOf(snapshot);
        TagSuggestionIndex.For(snapshot);
    }

    public IReadOnlyList<QuickTagSuggestion> Suggest(QuickTagSuggestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PrivateTagStoreSnapshot snapshot = request.Snapshot;
        if (!IsWarm(snapshot))
        {
            Warm(snapshot);
            return Stale(request);
        }
        TagContextOptions options;
        Func<IReadOnlyList<TagId>>? recent;
        lock (_gate) { options = _options; recent = _recent; }
        TagContext context = Context;
        if (recent != null) context = context with { Recent = recent() };
        QuickTagSuggestion[] mapped = TagContextSuggestions.Rank(snapshot, context, request.Excluded, options, null, request.Limit)
            .Select(Map).ToArray();
        lock (_gate) _last = mapped;
        return mapped;
    }

    // Until the new snapshot is warm: the last ranking without what the target carries now and without tags that are gone. Never builds an index.
    private IReadOnlyList<QuickTagSuggestion> Stale(QuickTagSuggestionRequest request)
    {
        IReadOnlyList<QuickTagSuggestion> last;
        lock (_gate) last = _last;
        if (last.Count == 0) return _fallback.Suggest(request);
        return last.Where(item => item.Id is not { } id || !request.Excluded.Contains(id) && request.Snapshot.Find(id) != null)
            .Take(Math.Max(0, request.Limit)).ToArray();
    }

    internal static QuickTagSuggestion Map(RankedTagSuggestion ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        return new QuickTagSuggestion(ranked.Name, ranked.Id, ranked.Score, MapReason(ranked.PrimaryReason), ranked.Uses);
    }

    /// <summary>The surface knows eight reasons; the thread reasons and the two title reasons share one text each.</summary>
    internal static QuickTagReason MapReason(TagSuggestionReason reason) => reason switch
    {
        TagSuggestionReason.SameFile => QuickTagReason.SameFile,
        TagSuggestionReason.ThreadTag or TagSuggestionReason.ThreadMedia => QuickTagReason.Thread,
        TagSuggestionReason.SourceTag => QuickTagReason.SourceTag,
        TagSuggestionReason.Recent => QuickTagReason.Recent,
        TagSuggestionReason.Neighbour => QuickTagReason.Neighbour,
        TagSuggestionReason.FileName => QuickTagReason.FileName,
        TagSuggestionReason.ThreadTitle or TagSuggestionReason.SlashMarker => QuickTagReason.Title,
        TagSuggestionReason.Frequent => QuickTagReason.Frequent,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    /// <summary>Stops a running warm-up (the panel closed); later calls answer from the cold path and start nothing.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Serialize cancellation with the worker's final disposal; never dispose while _build still uses the token.
            _cancelWarming?.Invoke();
            _cancelWarming = null;
            _pending = null;
        }
        Ready = null;
    }
}

/// <summary>
/// The tag subjects of the gallery list around the current medium, for the neighbour source (Q2). A private copy of the list is
/// mapped once on a worker; until that finished a neighbour has no subject and just contributes nothing, so the UI thread
/// never maps the list.
/// </summary>
internal sealed class GalleryTagNeighbourSubjects
{
    private readonly GalleryEntry[] _entries;
    private readonly Func<GalleryEntry, MediaTagSubject?> _subject;
    private MediaTagSubject?[]? _subjects;

    internal GalleryTagNeighbourSubjects(IReadOnlyList<GalleryEntry> entries, Func<GalleryEntry, MediaTagSubject?>? subject = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToArray();
        _subject = subject ?? (entry => MediaTagSubjects.Of(entry));
    }

    internal int Count => _entries.Length;
    internal bool IsFilled => Volatile.Read(ref _subjects) != null;

    /// <summary>The position of an entry in the copied list, or -1.</summary>
    internal int IndexOf(GalleryEntry entry)
    {
        for (int index = 0; index < _entries.Length; index++)
            if (_entries[index].EntryKey == entry.EntryKey) return index;
        return -1;
    }

    /// <summary>Maps the list (worker thread). Returns false when cancelled.</summary>
    internal bool Fill(CancellationToken token)
    {
        MediaTagSubject?[] subjects = new MediaTagSubject?[_entries.Length];
        for (int index = 0; index < subjects.Length; index++)
        {
            if ((index & 255) == 0 && token.IsCancellationRequested) return false;
            subjects[index] = _subject(_entries[index]);
        }
        Volatile.Write(ref _subjects, subjects);
        return true;
    }

    internal TagNeighbourhood Around(int index) => new(_entries.Length, index, at =>
        Volatile.Read(ref _subjects) is { } subjects && (uint)at < (uint)subjects.Length ? subjects[at] : null);
}
