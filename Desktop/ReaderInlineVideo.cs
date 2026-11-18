using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Inline video in the reader (R10) as one value. <see cref="Enabled"/>: videos play in the reader (in the media sidebar, or in the post
/// as well when <see cref="InPost"/>, the choice "Open in the post"); off with the choice "Open in the gallery". Autoplay (always muted)
/// is a second opt-in. The number of live players and the largest file played in the reader are
/// settings with default, minimum and maximum. Volume, "start muted" and "Repeat video" are the gallery's own preferences (one choice for
/// both places); the reader's settings popover offers volume and repeat too, and a change reaches the players that are playing.
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

    /// <summary>What exists before the page applied the user's choice: nothing plays in the reader. The configured default is <see cref="From"/> of a new config (sidebar).</summary>
    internal static ReaderInlineVideoOptions Default { get; } = new(false, false, MaxPlayersDefault, MaxFileMiBDefault);

    /// <summary>The choice behind the two flags: gallery only, in the post, or (the default) the sidebar.</summary>
    internal ReaderMediaClickMode Mode => !Enabled ? ReaderMediaClickMode.Gallery : InPost ? ReaderMediaClickMode.Post : ReaderMediaClickMode.Sidebar;

    /// <summary>The choice "Open in the post": a video plays in its post, an image expands there (a second click puts it back).</summary>
    internal bool InPost { get; init; }

    /// <summary>The gallery's volume (0 to 100), reused so a video sounds the same in both places.</summary>
    internal int Volume { get; init; } = 70;
    /// <summary>The gallery's "Repeat video": a new player repeats its video, and a change reaches the live players (the gallery's loop semantics, outside a slideshow).</summary>
    internal bool Repeat { get; init; }
    /// <summary>"Keep images expanded" (with "Open in the post"): an expanded image stays expanded when its post scrolls away and back or the posts are rebuilt.</summary>
    internal bool KeepImagesExpanded { get; init; }
    /// <summary>The gallery's "start muted"; autoplay is muted in any case.</summary>
    internal bool StartMuted { get; init; }
    // "Videos in the media sidebar": the viewer (the reader's sidebar, the archive view's, the Media page's panel) has three switches of its own,
    // apart from the videos in posts (<see cref="AutoplayMuted"/>, <see cref="Repeat"/>, <see cref="StartMuted"/>), so their defaults differ.
    // Its autoplay does not force silence: sound follows the volume unless "start muted" is on or the person muted it in this session.
    /// <summary>The sidebar's video starts by itself once the selection rested (applies to the next medium selected).</summary>
    internal bool SidebarAutoplay { get; init; } = true;
    /// <summary>The sidebar's video repeats (applies to the video shown now).</summary>
    internal bool SidebarLoop { get; init; } = true;
    /// <summary>The sidebar's video starts muted (applies to the video shown now).</summary>
    internal bool SidebarStartMuted { get; init; }
    /// <summary>The gallery's seek step (seconds of one "," or "." key), also used by the reader's video keys.</summary>
    internal int SeekStepSeconds { get; init; } = Config.GalleryVideoSeekStepSecondsDefault;

    internal long MaxFileBytes => (long)Math.Clamp(MaxFileMiB, MaxFileMiBMinimum, MaxFileMiBMaximum) * 1024 * 1024;

    internal static ReaderInlineVideoOptions From(Config config) => new(config.EffectiveReaderMediaClick != ReaderMediaClickMode.Gallery,
        config.ReaderInlineVideoAutoplayMuted, config.ReaderInlineVideoMaxPlayers, config.ReaderInlineVideoMaxFileMiB)
    {
        InPost = config.EffectiveReaderMediaClick == ReaderMediaClickMode.Post,
        Volume = config.VideoVolume,
        Repeat = config.GalleryVideoRepeat,
        KeepImagesExpanded = config.ReaderKeepImagesExpanded,
        StartMuted = config.VideoStartMuted,
        SidebarAutoplay = config.ReaderSidebarVideoAutoplay,
        SidebarLoop = config.ReaderSidebarVideoLoop,
        SidebarStartMuted = config.ReaderSidebarVideoStartMuted,
        SeekStepSeconds = config.GalleryVideoSeekStepSeconds
    };

    internal ReaderInlineVideoOptions Clamped() => this with
    {
        MaxPlayers = Math.Clamp(MaxPlayers, MaxPlayersMinimum, MaxPlayersMaximum),
        MaxFileMiB = Math.Clamp(MaxFileMiB, MaxFileMiBMinimum, MaxFileMiBMaximum),
        Volume = Math.Clamp(Volume, 0, 100),
        SeekStepSeconds = VideoKeyboardSteps.ClampSeconds(SeekStepSeconds)
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

    /// <summary>A video attachment of the reader that can play in the reader: videos are not sent to the gallery, the file is a video and the reader can find its saved bytes.</summary>
    internal static bool CanOffer(ReaderInlineVideoOptions options, ArchiveAttachmentRow? row) =>
        options.Enabled && row is { ResolveEntry: not null, Attachment.IsDeleted: false } && IsVideo(row.Attachment.OriginalFileName ?? row.Attachment.MediaId);

    /// <summary>Such a video that plays in its own post (the choice "Open in the post").</summary>
    internal static bool CanOfferInPost(ReaderInlineVideoOptions options, ArchiveAttachmentRow? row) => options.InPost && CanOffer(options, row);

    /// <summary>An image of a post that expands there (the choice "Open in the post"): a saved, not deleted image the reader can decode.</summary>
    internal static bool CanExpand(ReaderInlineVideoOptions options, ArchiveAttachmentRow? row) =>
        options.InPost && row is { LoadSized: not null, Attachment.IsDeleted: false }
        && SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? row.Attachment.MediaId ?? string.Empty) == SavedMediaKind.Image;

    /// <summary>Why a saved video does not play in the reader (it is larger than the limit), or null when it may.</summary>
    internal static string? LimitReason(ReaderInlineVideoOptions options, long length, string name) => length > options.MaxFileBytes
        ? Texts.T("reader.video.tooLarge", name.Length > 0 ? name : Texts.T("reader.video.unnamed"), SizeText.Format(options.MaxFileBytes))
        : null;

    /// <summary>Whether a shown video starts by itself: only with autoplay chosen, and never twice for the focused media.</summary>
    internal static bool ShouldAutoplay(ReaderInlineVideoOptions options, bool shown, bool presentedElsewhere) =>
        options.Enabled && options.AutoplayMuted && shown && !presentedElsewhere;

    /// <summary>Whether the sidebar's shown video starts by itself: only with the sidebar's own autoplay on (not muted by force), and not while the gallery covers it.</summary>
    internal static bool ShouldAutoplayViewer(ReaderInlineVideoOptions options, bool shown) => options.Enabled && options.SidebarAutoplay && shown;

    internal enum ViewportAction { Keep, Pause, Release }

    /// <summary>
    /// A video that left the view pauses (it keeps its place for scrolling back). On a native surface it is released as soon as it is
    /// not wholly inside the posts, because the native picture would be drawn over the reader's bars.
    /// </summary>
    internal static ViewportAction OnViewport(bool intersects, bool fullyVisible, bool nativeSurface) =>
        nativeSurface ? (intersects && fullyVisible ? ViewportAction.Keep : ViewportAction.Release)
            : intersects ? ViewportAction.Keep : ViewportAction.Pause;

    internal enum KeyAction { None, Play, Toggle }

    /// <summary>On the video (not on one of its buttons, which handle their own keys): Enter and Space play it or pause it.</summary>
    internal static KeyAction Classify(Key key, KeyModifiers modifiers) => modifiers != KeyModifiers.None ? KeyAction.None
        : key is Key.Enter or Key.Space ? KeyAction.Toggle : KeyAction.None;

    /// <summary>
    /// Whether a new inline player starts muted. Autoplay is always muted; otherwise the choice the person made with a speaker in this
    /// session wins, then the gallery's "start muted" setting (one preference for both places, never a second one).
    /// </summary>
    internal static bool StartMuted(ReaderInlineVideoOptions options, bool? sessionChoice, bool autoplay) =>
        autoplay || (sessionChoice ?? options.StartMuted);

    /// <summary>The same for the sidebar's viewer: its own "start muted"; its autoplay is not muted by force.</summary>
    internal static bool StartMutedViewer(ReaderInlineVideoOptions options, bool? sessionChoice) => sessionChoice ?? options.SidebarStartMuted;
}

