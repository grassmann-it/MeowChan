using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Inline video in the reader (R10) as one value: off unless chosen, then a click or Enter plays a video in its post or in the media
/// sidebar. Autoplay (always muted) is a second opt-in. The number of live players and the largest file played in the reader are
/// settings with default, minimum and maximum. Volume and "start muted" are the gallery's own preferences (one choice for both).
/// </summary>
internal readonly record struct ReaderInlineVideoOptions(bool Enabled, bool AutoplayMuted, int MaxPlayers, int MaxFileMiB)
{
    // Every live player owns its own video engine (decoder threads, buffers): two keep a paused video ready to continue while
    // another plays; four is the ceiling a reader needs. The file limit keeps very large videos in the gallery.
    internal const int MaxPlayersDefault = 2;
    internal const int MaxPlayersMinimum = 1;
    internal const int MaxPlayersMaximum = 4;
    internal const int MaxFileMiBDefault = 256;
    internal const int MaxFileMiBMinimum = 1;
    internal const int MaxFileMiBMaximum = 4096;

    internal static ReaderInlineVideoOptions Default { get; } = new(false, false, MaxPlayersDefault, MaxFileMiBDefault);

    /// <summary>The gallery's volume (0 to 100), reused so a video sounds the same in both places.</summary>
    internal int Volume { get; init; } = 70;
    /// <summary>The gallery's "start muted"; autoplay is muted in any case.</summary>
    internal bool StartMuted { get; init; }

    internal long MaxFileBytes => (long)Math.Clamp(MaxFileMiB, MaxFileMiBMinimum, MaxFileMiBMaximum) * 1024 * 1024;

    internal static ReaderInlineVideoOptions From(Config config) => new(config.ReaderInlineVideo, config.ReaderInlineVideoAutoplayMuted,
        config.ReaderInlineVideoMaxPlayers, config.ReaderInlineVideoMaxFileMiB)
    {
        Volume = config.VideoVolume,
        StartMuted = config.VideoStartMuted
    };

    internal ReaderInlineVideoOptions Clamped() => this with
    {
        MaxPlayers = Math.Clamp(MaxPlayers, MaxPlayersMinimum, MaxPlayersMaximum),
        MaxFileMiB = Math.Clamp(MaxFileMiB, MaxFileMiBMinimum, MaxFileMiBMaximum),
        Volume = Math.Clamp(Volume, 0, 100)
    };
}

/// <summary>The window-free rules of inline video: what may play, what a key does, what scrolling out of view does.</summary>
internal static class ReaderInlineVideoRules
{
    /// <summary>Autoplay waits until the selection rested this long, so holding ↑/↓ never starts a player per post.</summary>
    internal const int AutoplayDelayMs = 300;

    /// <summary>
    /// Windows draws video on a native surface that the posts' scroll viewer cannot clip; Linux and macOS draw it as an ordinary
    /// picture (the gallery's software surface). The same split as <see cref="GalleryVideoPlayer"/>.
    /// </summary>
    internal static bool NativeSurface => OperatingSystem.IsWindows();

    internal static bool IsVideo(string? fileName) => SavedMediaTypes.Classify(fileName ?? string.Empty) == SavedMediaKind.Video;

    /// <summary>A video attachment of the reader that can play in place: inline video is on, the file is a video and the reader can find its saved bytes.</summary>
    internal static bool CanOffer(ReaderInlineVideoOptions options, ArchiveAttachmentRow? row) =>
        options.Enabled && row is { ResolveEntry: not null, Attachment.IsDeleted: false } && IsVideo(row.Attachment.OriginalFileName ?? row.Attachment.MediaId);

    /// <summary>Why a saved video does not play in the reader (it is larger than the limit), or null when it may.</summary>
    internal static string? LimitReason(ReaderInlineVideoOptions options, long length, string name) => length > options.MaxFileBytes
        ? Texts.T("reader.video.tooLarge", name.Length > 0 ? name : Texts.T("reader.video.unnamed"), SizeText.Format(options.MaxFileBytes))
        : null;

    /// <summary>Whether a shown video starts by itself: only with autoplay chosen, and never twice for the focused media.</summary>
    internal static bool ShouldAutoplay(ReaderInlineVideoOptions options, bool shown, bool presentedElsewhere) =>
        options.Enabled && options.AutoplayMuted && shown && !presentedElsewhere;

    internal enum ViewportAction { Keep, Pause, Release }

    /// <summary>
    /// A video that left the view pauses (it keeps its place for scrolling back). On a native surface it is released as soon as it is
    /// not wholly inside the posts, because the native picture would be drawn over the reader's bars.
    /// </summary>
    internal static ViewportAction OnViewport(bool intersects, bool fullyVisible, bool nativeSurface) =>
        nativeSurface ? (intersects && fullyVisible ? ViewportAction.Keep : ViewportAction.Release)
            : intersects ? ViewportAction.Keep : ViewportAction.Pause;

