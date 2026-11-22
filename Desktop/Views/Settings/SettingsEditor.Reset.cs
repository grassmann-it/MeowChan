using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// Draft-only reset actions for the settings page. Every assignment is explicit:
/// the reset surface never copies hidden runtime state, consent, packs or shortcut
/// bindings, and it never writes or applies a Config.
/// </summary>
public partial class SettingsEditor
{
    internal enum SettingsResetScope
    {
        Interface,
        ReadingMedia,
        Downloads,
        WorkerBudgets,
        Updates,
        Notifications,
        Behavior,
        All,
    }

    private SettingsDefaults? _resetDefaults;
    private Flyout? _resetConfirmation;
    private Button? _resetConfirmationOrigin;
    private bool _resetStatusVisible;

    private SettingsDefaults ResetDefaults => _resetDefaults ??= new SettingsDefaults();

    /// <summary>The names of the section "Reset" buttons (one per settings section; the draft-only reset of that section).</summary>
    internal static readonly string[] SectionResetButtonNames =
    [
        "ResetInterfaceButton", "ResetReadingMediaButton", "ResetDownloadsButton", "ResetWorkerBudgetsButton",
        "ResetUpdatesButton", "ResetNotificationsButton", "ResetBehaviorButton"
    ];

    // I1b: every section's Reset carries the rotate-ccw icon before its text (the same icon the "Reset all" menu entry has). The text stays
    // the bound content, so the label, the per-section accessible name and the tooltip are unchanged; the content template draws the icon.
    private void InitializeResetIcons()
    {
        foreach (string name in SectionResetButtonNames)
            if (this.FindControl<Button>(name) is { } button) CommandBindings.EnsureIconBeforeText(button, "rotate-ccw");
    }

    public string ResetLabel => Texts.T("settings.reset");
    public string ResetAllLabel => Texts.T("settings.reset.all");
    public string ResetInterfaceLabel => Texts.T("settings.reset.section", InterfaceTitle);
    public string ResetReadingMediaLabel => Texts.T("settings.reset.section", ReadingMediaTitle);
    public string ResetDownloadsLabel => Texts.T("settings.reset.section", DownloadsTitle);
    public string ResetWorkerBudgetsLabel => Texts.T("settings.reset.section", StorageLimitsLabel);
    public string ResetUpdatesLabel => Texts.T("settings.reset.section", UpdatesTitle);
    public string ResetNotificationsLabel => Texts.T("settings.reset.section", NotificationsTitle);
    public string ResetBehaviorLabel => Texts.T("settings.reset.section", BehaviorTitle);

    private string ResetQuestion(SettingsResetScope scope) => scope == SettingsResetScope.All
        ? Texts.T("settings.reset.allQuestion")
        : Texts.T("settings.reset.question", ResetScopeTitle(scope));

    private string ResetScopeTitle(SettingsResetScope scope) => scope switch
    {
        SettingsResetScope.Interface => InterfaceTitle,
        SettingsResetScope.ReadingMedia => ReadingMediaTitle,
        SettingsResetScope.Downloads => DownloadsTitle,
        SettingsResetScope.WorkerBudgets => StorageLimitsLabel,
        SettingsResetScope.Updates => UpdatesTitle,
        SettingsResetScope.Notifications => NotificationsTitle,
        SettingsResetScope.Behavior => BehaviorTitle,
        _ => SettingsTitle,
    };

    private void RaiseResetTextsChanged()
    {
        if (_isClosed) return;
        foreach (string property in new[]
        {
            nameof(ResetLabel), nameof(ResetAllLabel), nameof(ResetInterfaceLabel), nameof(ResetReadingMediaLabel),
            nameof(ResetDownloadsLabel), nameof(ResetWorkerBudgetsLabel), nameof(ResetUpdatesLabel),
            nameof(ResetNotificationsLabel), nameof(ResetBehaviorLabel)
        })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
        if (_resetStatusVisible) SettingsResetStatus.Text = Texts.T("settings.reset.done");
        if (_resetConfirmation is { IsOpen: true }) OpenResetConfirmation(_resetConfirmationOrigin!, GetResetScope(_resetConfirmationOrigin!));
    }