/// <summary>What a key does to the selected post's video (the gallery's commands, used in the reader).</summary>
internal enum ReaderVideoAction { None, PlayPause, Mute, Loop, StepBack, StepForward, Slower, Faster, NormalSpeed, NextFrame }

/// <summary>
/// The reader's video keys. They are the gallery's own command ids (play / pause, mute, repeat, "," and "." seek step, X and C or [ ] speed,
/// Backspace normal speed, Shift+. next frame), read from the effective bindings, never hard-coded: a changed or removed binding follows.
/// Seeking by Ctrl+arrows is not among them (the arrows move between posts and threads). Enter, which is no command, plays or pauses too.
/// </summary>
internal static class ReaderVideoKeys
{
    internal static readonly (string Id, ReaderVideoAction Action)[] Commands =
    {
        ("video.play-pause", ReaderVideoAction.PlayPause), ("video.mute", ReaderVideoAction.Mute), ("video.loop", ReaderVideoAction.Loop),
        ("video.step-back", ReaderVideoAction.StepBack), ("video.step-forward", ReaderVideoAction.StepForward),
        ("video.speed-down", ReaderVideoAction.Slower), ("video.speed-up", ReaderVideoAction.Faster),
        ("video.speed-reset", ReaderVideoAction.NormalSpeed), ("video.frame-step", ReaderVideoAction.NextFrame)
    };