    internal enum KeyAction { None, Play, Toggle }

    /// <summary>On the video (not on one of its buttons, which handle their own keys): Enter plays, Space plays or pauses.</summary>
    internal static KeyAction Classify(Key key, KeyModifiers modifiers) => modifiers != KeyModifiers.None ? KeyAction.None
        : key == Key.Enter ? KeyAction.Play : key == Key.Space ? KeyAction.Toggle : KeyAction.None;
}

/// <summary>One live inline player as the controller sees it (the real one wraps <see cref="GalleryVideoPlayer"/>).</summary>
internal interface IReaderInlineVideoPlayer : IAsyncDisposable
{
    bool IsPlaying { get; }
    /// <summary>Loads the medium and starts it (the player was configured to play when it was created).</summary>
    Task StartAsync();
    Task PlayAsync();
    Task PauseAsync();
}

/// <summary>A surface that shows an inline player: a post's attachment or the media sidebar. Told about its player's life.</summary>
internal interface IReaderInlineVideoOwner
{
    /// <summary>The controller took the player away (another video needed the room, the reader closed, the setting went off).</summary>
    void PlayerReleased();
    /// <summary>This owner's video became, or stopped being, the reader's current video (the one whose controls are shown).</summary>
    void CurrentChanged(bool current);
    /// <summary>The video started or paused (announced through the owner's live region).</summary>
    void PlaybackChanged(bool playing);
}

/// <summary>
/// The reader's inline players (R10). No player exists until a video is asked to play and the setting is on; at most
/// <see cref="ReaderInlineVideoOptions.MaxPlayers"/> are live at any time (the least recently used one is released first); only one
/// plays: starting one pauses every other. The reader pauses them when it is covered, minimised or a video scrolls out, and releases
/// them all when it closes, switches threads or the setting goes off. Owners are referenced only while their player is live, so a
/// recycled post never stays reachable through the controller. UI thread only.
/// </summary>
internal sealed class ReaderInlineVideoController
{
    private sealed class Live
    {
        internal Live(IReaderInlineVideoOwner owner, IReaderInlineVideoPlayer player) { Owner = owner; Player = player; }
        internal IReaderInlineVideoOwner Owner { get; }
        internal IReaderInlineVideoPlayer Player { get; }
        internal long Used;
    }

    private readonly List<Live> _live = new();
    private readonly List<Task> _releasing = new();
    private long _clock;

    internal ReaderInlineVideoOptions Options { get; private set; } = ReaderInlineVideoOptions.Default;
    /// <summary>The focused card's video may autoplay (autoplay chosen and the media sidebar is not presenting the focused media).</summary>
    internal bool AutoplayInPosts { get; set; }
    /// <summary>The video whose controls are shown (the one started last); null without one.</summary>
    internal IReaderInlineVideoOwner? Current { get; private set; }
    internal int LiveCount => _live.Count;
    /// <summary>Players created and released over the controller's life (smoke and test evidence of the bound and the disposal).</summary>
    internal long Created { get; private set; }
    internal long Released { get; private set; }
    internal int PlayingCount => _live.Count(live => live.Player.IsPlaying);
    /// <summary>The disposals still running (a player's native cleanup may take a moment).</summary>
    internal Task WhenReleasedAsync()
    {
        lock (_releasing)
        {
            _releasing.RemoveAll(task => task.IsCompleted);
            return Task.WhenAll(_releasing.ToArray());
        }
    }

    internal bool IsLive(IReaderInlineVideoOwner owner) => Find(owner) != null;
    internal bool IsPlaying(IReaderInlineVideoOwner owner) => Find(owner)?.Player.IsPlaying == true;

    private Live? Find(IReaderInlineVideoOwner owner) => _live.FirstOrDefault(live => ReferenceEquals(live.Owner, owner));

    /// <summary>New settings: off releases every player; a lower bound releases the least recently used ones.</summary>
    internal Task ConfigureAsync(ReaderInlineVideoOptions options)
    {
        Options = options.Clamped();
        if (!Options.Enabled) return ReleaseAllAsync();
        Trim(Options.MaxPlayers, keep: null);
        return WhenReleasedAsync();
    }

