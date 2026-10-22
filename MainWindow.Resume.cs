using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private MenuItem? _resumeReadingMenu;
    private MenuItem? _resumeReadingToolsMenu;
    private Task _readingPositionSave = Task.CompletedTask;
    private bool _readingPositionForgetPending;

    private void InitializeReadingResume()
    {
        ArchivePage.ConsciousReadingPositionChanged += SaveConsciousReadingPosition;
        _resumeReadingMenu = new MenuItem();
        _resumeReadingMenu.Click += async (_, _) => await ResumeLastReadingAsync();
        this.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.Insert(1, _resumeReadingMenu);
        if (this.FindControl<Button>("ToolsNavigationButton")!.Flyout is MenuFlyout tools)
        {
            _resumeReadingToolsMenu = new MenuItem();
            _resumeReadingToolsMenu.Click += async (_, _) =>
            {
                this.FindControl<Button>("ToolsNavigationButton")!.Flyout?.Hide();
                await ResumeLastReadingAsync();
            };
            tools.Items.Insert(1, _resumeReadingToolsMenu);
        }
        RefreshReadingResumeLabels();
    }

    private void RefreshReadingResumeLabels()
    {
        foreach (MenuItem? item in new[] { _resumeReadingMenu, _resumeReadingToolsMenu })
        {
            if (item == null) continue;
            item.Header = Texts.T("resume.action");
            AutomationProperties.SetName(item, Texts.T("resume.action"));
            AutomationProperties.SetHelpText(item, Texts.T("resume.hint"));
        }
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
                ArchivePage.ShowReadingPositionMessage(Texts.T("resume.saveFailed", error.Message), true);
                if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("resume.saveFailed", error.Message), NotificationType.Error);
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
        try
        {
            await _readingPositionSave;
            long generation = _session.ReadingPositionGeneration;
            LocalReadingPosition? position = await _session.ReadReadingPositionAsync();
            bool StillRequested() => !_isClosing && _selectedPage == WorkspacePage.Archive && generation == _session.ReadingPositionGeneration;
            if (!StillRequested()) return;
            if (position == null) ArchivePage.ShowReadingPositionMessage(Texts.T("resume.none"));
            else await ArchivePage.ResumeReadingPositionAsync(position, StillRequested);
        }
        catch (Exception error) { ArchivePage.ShowReadingPositionMessage(Texts.T("resume.readFailed", error.Message), true); }
    }
}
