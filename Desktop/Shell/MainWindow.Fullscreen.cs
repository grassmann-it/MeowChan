using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    // Avalonia.Native reports state through this callback as well as through
    // managed WindowState assignments. Only the latter express a new request.
    internal sealed class FullscreenWindowStateFeedback(Action<WindowState>? originalCallback)
    {
        internal Action<WindowState>? OriginalCallback { get; } = originalCallback;
        private int _nativeCallbackDepth;

        internal void OnNativeStateChanged(WindowState state)
        {
            ++_nativeCallbackDepth;
            try { OriginalCallback?.Invoke(state); }
            finally { --_nativeCallbackDepth; }
        }

        internal bool IsExplicitNormalRestore(WindowState previous, WindowState current, bool restoringFullscreen) =>
            _nativeCallbackDepth == 0 && !restoringFullscreen
            && previous == WindowState.Maximized && current == WindowState.Normal;
    }

    private FullscreenWindowStateFeedback? _fullscreenWindowStateFeedback;
    internal sealed record FullscreenOrigin(WindowState State, Size Client, PixelPoint Position, PixelRect? Monitor, Size Decoration)
    {
        // The destination can become Normal without changing which native exit
        // frames are eligible for repair. Record copies retain this provenance.
        internal bool RestoredFromMaximized { get; } = State == WindowState.Maximized;

        internal FullscreenOrigin WithNormalRequest() => this with { State = WindowState.Normal };

        internal bool ShouldRepairExitFrame(Size client, IReadOnlyCollection<Size> staleClients, bool maximizePending) =>
            !RestoredFromMaximized || !maximizePending && ShouldRepairKnownMaximizedExitFrame(client, Client, staleClients);
    }
    private FullscreenOrigin? _fullscreenOrigin;
    private Size _lastNormalClient = new(1380, 850);
    private PixelPoint _lastNormalPosition;
    private bool _restoringFullscreen;
    private Control? _helpOrigin;
    private Size _normalDecoration;
    private Size? _fullscreenRestoreClient;
    private Size _fullscreenRestoreFromClient;
    private bool _fullscreenCorrectionQueued;
    // Cocoa can acknowledge an Enter after Avalonia has accepted an early Exit.
    // Keep the intended destination until a later native FullScreen report can be corrected.
    private FullscreenOrigin? _fullscreenExitIntent;
    private string? _fullscreenExitMode;
    private bool _fullscreenExitRepairQueued;
    private bool _fullscreenExitRepairConsumed;
    private bool _fullscreenExitSizeCorrectionQueued;
    private readonly List<Size> _fullscreenExitStaleNativeClients = new();
    private int _fullscreenExitGeneration;
    private int _fullscreenRestoreGeneration;
    private bool _applyingFullscreenRestoreGeometry;
    private Size? _fullscreenRestoreRequestedClient;
    private Size? _fullscreenMaximizeRestoreClient;
    private bool _fullscreenMaximizeRestoreQueued;
    private bool _fullscreenMaximizeSizeCorrectionQueued;
    private bool _fullscreenMaximizeRestoreCancelled;
    private (FullscreenOrigin Origin, int Generation)? _linuxFullscreenNormalRestore;
    private Size _fullscreenMaximizeRestoreDecoration;
    private readonly bool _traceFullscreen = Environment.GetEnvironmentVariable("MEOWCHAN_FULLSCREEN_TRACE") == "1";
    private int _fullscreenTraceCount;

    private void TraceFullscreen(string step)
    {
        if (!_traceFullscreen || _fullscreenTraceCount++ >= 256) return;
        Console.Error.WriteLine($"FULLSCREEN TRACE {DateTime.UtcNow:HH:mm:ss.fff} {step}; state={WindowState}; client={ClientSize}; frame={FrameSize}; requested={Width}x{Height}; "
            + $"origin={_fullscreenOrigin?.State}/{_fullscreenOrigin?.Client}; intent={_fullscreenExitIntent?.State}/{_fullscreenExitIntent?.Client}; "
            + $"restore={_restoringFullscreen}/{_fullscreenRestoreGeneration}; requested-restore={_fullscreenRestoreRequestedClient}; "
            + $"expected={_fullscreenRestoreClient}; from={_fullscreenRestoreFromClient}; pending-max={_fullscreenMaximizeRestoreClient}/{_fullscreenMaximizeRestoreQueued}/{_fullscreenMaximizeSizeCorrectionQueued}/{_fullscreenMaximizeRestoreCancelled}; repair={_fullscreenExitRepairQueued}/{_fullscreenExitRepairConsumed}/{_fullscreenExitSizeCorrectionQueued}; exit-generation={_fullscreenExitGeneration}; mode={_config.WorkspaceMode}.");
    }

    internal static Avalonia.Styling.ThemeVariant ThemeForPreference(string theme) => theme switch
    {
        "Light" => Avalonia.Styling.ThemeVariant.Light,
        "System" => Avalonia.Styling.ThemeVariant.Default,
        _ => Avalonia.Styling.ThemeVariant.Dark
    };

    private void InitializeShellPresentation()
    {
        if (OperatingSystem.IsMacOS() && PlatformImpl is { } platform)
        {
            _fullscreenWindowStateFeedback = new(platform.WindowStateChanged);
            platform.WindowStateChanged = _fullscreenWindowStateFeedback.OnNativeStateChanged;
        }
        InitializeShortcutRouting();
        AddHandler(KeyDownEvent, OnShellKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PositionChanged += OnShellPositionChanged;
        Resized += OnFullscreenRestoreResized;
        PropertyChanged += OnFullscreenStateChanged;
        Screens.Changed += OnShellScreensChanged;
        WorkspaceControl<Button>("FullscreenButton").Click += (_, _) => ToggleFullscreen();
        WorkspaceControl<Button>("ShellHelpClose").Click += (_, _) => DismissShellHelp();
        WorkspaceControl<MenuItem>("ShortcutHelpMenuItem").Click += (_, _) => ShowShellHelp();
        RefreshShellCommands();
    }

    private void DisposeShellPresentation()
    {
        if (_fullscreenWindowStateFeedback is { } feedback && PlatformImpl is { } platform
            && platform.WindowStateChanged == feedback.OnNativeStateChanged)
            platform.WindowStateChanged = feedback.OriginalCallback;
        _fullscreenWindowStateFeedback = null;
        DisposeShortcutRouting();
        RemoveHandler(KeyDownEvent, OnShellKeyDown);
        PositionChanged -= OnShellPositionChanged;
        Resized -= OnFullscreenRestoreResized;
        PropertyChanged -= OnFullscreenStateChanged;
        Screens.Changed -= OnShellScreensChanged;
        _linuxFullscreenNormalRestore = null;
        ClearFullscreenExitIntent();
    }

    private void OnShellPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (WindowState == WindowState.Normal && _fullscreenOrigin == null && !_restoringFullscreen)
            { _lastNormalPosition = Position; QueueWindowPreferences(); }
    }

    private void RememberNormalGeometry()
    {
        if (WindowState != WindowState.Normal || _fullscreenOrigin != null || _restoringFullscreen || ClientSize.Width <= 0 || IsFullscreenRestoreStale(ClientSize)) return;
        if (_startupWindowClient is Size target
            && (Math.Abs(ClientSize.Width - target.Width) > 1 || Math.Abs(ClientSize.Height - target.Height) > 1)) return;
        _lastNormalClient = ClientSize;
        _lastNormalPosition = Position;
        if (FrameSize is Size frame)
            _normalDecoration = new Size(Math.Max(0, frame.Width - ClientSize.Width), Math.Max(0, frame.Height - ClientSize.Height));
    }

    private void OnShellScreensChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.FullScreen && Screens.ScreenFromWindow(this) == null && Screens.Primary is { } screen)
            Position = screen.WorkingArea.Position;
    }

    internal void ToggleFullscreen()
    {
        TraceFullscreen("toggle requested");
        if (_isClosing || _restoringFullscreen || !IsEnabled || HasVisibleShellDialog())
        {
            TraceFullscreen("toggle blocked");
            return;
        }
        if (WindowState == WindowState.FullScreen) LeaveFullscreen();
        else
        {
            _fullscreenMaximizeRestoreClient = null;
            _fullscreenMaximizeRestoreQueued = false;
            _fullscreenMaximizeSizeCorrectionQueued = false;
            ClearFullscreenExitIntent();
            RememberNormalGeometry();
            _fullscreenRestoreClient = null;
            _fullscreenOrigin = new(WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal,
                _lastNormalClient, _lastNormalPosition, Screens.ScreenFromWindow(this)?.Bounds, _normalDecoration);
            TraceFullscreen("enter requested");
            WindowState = WindowState.FullScreen;
        }
        TraceFullscreen("toggle completed");
        RefreshShellCommands();
    }

    private void LeaveFullscreen()
    {
        TraceFullscreen("leave started");
        FullscreenOrigin origin = _fullscreenOrigin ?? _fullscreenExitIntent
            ?? new(WindowState.Normal, _lastNormalClient, _lastNormalPosition, null, _normalDecoration);
        if (OperatingSystem.IsMacOS() && _fullscreenExitIntent == null)
        {
            _fullscreenExitIntent = origin;
            _fullscreenExitMode = _config.WorkspaceMode;
            // Keep frames already observed during native entry: Cocoa can
            // acknowledge one of them again after Normal and a newer resize.
            // A fresh explicit entry already cleared the previous history.
        }
        _fullscreenOrigin = null;
        _fullscreenRestoreFromClient = ClientSize;
        _restoringFullscreen = true;
        _fullscreenRestoreRequestedClient = null;
        _fullscreenMaximizeRestoreClient = null;
        _fullscreenMaximizeRestoreQueued = false;
        _fullscreenMaximizeSizeCorrectionQueued = false;
        _fullscreenMaximizeRestoreCancelled = false;
        int restoreGeneration = ++_fullscreenRestoreGeneration;
        _linuxFullscreenNormalRestore = null;
        WindowState = WindowState.Normal;
        TraceFullscreen("leave normal requested");
        // KWin/XWayland can retain FullScreen past this dispatcher turn. Wait for
        // its Normal acknowledgement before asking for saved normal geometry.
        if (OperatingSystem.IsLinux())
        {
            _linuxFullscreenNormalRestore = (origin, restoreGeneration);
            ContinueLinuxFullscreenNormalRestore();
        }
        else Dispatcher.UIThread.Post(() => ApplyFullscreenRestore(origin, restoreGeneration), DispatcherPriority.Loaded);
    }

    private void ContinueLinuxFullscreenNormalRestore()
    {
        if (!OperatingSystem.IsLinux() || _linuxFullscreenNormalRestore is not { } pending
            || WindowState != WindowState.Normal || _isClosing) return;
        _linuxFullscreenNormalRestore = null;
        TraceFullscreen($"native normal acknowledged generation={pending.Generation}");
        Dispatcher.UIThread.Post(() => ApplyFullscreenRestore(pending.Origin, pending.Generation), DispatcherPriority.Loaded);
    }

    private void ApplyFullscreenRestore(FullscreenOrigin origin, int restoreGeneration)
    {
        TraceFullscreen($"restore callback start generation={restoreGeneration}");
        if (restoreGeneration != _fullscreenRestoreGeneration) return;
        if (_isClosing) { _restoringFullscreen = false; return; }
        if (OperatingSystem.IsLinux() && WindowState != WindowState.Normal)
        {
            _linuxFullscreenNormalRestore = (origin, restoreGeneration);
            return;
        }
        FullscreenOrigin destination = OperatingSystem.IsMacOS() ? _fullscreenExitIntent ?? origin : origin;
        TraceFullscreen($"restore destination={destination.State}/{destination.Client}");
        _applyingFullscreenRestoreGeometry = true;
        try
        {
            Screen? screen = Screens.All.FirstOrDefault(candidate => candidate.Bounds == destination.Monitor)
                ?? Screens.ScreenFromPoint(destination.Position) ?? Screens.Primary;
            if (screen != null)
            {
                WindowStartupFit fit = WindowStartupGeometry.Fit(destination.Client, new Size(destination.Client.Width + destination.Decoration.Width, destination.Client.Height + destination.Decoration.Height),
                    new Size(MinWidth, MinHeight), screen.WorkingArea, screen.Scaling, destination.Position,
                    WindowStartupGeometry.NormalFrameInsetPixels);
                MinWidth = fit.Minimum.Width; MinHeight = fit.Minimum.Height;
                Width = fit.Client.Width; Height = fit.Client.Height; Position = fit.Position;
                _lastNormalClient = fit.Client; _lastNormalPosition = fit.Position;
                // Keep the known normal frame even when returning to Maximized.
                // X11 may otherwise use the fullscreen-sized frame when a later
                // native restore requests Normal.
                _fullscreenRestoreClient = fit.Client;
            }
            else { Width = destination.Client.Width; Height = destination.Client.Height; Position = destination.Position; }
            // A normal resize can arrive after the managed state changes but
            // before this queued native restore applies its saved geometry.
            // Keep that explicit request instead of restoring the old size.
            if (_fullscreenRestoreRequestedClient is Size requested)
            {
                Width = requested.Width; Height = requested.Height;
                _lastNormalClient = requested;
                _fullscreenRestoreClient = requested;
            }
            if (destination.State == WindowState.Maximized && !_fullscreenMaximizeRestoreCancelled)
            {
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                {
                    _fullscreenMaximizeRestoreClient = _fullscreenRestoreClient ?? destination.Client;
                    _fullscreenMaximizeRestoreDecoration = destination.Decoration;
                }
                else WindowState = WindowState.Maximized;
            }
        }
        finally { _fullscreenRestoreRequestedClient = null; _applyingFullscreenRestoreGeometry = false; _restoringFullscreen = false; }
        ContinueFullscreenRestore();
        ContinuePendingFullscreenExitSizeRepair();
        ContinueFullscreenMaximizeRestore();
        TraceFullscreen("restore callback completed");
        RefreshShellCommands();
    }

    private bool IsFullscreenRestoreStale(Size size) => _fullscreenRestoreClient is Size expected
        && ((Math.Abs(size.Width - _fullscreenRestoreFromClient.Width) <= 1
                && Math.Abs(size.Height - _fullscreenRestoreFromClient.Height) <= 1)
            || (_normalDecoration.Width > 0 || _normalDecoration.Height > 0)
                && ((Math.Abs(size.Width - (expected.Width - _normalDecoration.Width)) <= 1
                        && Math.Abs(size.Height - (expected.Height - _normalDecoration.Height)) <= 1)
                    || (Math.Abs(size.Width - (_fullscreenRestoreFromClient.Width - _normalDecoration.Width)) <= 1
                        && Math.Abs(size.Height - (_fullscreenRestoreFromClient.Height - _normalDecoration.Height)) <= 1)));

    private void OnFullscreenRestoreResized(object? sender, WindowResizedEventArgs e)
    {
        ObserveStartupWindowResize(e);
        if (_fullscreenOrigin != null || _fullscreenExitIntent != null || _restoringFullscreen)
            TraceFullscreen($"resized reason={e.Reason} event-client={e.ClientSize}");
        if (OperatingSystem.IsMacOS() && _fullscreenOrigin != null && WindowState == WindowState.FullScreen)
            RememberFullscreenExitStaleClient(e.ClientSize);
        if (_fullscreenMaximizeRestoreClient != null)
        {
            ContinueFullscreenMaximizeRestore();
            return;
        }
        if (e.Reason == WindowResizeReason.User)
        {
            if (OperatingSystem.IsMacOS() && _fullscreenExitIntent is { } intent
                && WindowState == WindowState.Normal)
            {
                if (_restoringFullscreen)
                {
                    // Cocoa can label its own exit animation as User. Remember
                    // this exact native frame, including a Maximized origin,
                    // so a delayed duplicate cannot replace the normal frame.
                    if (ClientSize != intent.Client) RememberFullscreenExitStaleClient(ClientSize);
                    return;
                }
                if (ClientSize != intent.Client && _fullscreenExitStaleNativeClients.Contains(ClientSize))
                {
                    TraceFullscreen($"duplicate native exit frame ignored={ClientSize}");
                    ContinuePendingFullscreenExitSizeRepair();
                    return;
                }
                if (ClientSize != intent.Client)
                    _fullscreenExitStaleNativeClients.Clear();
            }
            _fullscreenRestoreClient = null;
            if (WindowState == WindowState.Normal && !_restoringFullscreen)
                CapturePendingFullscreenExitSize(ClientSize);
        }
        else
        {
            ContinueFullscreenRestore();
            ContinuePendingFullscreenExitSizeRepair();
        }
    }

    private void OnFullscreenStateChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
            TraceFullscreen($"state changed old={e.OldValue} new={e.NewValue}");
        else if (_fullscreenExitIntent != null && (e.Property == WidthProperty || e.Property == HeightProperty))
            TraceFullscreen($"size property changed {e.Property.Name} old={e.OldValue} new={e.NewValue}");
        if (e.Property == WindowStateProperty)
        {
            if (e.OldValue is WindowState previous
                && _fullscreenWindowStateFeedback?.IsExplicitNormalRestore(
                    previous, WindowState, _restoringFullscreen) == true
                && _fullscreenExitIntent is { State: WindowState.Maximized } intent
                && _fullscreenOrigin == null && !_applyingFullscreenRestoreGeometry
                && _config.WorkspaceMode == _fullscreenExitMode)
            {
                // A later unmaximize supersedes the saved exit destination, but
                // keep its normal geometry and late-entry repair generation.
                // Cocoa's intermediate Normal reports must not do this.
                _fullscreenExitIntent = intent.WithNormalRequest();
                _fullscreenMaximizeRestoreCancelled = true;
                _fullscreenMaximizeRestoreClient = null;
                _fullscreenMaximizeRestoreQueued = false;
                _fullscreenMaximizeSizeCorrectionQueued = false;
                TraceFullscreen("maximized exit superseded by explicit normal request");
            }
            UpdateGalleryFullscreenPresentation();
            ContinueLinuxFullscreenNormalRestore();
            ContinueFullscreenRestore();
            ContinuePendingFullscreenExitSizeRepair();
            ContinueFullscreenMaximizeRestore();
        }
        else if (e.Property == FrameSizeProperty)
            ContinueFullscreenMaximizeRestore();
        if (!OperatingSystem.IsMacOS() || _fullscreenExitIntent == null) return;
        if (e.Property != WindowStateProperty) return;
        if (_config.WorkspaceMode != _fullscreenExitMode)
        {
            ClearFullscreenExitIntent();
            return;
        }
        if (WindowState == WindowState.FullScreen)
            QueueFullscreenExitRepair();
    }

    internal void RequestNormalWindowSize(Size requested)
    {
        if (!_applyingStartupWindowSize) CancelStartupWindowFit();
        TraceFullscreen($"normal size requested={requested}");
        bool pendingFullscreenExit = OperatingSystem.IsMacOS() && _fullscreenExitIntent != null
            && _fullscreenOrigin == null;
        if (OperatingSystem.IsMacOS() && (_restoringFullscreen || _fullscreenMaximizeRestoreClient != null
            || pendingFullscreenExit && (WindowState == WindowState.Normal || WindowState == WindowState.FullScreen)))
        {
            // Persist the cancellation in the destination: a late FullScreen
            // acknowledgement starts another restore and resets transient flags.
            if (pendingFullscreenExit && _fullscreenExitIntent is { } intent
                && _config.WorkspaceMode == _fullscreenExitMode)
                _fullscreenExitIntent = intent.WithNormalRequest();
            _fullscreenMaximizeRestoreCancelled = true;
            _fullscreenMaximizeRestoreClient = null;
            _fullscreenMaximizeRestoreQueued = false;
            _fullscreenMaximizeSizeCorrectionQueued = false;
            TraceFullscreen("pending maximize cancelled by normal size request");
        }
        // Cocoa can report its earlier fullscreen entry after an exit request.
        // Keep an explicit normal resize in that pending exit even if the native
        // report has temporarily changed the managed state back to FullScreen.
        if (WindowState == WindowState.Normal || (pendingFullscreenExit && WindowState == WindowState.FullScreen))
        {
            if (_fullscreenExitIntent is { State: WindowState.Normal } prior && prior.Client != requested)
                RememberFullscreenExitStaleClient(prior.Client);
            CapturePendingFullscreenExitSize(requested);
            if (_restoringFullscreen && WindowState == WindowState.Normal)
                _fullscreenRestoreRequestedClient = requested;
            if (WindowState == WindowState.FullScreen) return;
            if (!_restoringFullscreen) _fullscreenRestoreClient = null;
        }
        Width = requested.Width;
        Height = requested.Height;
        TraceFullscreen("normal size applied");
    }

    private void CapturePendingFullscreenExitSize(Size requested)
    {
        FullscreenOrigin? intent = _fullscreenExitIntent;
        if (!OperatingSystem.IsMacOS() || intent == null
            || (WindowState != WindowState.Normal
                && (WindowState != WindowState.FullScreen || _fullscreenOrigin != null))
            || _applyingFullscreenRestoreGeometry || !double.IsFinite(requested.Width) || !double.IsFinite(requested.Height)
            || requested.Width <= 0 || requested.Height <= 0) return;
        _fullscreenExitIntent = intent with { Client = requested };
        TraceFullscreen($"exit intent captured={requested}");
    }

    private void ClearFullscreenExitIntent()
    {
        if (_fullscreenExitIntent != null) TraceFullscreen("exit intent cleared");
        _fullscreenExitIntent = null;
        _fullscreenExitMode = null;
        _fullscreenExitRepairQueued = false;
        _fullscreenExitRepairConsumed = false;
        _fullscreenExitSizeCorrectionQueued = false;
        _fullscreenExitStaleNativeClients.Clear();
        ++_fullscreenExitGeneration;
    }

    private void RememberFullscreenExitStaleClient(Size client)
    {
        if (RememberFullscreenTransitionClient(_fullscreenExitStaleNativeClients, client))
            TraceFullscreen($"native transition frame remembered={client}");
    }

    internal static bool RememberFullscreenTransitionClient(List<Size> clients, Size client)
    {
        if (clients.Contains(client)) return false;
        if (clients.Count == 4) clients.RemoveAt(0);
        clients.Add(client);
        return true;
    }

    internal static bool ShouldRepairKnownMaximizedExitFrame(Size client, Size target, IReadOnlyCollection<Size> staleClients) =>
        client != target && staleClients.Contains(client);

    private void ContinuePendingFullscreenExitSizeRepair()
    {
        // After a Maximized exit has been restaged and the caller unmaximizes,
        // only a frame observed during the exit is stale. Other native Layout
        // sizes can be later, deliberate normal-window resizes.
        if (!OperatingSystem.IsMacOS() || _fullscreenExitIntent is not { } intent
            || _fullscreenOrigin != null || _restoringFullscreen || _applyingFullscreenRestoreGeometry
            || !intent.ShouldRepairExitFrame(ClientSize, _fullscreenExitStaleNativeClients, _fullscreenMaximizeRestoreClient != null)
            || WindowState != WindowState.Normal || _isClosing
            || Math.Abs(ClientSize.Width - intent.Client.Width) <= 1
                && Math.Abs(ClientSize.Height - intent.Client.Height) <= 1
            || _fullscreenExitSizeCorrectionQueued) return;
        TraceFullscreen("normal size correction queued");
        _fullscreenExitSizeCorrectionQueued = true;
        int generation = _fullscreenExitGeneration;
        // A native resize acknowledgement can arrive after an explicit normal
        // resize and overwrite Width/Height. Reapply only the still-pending exit
        // target; a real user drag updates that target in OnFullscreenRestoreResized.
        Dispatcher.UIThread.Post(() =>
        {
            TraceFullscreen($"normal size correction callback generation={generation}");
            if (generation != _fullscreenExitGeneration) return;
            _fullscreenExitSizeCorrectionQueued = false;
            if (_fullscreenExitIntent is not { } target
                || _fullscreenOrigin != null || _restoringFullscreen || WindowState != WindowState.Normal || _isClosing
                || !target.ShouldRepairExitFrame(ClientSize, _fullscreenExitStaleNativeClients, _fullscreenMaximizeRestoreClient != null)
                || Math.Abs(ClientSize.Width - target.Client.Width) <= 1
                    && Math.Abs(ClientSize.Height - target.Client.Height) <= 1) return;
            TraceFullscreen($"normal size correction applied={target.Client}");
            Width = target.Client.Width;
            Height = target.Client.Height;
        }, DispatcherPriority.Background);
    }

    private void ContinueFullscreenMaximizeRestore()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) || _fullscreenMaximizeRestoreClient is not Size expected
            || _fullscreenMaximizeRestoreCancelled || _restoringFullscreen || _isClosing
            || WindowState != WindowState.Normal) return;
        if (Math.Abs(ClientSize.Width - expected.Width) > 1
            || Math.Abs(ClientSize.Height - expected.Height) > 1)
        {
            QueueFullscreenMaximizeSizeCorrection(expected);
            return;
        }
        if (_fullscreenMaximizeRestoreQueued
            || FrameSize is not Size frame
            || !FullscreenNormalFrameMatches(frame, expected)) return;
        _fullscreenMaximizeRestoreQueued = true;
        int generation = _fullscreenRestoreGeneration;
        TraceFullscreen($"maximize after native normal frame queued={expected}");
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _fullscreenRestoreGeneration) return;
            _fullscreenMaximizeRestoreQueued = false;
            if (_fullscreenMaximizeRestoreClient != expected || _fullscreenMaximizeRestoreCancelled
                || _restoringFullscreen || _isClosing || WindowState != WindowState.Normal
                || Math.Abs(ClientSize.Width - expected.Width) > 1
                || Math.Abs(ClientSize.Height - expected.Height) > 1
                || FrameSize is not Size currentFrame
                || !FullscreenNormalFrameMatches(currentFrame, expected)) return;
            _fullscreenMaximizeRestoreClient = null;
            TraceFullscreen("maximize after native normal frame applied");
            WindowState = WindowState.Maximized;
        }, DispatcherPriority.Loaded);
    }

    private bool FullscreenNormalFrameMatches(Size frame, Size client)
    {
        Size decoration = OperatingSystem.IsLinux() ? _fullscreenMaximizeRestoreDecoration : default;
        // X11 can report the normal client frame with or without its saved WM
        // border; both are a settled normal size, unlike the fullscreen frame.
        return (Math.Abs(frame.Width - client.Width - decoration.Width) <= 1
                || OperatingSystem.IsLinux() && Math.Abs(frame.Width - client.Width) <= 1)
            && (Math.Abs(frame.Height - client.Height - decoration.Height) <= 1
                || OperatingSystem.IsLinux() && Math.Abs(frame.Height - client.Height) <= 1);
    }

    private void QueueFullscreenMaximizeSizeCorrection(Size expected)
    {
        if (_fullscreenMaximizeSizeCorrectionQueued) return;
        _fullscreenMaximizeSizeCorrectionQueued = true;
        int generation = _fullscreenRestoreGeneration;
        TraceFullscreen($"normal frame before maximize correction queued={expected}");
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _fullscreenRestoreGeneration) return;
            _fullscreenMaximizeSizeCorrectionQueued = false;
            if (_fullscreenMaximizeRestoreClient != expected || _fullscreenMaximizeRestoreCancelled
                || _restoringFullscreen || _isClosing || WindowState != WindowState.Normal
                || Math.Abs(ClientSize.Width - expected.Width) <= 1
                    && Math.Abs(ClientSize.Height - expected.Height) <= 1) return;
            TraceFullscreen($"normal frame before maximize correction applied={expected}");
            Width = expected.Width;
            Height = expected.Height;
            // X11 can acknowledge the undecorated exit frame after Width/Height
            // already contain the intended normal client size. Reassigning those
            // unchanged properties then sends no native resize and leaves this
            // pending maximize waiting forever. The inherited ClientSize setter
            // requests the resize through Window, including its decoration inset;
            // the native frame acknowledgement still gates the pending maximize.
            if (OperatingSystem.IsLinux() && _fullscreenMaximizeRestoreClient == expected
                && !_fullscreenMaximizeRestoreCancelled && !_restoringFullscreen && !_isClosing
                && WindowState == WindowState.Normal
                && (Math.Abs(ClientSize.Width - expected.Width) > 1
                    || Math.Abs(ClientSize.Height - expected.Height) > 1))
                ClientSize = expected;
        }, DispatcherPriority.Background);
    }

    private void QueueFullscreenExitRepair()
    {
        if (_fullscreenExitRepairQueued || _fullscreenExitRepairConsumed || _fullscreenExitIntent == null || _isClosing) return;
        TraceFullscreen("late fullscreen exit repair queued");
        _fullscreenExitRepairQueued = true;
        int generation = _fullscreenExitGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            TraceFullscreen($"late fullscreen exit repair callback generation={generation}");
            if (generation != _fullscreenExitGeneration) return;
            _fullscreenExitRepairQueued = false;
            if (_isClosing || _fullscreenExitRepairConsumed || _fullscreenExitIntent == null
                || WindowState != WindowState.FullScreen) return;
            if (_config.WorkspaceMode != _fullscreenExitMode)
            {
                ClearFullscreenExitIntent();
                return;
            }
            if (_restoringFullscreen)
            {
                // The original restore callback was posted before this late native report.
                QueueFullscreenExitRepair();
                return;
            }
            LeaveFullscreen();
            // Keep the revised target until the queued native restore applies it.
            // Only one late-entry correction is allowed for this exit request.
            _fullscreenExitRepairConsumed = true;
            TraceFullscreen("late fullscreen exit repair consumed");
        }, DispatcherPriority.Loaded);
    }

    private void ContinueFullscreenRestore()
    {
        if (_fullscreenRestoreClient is not Size expected || WindowState != WindowState.Normal || _restoringFullscreen || _isClosing) return;
        if (!IsFullscreenRestoreStale(ClientSize))
        {
            // A new explicit normal resize supersedes this transition correction.
            if (ClientSize != expected && ClientSize != _fullscreenRestoreFromClient
                && (Math.Abs(ClientSize.Width - (_fullscreenRestoreFromClient.Width - _normalDecoration.Width)) > 1
                    || Math.Abs(ClientSize.Height - (_fullscreenRestoreFromClient.Height - _normalDecoration.Height)) > 1))
                _fullscreenRestoreClient = null;
            return;
        }
        if (_fullscreenCorrectionQueued) return;
        _fullscreenCorrectionQueued = true;
        // On X11 a queued ConfigureNotify may contain the undecorated requested
        // size after Normal restored its managed shadow. Avalonia subtracts that
        // shadow again. Repair only this exact stale size, never an unrelated resize.
        Dispatcher.UIThread.Post(() =>
        {
            _fullscreenCorrectionQueued = false;
            if (_isClosing || WindowState != WindowState.Normal || _fullscreenRestoreClient != expected || !IsFullscreenRestoreStale(ClientSize)) return;
            Width = expected.Width; Height = expected.Height;
        }, DispatcherPriority.Background);
    }

    private bool DismissShellHelp()
    {
        Border help = WorkspaceControl<Border>("ShellHelpOverlay");
        if (!help.IsVisible) return false;
        help.IsVisible = false;
        if (_helpOrigin is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } origin
            && origin.IsAttachedToVisualTree() && ReferenceEquals(TopLevel.GetTopLevel(origin), this))
        {
            if (!origin.Focus()) Focus();
        }
        else Focus();
        _helpOrigin = null;
        return true;
    }

    private void ShowShellHelp()
    {
        Border help = WorkspaceControl<Border>("ShellHelpOverlay");
        if (help.IsVisible) { DismissShellHelp(); return; }
        _helpOrigin = FocusManager?.GetFocusedElement() as Control;
        var ids = new List<string> { "navigation.downloads", "navigation.archive", "navigation.rules", "navigation.sources", "navigation.diagnostics",
            "session.pause", "input.focus-url", "navigation.settings", "window.fullscreen", "viewer.shortcut-help" };
        if (_largeReaderOpen)
        {
            foreach (var pair in new[] { ("ArchiveOpenGallery", "gallery.open"), ("ExportArchiveButton", "export.open-html-dialog"),
                ("ReaderPreviousPost", "reader.previous-post"), ("ReaderNextPost", "reader.next-post"),
                ("ReaderPreviousThread", "reader.previous-thread"), ("ReaderNextThread", "reader.next-thread") })
                if (_archivePage?.FindControl<Button>(pair.Item1)?.IsEnabled == true) ids.Insert(0, pair.Item2);
        }
        else if (_selectedPage == WorkspacePage.Downloads && _restoreCompleted && !WorkspaceCommandBusy)
        {
            ids.InsertRange(0, new[] { "input.focus-download-search", "selection.select-all", "menu.context-open", "watch.import-clipboard" });
            bool boards = WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1;
            DownloadMenuTarget? target = CaptureDownloadMenu(boards);
            if (target != null)
            {
                ids.InsertRange(0, new[] { "thread.details", "selection.copy-links", "location.open-folder", "location.open-source", "rules.edit-scope", "watch.remove" });
                if (!boards)
                {
                    ids.InsertRange(0, new[] { "gallery.open", "export.open-html-dialog" });
                    if (target.Threads.Length == 1) ids.Insert(0, "thread.read");
                }
                else ids.Insert(0, "board.filters");
                if (target.Checkable.Length > 0) ids.Insert(0, "watch.check");
                if (target.Retriable.Length > 0) ids.Insert(0, "watch.retry-media");
            }
        }
        if (_selectedPage == WorkspacePage.Archive && !_largeReaderOpen)
            ids.InsertRange(0, new[] { "input.focus-search", "archive.select-page", "menu.context-open",
                "archive.previous-page", "archive.next-page", "export.open-all-results-html-dialog", "archive.delete-permanently" });
        string activeContext = _largeReaderOpen ? "reader" : _selectedPage.ToString().ToLowerInvariant();
        // These download commands were selected above from the actual row target.
        // The registry supplement must not add unavailable selection actions back.
        var downloadSelectionCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "thread.read", "thread.details", "selection.copy-links", "location.open-folder", "location.open-source",
            "rules.edit-scope", "watch.remove", "gallery.open", "export.open-html-dialog", "board.filters",
            "watch.check", "watch.retry-media"
        };
        ids.AddRange(CommandCatalog.All.Where(command => command.IsImplemented
            && (command.ExecutionContexts.Contains(activeContext) || command.ExecutionContexts.Contains("shell"))
            && (activeContext != "downloads" || !downloadSelectionCommands.Contains(command.Id))
            && command.ShortcutDisplay != null).Select(command => command.Id));
        WorkspaceControl<TextBlock>("ShellHelpTitle").Text = Texts.T("shortcuts.title");
        // The command registry supplies the visible help and its scoped gestures.
        // Keep this named text surface for the existing automation consumers.
        string[] effectiveIds = ids.Distinct().Where(id => CommandCatalog.TryGet(id, out _)
            && CommandCatalog.ShortcutDisplay(id) != null).ToArray();
        string HelpLine(string id) => CommandCatalog.Label(id) + "   " + CommandCatalog.ShortcutDisplay(id)
            + (CommandCatalog.ScopeHint(id, _largeReaderOpen ? CommandScope.Reader : CommandScope.None) is string hint ? " · " + hint : string.Empty);
        List<string> sections = new();
        foreach ((string label, bool shell) in new[] { ("help.scope.current", false), ("help.scope.application", true) })
        {
            string[] lines = effectiveIds.Where(id => CommandCatalog.Get(id).ExecutionContexts.Contains("shell") == shell)
                .Select(HelpLine).ToArray();
            if (lines.Length > 0) sections.Add(Texts.T(label) + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }
        if (_largeReaderOpen && _archivePage?.ReaderShowsPosts == true)
            sections.Insert(0, Texts.T("help.reader.mediaActivation"));
        WorkspaceControl<TextBlock>("ShellHelpText").Text = string.Join(Environment.NewLine + Environment.NewLine, sections)
            + Environment.NewLine + Environment.NewLine + Texts.T("help.escape");
        help.MaxHeight = Math.Max(180, ClientSize.Height - 40);
        help.IsVisible = true;
        WorkspaceControl<Button>("ShellHelpClose").Focus();
    }

    private bool DismissShellMenu()
    {
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.ContextMenu?.IsOpen == true) { control.ContextMenu.Close(); return true; }
            if (control is Button { Flyout.IsOpen: true } button) { button.Flyout!.Hide(); return true; }
        }
        MenuItem? submenu = this.GetLogicalDescendants().OfType<MenuItem>().LastOrDefault(item => item.IsSubMenuOpen);
        if (submenu == null) return false;
        submenu.IsSubMenuOpen = false;
        return true;
    }

    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShortcutService.IsCapturing) return;
        if (e.Handled || _isClosing || !IsEnabled || HasVisibleShellDialog()) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            // Popups/editors own their first Escape; never cascade a handled key.
            if (e.Source is Visual inputVisual && inputVisual.GetSelfAndVisualAncestors().Any(v => v is Ursa.Controls.AutoCompleteBox)) return;
            if (e.Source is Control control && control.GetLogicalAncestors().Prepend(control).OfType<ComboBox>().Any(combo => combo.IsDropDownOpen)) return;
            if (DismissShellMenu() || DismissShellHelp() || (_gallery != null && !_gallerySuspended && _gallery.TryDismissOverlay()) ||
                (_archivePage?.TryDismissOverlay() ?? false))
            { e.Handled = true; return; }
            if (_archivePage?.IsReaderDialogOpen == true) return;
            if (_selectedPage == WorkspacePage.Rules && _rulesPage?.TryDismissRuleDecision() == true)
            { e.Handled = true; return; }
            if (_selectedPage == WorkspacePage.Plugins && _pluginsPage?.IsDiscardConfirmationOpen == true)
            { _pluginsPage.CancelDiscardConfirmation(); e.Handled = true; return; }
            if (WindowState == WindowState.FullScreen) { e.Handled = true; LeaveFullscreen(); return; }
            if (_gallery != null && !_gallerySuspended) { e.Handled = true; CloseGallery(); return; }
            if (_largeReaderOpen) { e.Handled = true; CloseLargeReader(); return; }
            if (_selectedPage == WorkspacePage.Downloads && WorkspaceControl<Border>("WorkspaceInspector").IsVisible)
            {
                e.Handled = true;
                OnDetailsToggleClick(sender, e);
                DownloadList(WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1).Focus();
            }
            return;
        }
        if (HasBlockingPageDecision()) return;
        if (CommandBindings.Matches(e, "window.fullscreen"))
        {
            if (_archivePage?.IsReaderDialogOpen == true) return;
            e.Handled = true; ToggleFullscreen(); return;
        }
        if (CommandBindings.Matches(e, "viewer.shortcut-help") && (_gallery == null || _gallerySuspended))
        { e.Handled = true; ShowShellHelp(); return; }
        if (_archivePage?.IsReaderDialogOpen == true) return;
        if (CanExecuteShellPowerCommand(e.Source, e.Key))
        {
            foreach ((string id, WorkspacePage page) in new[]
            {
                ("navigation.downloads", WorkspacePage.Downloads), ("navigation.archive", WorkspacePage.Archive),
                ("navigation.rules", WorkspacePage.Rules), ("navigation.sources", WorkspacePage.Sources),
                ("navigation.diagnostics", WorkspacePage.Diagnostics)
            })
            {
                if (!CommandBindings.Matches(e, id)) continue;
                if (_gallery != null) CloseGallery(restoreOrigin: false);
                if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
                e.Handled = NavigateWorkspace(page);
                return;
            }
            if (CommandBindings.Matches(e, "session.pause"))
            {
                if (_restoreCompleted && !_session.IsPauseTransitioning && WorkspaceControl<Button>("PauseButton").IsEnabled)
                {
                    e.Handled = true;
                    OnPauseClick(sender, e);
                }
                return;
            }
            if (CommandBindings.Matches(e, "input.focus-download-search") && CanFocusDownloadSearch)
            {
                e.Handled = true;
                FocusDownloadSearch();
                return;
            }
        }
        if (CommandBindings.Matches(e, "input.focus-url"))
        {
            e.Handled = true;
            if (_gallery != null) CloseGallery(restoreOrigin: false);
            if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
            if (NavigateWorkspace(WorkspacePage.Downloads)) WorkspaceControl<TextBox>("UrlInput").Focus();
        }
        else if (CommandBindings.Matches(e, "navigation.settings"))
        {
            e.Handled = true;
            if (_gallery != null) CloseGallery(restoreOrigin: false);
            if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
            NavigateWorkspace(WorkspacePage.Settings);
        }
        else if (CommandBindings.Matches(e, "input.focus-search") && _selectedPage == WorkspacePage.Archive && !_largeReaderOpen && (_gallery == null || _gallerySuspended))
        { e.Handled = true; _archivePage?.FindControl<TextBox>("ArchiveSearchText")?.Focus(); }
    }

    internal bool CanExecuteShellPowerCommand(object? source, Key key) => _workspaceInitialized && _restoreCompleted
        && !_isClosing && IsEnabled && !WorkspaceCommandBusy && !HasVisibleShellDialog() && !HasBlockingPageDecision()
        && !CommandRouter.IsNativeInput(source, key) && !WorkspaceControl<Border>("ShellHelpOverlay").IsVisible
        && _archivePage?.IsReaderDialogOpen != true && !(_gallery?.IsOverlayOpen ?? false)
        && !this.GetLogicalDescendants().OfType<Control>().Any(control => control.ContextMenu?.IsOpen == true
            || control is Button { Flyout.IsOpen: true } || control is MenuItem { IsSubMenuOpen: true }
            || control is ComboBox { IsDropDownOpen: true });

    private bool HasVisibleShellDialog() => OwnedWindows.Any(window => window.IsVisible
        && !(_gallery?.OwnsOverlayWindow(window) ?? false));

    private void RefreshShellCommands()
    {
        RefreshReaderGroupsEntry(); // URSA-12 shared localization/availability refresh.
        if (!_workspaceInitialized) return;
        CommandBindings.Apply(WorkspaceControl<Button>("FullscreenButton"), "window.fullscreen", iconOnly: true);
        WorkspaceControl<Button>("FullscreenButton").Content = CommandBindings.Icon("window.fullscreen", 16,
            WindowState == WindowState.FullScreen ? "fullscreen" : "windowed");
        WorkspaceControl<Button>("FullscreenButton").Classes.Set("selected", WindowState == WindowState.FullScreen);
        Avalonia.Automation.AutomationProperties.SetHelpText(WorkspaceControl<Button>("FullscreenButton"),
            CommandBindings.Tooltip("window.fullscreen") + (WindowState == WindowState.FullScreen ? (MeowChan.Desktop.Localization.Texts.Language == "de" ? " · Ein" : " · On") : (MeowChan.Desktop.Localization.Texts.Language == "de" ? " · Aus" : " · Off")));
        CommandBindings.Apply(WorkspaceControl<Button>("ShellHelpClose"), "window.close");
        foreach (var pair in new[] { ("ArchiveMenuItem", "navigation.archive"), ("RulesMenuItem", "navigation.rules"),
            ("SourcesMenuItem", "navigation.sources"), ("DiagnosticsMenuItem", "navigation.diagnostics"),
            ("SettingsMenuItem", "navigation.settings"), ("AboutMenuItem", "navigation.about"),
            ("ShortcutHelpMenuItem", "viewer.shortcut-help"), ("ExitMenuItem", "app.quit") })
            CommandBindings.Apply(WorkspaceControl<MenuItem>(pair.Item1), pair.Item2);
        if (WorkspaceControl<Button>("ToolsNavigationButton").Flyout is MenuFlyout tools)
            foreach (MenuItem item in tools.Items.OfType<MenuItem>())
            {
                string? id = (item.Tag as string) switch { "nav.archive" => "navigation.archive", "nav.rules" => "navigation.rules",
                    "nav.sources" => "navigation.sources", "nav.diagnostics" => "navigation.diagnostics", _ => null };
                if (id != null) CommandBindings.Apply(item, id);
            }
    }
}
