using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// The suggestion strip of the quick tag surface from the offline context ranking (<see cref="TagContextSuggestions.Rank"/>):
/// the same thread, neighbours of the shown list, files with the same bytes, words of the file name and title, recent use.
/// It is an adapter only: each <see cref="RankedTagSuggestion"/> becomes one <see cref="QuickTagSuggestion"/> (tag, score,
/// strongest reason, uses). Nothing is assigned or created here; the surface does that when a chip is chosen.
/// The index of a large tag store is built off the UI thread (<see cref="Prewarm"/>); until it exists the strip keeps its
/// last suggestions (or the most used tags), so no key press or write ever waits for the build, and <see cref="Warmed"/>
/// asks the surface to repaint once the context ranking is ready.
/// <para>
/// Self-contained on purpose: the only users are the three construction sites of the popover hosts and the type test in
/// <c>QuickTagPopover.Create</c>. The panel of TP2 brings its own adapter (<c>GalleryTagPanelSuggestions</c>); at integration this
/// class can be dropped by switching those sites (and the <c>Warmed</c>/<c>Attach</c> wiring) to that adapter.
/// </para>
/// </summary>
internal sealed class QuickTagContextSource : IQuickTagSuggestionSource, IDisposable
{
    /// <summary>
    /// A store with at most this many tags and assignments builds its context index on the calling thread (well under a
    /// millisecond); a larger one is built in the background and the strip falls back meanwhile. A technical measure, not a limit.
    /// </summary>
    internal const int SynchronousBuildLimit = 4096;

    /// <summary>The recently used tags of this app session, shared by every popover and panel (Q3 of the plan).</summary>
    internal static TagRecentList SessionRecent { get; } = new();

    private readonly TagRecentList _recent;
    private readonly TagContextOptions _options;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<QuickTagSuggestion> _last = Array.Empty<QuickTagSuggestion>();
    private PrivateTagStoreSnapshot? _warming;
    private bool _disposed;

    /// <param name="context">What is known about the medium; replace it with <see cref="Context"/> when the shown medium changes.</param>
    /// <param name="recent">The recent-use list; null shares <see cref="SessionRecent"/>.</param>
    /// <param name="options">The limits of the ranking (settings of TP5); null uses the defaults.</param>
    /// <param name="post">How <see cref="Warmed"/> reaches the UI thread; tests pass a direct call.</param>
    internal QuickTagContextSource(TagContext? context = null, TagRecentList? recent = null, TagContextOptions? options = null,
        Action<Action>? post = null)
    {
        Context = context ?? TagContext.Empty;
        _recent = recent ?? SessionRecent;
        _options = options ?? TagContextOptions.Default;
        _post = post ?? (action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
    }

    /// <summary>The context of the shown medium. <see cref="TagContext.Recent"/> is filled from the recent list on every call.</summary>
    internal TagContext Context { get; set; }

    /// <summary>Raised on the UI thread after a background index build finished: the surface refreshes its strip.</summary>
    internal event Action? Warmed;

    /// <summary>Feeds the recent-use list from every change the session accepts (an assignment, never a removal or an undo).</summary>
    internal void Attach(QuickTagSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Applied += change =>
        {
            if (change.Assigned) _recent.Touch(change.Tag);
        };
    }

    public IReadOnlyList<QuickTagSuggestion> Suggest(QuickTagSuggestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PrivateTagStoreSnapshot snapshot = request.Snapshot;
        if (!TagContextIndex.IsBuilt(snapshot) && !IsSmall(snapshot))
        {
            Prewarm(snapshot);
            return Fallback(request);
        }
        TagContext context = Context with { Recent = _recent.Snapshot() };
        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, context, request.Excluded, _options, limit: request.Limit);
        _last = ranked.Select(Map).ToArray();
        return _last;
    }

