using System;
using System.Collections.Generic;
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
    // R5: the recent reading places, one per thread. Right-click on the book button and the "Reading history" submenu list them.
    private MenuItem? _readingHistoryMenu;
    private ContextMenu? _readingHistoryContext;
    private int _readingHistoryRevision;
    private const int ReadingHistoryTitleDisplayLength = 60;
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

    private bool ReadingResumeContextVisible =>
        this.FindControl<ContentControl>("ReaderHost")?.IsEffectivelyVisible == true
        || this.FindControl<ContentControl>("GalleryHost")?.IsEffectivelyVisible == true;

    private bool CanResumeReading => _restoreCompleted && !_isClosing && !_applyingPreferences
        && _session.State == DesktopSessionState.Ready && !WorkspaceCommandBusy
        && !_readingResumePending && !_readingPositionForgetPending && !ReadingResumeContextVisible
        && IsEnabled && !HasVisibleShellDialog() && !HasBlockingPageDecision()
        && _archivePage?.IsReaderDialogOpen != true;

    private void RegisterReadingResumeCommand()
    {
        _shortcutRouter?.Register(new CommandRegistration("navigation.continue-reading", this,
            () => !ReadingResumeContextVisible, () => CanResumeReading,
            () => _ = ResumeLastReadingAsync()));
    }

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
    }

    private void RefreshReadingResumePresentation()
    {
        // The button joins the policy in InitializeReadingResume; before that nothing can be shown anyway.
        if (this.FindControl<Button>("ContinueReadingButton") is not Button button || WorkViewElement.GetId(button) == null) return;
        bool visible = _config.RememberReadingPosition && _readingResumeState == ReadingResumeState.Present
            && !ReadingResumeContextVisible;
        // The policy owns the view part of the visibility (a neutral row today); the state part is the reading position.
        WorkView view = ConfiguredWorkView;
        bool shown = visible && WorkViewGate.Visible(button, view);
        bool transferFocus = button.IsKeyboardFocusWithin && !shown;
        WorkViewGate.Apply(button, view, visible);
        button.IsEnabled = CanResumeReading;
        if (_resumeReadingMenu != null) _resumeReadingMenu.IsEnabled = CanResumeReading;
        if (_readingHistoryMenu != null)
        {
            _readingHistoryMenu.IsVisible = _config.RememberReadingPosition;
            _readingHistoryMenu.IsEnabled = CanResumeReading && _readingResumeState == ReadingResumeState.Present;
        }
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
        WorkViewElement.SetId(ContinueReadingButton, "shell.header.continue-reading");
        _resumeReadingMenu = new MenuItem();
        _resumeReadingMenu.Click += async (_, _) => await ResumeLastReadingAsync();
        // First entry of the tools group, directly behind the page group's separator.
        WorkspaceMenuItem.Items.Insert(WorkspaceMenuItem.Items.IndexOf(MenuPagesToolsSeparator) + 1, _resumeReadingMenu);
        ContinueReadingButton.Click += async (_, _) => await ResumeLastReadingAsync();
        // The header display makes room for the book icon (HeaderMonitorShown).
        ContinueReadingButton.PropertyChanged += (_, change) => { if (change.Property == IsVisibleProperty) ApplyHeaderMonitorVisibility(); };
        InitializeReadingHistoryMenus();
        RefreshReadingResumeLabels();
    }

    private void RefreshReadingResumeLabels()
    {
        Button button = ContinueReadingButton;
        CommandBindings.Apply(button, "navigation.continue-reading",
            () => Texts.T("resume.actionHint"), iconOnly: true);
        string help = CommandBindings.Tooltip("navigation.continue-reading") + " · " + Texts.T("resume.actionHint")
            + " " + Texts.T("resume.history.hint");
        ToolTip.SetTip(button, help);
        AutomationProperties.SetHelpText(button, help);
        if (_resumeReadingMenu == null) return;
        CommandBindings.Apply(_resumeReadingMenu, "navigation.continue-reading",
            () => Texts.T("resume.actionHint"));
        AutomationProperties.SetHelpText(_resumeReadingMenu, help);
        if (_readingHistoryMenu != null) _readingHistoryMenu.Header = Texts.T("resume.history.menu");
    }

    private void InitializeReadingHistoryMenus()
    {
        // The submenu sits directly behind "Continue reading" (which keeps resuming the newest place); its entries are read when it opens.
        _readingHistoryMenu = new MenuItem { Header = Texts.T("resume.history.menu") };
        _readingHistoryMenu.Items.Add(ReadingHistoryNote(Texts.T("resume.history.loading")));
        _readingHistoryMenu.SubmenuOpened += (_, _) => _ = LoadReadingHistoryAsync(
            items => { _readingHistoryMenu.Items.Clear(); foreach (Control item in items) _readingHistoryMenu.Items.Add(item); },
            () => _readingHistoryMenu.IsSubMenuOpen);
        WorkspaceMenuItem.Items.Insert(WorkspaceMenuItem.Items.IndexOf(_resumeReadingMenu!) + 1, _readingHistoryMenu);
        _readingHistoryContext = ContextMenuBuilder.Empty();
        _readingHistoryContext.Opening += (_, e) =>
        {
            if (!CanResumeReading) { e.Cancel = true; return; }
            _ = LoadReadingHistoryAsync(
                items => { _readingHistoryContext.Items.Clear(); foreach (Control item in items) _readingHistoryContext.Items.Add(item); },
                () => _readingHistoryContext.IsOpen);
        };
        ContinueReadingButton.ContextMenu = _readingHistoryContext;
    }

    private static MenuItem ReadingHistoryNote(string text) => new() { Header = text.Replace("_", "__"), IsEnabled = false };

    // "{title} · No.{post} · {2 h ago}". Underscores in a title are access-key markers in a menu header: doubled they stay literal.
    internal static string ReadingHistoryEntryText(LocalReadingHistoryEntry entry, DateTimeOffset now)
    {
        LocalReadingPosition position = entry.Position;
        string title = position.Title ?? Texts.T("resume.history.untitled", position.Board, position.Thread);
        if (title.Length > ReadingHistoryTitleDisplayLength) title = title[..(ReadingHistoryTitleDisplayLength - 1)].TrimEnd() + "…";
        return title + " · " + Texts.T("reader.postNumber", position.PostId) + " · " + ReaderDateText.Relative(entry.At, now);
    }

    private async Task LoadReadingHistoryAsync(Action<IReadOnlyList<Control>> show, Func<bool> stillOpen)
    {
        int revision = ++_readingHistoryRevision;
        show(new[] { ReadingHistoryNote(Texts.T("resume.history.loading")) });
        List<Control> items = new();
        try
        {
            try { await _readingPositionSave; } catch (Exception) { } // A failed save is reported where it happened.
            IReadOnlyList<LocalReadingHistoryEntry> entries = await _session.ReadReadingHistoryAsync();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (LocalReadingHistoryEntry entry in entries)
            {
                LocalReadingPosition place = entry.Position;
                string text = ReadingHistoryEntryText(entry, now);
                MenuItem item = new() { Header = text.Replace("_", "__"), IsEnabled = CanResumeReading };
                AutomationProperties.SetName(item, text);
                ToolTip.SetTip(item, place.Title is { } full ? full + " · " + place.Source + " /" + place.Board + "/ · " + place.Thread : null);
                item.Click += async (_, _) => await ResumeReadingPlaceAsync(place);
                items.Add(item);
            }
            if (items.Count == 0) items.Add(ReadingHistoryNote(Texts.T("resume.history.empty")));
        }
        catch (Exception error)
        {
            items.Clear();
            items.Add(ReadingHistoryNote(Texts.T("resume.readFailed", UserFacingError.Report(error, "Reading the reading history"))));
        }
        if (_isClosing || revision != _readingHistoryRevision || !stillOpen()) return;
        show(items);
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
        if (!CanResumeReading || !NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
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
