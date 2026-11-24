using System;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// The "Footer" group of Settings › Appearance (F1): which main pages have a footer button of their own and in which order. The list itself is
// the FooterLayoutEditor; this file holds its texts, its draft life (load, cancel, reset, save) and the link to the saved layout. The layout is a
// profile setting (Config.FooterPages); a work view can still move buttons under More, and Ctrl+1, 2, 3 ... follow the shown buttons.
public partial class SettingsEditor
{
    private FooterLayoutEditor? _footerEditor;
    // The layout the draft was loaded from: a change of the confirmed layout from outside (the Booru hint) replaces the draft only if it is untouched.
    private FooterLayout _footerBaseline = FooterLayout.Default;
    private FooterDisplayPreferences _footerDisplaysBaseline;
    private bool _resetFooterDisplaysRequested;

    public string FooterLayoutLabel => Texts.T("settings.footer.title");
    public string FooterLayoutHelp => Texts.T("settings.footer.help");
    public string FooterLayoutBooruHint => Texts.T("settings.footer.booruHint");
    public string ShowBooruActionsLabel => Texts.T("settings.footer.booruActions");
    public string ShowBooruActionsHint => Texts.T("settings.footer.booruActions.hint");
    public string FooterDisplaysLabel => Texts.T("settings.footer.displays");
    public string FooterNetworkLabel => IoStatsDisplay.NetworkTitle();
    public string FooterDiskLabel => IoStatsDisplay.DiskTitle();
    public string FooterStatusLabel => Texts.T("settings.footer.statusLines");

    private bool _showFooterNetwork, _showFooterDisk, _showFooterStatus;
    public bool ShowFooterNetwork
    {
        get => _showFooterNetwork;
        set => SetField(ref _showFooterNetwork, value, nameof(ShowFooterNetwork));
    }

    public bool ShowFooterDisk
    {
        get => _showFooterDisk;
        set => SetField(ref _showFooterDisk, value, nameof(ShowFooterDisk));
    }

    public bool ShowFooterStatus
    {
        get => _showFooterStatus;
        set => SetField(ref _showFooterStatus, value, nameof(ShowFooterStatus));
    }

    // "Show Booru actions": the profile's Config.ShowBooruTools (the master of the Booru page and the Danbooru tools). It used to be the check box
    // at the right of the footer; it is a draft like every other setting of this page now.
    private bool _showBooruActions = true;
    private bool _showBooruActionsBaseline = true;
    public bool ShowBooruActions
    {
        get => _showBooruActions;
        set { SetField(ref _showBooruActions, value, nameof(ShowBooruActions)); UpdateFooterNumberingContext(); }
    }

    // The footer list counts the digits in the work view of the draft with the draft's Booru switch (the Ctrl/Cmd+digit hints follow both live).
    internal void UpdateFooterNumberingContext() =>
        _footerEditor?.SetNumberingContext(WorkViewSetting.Parse(SelectedWorkspaceMode?.Value ?? _config?.WorkspaceMode), _showBooruActions);

    /// <summary>The footer list (built with the page).</summary>
    internal FooterLayoutEditor FooterEditor => _footerEditor ??= CreateFooterEditor();

    private FooterLayoutEditor CreateFooterEditor()
    {
        var editor = new FooterLayoutEditor();
        editor.LayoutChanged += (_, _) =>
        {
            _draftRevision++;
            RetireSavedStatus();
        };
        FooterLayoutHost.Content = editor;
        return editor;
    }

    private void LoadFooterDraft(Config config)
    {
        _footerBaseline = config.FooterLayout;
        _footerDisplaysBaseline = FooterDisplayPreferences.FromConfig(config);
        _resetFooterDisplaysRequested = false;
        LoadFooterDisplayDraft(_footerDisplaysBaseline);
        _showBooruActionsBaseline = config.ShowBooruTools;
        ShowBooruActions = config.ShowBooruTools;
        FooterEditor.SetNumberingContext(WorkViewSetting.Parse(SelectedWorkspaceMode?.Value ?? config.WorkspaceMode), config.ShowBooruTools);
        FooterEditor.Load(_footerBaseline);
    }

    private void LoadFooterDisplayDraft(FooterDisplayPreferences displays)
    {
        ShowFooterNetwork = displays.Network;
        ShowFooterDisk = displays.Disk;
        ShowFooterStatus = displays.Status;
    }

    /// <summary>Refresh the footer layout after a hint change, keeping any edits in the draft.</summary>
    internal void RefreshConfirmedFooter()
    {
        if (_isClosed) return;
        FooterLayout confirmed = _config.FooterLayout;
        if (FooterEditor.Layout.Equals(_footerBaseline)) FooterEditor.Load(confirmed);
        _footerBaseline = confirmed;
        if (_showBooruActions == _showBooruActionsBaseline) ShowBooruActions = _config.ShowBooruTools;
        _showBooruActionsBaseline = _config.ShowBooruTools;
    }

    internal void RefreshConfirmedFooterDisplays()
    {
        if (_isClosed || _resetFooterDisplaysRequested) return;
        FooterDisplayPreferences displays = FooterDisplayPreferences.FromConfig(_config);
        if (ShowFooterNetwork == _footerDisplaysBaseline.Network) ShowFooterNetwork = displays.Network;
        if (ShowFooterDisk == _footerDisplaysBaseline.Disk) ShowFooterDisk = displays.Disk;
        if (ShowFooterStatus == _footerDisplaysBaseline.Status) ShowFooterStatus = displays.Status;
        _footerDisplaysBaseline = displays;
    }

    private void ResetFooterDraft()
    {
        FooterEditor.Load(FooterLayout.Default);
        Config defaults = new();
        ShowBooruActions = defaults.ShowBooruTools;
        LoadFooterDisplayDraft(FooterDisplayPreferences.FromConfig(defaults));
        _resetFooterDisplaysRequested = true;
    }

    private void ApplyFooterDraft(Config candidate)
    {
        candidate.FooterPages = FooterEditor.Layout.ToStored();
        candidate.ShowBooruTools = ShowBooruActions;
        candidate.ShowFooterNetwork = ShowFooterNetwork;
        candidate.ShowFooterDisk = ShowFooterDisk;
        candidate.ShowFooterStatus = ShowFooterStatus;
        candidate.SettingsBaselineFooterDisplays = _resetFooterDisplaysRequested ? null : _footerDisplaysBaseline;
    }

    private void RaiseFooterTextsChanged()
    {
        if (_isClosed) return;
        foreach (string property in new[] { nameof(FooterLayoutLabel), nameof(FooterLayoutHelp), nameof(FooterLayoutBooruHint),
            nameof(ShowBooruActionsLabel), nameof(ShowBooruActionsHint), nameof(FooterDisplaysLabel), nameof(FooterNetworkLabel), nameof(FooterDiskLabel), nameof(FooterStatusLabel) })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
    }
}
