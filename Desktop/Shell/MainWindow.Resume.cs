using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

public partial class MainWindow
{
    private MenuItem? _resumeReadingMenu;
    // R5/RH1: the recent reading places, one per thread. The arrow beside the book button, right-click or the menu key on the button and
    // the "Reading history" entry of the app menu open the same list under the button (ReadingHistoryFlyout); it reads the snapshot
    // below, which every save, forget and opening refreshes.
    private MenuItem? _readingHistoryMenu;
    private ReadingHistoryFlyout? _readingHistoryFlyout;
    private IReadOnlyList<LocalReadingHistoryEntry> _readingHistorySnapshot = Array.Empty<LocalReadingHistoryEntry>();
    private int _readingHistorySnapshotRevision;
    private Task _readingPositionSave = Task.CompletedTask;
    private Task _readingPositionWrite = Task.CompletedTask;
    private LocalReadingPosition? _submittedReadingPosition;
    private long _submittedReadingGeneration;
    private int _submittedReadingRevision;
    private bool _readingPositionForgetPending;
    private int _readingResumeNavigationRevision;
    private enum ReadingResumeState { Unknown, Loading, Absent, Present, Error }
    private ReadingResumeState _readingResumeState;
    private bool? _readingResumeOptIn;
    private int _readingResumeStateRevision;
    private bool _readingResumePending;

    /// <summary>True while a thread is open (the reader, or the gallery over it): a click on the button then opens the history list.</summary>
    private bool ReadingThreadOpen =>
        this.FindControl<ContentControl>("ReaderHost")?.IsEffectivelyVisible == true || GalleryOverlayVisible;

    private bool GalleryOverlayVisible => this.FindControl<ContentControl>("GalleryHost")?.IsEffectivelyVisible == true;

    // An open thread does not block it (the place replaces the thread); the gallery over it does (ReadingResumeRouting.PlacesOpenable).
    private bool CanResumeReading => _restoreCompleted && !_isClosing && !_applyingPreferences
        && _session.State == DesktopSessionState.Ready
        && ReadingResumeRouting.PlacesOpenable(GalleryOverlayVisible, WorkspaceCommandBusy || _readingResumePending || _readingPositionForgetPending)
        && IsEnabled && !HasVisibleShellDialog() && !HasBlockingPageDecision()
        && _archivePage?.IsReaderDialogOpen != true;

    private void RegisterReadingResumeCommand()
    {
        _shortcutRouter?.Register(new CommandRegistration("navigation.continue-reading", this,
            () => true, () => CanResumeReading,
            () => ActivateReadingResume(keyboard: true)));
    }

    /// <summary>
    /// What the button, its shortcut and the "Continue reading" entry of the app menu do (ReadingResumeRouting.Click): outside a thread
    /// they jump to the last reading position, inside an open thread they open the history list.
    /// </summary>
    private void ActivateReadingResume(bool keyboard)
    {
        if (ReadingResumeRouting.Click(ReadingThreadOpen) == ReadingResumeRouting.ClickAction.OpenHistory) ShowReadingHistoryList(keyboard);
        else _ = ResumeLastReadingAsync();
    }

    /// <summary>Opens the history list under the arrow button (under the app menu button while the book is not shown).</summary>
    internal bool ShowReadingHistoryList(bool keyboard = false) => _readingHistoryFlyout?.Show(keyboard) == true;

    private void InvalidateReadingResumeState()
    {
        ++_readingResumeStateRevision;
        _readingResumeState = ReadingResumeState.Unknown;
    }

    private void RefreshReadingResumeState()
    {
        if (_readingResumeOptIn != _config.RememberReadingPosition)
        {
            _readingResumeOptIn = _config.RememberReadingPosition;
            InvalidateReadingResumeState();
        }
        if (!_config.RememberReadingPosition) _readingResumeState = ReadingResumeState.Absent;
        else if (_restoreCompleted && !_isClosing && _session.State == DesktopSessionState.Ready
            && _readingResumeState == ReadingResumeState.Unknown)
            _ = LoadReadingResumeStateAsync();
        RefreshReadingResumePresentation();
    }

