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
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// The real inline player: the gallery's <see cref="GalleryVideoPlayer"/> (same engine, verified local stream, same transport row)
/// wrapped, not changed. The picture goes into the owner's media frame, the transport into the owner's row below it through the
/// player's existing external transport host. Disposal goes through the player's own lifetime; only once its native cleanup
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

    internal GalleryInlineVideoPlayer(Panel area, Control below, Panel transportHost, GalleryEntry entry, ReaderInlineVideoOptions options, bool muted, string name)
    {
        _area = area;
        _entry = entry;
        AutomationProperties.SetName(Player, name);
        // An inline player is created to play: the activation is the user's Play (or the opted-in muted autoplay).
        Player.Configure(autoplay: true, startMuted: muted || options.StartMuted, options.Volume);
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
        Player.SetExternalTransportHost(transportHost);
    }

    public bool IsPlaying => !_failed && !_disposed && Player.PlaybackMode == VideoPlaybackMode.Playing;

    public Task StartAsync() => _disposed ? Task.CompletedTask : Player.ShowAsync(_entry, _show.Token);
    public Task PlayAsync() { _failed = false; return _disposed ? Task.CompletedTask : Player.PlayAsync(); }
    public Task PauseAsync() => _disposed ? Task.CompletedTask : Player.PauseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _show.Cancel(); } catch (ObjectDisposedException) { }
        try { await Player.DisposeAsync(); }
        finally
        {
            _area.Children.Remove(Player);
            _show.Dispose();
        }
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
    private readonly Panel _transportHost = new() { Name = "ReaderInlineVideoTransport" };
    private readonly TextBlock _error = new() { Name = "ReaderInlineVideoError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _live = new() { Name = "ReaderInlineVideoLive", Opacity = 0, IsHitTestVisible = false, Height = 0 };
    private ReaderInlineVideoController? _controller;
    private GalleryInlineVideoPlayer? _player;
    private CancellationTokenSource? _activation;
    private string? _key;
    private Func<CancellationToken, Task<GalleryEntry?>>? _resolve;
    private string _name = string.Empty;
    private bool _current;

    /// <summary>The video row keeps this width under a small picture, so the transport's two compact rows fit (layout constant).</summary>
    internal const double MinimumRowWidth = 288;

    /// <summary>The identity of an attachment for the slot: the same file at the same place of a rebuilt row keeps its player.</summary>
    internal static string KeyOf(MeowChan.Archive.CapturedAttachment attachment) => string.Join("\n", attachment.Position,
        attachment.MediaId ?? string.Empty, attachment.Variant, attachment.OriginalFileName ?? string.Empty, attachment.AttachmentRevisionKey ?? string.Empty);

    internal ReaderInlineVideoSlot(Panel area, bool activatorWhenIdle)
    {
        _area = area;
        _activatorWhenIdle = activatorWhenIdle;
        Activator = new Button
        {
            Name = "ReaderInlineVideoPlay", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        Activator.Click += (_, e) => { e.Handled = true; _ = PlayAsync(); };
        area.Children.Add(Activator);
        _error.Classes.Add("workspace-error");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(_live, AutomationLiveSetting.Polite);
        Below = new StackPanel { Name = "ReaderInlineVideoRow", Spacing = 4, IsVisible = false, Children = { _transportHost, _error, _live } };
        // Keys on the video's own surface (not its buttons, which handle Enter and Space themselves): Enter plays, Space toggles.
        foreach (Control target in new Control[] { area, Below })
            target.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(e), RoutingStrategies.Bubble);
    }

    /// <summary>The focusable "Play video here" button over the poster (keyboard path; also over a paused older video).</summary>
    internal Button Activator { get; }
    /// <summary>The row the owner places under the media frame: transport (current video only), error line, live region.</summary>
    internal Control Below { get; }
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
    internal void Offer(ReaderInlineVideoController? controller, string? key, Func<CancellationToken, Task<GalleryEntry?>>? resolve, string name)
    {
        bool usable = controller is { Options.Enabled: true } && key != null && resolve != null;
        if (!usable) { key = null; resolve = null; name = string.Empty; }
        if (!ReferenceEquals(controller, _controller) || !string.Equals(key, _key, StringComparison.Ordinal)) _ = ReleaseAsync();
        _controller = controller;
        _key = key;
        _resolve = resolve;
        _name = name;
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
        bool focus = Activator.IsKeyboardFocusWithin;
        if (_player != null) return ActivationTask = ResumeAsync(controller, focus);
        if (_activation != null) return ActivationTask; // already loading
        CancellationTokenSource activation = new();
        _activation = activation;
        return ActivationTask = CreateAsync(controller, activation, muted, focus);
    }

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
                GalleryInlineVideoPlayer player = new(_area, Activator, _transportHost, entry, options, muted,
                    _name.Length > 0 ? _name : Texts.T("reader.video.unnamed"));
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
            if (key == _key) ShowError(Texts.T("reader.video.failed", error.Message));
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

    private void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled || !IsOffered || e.Source is Button) return;
        switch (ReaderInlineVideoRules.Classify(e.Key, e.KeyModifiers))
        {
            case ReaderInlineVideoRules.KeyAction.Play:
                e.Handled = true;
                _ = PlayAsync();
                break;
            case ReaderInlineVideoRules.KeyAction.Toggle:
                e.Handled = true;
                _ = ToggleAsync();
                break;
        }
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
        Below.IsVisible = _transportHost.IsVisible || _error.IsVisible || !string.IsNullOrEmpty(_live.Text);
    }

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