    internal static bool IsVideoCommand(string id) => Array.Exists(Commands, command => command.Id == id);

    /// <summary>The action of the key the bindings give: <paramref name="matches"/> says whether the key is a command's gesture.</summary>
    internal static ReaderVideoAction Classify(Func<string, bool> matches)
    {
        foreach ((string id, ReaderVideoAction action) in Commands)
            if (matches(id)) return action;
        return ReaderVideoAction.None;
    }

    /// <summary>
    /// <see cref="Classify"/> for the reader: a key that one of the reader's own commands also has (a binding the person saved for it) is that command's, not the
    /// video's. On the reader the video keys give way instead of conflicting (EffectiveShortcutBindings.ConflictContexts), so the gallery keeps them.
    /// </summary>
    internal static ReaderVideoAction ClassifyUnclaimed(Func<string, bool> matches) => ReaderClaims(matches) ? ReaderVideoAction.None : Classify(matches);

    /// <summary>Whether one of the reader's own commands (not a video key) has the key.</summary>
    internal static bool ReaderClaims(Func<string, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        foreach (string id in ReaderCommands.Value)
            if (matches(id)) return true;
        return false;
    }

    // Every command that runs in the reader and competes there for its gestures: not the video keys themselves (they give way).
    private static readonly Lazy<string[]> ReaderCommands = new(() => CommandCatalog.All
        .Where(command => command.IsImplemented && !IsVideoCommand(command.Id)
            && EffectiveShortcutBindings.ConflictContexts(command.Id).Contains("reader", StringComparer.Ordinal))
        .Select(command => command.Id).ToArray());

    /// <summary>Enter on the selected post's video: a fixed content key (no command) that plays or pauses.</summary>
    internal static bool IsEnterToggle(Key key, KeyModifiers modifiers) => key == Key.Enter && modifiers == KeyModifiers.None;