    private void OnResetCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || !TryGetResetScope(id, out SettingsResetScope scope)) return;
        OpenResetConfirmation(button, scope);
    }

    private static bool TryGetResetScope(string id, out SettingsResetScope scope)
    {
        scope = id switch
        {
            "settings.interface" => SettingsResetScope.Interface,
            "settings.reading-media" => SettingsResetScope.ReadingMedia,
            "settings.downloads" => SettingsResetScope.Downloads,
            "settings.worker-budgets" => SettingsResetScope.WorkerBudgets,
            "settings.updates" => SettingsResetScope.Updates,
            "settings.notifications" => SettingsResetScope.Notifications,
            "settings.behavior" => SettingsResetScope.Behavior,
            _ => SettingsResetScope.All,
        };
        return id is "settings.interface" or "settings.reading-media" or "settings.downloads"
            or "settings.worker-budgets" or "settings.updates" or "settings.notifications" or "settings.behavior";
    }

    private static SettingsResetScope GetResetScope(Button button) => button.Tag is string id && TryGetResetScope(id, out SettingsResetScope scope)
        ? scope : SettingsResetScope.All;

    private void OpenResetConfirmation(Button origin, SettingsResetScope scope)
    {
        if (_isClosed || !IsEditable) return;
        _resetConfirmation?.Hide();
        _resetConfirmationOrigin = origin;

        var question = new TextBlock { Text = ResetQuestion(scope), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(question, AutomationLiveSetting.Polite);
        var confirm = new Button { Content = Texts.T("settings.reset.confirm"), HorizontalAlignment = HorizontalAlignment.Stretch };
        confirm.Classes.Add("danger");
        AutomationProperties.SetName(confirm, Texts.T("settings.reset.confirm"));
        var cancel = new Button { Content = Texts.T("settings.reset.cancel"), HorizontalAlignment = HorizontalAlignment.Stretch };
        cancel.Classes.Add("ghost");
        AutomationProperties.SetName(cancel, Texts.T("settings.reset.cancel"));
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        var panel = new Border
        {
            Padding = new Thickness(12),
            MaxWidth = 360,
            Child = new StackPanel { Spacing = 12, Children = { question, actions } }
        };
        AutomationProperties.SetName(panel, ResetQuestion(scope));
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = panel };
        _resetConfirmation = flyout;
        confirm.Click += (_, _) =>
        {
            ResetDraft(scope);
            flyout.Hide();
            origin.Focus();
        };
        cancel.Click += (_, _) =>
        {
            flyout.Hide();
            origin.Focus();
        };
        flyout.Opened += (_, _) => cancel.Focus();
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_resetConfirmation, flyout)) _resetConfirmation = null;
        };
        flyout.ShowAt(origin);
    }

    // Internal for the tests (SettingsResetCoverageTests): they run every scope against the real controls.
    internal void ResetDraft(SettingsResetScope scope)
    {
        if (_isClosed || IsBusy || !IsAvailable) return;
        switch (scope)
        {
            case SettingsResetScope.Interface: ResetInterfaceDraft(); break;
            case SettingsResetScope.ReadingMedia: ResetReadingMediaDraft(); break;
            case SettingsResetScope.Downloads: ResetDownloadsDraft(); break;
            case SettingsResetScope.WorkerBudgets: ResetWorkerBudgetsDraft(); break;
            case SettingsResetScope.Updates: ResetUpdatesDraft(); break;
            case SettingsResetScope.Notifications: ResetNotificationsDraft(); break;
            case SettingsResetScope.Behavior: ResetBehaviorDraft(); break;
            case SettingsResetScope.All:
                ResetInterfaceDraft();
                ResetReadingMediaDraft();
                ResetDownloadsDraft();
                ResetWorkerBudgetsDraft();
                ResetUpdatesDraft();
                ResetNotificationsDraft();
                ResetBehaviorDraft();
                break;
        }

        // A reset returns the affected numeric text to a valid draft. Reuse the
        // editor's existing validation cleanup and let R6 perform one coalesced
        // dirty/error sweep after the burst of property changes.
        ClearFieldErrors();
        SetStatusMessage("settings.reset.done");
        _resetStatusVisible = true;
        SettingsResetStatus.Text = Texts.T("settings.reset.done");
        SettingsResetStatus.IsVisible = true;
        SweepGroups();
    }

    private void ResetInterfaceDraft()
    {
        Config defaults = ResetDefaults.Values;
        SelectedTheme = Choice(ThemeChoices, defaults.Theme);
        SelectedAnimations = Choice(AnimationsChoices, defaults.Animations);
        SelectedLanguage = LanguageChoices.FirstOrDefault(item => string.Equals(item.Code, defaults.Language, StringComparison.OrdinalIgnoreCase));
        SelectedWorkspaceMode = Choice(WorkspaceChoices, defaults.WorkspaceMode);
        CompactDensity = defaults.CompactDensity;
        ShrinkWindowForBarebones = defaults.ShrinkWindowForBarebones;
        ResetFooterDraft();
        // The header tagline (mode and fixed quip) and the header monitor switch are shown in this section. The monitor switch and the
        // "Header graph" choice of the Behavior section are one stored setting, so the graph choice is reset here too.
        RefreshHeaderTaglineChoices(defaults.HeaderTagline, defaults.HeaderTaglineFixedId);
        LoadHeaderTaglineIntervalDraft(defaults.HeaderTaglineIntervalMinutes);
        RefreshStatsHeaderGraphChoices(defaults.StatsHeaderGraph);
    }

    private void ResetReadingMediaDraft()
    {
        Config defaults = ResetDefaults.Values;
        SelectedReadingFontSize = Choice(ReadingFontSizeChoices, defaults.ReadingFontSize);
        SelectedReaderPostWidth = Choice(ReaderPostWidthChoices, defaults.ReaderPostWidth);
        VideoAutoplay = defaults.VideoAutoplay;
        VideoStartMuted = defaults.VideoStartMuted;
        VideoVolume = defaults.VideoVolume;
        ResetNumber(VideoVolumeInput, defaults.VideoVolume);
        RememberReadingPosition = defaults.RememberReadingPosition;
        ShowShortcutHelp = defaults.ShowShortcutHelp;

        LoadReaderDraft(defaults);
        GalleryPlaybackPreferences playback = GalleryPlaybackPreferences.FromConfig(defaults);
        SelectedGalleryPlaybackMode = Choice(GalleryPlaybackModeChoices, playback.Mode.ToString());
        GallerySlideshowSeconds = playback.ImageSeconds;
        ResetNumber(GallerySlideshowSecondsInput, playback.ImageSeconds);
        GalleryMediaFocus = playback.MediaFocus;
        GalleryOverlayControls = playback.OverlayControls;
        GalleryOverlayPostContext = playback.OverlayPostContext;
        LoadGalleryVideoDraft(playback);
        LoadViewerDraft(defaults, layoutReset: true);
        // The tagging group is shown here (after the gallery). Draft only: "start in the card mode" keeps its loaded baseline.
        LoadTaggingDraft(defaults, captureBaseline: false);
    }

    private void ResetDownloadsDraft()
    {
        Config defaults = ResetDefaults.Values;
        DownloadPath = defaults.DownloadPath;
        ExportFilenameTemplate = defaults.ExportFilenameTemplate;
        RecheckTimer = defaults.RecheckTimer;
        MaxParallelDownloads = defaults.MaxParallelDownloads;
        ParallelDownloadsSafetyLimit = defaults.ParallelDownloadsSafetyLimit;
        BandwidthLimiterMB = ResetDefaults.BandwidthLimiterMiB;
        ResetNumber(RecheckTimerInput, defaults.RecheckTimer);
        ResetNumber(ParallelDownloadsInput, defaults.MaxParallelDownloads);
        ResetNumber(ParallelDownloadsSafetyInput, defaults.ParallelDownloadsSafetyLimit);
        ResetNumber(BandwidthInput, BandwidthLimiterMB.Value);
        _destinationStatus = null;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DestinationStatusText)));
    }

    private void ResetWorkerBudgetsDraft()
    {
        Config defaults = ResetDefaults.Values;
        // The budgets, caches, imports and folder-source limits come from the loader that the start, the discard and the save use too.
        LoadBudgetsDraft(defaults);
        // Shown in this section although the reader loader owns them.
        PostFilterRuleLimit = defaults.PostFilterRuleLimit;
        HiddenPostLimit = defaults.HiddenPostLimit;
        ResetNumber(PostFilterRuleLimitInput, defaults.PostFilterRuleLimit);
        ResetNumber(HiddenPostLimitInput, defaults.HiddenPostLimit);
        LoadQuickTagDraft(defaults);
        LoadLimitNumbersDraft(defaults);
        // The tagging queue limits and the media-page widths; the widths keep their loaded baseline (the merge on save compares with it).
        LoadTagQueueDraft(defaults, captureBaseline: false);
        LoadBooruGalleryDraft(defaults);
    }

    private void ResetUpdatesDraft()
    {
        Config defaults = ResetDefaults.Values;
        AutomaticUpdateChecks = defaults.AutomaticUpdateChecks;
        IncludePrereleaseUpdates = defaults.IncludePrereleaseUpdates;
    }

    private void ResetNotificationsDraft()
    {
        Config defaults = ResetDefaults.Values;
        ShowInformationNotifications = defaults.ShowInformationNotifications;
        ShowWarningNotifications = defaults.ShowWarningNotifications;
        ShowErrorNotifications = defaults.ShowErrorNotifications;
        WorkViewSwitchNoticeSeconds = defaults.WorkViewSwitchNoticeSeconds;
        ResetNumber(WorkViewSwitchNoticeSecondsInput, defaults.WorkViewSwitchNoticeSeconds);
    }

    private void ResetBehaviorDraft()
    {
        Config defaults = ResetDefaults.Values;
        SaveUrlsOnExit = defaults.SaveUrlsOnExit;
        ShowBoardAdmissionWizard = defaults.ShowBoardAdmissionWizard;
        MonitorClipboardUrls = defaults.MonitorClipboardUrls;
        MinimizeToTray = defaults.MinimizeToTray;
        CloseToTray = defaults.CloseToTray;
        LoadShutdownLimitDraft(defaults);
        LoadStatisticsDraft(defaults);
        StatsHeaderIcons = defaults.StatsHeaderIcons;
        RefreshStatsHeaderGraphChoices(defaults.StatsHeaderGraph);
    }

    private static SettingsChoice? Choice(System.Collections.Generic.IReadOnlyList<SettingsChoice> choices, string value) =>
        choices.FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.Ordinal));

    private void EndResetLifetime()
    {
        _resetConfirmation?.Hide();
        _resetConfirmation = null;
        _resetConfirmationOrigin = null;
    }
}
