using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// The real inline player: the gallery's <see cref="GalleryVideoPlayer"/> (same engine, verified local stream) wrapped, not changed. The
/// picture goes into the owner's media frame, the transport into the owner's row below it through the player's existing external
/// transport host. The transport is the reader's minimal bar (play or pause, timeline, time, mute, optionally "Open in gallery"), one row
/// exactly as wide as the picture's frame: under the picture rather than over it, because a native video surface (Windows) is always
/// drawn above any overlay, and so that nothing of the picture is covered. Disposal goes through the player's own lifetime; only once its native cleanup
/// returned is the player taken out of the frame (the gallery's contract: the drawable stays owned until then).
/// </summary>
internal sealed class GalleryInlineVideoPlayer : IReaderInlineVideoPlayer
{
    private readonly Panel _area;
    private readonly GalleryEntry _entry;
    private readonly CancellationTokenSource _show = new();
    private bool _failed, _disposed;

    internal GalleryVideoPlayer Player { get; } = new() { Name = "ReaderInlineVideoPlayer" };
    internal event Action<string>? Failed;
    internal event Action<bool>? UserPlayback;
    internal event Action? Clicked;
    internal event Action<bool>? MuteChosen;

    internal GalleryInlineVideoPlayer(Panel area, Control below, Panel transportHost, GalleryEntry entry, ReaderInlineVideoOptions options, bool startMuted,
        string name, Action? openGallery = null, bool viewer = false, VideoScrubPreviewCache? scrubCache = null)
    {
        _area = area;
        _entry = entry;
        AutomationProperties.SetName(Player, name);
        // An inline player is created to play: the activation is the user's Play (or the opted-in muted autoplay).
        Player.Configure(autoplay: true, startMuted, options.Volume);
        Player.ConfigureSeekStep(options.SeekStepSeconds);
        // The timeline's hover preview is the gallery's (its component, cache and budget); the bar's timeline is the very slider it listens to.
        Player.ConfigureScrubPreview(scrubCache);
        // The loop switch of its place (the gallery's "Repeat video" for a video in a post, the sidebar's own for the viewer): a silent
        // preference, not a user toggle; the video loops from its first play on.
        Player.SetLoopEnabled(viewer ? options.SidebarLoop : options.Repeat);
        Player.MuteChosen += muted => MuteChosen?.Invoke(muted);
        Player.PlaybackFailed += () => { _failed = true; Failed?.Invoke(Texts.T("reader.video.failed", Texts.T("gallery.mediaUnavailable"))); };
        Player.DiagnosticNotice += message => { if (!string.IsNullOrWhiteSpace(message)) Failed?.Invoke(Texts.T("reader.video.failed", message)); };
        Player.CleanupDiagnostic += message => Failed?.Invoke(message);
        Player.UserPlaybackRequested += playing => UserPlayback?.Invoke(playing);
        // A click on the picture plays or pauses it (and never reaches the frame below, which would open the gallery).
        Player.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;
            Clicked?.Invoke();
        }, RoutingStrategies.Bubble);
        // Under the slot's activator, which covers a paused older video so a click or Enter continues it.
        int index = area.Children.IndexOf(below);
        if (index >= 0) area.Children.Insert(index, Player); else area.Children.Add(Player);
        Button? open = null;
        if (openGallery != null)
        {
            open = new Button { Name = "ReaderInlineVideoOpen", Classes = { "media-control" } };
            CommandBindings.Apply(open, "gallery.open", iconOnly: true, iconSize: 18);
            open.Click += (_, e) => { e.Handled = true; openGallery(); };
        }
        Player.UseMinimalTransport(open);
        Player.SetExternalTransportHost(transportHost);
    }

    public bool IsPlaying => !_failed && !_disposed && Player.PlaybackMode == VideoPlaybackMode.Playing;

    public Task StartAsync() => _disposed ? Task.CompletedTask : Player.ShowAsync(_entry, _show.Token);
    public Task PlayAsync() { _failed = false; return _disposed ? Task.CompletedTask : Player.PlayAsync(); }
    public Task PauseAsync() => _disposed ? Task.CompletedTask : Player.PauseAsync();

    public void ApplyLive(int? volume, bool? repeat, bool? muted = null)
    {
        if (_disposed) return;
        if (volume.HasValue) Player.ConfigureVolume(volume.Value);
        if (repeat.HasValue) Player.SetLoopEnabled(repeat.Value);
        // "Start muted" changed: the video shown now mutes or unmutes (the speaker is the player's own; this is not the person's session choice).
        if (muted.HasValue && Player.Muted != muted.Value) _ = Player.ToggleMuteAsync();
    }

    /// <summary>True when the player's native cleanup failed: it stays in the frame, hidden and disabled, still owning its drawable.</summary>
    internal bool CleanupPending { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _show.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            await InlineVideoDisposal.DisposeAsync(() => Player.DisposeAsync(), () => _area.Children.Remove(Player), () =>
            {
                // Ownership is retained (the gallery's contract): the drawable stays where it is, but nothing shows or takes input.
                CleanupPending = true;
                Player.IsVisible = false;
                Player.IsEnabled = false;
            });
        }
        finally { _show.Dispose(); }
    }
}