    private async Task LoadReadingResumeStateAsync()
    {
        int revision = ++_readingResumeStateRevision;
        long generation = _session.ReadingPositionGeneration;
        _readingResumeState = ReadingResumeState.Loading;
        ReadingResumeState state;
        try { state = await _session.ReadReadingPositionAsync() == null ? ReadingResumeState.Absent : ReadingResumeState.Present; }
        catch { state = ReadingResumeState.Error; } // Explicit invocation retains the existing error/forget path.
        if (_isClosing || revision != _readingResumeStateRevision
            || generation != _session.ReadingPositionGeneration || !_config.RememberReadingPosition) return;
        _readingResumeState = state;
        RefreshReadingResumePresentation();
        _ = RefreshReadingHistorySnapshotAsync();
    }

    private void RefreshReadingResumePresentation()
    {
        // The button joins the policy in InitializeReadingResume; before that nothing can be shown anyway.
        if (this.FindControl<Button>("ContinueReadingButton") is not Button button || WorkViewElement.GetId(button) == null) return;
        // Shown whenever the history has an entry: in every work view, on every page, with a thread open (ReadingResumeRouting.Visible).
        bool visible = !_readingPositionForgetPending && ReadingResumeRouting.Visible(_config.RememberReadingPosition,
            _readingHistorySnapshot.Count, _readingResumeState == ReadingResumeState.Present);
        // The policy owns the view part of the visibility (a neutral row today); the state part is the reading position.
        WorkView view = ConfiguredWorkView;
        bool shown = visible && WorkViewGate.Visible(button, view);
        Button arrow = ContinueReadingHistoryButton;
        bool transferFocus = (button.IsKeyboardFocusWithin || arrow.IsKeyboardFocusWithin) && !shown;
        WorkViewGate.Apply(button, view, visible);
        arrow.IsVisible = button.IsVisible; // the arrow belongs to the book
        button.IsEnabled = arrow.IsEnabled = CanResumeReading;
        if (_resumeReadingMenu != null) _resumeReadingMenu.IsEnabled = CanResumeReading;
        if (_readingHistoryMenu != null)
        {
            _readingHistoryMenu.IsVisible = _config.RememberReadingPosition;
            _readingHistoryMenu.IsEnabled = CanResumeReading && visible;
        }
        RefreshReadingResumeLabels(); // the tooltip names what a click does now (a thread open or not)
        if (transferFocus)
        {
            // Never leave focus on a disappearing top-bar action. Reader anchor work
            // still owns its final exact post; other transitions use a stable shell target.
            FocusReadingResumeFallback();
        }
    }