    /// <summary>Builds the context index of the snapshot on a worker (once per snapshot); <see cref="Warmed"/> follows.</summary>
    internal void Prewarm(PrivateTagStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_disposed || TagContextIndex.IsBuilt(snapshot) || ReferenceEquals(_warming, snapshot)) return;
        _warming = snapshot;
        _ = WarmAsync(snapshot);
    }

    /// <summary>The background build itself, awaitable for tests; a cancelled or failed build only means the strip keeps its fallback.</summary>
    internal async Task WarmAsync(PrivateTagStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CancellationToken token = _lifetime.Token;
        try { await Task.Run(() => TagContextIndex.For(snapshot, token), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Serilog.Log.Warning(error, "The context index of the quick tag suggestions could not be built; the strip keeps its fallback.");
            return;
        }
        if (_disposed) return;
        _post(() =>
        {
            if (!_disposed) Warmed?.Invoke();
        });
    }

    private static bool IsSmall(PrivateTagStoreSnapshot snapshot) =>
        (long)snapshot.Tags.Count + snapshot.MediaAssignments.Count + snapshot.LocalAssignments.Count + snapshot.ThreadAssignments.Count <= SynchronousBuildLimit;

    // The last ranking without what the target carries now, or the most used tags when there was none yet: the strip never
    // blanks out or waits while the index of a new snapshot is built.
    private IReadOnlyList<QuickTagSuggestion> Fallback(QuickTagSuggestionRequest request)
    {
        QuickTagSuggestion[] kept = _last.Where(item => item.Id is not { } id
            || !request.Excluded.Contains(id) && request.Snapshot.Find(id) != null).Take(request.Limit).ToArray();
        return kept.Length > 0 ? kept : FrequentQuickTagSuggestions.Instance.Suggest(request);
    }

    /// <summary>One ranked suggestion as the strip shows it: the strongest contribution names the reason.</summary>
    internal static QuickTagSuggestion Map(RankedTagSuggestion item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new QuickTagSuggestion(item.Name, item.Id, item.Score, Map(item.PrimaryReason), item.Uses);
    }

    /// <summary>The strip knows eight reasons; the ranking's ten collapse onto them (thread tag and thread files, title word and board marker).</summary>
    internal static QuickTagReason Map(TagSuggestionReason reason) => reason switch
    {
        TagSuggestionReason.SameFile => QuickTagReason.SameFile,
        TagSuggestionReason.ThreadTag or TagSuggestionReason.ThreadMedia => QuickTagReason.Thread,
        TagSuggestionReason.SourceTag => QuickTagReason.SourceTag,
        TagSuggestionReason.Recent => QuickTagReason.Recent,
        TagSuggestionReason.Neighbour => QuickTagReason.Neighbour,
        TagSuggestionReason.FileName => QuickTagReason.FileName,
        TagSuggestionReason.ThreadTitle or TagSuggestionReason.SlashMarker => QuickTagReason.Title,
        _ => QuickTagReason.Frequent
    };

    // ---- context of a menu origin -------------------------------------------------------------------------------------

    /// <summary>
    /// The context of a media page tile: its thread (and title), file name and bytes, and the tag subjects of the shown tiles
    /// around it. <paramref name="selection"/> drops the bytes (a selection has no single file).
    /// </summary>
    internal static TagContext ContextOf(MediaLibraryEntry entry, IReadOnlyList<MediaLibraryEntry>? shown, int index, bool selection = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new TagContext
        {
            Thread = entry.IsCatalogMedium ? null : entry.Thread,
            Sha256 = selection ? null : entry.Sha256,
            FileName = entry.Name,
            TitleTexts = new[] { entry.ThreadTitle },
            Neighbours = shown == null ? TagNeighbourhood.None : Neighbourhood(shown, index, item => MediaTagSubjects.Of(item))
        };
    }

    /// <summary>The context of an archive or reader file: its thread, title, file name and bytes, and the visible files around it.</summary>
    internal static TagContext ContextOf(GalleryEntry entry, IReadOnlyList<GalleryEntry>? shown, string? threadTitle)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int index = shown == null ? -1 : IndexOf(shown, entry);
        return new TagContext
        {
            Thread = BooruMediaIdentity.IsBooruThread(entry.Key.Thread) ? null : entry.Key.Thread,
            Sha256 = entry.Asset.Sha256,
            FileName = entry.Asset.OriginalFileName ?? System.IO.Path.GetFileName(entry.Asset.Path),
            TitleTexts = new[] { threadTitle, entry.Post?.Subject },
            Neighbours = shown == null ? TagNeighbourhood.None : Neighbourhood(shown, index, item => MediaTagSubjects.Of(item))
        };
    }

    /// <summary>
    /// The tag subjects of the list around <paramref name="index"/> (at most <paramref name="radius"/> each way), frozen into an
    /// array, so the ranking may read it on any thread while the list changes.
    /// </summary>
    internal static TagNeighbourhood Neighbourhood<T>(IReadOnlyList<T> list, int index, Func<T, MediaTagSubject?> subjectOf,
        int radius = TagContextOptions.DefaultNeighbourScan)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(subjectOf);
        if (index < 0 || index >= list.Count) return TagNeighbourhood.None;
        int from = Math.Max(0, index - radius), to = Math.Min(list.Count, index + radius + 1);
        MediaTagSubject?[] subjects = new MediaTagSubject?[to - from];
        for (int at = from; at < to; at++) subjects[at - from] = at == index ? null : subjectOf(list[at]);
        return new TagNeighbourhood(subjects.Length, index - from, position => subjects[position]);
    }

    private static int IndexOf(IReadOnlyList<GalleryEntry> list, GalleryEntry entry)
    {
        for (int index = 0; index < list.Count; index++)
            if (ReferenceEquals(list[index], entry) || list[index].EntryKey == entry.EntryKey) return index;
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Warmed = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