/// <summary>
/// The disposal contract of an inline player's frame (the gallery's own): the player leaves the frame only once its native cleanup
/// returned. When the cleanup failed it stays (the caller hides it) and the failure is raised once, to the caller.
/// </summary>
internal static class InlineVideoDisposal
{
    internal static async Task DisposeAsync(Func<ValueTask> disposePlayer, Action removeFromFrame, Action retainAfterFailure)
    {
        ArgumentNullException.ThrowIfNull(disposePlayer);
        ArgumentNullException.ThrowIfNull(removeFromFrame);
        ArgumentNullException.ThrowIfNull(retainAfterFailure);
        try { await disposePlayer(); }
        catch
        {
            retainAfterFailure();
            throw;
        }
        removeFromFrame();
    }
}

/// <summary>
/// One place in the reader where a video can play inline: a post's attachment or the media sidebar. It owns the activator button over
/// the poster (focusable, named, Enter or click plays), the row under the frame with the moved transport, a visible error line and a
/// polite live region that announces play and pause. Only the reader's current video shows its transport (one set of video controls
/// in the reader at a time); a paused older one keeps its picture and shows the activator again to continue. Nothing is created until
/// the video is asked to play; the player is given back to the controller when the medium changes or the owner leaves the tree.
/// </summary>
internal sealed class ReaderInlineVideoSlot : IReaderInlineVideoOwner
{
    private readonly Panel _area;
    private readonly bool _activatorWhenIdle;
    // "in-dock": the bar's own strip style of the gallery's dock (no side inset, 4 dip above and below its 32 dip controls).
    private readonly Panel _transportHost = new() { Name = "ReaderInlineVideoTransport", Classes = { "in-dock" } };
    private readonly TextBlock _error = new() { Name = "ReaderInlineVideoError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _live = new() { Name = "ReaderInlineVideoLive", Opacity = 0, IsHitTestVisible = false, Height = 0 };
    private ReaderInlineVideoController? _controller;
    private GalleryInlineVideoPlayer? _player;
    private CancellationTokenSource? _activation;
    private string? _key;
    private Func<CancellationToken, Task<GalleryEntry?>>? _resolve;
    private string _name = string.Empty;
    private Action? _openGallery;
    private bool _current;

    /// <summary>
    /// Whether one of the owner's own commands has a key (the argument says whether the key is a command's gesture): the video key then gives way to it.
    /// The reader's by default; the Media page's panel sets its page's.
    /// </summary>
    internal Func<Func<string, bool>, bool> OwnerClaims { get; set; } = ReaderVideoKeys.ReaderClaims;

    /// <summary>The identity of an attachment for the slot: the same file at the same place of a rebuilt row keeps its player.</summary>
    internal static string KeyOf(MeowChan.Archive.CapturedAttachment attachment) => string.Join("\n", attachment.Position,
        attachment.MediaId ?? string.Empty, attachment.Variant, attachment.OriginalFileName ?? string.Empty, attachment.AttachmentRevisionKey ?? string.Empty);

    internal ReaderInlineVideoSlot(Panel area, bool activatorWhenIdle)
    {
        _area = area;
        _activatorWhenIdle = activatorWhenIdle;
        // Over the poster, so it never paints a surface of its own: the class (WorkspaceStyles) keeps every state of the button transparent
        // (Semi's hover tint would otherwise turn the whole picture into a flat grey rectangle) and shows a thin ring on hover.
        Activator = new Button
        {
            Name = "ReaderInlineVideoPlay", Classes = { "video-activator" },
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        // A pointer click on the activator is told apart from Enter or Space on it: the pointer's play gives the focus back (PointerPlayFocus), the keyboard's moves it to the bar.
        Activator.AddHandler(InputElement.PointerPressedEvent, (_, _) => _pointerPlay = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Activator.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(() => _pointerPlay = false, DispatcherPriority.Background),
            RoutingStrategies.Bubble, handledEventsToo: true);
        Activator.Click += (_, e) => { e.Handled = true; _ = PlayAsync(); };
        area.Children.Add(Activator);
        _error.Classes.Add("workspace-error");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(_live, AutomationLiveSetting.Polite);
        Row = new ReaderInlineVideoRow(_transportHost, _error, _live) { IsVisible = false };
        // Keys on the video's own surface (not its buttons, which handle Enter and Space themselves): Enter and Space play or pause, the
        // reader's video keys act on it (bubble). The reader's navigation keys come first (tunnel): the timeline never keeps the arrows.
        foreach (Control target in new Control[] { area, Row })
        {
            target.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(e), RoutingStrategies.Bubble);
            target.AddHandler(InputElement.KeyDownEvent, (_, e) => OnNavigationKey(e), RoutingStrategies.Tunnel);
        }
    }

    /// <summary>The row under the frame as its type (the owner gives it the frame's width).</summary>
    internal ReaderInlineVideoRow Row { get; }

    /// <summary>The focusable "Play video here" button over the poster (keyboard path; also over a paused older video).</summary>
    internal Button Activator { get; }
    /// <summary>The row the owner places under the media frame: transport (current video only), error line, live region.</summary>
    internal Control Below => Row;
    internal bool IsLive => _player != null;
    internal bool IsOffered => _key != null && _controller?.Options.Enabled == true;
    internal bool IsPlaying => _player?.IsPlaying == true;
    internal string LiveText => _live.Text ?? string.Empty;
    internal string ErrorText => _error.IsVisible ? _error.Text ?? string.Empty : string.Empty;
    internal GalleryVideoPlayer? Player => _player?.Player;
    internal Task ActivationTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The video this slot can play now (a key per attachment, so a rebuilt row keeps a running player), or none. A different video
    /// or none gives the player back. <paramref name="controller"/> null or switched off offers nothing.
    /// </summary>
    internal void Offer(ReaderInlineVideoController? controller, string? key, Func<CancellationToken, Task<GalleryEntry?>>? resolve, string name,
        Action? openGallery = null)
    {
        bool usable = controller is { Options.Enabled: true } && key != null && resolve != null;
        if (!usable) { key = null; resolve = null; name = string.Empty; }
        if (!ReferenceEquals(controller, _controller) || !string.Equals(key, _key, StringComparison.Ordinal)) _ = ReleaseAsync();
        _controller = controller;
        _key = key;
        _resolve = resolve;
        _name = name;
        _openGallery = usable ? openGallery : null;
        string label = Texts.T("reader.video.play", name.Length > 0 ? name : Texts.T("reader.video.unnamed"));
        AutomationProperties.SetName(Activator, label);
        AutomationProperties.SetHelpText(Activator, Texts.T("reader.video.playHint"));
        ToolTip.SetTip(Activator, label);
        if (!usable) ShowError(null);
        UpdatePresentation();
    }

    /// <summary>Click, Enter or autoplay: plays the offered video here (creating its player if the bound allows), or resumes it.</summary>
    internal Task PlayAsync(bool muted = false)
    {
        if (_controller is not { } controller || !IsOffered) return Task.CompletedTask;
        bool pointer = _pointerPlay;
        _pointerPlay = false;
        bool focus = Activator.IsKeyboardFocusWithin && !pointer;
        Task task;
        if (_player != null) task = ActivationTask = ResumeAsync(controller, focus);
        else if (_activation != null) return ActivationTask; // already loading
        else
        {
            CancellationTokenSource activation = new();
            _activation = activation;
            task = ActivationTask = CreateAsync(controller, activation, muted, focus);
        }
        // The play button hides itself once the video runs; after a click the focus goes back where the reader's keys work (not into the bar).
        if (pointer && PointerPlayFocus is { } back)
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(back, DispatcherPriority.Loaded), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        return task;
    }

    private bool _pointerPlay;

    /// <summary>
    /// Where the focus goes after a pointer click started or resumed the video here (the owner's posts or grid), so Up and Down keep moving the reader
    /// and the video keys keep acting on the video; a keyboard start puts it on the bar's play button instead. Unset leaves the focus alone.
    /// </summary>
    internal Action? PointerPlayFocus { get; set; }

    private async Task ResumeAsync(ReaderInlineVideoController controller, bool focus)
    {
        await controller.PlayAsync(this);
        if (focus) FocusTransport();
    }

    private async Task CreateAsync(ReaderInlineVideoController controller, CancellationTokenSource activation, bool muted, bool focus)
    {
        string? key = _key;
        try
        {
            ShowError(null);
            GalleryEntry? entry = _resolve == null ? null : await _resolve(activation.Token);
            if (activation.IsCancellationRequested || key != _key) return;
            if (entry == null) { ShowError(Texts.T("archive.notSaved")); return; }
            if (entry.Kind != SavedMediaKind.Video) { ShowError(Texts.T("reader.sidebar.unavailable")); return; }
            if (ReaderInlineVideoRules.LimitReason(controller.Options, entry.Asset.Length, _name) is { } reason) { ShowError(reason); return; }
            ReaderInlineVideoOptions options = controller.Options;
            await controller.PlayAsync(this, () =>
            {
                GalleryInlineVideoPlayer player = new(_area, Activator, _transportHost, entry, options,
                    _activatorWhenIdle ? ReaderInlineVideoRules.StartMutedViewer(options, controller.SessionMuted)
                        : ReaderInlineVideoRules.StartMuted(options, controller.SessionMuted, autoplay: muted),
                    _name.Length > 0 ? _name : Texts.T("reader.video.unnamed"), _openGallery, viewer: _activatorWhenIdle, scrubCache: controller.ScrubCache);
                player.MuteChosen += muted => controller.SessionMuted = muted;
                player.Failed += message => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_player, player)) ShowError(message); });
                player.UserPlayback += playing => { if (ReferenceEquals(_player, player)) controller.OnUserPlayback(this, playing); };
                player.Clicked += () => { if (ReferenceEquals(_player, player)) _ = controller.ToggleAsync(this); };
                _player = player;
                UpdatePresentation();
                if (focus) Dispatcher.UIThread.Post(FocusTransport, DispatcherPriority.Loaded);
                return player;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (key == _key) ShowError(Texts.T("reader.video.failed", UserFacingError.Report(error, "Starting an inline reader video")));
        }
        finally
        {
            if (ReferenceEquals(_activation, activation)) _activation = null;
            activation.Dispose();
        }
    }

    internal Task ToggleAsync() => _player != null && _controller != null ? _controller.ToggleAsync(this) : PlayAsync();

    internal Task PauseAsync() => _player != null && _controller != null ? _controller.PauseAsync(this) : Task.CompletedTask;

    /// <summary>Gives the player back (no-op without one); an activation still resolving its file is cancelled.</summary>
    internal Task ReleaseAsync()
    {
        CancellationTokenSource? activation = _activation;
        _activation = null;
        try { activation?.Cancel(); } catch (ObjectDisposedException) { }
        return _player != null && _controller != null ? _controller.ReleaseAsync(this) : Task.CompletedTask;
    }

    /// <summary>The page applies the viewport rule (scrolled out pauses; on a native surface, partly out releases).</summary>
    internal void ApplyViewport(bool intersects, bool fullyVisible)
    {
        if (_player == null) return;
        switch (ReaderInlineVideoRules.OnViewport(intersects, fullyVisible, ReaderInlineVideoRules.NativeSurface))
        {
            case ReaderInlineVideoRules.ViewportAction.Pause: _ = PauseAsync(); break;
            case ReaderInlineVideoRules.ViewportAction.Release: _ = ReleaseAsync(); break;
        }
    }

    // Tunnel, before the control sees the key. Up, Down and the page keys never belong to a video control: they move the reader's posts (by its bindings)
    // from the play button, the timeline or the picture alike, so a click on a video never costs the keyboard its place in the reader. Left and Right
    // are the video's: the timeline seeks with them (its own keys), and on the picture they seek by the seek step; without a player they go on
    // to the reader. A key the reader does not bind stays with the control (a rebinding never costs the keyboard a function).
    private void OnNavigationKey(KeyEventArgs e)
    {
        if (e.Handled || !IsOffered || !ReaderVideoKeys.IsNavigationKey(e.Key)) return;
        bool inTimeline = e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(node => node is Slider);
        switch (ReaderVideoKeys.OwnerOf(e.Key, e.KeyModifiers, inTimeline, _player != null))
        {
            case VideoControlKeyOwner.Reader:
                if (_controller?.NavigationKeys is { } route && route(e)) e.Handled = true;
                break;
            case VideoControlKeyOwner.Seek:
                if (_player?.Player is { } player)
                {
                    e.Handled = true;
                    player.SeekByStep(forward: e.Key == Key.Right);
                }
                break;
        }
    }

    private void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled || !IsOffered) return;
        // Enter and Space on the video's own surface (never on one of its buttons, which handle both themselves).
        if (e.Source is not Button)
        {
            switch (ReaderInlineVideoRules.Classify(e.Key, e.KeyModifiers))
            {
                case ReaderInlineVideoRules.KeyAction.Play:
                    e.Handled = true;
                    _ = PlayAsync();
                    return;
                case ReaderInlineVideoRules.KeyAction.Toggle:
                    // Space only when it is the toggle's binding (a person may have moved it); Enter is a fixed content key.
                    if (ReaderVideoKeys.IsEnterToggle(e.Key, e.KeyModifiers) || CommandBindings.Matches(e, "video.play-pause") && !OwnerClaims(id => CommandBindings.Matches(e, id)))
                    {
                        e.Handled = true;
                        _ = ToggleAsync();
                        return;
                    }
                    break;
            }
        }
        // The other video keys: the gallery's commands from the bindings (the text-input guard is the binding match's own).
        ReaderVideoAction action = ReaderVideoKeys.Classify(id => CommandBindings.Matches(e, id));
        if (action == ReaderVideoAction.None || action == ReaderVideoAction.PlayPause || OwnerClaims(id => CommandBindings.Matches(e, id))) return;
        e.Handled = true;
        _ = ExecuteAsync(action);
    }

    /// <summary>One of the reader's video keys on this video. Without a live player only play or pause (which creates it) does anything.</summary>
    internal Task ExecuteAsync(ReaderVideoAction action)
    {
        if (!IsOffered) return Task.CompletedTask;
        if (action == ReaderVideoAction.PlayPause) return ToggleAsync();
        if (_player?.Player is not { } player) return Task.CompletedTask;
        switch (action)
        {
            case ReaderVideoAction.Mute: return player.ToggleMuteAsync();
            case ReaderVideoAction.Loop: player.ToggleLoopByKey(); break;
            case ReaderVideoAction.StepBack: player.SeekByStep(forward: false); break;
            case ReaderVideoAction.StepForward: player.SeekByStep(forward: true); break;
            case ReaderVideoAction.Slower: player.StepRate(-1); break;
            case ReaderVideoAction.Faster: player.StepRate(1); break;
            case ReaderVideoAction.NormalSpeed: player.StepRate(0); break;
            case ReaderVideoAction.NextFrame: return player.StepFrameAsync();
        }
        return Task.CompletedTask;
    }

    private void FocusTransport()
    {
        // The transport's play/pause button: the next stop after the activator, which gives way once the video plays.
        Button? playPause = _transportHost.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "GalleryVideoPlayPause");
        if (playPause?.IsEffectivelyVisible == true) playPause.Focus(NavigationMethod.Tab);
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.IsVisible = !string.IsNullOrEmpty(message);
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        bool live = _player != null;
        _transportHost.IsVisible = live && _current;
        Activator.IsVisible = IsOffered && (live ? !_current : _activatorWhenIdle);
        Row.IsVisible = _transportHost.IsVisible || _error.IsVisible || !string.IsNullOrEmpty(_live.Text);
    }

    // The slots of the sidebar, the large reader and the Media page's panel are the viewer (they show the idle poster with the activator).
    bool IReaderInlineVideoOwner.IsViewer => _activatorWhenIdle;

    void IReaderInlineVideoOwner.PlayerReleased()
    {
        _player = null;
        _current = false;
        UpdatePresentation();
    }

    void IReaderInlineVideoOwner.CurrentChanged(bool current)
    {
        _current = current;
        UpdatePresentation();
    }

    void IReaderInlineVideoOwner.PlaybackChanged(bool playing)
    {
        string name = _name.Length > 0 ? _name : Texts.T("reader.video.unnamed");
        _live.Text = Texts.T(playing ? "reader.video.playing" : "reader.video.paused", name);
        UpdatePresentation();
    }
}