    /// <summary>
    /// Plays this owner's video: an existing player resumes; otherwise (only while the setting is on) one is created with
    /// <paramref name="create"/> after room was made, and started. Every other video pauses first. False when nothing plays.
    /// </summary>
    internal async Task<bool> PlayAsync(IReaderInlineVideoOwner owner, Func<IReaderInlineVideoPlayer>? create = null)
    {
        Live? live = Find(owner);
        if (live == null)
        {
            if (!Options.Enabled || create == null) return false;
            Task others = PauseOthersAsync(owner);
            // Room first: the bound holds at every moment, also while the new player starts.
            Trim(Options.MaxPlayers - 1, keep: owner);
            live = new Live(owner, create()) { Used = ++_clock };
            _live.Insert(0, live);
            Created++;
            SetCurrent(owner);
            await others;
            if (Find(owner) != live) return false;
            await live.Player.StartAsync();
            if (Find(owner) != live) return false;
            owner.PlaybackChanged(true);
            return true;
        }
        await PauseOthersAsync(owner);
        if (Find(owner) != live) return false;
        live.Used = ++_clock;
        SetCurrent(owner);
        await live.Player.PlayAsync();
        if (Find(owner) == live) owner.PlaybackChanged(true);
        return true;
    }

    /// <summary>Space: a playing video pauses, any other plays (or is created, see <see cref="PlayAsync"/>).</summary>
    internal Task ToggleAsync(IReaderInlineVideoOwner owner, Func<IReaderInlineVideoPlayer>? create = null) =>
        IsPlaying(owner) ? PauseAsync(owner) : PlayAsync(owner, create);

    /// <summary>The player's own play button was used: the same one-at-a-time rule as <see cref="PlayAsync"/>.</summary>
    internal void OnUserPlayback(IReaderInlineVideoOwner owner, bool playing)
    {
        if (Find(owner) is not { } live) return;
        if (playing)
        {
            live.Used = ++_clock;
            SetCurrent(owner);
            _ = PauseOthersAsync(owner);
        }
        owner.PlaybackChanged(playing);
    }

    internal async Task PauseAsync(IReaderInlineVideoOwner owner)
    {
        if (Find(owner) is not { Player.IsPlaying: true } live) return;
        await live.Player.PauseAsync();
        if (Find(owner) == live) owner.PlaybackChanged(false);
    }

    /// <summary>The reader was covered by the gallery or minimised: every video pauses; the players stay for later.</summary>
    internal Task PauseAllAsync() => PauseOthersAsync(null);

    private Task PauseOthersAsync(IReaderInlineVideoOwner? except) =>
        Task.WhenAll(_live.Where(live => !ReferenceEquals(live.Owner, except) && live.Player.IsPlaying).ToArray().Select(live => PauseAsync(live.Owner)));

    /// <summary>This owner's player is disposed (its post was recycled or scrolled away on a native surface, its medium changed).</summary>
    internal Task ReleaseAsync(IReaderInlineVideoOwner owner) => Find(owner) is { } live ? Release(live) : Task.CompletedTask;

    /// <summary>The reader closed, switched threads or the setting went off: every player is disposed.</summary>
    internal Task ReleaseAllAsync()
    {
        foreach (Live live in _live.ToArray()) Release(live);
        return WhenReleasedAsync();
    }

    // Keeps at most `count` players, releasing the least recently used first; `keep` (not live yet when it asks for room) never goes.
    private void Trim(int count, IReaderInlineVideoOwner? keep)
    {
        while (_live.Count > Math.Max(0, count))
        {
            Live? oldest = _live.Where(live => !ReferenceEquals(live.Owner, keep)).OrderBy(live => live.Used).FirstOrDefault();
            if (oldest == null) break;
            Release(oldest);
        }
    }

    private Task Release(Live live)
    {
        if (!_live.Remove(live)) return Task.CompletedTask;
        Released++;
        if (ReferenceEquals(Current, live.Owner)) SetCurrent(null, notifyPrevious: false);
        live.Owner.PlayerReleased();
        Task disposal = DisposeAsync(live.Player);
        lock (_releasing)
        {
            _releasing.RemoveAll(task => task.IsCompleted);
            if (!disposal.IsCompleted) _releasing.Add(disposal);
        }
        return disposal;
    }

    private static async Task DisposeAsync(IReaderInlineVideoPlayer player)
    {
        try { await player.DisposeAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The gallery's player keeps ownership of a native cleanup that failed and reports it itself.
            Serilog.Log.Warning(error, "Disposing a reader inline video player failed; its cleanup ownership stays with the player.");
        }
    }

    private void SetCurrent(IReaderInlineVideoOwner? owner, bool notifyPrevious = true)
    {
        IReaderInlineVideoOwner? previous = Current;
        if (ReferenceEquals(previous, owner)) return;
        Current = owner;
        if (notifyPrevious && previous != null && Find(previous) != null) previous.CurrentChanged(false);
        owner?.CurrentChanged(true);
    }
}