    private void FocusReadingResumeFallback()
    {
        Control? target = this.FindControl<ContentControl>("GalleryHost")?.IsEffectivelyVisible == true ? _gallery
            : this.FindControl<ContentControl>("ReaderHost")?.IsEffectivelyVisible == true
                ? _archivePage?.FindControl<ListBox>("ArchivePosts") : null;
        target ??= this.FindControl<Button>("PauseButton");
        if (target is not { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
            target = this.FindControl<MenuItem>("WorkspaceMenuItem");
        if (!_isClosing && target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) target.Focus(NavigationMethod.Tab);
    }

    private void InitializeReadingResume()
    {
        ArchivePage.ReadingPositionCaptureAllowed = () => !_isClosing && !_readingPositionForgetPending
            && _config.RememberReadingPosition;
        ArchivePage.ConsciousReadingPositionChanged += SaveConsciousReadingPosition;
        // The archive list's thread menu offers "Continue reading at post No. n" for a thread that has a saved place (RH1).
        ArchivePage.ReadingPlaceFor = thread =>
        {
            if (!_config.RememberReadingPosition) return null;
            _ = RefreshReadingHistorySnapshotAsync(); // Current for the next opening of a menu.
            return _readingHistorySnapshot.FirstOrDefault(entry => entry.Position.ThreadId == thread)?.Position;
        };
        ArchivePage.ResumeReadingPlace = place => ResumeReadingPlaceAsync(place);
        WorkViewElement.SetId(ContinueReadingButton, "shell.header.continue-reading");
        _resumeReadingMenu = new MenuItem();
        _resumeReadingMenu.Click += (_, _) => ActivateReadingResume(keyboard: false);
        // First entry of the menu, in front of Import files (MenuToolsAnchor).
        WorkspaceMenuItem.Items.Insert(MenuToolsAnchor + 1, _resumeReadingMenu);
        // A click on the button: the last place outside a thread, the history list inside one. The keyboard (Enter, Space) is told apart from
        // the pointer by where the pointer is, so the focus ring returns after the list closes only for the keyboard.
        ContinueReadingButton.Click += (_, _) => ActivateReadingResume(keyboard: !ContinueReadingButton.IsPointerOver);
        // The header display makes room for the book icon and its arrow (HeaderMonitorShown).
        ContinueReadingButton.PropertyChanged += (_, change) => { if (change.Property == IsVisibleProperty) ApplyHeaderMonitorVisibility(); };
        InitializeReadingHistoryMenus();
        RefreshReadingResumeLabels();
    }

    private void RefreshReadingResumeLabels()
    {
        Button button = ContinueReadingButton;
        // The tooltip names what a click does now: the last place outside a thread, the history list inside one (ReadingResumeRouting).
        string ClickHint() => Texts.T(ReadingResumeRouting.TipKey(ReadingThreadOpen));
        CommandBindings.Apply(button, "navigation.continue-reading", ClickHint, iconOnly: true);
        string help = CommandBindings.Tooltip("navigation.continue-reading", ClickHint());
        // The tooltip names the thread and the post the button continues at (the newest place of the history).
        if (_readingHistorySnapshot.Count > 0 && _config.RememberReadingPosition && !ReadingThreadOpen)
            help += "\n" + ReadingHistoryMenu.EntryTip(_readingHistorySnapshot[0].Position);
        ToolTip.SetTip(button, help);
        AutomationProperties.SetHelpText(button, help);
        Button arrow = ContinueReadingHistoryButton;
        string arrowName = Texts.T("resume.history.open");
        AutomationProperties.SetName(arrow, arrowName);
        string arrowHelp = arrowName + " · " + Texts.T("resume.history.menu");
        ToolTip.SetTip(arrow, arrowName);
        AutomationProperties.SetHelpText(arrow, arrowHelp);
        if (_resumeReadingMenu == null) return;
        CommandBindings.Apply(_resumeReadingMenu, "navigation.continue-reading", ClickHint);
        AutomationProperties.SetHelpText(_resumeReadingMenu, help);
        if (_readingHistoryMenu != null) _readingHistoryMenu.Header = Texts.T("resume.history.menu");
    }

    private void InitializeReadingHistoryMenus()
    {
        // The app menu entry sits directly behind "Continue reading" (which keeps resuming the newest place outside a thread). It opens the
        // same list as the arrow: a submenu popup is as narrow as its shortest entry and cannot scroll, the list can do both.
        _readingHistoryMenu = new MenuItem { Header = Texts.T("resume.history.menu"), Icon = CommandBindings.Icon(ReadingHistoryMenu.ContinueCommandId) };
        _readingHistoryMenu.Click += (_, _) => Dispatcher.UIThread.Post(() => ShowReadingHistoryList(keyboard: false), DispatcherPriority.Input);
        WorkspaceMenuItem.Items.Insert(WorkspaceMenuItem.Items.IndexOf(_resumeReadingMenu!) + 1, _readingHistoryMenu);
        // The arrow, right-click, the menu key and Shift+F10 on the book button open the list under the arrow (or under the app menu
        // button when the header does not show the book).
        _readingHistoryFlyout = new ReadingHistoryFlyout(ReadingHistoryAnchor, ComposeReadingHistoryContent, () => new Avalonia.Size(Bounds.Width, Bounds.Height),
            place => _ = ResumeReadingPlaceAsync(place), () => _ = ShowReadingHistoryAsync(), FocusReadingResumeFallback);
        _readingHistoryFlyout.AddTrigger(ContinueReadingButton);
        _readingHistoryFlyout.AddTrigger(ContinueReadingHistoryButton);
        ContinueReadingHistoryButton.Click += (_, _) => ShowReadingHistoryList(keyboard: !ContinueReadingHistoryButton.IsPointerOver);
    }

    private Control? ReadingHistoryAnchor() =>
        ContinueReadingHistoryButton.IsEffectivelyVisible ? ContinueReadingHistoryButton : WorkspaceMenuItem;

    /// <summary>The history list as it would open now (smoke checks and tests); null when it would not open.</summary>
    internal ReadingHistoryFlyoutContent? ReadingHistoryContent => ComposeReadingHistoryContent();
    internal ReadingHistoryFlyout? ReadingHistoryList => _readingHistoryFlyout;
    internal IReadOnlyList<LocalReadingHistoryEntry> ReadingHistorySnapshot => _readingHistorySnapshot;

    private ReadingHistoryFlyoutContent? ComposeReadingHistoryContent()
    {
        if (_isClosing || !_config.RememberReadingPosition || !CanResumeReading) return null;
        _ = RefreshReadingHistorySnapshotAsync(); // The next opening shows what a write in between changed.
        return new ReadingHistoryFlyoutContent(
            ReadingHistoryMenu.Rows(_readingHistorySnapshot, _config.ReadingHistoryMenuEntries, _config.ReadingHistoryLimit, DateTimeOffset.UtcNow),
            CanResumeReading, _readingHistorySnapshot.Count > 0);
    }

    // "{title} · No.{post} · {2 h ago}" (ReadingHistoryMenu.EntryText); kept here for the callers of the window.
    internal static string ReadingHistoryEntryText(LocalReadingHistoryEntry entry, DateTimeOffset now) => ReadingHistoryMenu.EntryText(entry, now);

    /// <summary>
    /// Reads the saved places for the menus. A failed read leaves the last snapshot (the explicit resume reports its own errors); a read
    /// that a newer one or a forget overtook is dropped.
    /// </summary>
    internal async Task RefreshReadingHistorySnapshotAsync()
    {
        if (_isClosing || !_restoreCompleted || _session.State != DesktopSessionState.Ready) return;
        int revision = ++_readingHistorySnapshotRevision;
        long generation = _session.ReadingPositionGeneration;
        IReadOnlyList<LocalReadingHistoryEntry> entries;
        try
        {
            try { await _readingPositionSave; } catch (Exception) { } // A failed save is reported where it happened.
            entries = await _session.ReadReadingHistoryAsync();
        }
        catch (Exception) { return; }
        if (_isClosing || revision != _readingHistorySnapshotRevision || generation != _session.ReadingPositionGeneration) return;
        _readingHistorySnapshot = entries;
        RefreshReadingResumePresentation(); // the button is there as long as the history has an entry; it also refreshes the labels
    }

    private void ClearReadingHistorySnapshot()
    {
        ++_readingHistorySnapshotRevision;
        _readingHistorySnapshot = Array.Empty<LocalReadingHistoryEntry>();
        RefreshReadingResumePresentation();
    }

    /// <summary>"Show full history…": every remembered thread in a window; a click opens that place.</summary>
    internal async Task ShowReadingHistoryAsync()
    {
        if (!CanResumeReading) return;
        IReadOnlyList<LocalReadingHistoryEntry> entries;
        try
        {
            try { await _readingPositionSave; } catch (Exception) { }
            entries = await _session.ReadReadingHistoryAsync();
        }
        catch (Exception error)
        {
            if (!_isClosing) ShowNotification(Texts.T("notification.error"),
                Texts.T("resume.readFailed", UserFacingError.Report(error, "Reading the reading history")), NotificationType.Error, operation: "preferences.reading-position");
            return;
        }
        if (_isClosing || !CanResumeReading) return;
        LocalReadingPosition? place = await ReadingHistoryWindow.ShowAsync(this, entries, DateTimeOffset.UtcNow);
        if (place != null && !_isClosing) await ResumeReadingPlaceAsync(place);
    }

    private void SaveConsciousReadingPosition(LocalReadingPosition position)
    {
        // Reading before the session is ready is display only: no profile write is queued for later.
        if (_isClosing || !_restoreCompleted || _readingPositionForgetPending || !_config.RememberReadingPosition) return;
        long generation = _session.ReadingPositionGeneration;
        int revision = ++_readingResumeStateRevision;
        _readingResumeState = ReadingResumeState.Loading;
        RefreshReadingResumePresentation();
        _submittedReadingPosition = position;
        _submittedReadingGeneration = generation;
        _submittedReadingRevision = revision;
        Task write;
        try { write = _session.RememberReadingPositionAsync(position, generation); }
        catch (Exception error) { write = Task.FromException(error); }
        if (!ReferenceEquals(write, _readingPositionWrite))
        {
            _readingPositionWrite = write;
            _readingPositionSave = ObserveReadingPositionWriteAsync(write, position);
        }
    }

    private async Task ObserveReadingPositionWriteAsync(Task write, LocalReadingPosition firstPosition)
    {
        try
        {
            await write;
            // A shared pending completion represents its latest anchor, not its first caller's revision.
            if (ReferenceEquals(write, _readingPositionWrite) && !_isClosing
                && _submittedReadingRevision == _readingResumeStateRevision && _config.RememberReadingPosition
                && _submittedReadingGeneration == _session.ReadingPositionGeneration)
            {
                _readingResumeState = ReadingResumeState.Present;
                RefreshReadingResumePresentation();
                _ = RefreshReadingHistorySnapshotAsync();
            }
        }
        catch (Exception error)
        {
            bool current = ReferenceEquals(write, _readingPositionWrite);
            LocalReadingPosition position = current ? _submittedReadingPosition! : firstPosition;
            if (!_isClosing && current && _submittedReadingRevision == _readingResumeStateRevision)
            {
                _readingResumeState = ReadingResumeState.Error;
                RefreshReadingResumePresentation();
            }
            string cause = UserFacingError.Report(error, "Saving the reading position");
            if (!_isClosing && ArchivePage.CurrentThread?.Summary.Id == position.ThreadId)
                ArchivePage.ShowReadingPositionMessage(Texts.T("resume.saveFailed", cause), true);
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("resume.saveFailed", cause), NotificationType.Error, context: position.ThreadId.ToString(), operation: "preferences.reading-position");
        }
    }

    private async Task ForgetReadingPositionAsync()
    {
        _readingPositionForgetPending = true;
        InvalidateReadingResumeState();
        RefreshReadingResumePresentation();
        ArchivePage.InvalidatePendingReadingInput();
        try
        {
            await _session.ForgetReadingPositionAsync();
            _readingResumeState = ReadingResumeState.Absent;
            ClearReadingHistorySnapshot();
        }
        catch { _readingResumeState = ReadingResumeState.Error; throw; }
        finally { _readingPositionForgetPending = false; RefreshReadingResumePresentation(); }
    }

    internal Task ResumeLastReadingAsync() => ResumeReadingCoreAsync(async stillRequested =>
    {
        await _readingPositionSave;
        return stillRequested() ? await _session.ReadReadingPositionAsync() : null;
    });

    /// <summary>Opens one place of the reading history; the newest place and the stored position stay as they are.</summary>
    internal Task ResumeReadingPlaceAsync(LocalReadingPosition place) =>
        ResumeReadingCoreAsync(_ => Task.FromResult<LocalReadingPosition?>(place));

    private async Task ResumeReadingCoreAsync(Func<Func<bool>, Task<LocalReadingPosition?>> load)
    {
        // With a thread open the place replaces it where it is; the page behind the reader stays as it is.
        if (!CanResumeReading || (!_largeReaderOpen && !NavigateWorkspace(WorkspacePage.Archive, activateArchive: false))) return;
        bool ownedFocus = ContinueReadingButton.IsKeyboardFocusWithin;
        _readingResumePending = true;
        RefreshReadingResumePresentation();
        int navigation = _readingResumeNavigationRevision;
        long generation = _session.ReadingPositionGeneration;
        Func<bool> readerUnchanged = ArchivePage.CaptureReadingRequestGuard();
        bool StillRequested() => !_isClosing && navigation == _readingResumeNavigationRevision
            && generation == _session.ReadingPositionGeneration && readerUnchanged();
        try
        {
            LocalReadingPosition? position = await load(StillRequested);
            if (!StillRequested()) return;
            _readingResumeState = position == null ? ReadingResumeState.Absent : ReadingResumeState.Present;
            if (position == null) ArchivePage.ShowReadingPositionMessage(Texts.T("resume.none"));
            else await ArchivePage.ResumeReadingPositionAsync(position, StillRequested);
        }
        catch (Exception error)
        {
            if (StillRequested())
            {
                _readingResumeState = ReadingResumeState.Error;
                ArchivePage.ShowReadingPositionMessage(Texts.T("resume.readFailed", UserFacingError.Report(error, "Resuming the reading position")), true);
            }
        }
        finally
        {
            _readingResumePending = false;
            RefreshReadingResumePresentation();
            if (ownedFocus && FocusManager?.GetFocusedElement() == null) FocusReadingResumeFallback();
        }
    }
}
