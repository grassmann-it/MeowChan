using System;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The "Footer" group of Settings › Appearance (F1): which main pages have a footer button of their own and in which order. The list itself is
// the FooterLayoutEditor; this file holds its texts, its draft life (load, cancel, reset, save) and the link to the saved layout. The layout is a
// profile setting (Config.FooterPages); a work view can still move buttons under More, and Ctrl+1, 2, 3 ... follow the shown buttons.
public partial class SettingsEditor
{
    private FooterLayoutEditor? _footerEditor;
    // The layout the draft was loaded from: a change of the confirmed layout from outside (the Booru hint) replaces the draft only if it is untouched.
    private FooterLayout _footerBaseline = FooterLayout.Default;

    public string FooterLayoutLabel => Texts.T("settings.footer.title");
    public string FooterLayoutHelp => Texts.T("settings.footer.help");
    public string FooterLayoutBooruHint => Texts.T("settings.footer.booruHint");
    public string ShowBooruActionsLabel => Texts.T("settings.footer.booruActions");
    public string ShowBooruActionsHint => Texts.T("settings.footer.booruActions.hint");

    // "Show Booru actions": the profile's Config.ShowBooruTools (the master of the Booru page and the Danbooru tools). It used to be the check box
    // at the right of the footer; it is a draft like every other setting of this page now.
    private bool _showBooruActions = true;
    private bool _showBooruActionsBaseline = true;
    public bool ShowBooruActions
    {
        get => _showBooruActions;
        set => SetField(ref _showBooruActions, value, nameof(ShowBooruActions));
    }

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
        FooterEditor.Load(_footerBaseline);
        _showBooruActionsBaseline = config.ShowBooruTools;
        ShowBooruActions = config.ShowBooruTools;
    }

    /// <summary>The saved layout changed outside this page (a hint switched Booru on): show it unless the person has unsaved footer edits.</summary>
    internal void RefreshConfirmedFooter()
    {
        if (_isClosed) return;
        FooterLayout confirmed = _config.FooterLayout;
        if (FooterEditor.Layout.Equals(_footerBaseline)) FooterEditor.Load(confirmed);
        _footerBaseline = confirmed;
        if (_showBooruActions == _showBooruActionsBaseline) ShowBooruActions = _config.ShowBooruTools;
        _showBooruActionsBaseline = _config.ShowBooruTools;
    }

    private void ResetFooterDraft()
    {
        FooterEditor.Load(FooterLayout.Default);
        ShowBooruActions = new Config().ShowBooruTools;
    }

    private void ApplyFooterDraft(Config candidate)
    {
        candidate.FooterPages = FooterEditor.Layout.ToStored();
        candidate.ShowBooruTools = ShowBooruActions;
    }

    private void RaiseFooterTextsChanged()
    {
        if (_isClosed) return;
        foreach (string property in new[] { nameof(FooterLayoutLabel), nameof(FooterLayoutHelp), nameof(FooterLayoutBooruHint), nameof(ShowBooruActionsLabel), nameof(ShowBooruActionsHint) })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
    }
}
