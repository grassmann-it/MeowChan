using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
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

    private void InitializeReadingResume()
    {
        ArchivePage.ReadingPositionCaptureAllowed = () => !_isClosing && !_readingPositionForgetPending
            && _config.RememberReadingPosition;
        ArchivePage.ConsciousReadingPositionChanged += SaveConsciousReadingPosition;
        _resumeReadingMenu = new MenuItem();
        _resumeReadingMenu.Click += async (_, _) => await ResumeLastReadingAsync();
        this.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.Insert(1, _resumeReadingMenu);
        this.FindControl<Button>("ContinueReadingNavigationButton")!.Click += async (_, _) => await ResumeLastReadingAsync();
        RefreshReadingResumeLabels();
    }

    private void RefreshReadingResumeLabels()
    {
        Button button = this.FindControl<Button>("ContinueReadingNavigationButton")!;
        CommandBindings.Apply(button, "navigation.continue-reading", iconWithLabel: true);
        ToolTip.SetTip(button, Texts.T("resume.hint"));
        AutomationProperties.SetHelpText(button, Texts.T("resume.hint"));
        if (_resumeReadingMenu == null) return;
        CommandBindings.Apply(_resumeReadingMenu, "navigation.continue-reading");
        AutomationProperties.SetHelpText(_resumeReadingMenu, Texts.T("resume.hint"));
    }

    private void SaveConsciousReadingPosition(LocalReadingPosition position)
    {
        if (_isClosing || _readingPositionForgetPending || !_config.RememberReadingPosition) return;
        long generation = _session.ReadingPositionGeneration;
        Task previous = _readingPositionSave;
        async Task SaveAsync()
        {
            try
            {
                await previous;
                await _session.RememberReadingPositionAsync(position, generation);
            }
            catch (Exception error)
            {
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
        ArchivePage.InvalidatePendingReadingInput();
        try { await _session.ForgetReadingPositionAsync(); }
        finally { _readingPositionForgetPending = false; }
    }

    internal async Task ResumeLastReadingAsync()
    {
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
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
            if (position == null) ArchivePage.ShowReadingPositionMessage(Texts.T("resume.none"));
            else await ArchivePage.ResumeReadingPositionAsync(position, StillRequested);
        }
        catch (Exception error)
        {
            if (StillRequested()) ArchivePage.ShowReadingPositionMessage(Texts.T("resume.readFailed", error.Message), true);
        }
    }
}