    /// <summary>Keys that move between posts and threads (the reader's navigation).</summary>
    internal static bool IsNavigationKey(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    /// <summary>Keys that move between posts (and, where the person bound them, the first, the last and by page): no video control owns them, so a bar's timeline never keeps them for itself.</summary>
    internal static bool IsPostNavigationKey(Key key) => key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    /// <summary>
    /// Who gets a key pressed in a video's controls (pure). The post keys always go to the reader's navigation (its bindings decide which of them move);
    /// Left and Right belong to the video: the timeline seeks with them when it has the focus, and on the video itself they seek by the seek step (when no
    /// player lives yet there is nothing to seek, and they go on to the reader). Every other key stays with the control or its video keys.
    /// </summary>
    internal static VideoControlKeyOwner OwnerOf(Key key, KeyModifiers modifiers, bool inTimeline, bool playerLive)
    {
        if (IsPostNavigationKey(key)) return VideoControlKeyOwner.Reader;
        if (key is not (Key.Left or Key.Right)) return VideoControlKeyOwner.Control;
        if (modifiers != KeyModifiers.None) return VideoControlKeyOwner.Control;
        if (inTimeline) return VideoControlKeyOwner.Control;
        return playerLive ? VideoControlKeyOwner.Seek : VideoControlKeyOwner.Reader;
    }
}

/// <summary>Where a key pressed in a video's controls goes (<see cref="ReaderVideoKeys.OwnerOf"/>).</summary>
internal enum VideoControlKeyOwner { Control, Reader, Seek }

/// <summary>One live inline player as the controller sees it (the real one wraps <see cref="GalleryVideoPlayer"/>).</summary>
internal interface IReaderInlineVideoPlayer : IAsyncDisposable
{
    bool IsPlaying { get; }
    /// <summary>Loads the medium and starts it (the player was configured to play when it was created).</summary>
    Task StartAsync();
    Task PlayAsync();
    Task PauseAsync();
    /// <summary>The volume (0 to 100), the repeat choice or "start muted" changed while the player lives (null: unchanged). The default does nothing, as for a player without those controls.</summary>
    void ApplyLive(int? volume, bool? repeat, bool? muted = null) { }
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
    /// <summary>The owner is the sidebar's viewer (its own video settings apply) and not a video in a post.</summary>
    bool IsViewer => false;
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
    /// <summary>The shell's gallery gate; while it owns playback this controller cannot create or resume an inline player.</summary>
    internal PlaybackArbitrationGate? PlaybackGate { get; set; }
    /// <summary>The focused card's video may autoplay (autoplay chosen and the media sidebar is not presenting the focused media).</summary>
    internal bool AutoplayInPosts { get; set; }
    /// <summary>Mute or unmute as the person chose it with a speaker in this reader session (null: not chosen, the gallery's setting decides).</summary>
    internal bool? SessionMuted { get; set; }
    /// <summary>
    /// The reader's navigation keys (arrows between posts and threads): called for a navigation key pressed anywhere in a video's controls,
    /// before its timeline gets it. True when the key moved the reader; a timeline never keeps the arrows for itself.
    /// </summary>
    internal Func<KeyEventArgs, bool>? NavigationKeys { get; set; }
    /// <summary>
    /// The profile's hover-preview cache of the timeline (the gallery's own, with its cache budget setting); null shows no preview. Every player made after it is set
    /// connects to it: a thumbnail is only made while the pointer rests on the timeline and its request ends when the pointer leaves.
    /// </summary>
    internal VideoScrubPreviewCache? ScrubCache { get; set; }
    /// <summary>The video whose controls are shown (the one started last); null without one.</summary>
    internal IReaderInlineVideoOwner? Current { get; private set; }
    /// <summary>The images the reader expanded in their posts, remembered while "Keep images expanded" is on (see <see cref="ReaderExpandedImages"/>).</summary>
    internal ReaderExpandedImages ExpandedImages { get; } = new();
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
        ReaderInlineVideoOptions previous = Options;
        Options = options.Clamped();
        if (previous.InPost != Options.InPost || previous.Enabled != Options.Enabled || previous.KeepImagesExpanded != Options.KeepImagesExpanded)
        {
            // Not keeping any more forgets what was remembered, so switching it on later does not bring old expansions back.
            if (!Options.KeepImagesExpanded) ExpandedImages.Clear();
            OptionsChanged?.Invoke();
        }
        // Volume and repeat act on the players that live now (a playing video changes without being reloaded); a new player reads them when created.
        // The sidebar's viewer and a video in a post each follow their own loop and mute switch.
        bool volumeChanged = previous.Volume != Options.Volume;
        if (previous.StartMuted != Options.StartMuted || previous.SidebarStartMuted != Options.SidebarStartMuted) SessionMuted = null; // the choice made now wins over an older one of this session
        foreach (Live live in _live.ToArray())
        {
            bool viewer = live.Owner.IsViewer;
            bool? repeat = viewer ? Changed(previous.SidebarLoop, Options.SidebarLoop) : Changed(previous.Repeat, Options.Repeat);
            bool? muted = viewer ? Changed(previous.SidebarStartMuted, Options.SidebarStartMuted) : Changed(previous.StartMuted, Options.StartMuted);
            if (volumeChanged || repeat.HasValue || muted.HasValue) live.Player.ApplyLive(volumeChanged ? Options.Volume : null, repeat, muted);
        }
        // Gallery only, or leaving "in the post": the players of the posts go (the sidebar's slot offers again when it shows a video).
        if (!Options.Enabled || previous.InPost && !Options.InPost) return ReleaseAllAsync();
        Trim(Options.MaxPlayers, keep: null);
        return WhenReleasedAsync();
    }

