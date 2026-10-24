using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private MenuItem? _resumeReadingMenu;
    private Task _readingPositionSave = Task.CompletedTask;
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
        if (this.FindControl<Button>("ContinueReadingButton") is not Button button) return;
        bool visible = _config.RememberReadingPosition && _readingResumeState == ReadingResumeState.Present
            && !ReadingResumeContextVisible;
        bool transferFocus = button.IsKeyboardFocusWithin && !visible;
        button.IsVisible = visible;
        button.IsEnabled = CanResumeReading;
        if (_resumeReadingMenu != null) _resumeReadingMenu.IsEnabled = CanResumeReading;
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
        _resumeReadingMenu = new MenuItem();
        _resumeReadingMenu.Click += async (_, _) => await ResumeLastReadingAsync();
        this.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.Insert(1, _resumeReadingMenu);
        this.FindControl<Button>("ContinueReadingButton")!.Click += async (_, _) => await ResumeLastReadingAsync();
        RefreshReadingResumeLabels();
    }

    private void RefreshReadingResumeLabels()
    {
        Button button = this.FindControl<Button>("ContinueReadingButton")!;
        CommandBindings.Apply(button, "navigation.continue-reading",
            () => Texts.T("resume.actionHint"), iconOnly: true);
        string help = CommandBindings.Tooltip("navigation.continue-reading") + " · " + Texts.T("resume.actionHint");
        ToolTip.SetTip(button, help);
        AutomationProperties.SetHelpText(button, help);
        if (_resumeReadingMenu == null) return;
        CommandBindings.Apply(_resumeReadingMenu, "navigation.continue-reading",
            () => Texts.T("resume.actionHint"));
        AutomationProperties.SetHelpText(_resumeReadingMenu, help);
    }

    private void SaveConsciousReadingPosition(LocalReadingPosition position)
    {
        if (_isClosing || _readingPositionForgetPending || !_config.RememberReadingPosition) return;
        long generation = _session.ReadingPositionGeneration;
        int revision = ++_readingResumeStateRevision;
        _readingResumeState = ReadingResumeState.Loading;
        RefreshReadingResumePresentation();
        Task previous = _readingPositionSave;
        async Task SaveAsync()
        {
            try
            {
                await previous;
                await _session.RememberReadingPositionAsync(position, generation);
                if (!_isClosing && revision == _readingResumeStateRevision && _config.RememberReadingPosition
                    && generation == _session.ReadingPositionGeneration)
                {
                    _readingResumeState = ReadingResumeState.Present;
                    RefreshReadingResumePresentation();
                }
            }
            catch (Exception error)
            {
                if (!_isClosing && revision == _readingResumeStateRevision)
                {
                    _readingResumeState = ReadingResumeState.Error;
                    RefreshReadingResumePresentation();
                }
                if (!_isClosing && ArchivePage.CurrentThread?.Summary.Id == position.ThreadId)
                    ArchivePage.ShowReadingPositionMessage(Texts.T("resume.saveFailed", error.Message), true);
                if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("resume.saveFailed", error.Message), NotificationType.Error, context: position.ThreadId.ToString(), operation: "preferences.reading-position");
            }
        }
        _readingPositionSave = SaveAsync();
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

    internal async Task ResumeLastReadingAsync()
    {
        if (!CanResumeReading || !NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        bool ownedFocus = this.FindControl<Button>("ContinueReadingButton")!.IsKeyboardFocusWithin;
        _readingResumePending = true;
        RefreshReadingResumePresentation();
        int navigation = _readingResumeNavigationRevision;
        long generation = _session.ReadingPositionGeneration;
        Func<bool> readerUnchanged = ArchivePage.CaptureReadingRequestGuard();
        bool StillRequested() => !_isClosing && navigation == _readingResumeNavigationRevision
            && generation == _session.ReadingPositionGeneration && readerUnchanged();
        try
        {
            await _readingPositionSave;
            if (!StillRequested()) return;
            LocalReadingPosition? position = await _session.ReadReadingPositionAsync();
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
                ArchivePage.ShowReadingPositionMessage(Texts.T("resume.readFailed", error.Message), true);
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