    private static bool? Changed(bool before, bool after) => before != after ? after : null;

    /// <summary>The choice between sidebar, post and gallery changed: a post puts an expanded image back.</summary>
    internal event Action? OptionsChanged;

    /// <summary>
    /// Plays this owner's video: an existing player resumes; otherwise (only while the setting is on) one is created with
    /// <paramref name="create"/> after room was made, and started. Every other video pauses first. False when nothing plays.
    /// </summary>
    internal async Task<bool> PlayAsync(IReaderInlineVideoOwner owner, Func<IReaderInlineVideoPlayer>? create = null)
    {
        if (PlaybackGate?.GalleryOwnsPlayback == true) return false;
        Live? live = Find(owner);
        if (live == null)
        {
            if (!Options.Enabled || create == null) return false;
            Task others = PauseOthersAsync(owner);
            // Let a competing gallery handoff win before allocating a native player. A double-click can queue a second
            // activation while the first one is still yielding; creating here synchronously would leave a player behind
            // for the gallery to release even though playback never started.
            await Task.Yield();
            if (PlaybackGate?.GalleryOwnsPlayback == true) return false;
            // Room first: the bound holds at every moment, also while the new player starts.
            Trim(Options.MaxPlayers - 1, keep: owner);
            live = new Live(owner, create()) { Used = ++_clock };
            _live.Insert(0, live);
            Created++;
            SetCurrent(owner);
            await others;
            if (Find(owner) != live || PlaybackGate?.GalleryOwnsPlayback == true)
            {
                if (Find(owner) == live) await Release(live);
                return false;
            }
            await live.Player.StartAsync();
            if (Find(owner) != live || PlaybackGate?.GalleryOwnsPlayback == true)
            {
                if (Find(owner) == live) await Release(live);
                return false;
            }
            owner.PlaybackChanged(true);
            return true;
        }
        await PauseOthersAsync(owner);
        if (Find(owner) != live || PlaybackGate?.GalleryOwnsPlayback == true) return false;
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
        if (PlaybackGate?.GalleryOwnsPlayback == true)
        {
            if (playing) OpenActions.Start(() => PauseAsync(owner), "reader.video.pause");
            return;
        }
        if (playing)
        {
            live.Used = ++_clock;
            SetCurrent(owner);
            OpenActions.Start(() => PauseOthersAsync(owner), "reader.video.pause-others");
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
