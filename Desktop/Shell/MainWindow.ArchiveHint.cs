using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

// UX flows AP1: after the very first successful Add of a profile, one dismissible line says where reading happens.
// Downloads shows what is saved; the posts are read in the Archive. Shown once per profile: the dismissal (or using the
// action, or visiting the Archive by any route) is stored as a profile preference, so it never returns.
public partial class MainWindow
{
    private Border? _archiveBridgeHint;
    private TextBlock? _archiveBridgeHintText;
    private Button? _archiveBridgeHintOpen, _archiveBridgeHintDismiss;
    // The list was empty when the current Add started: only then is this the profile's first download.
    private bool _admissionStartedEmpty;
    private bool _archiveBridgeHintVisible;

    internal Border? ArchiveBridgeHintForTests => _archiveBridgeHint;

    private void InitializeArchiveBridgeHint()
    {
        _archiveBridgeHintText = new TextBlock { Name = "ArchiveBridgeHintText", TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(_archiveBridgeHintText, AutomationLiveSetting.Polite);
        _archiveBridgeHintOpen = new Button { Name = "ArchiveBridgeHintOpen", VerticalAlignment = VerticalAlignment.Center };
        _archiveBridgeHintOpen.Classes.Add("primary");
        _archiveBridgeHintOpen.Click += (_, _) => { MarkArchiveBridgeHintSeen(); NavigateWorkspace(WorkspacePage.Archive); };
        _archiveBridgeHintDismiss = new Button { Name = "ArchiveBridgeHintDismiss", VerticalAlignment = VerticalAlignment.Center };
        _archiveBridgeHintDismiss.Click += (_, _) => MarkArchiveBridgeHintSeen();
        Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        layout.Children.Add(_archiveBridgeHintText);
        Grid.SetColumn(_archiveBridgeHintOpen, 1);
        Grid.SetColumn(_archiveBridgeHintDismiss, 2);
        layout.Children.Add(_archiveBridgeHintOpen);
        layout.Children.Add(_archiveBridgeHintDismiss);
        _archiveBridgeHint = new Border { Name = "ArchiveBridgeHint", IsVisible = false, Child = layout };
        _archiveBridgeHint.Classes.Add("workspace-section");
        _archiveBridgeHint[!Border.PaddingProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Inset.Message");
        // Right below the Add results: the line answers "and now?" at the place the user just looked at.
        int after = WorkspaceIntakeStack.Children.IndexOf(AdmissionResultsPanel);
        WorkspaceIntakeStack.Children.Insert(after < 0 ? WorkspaceIntakeStack.Children.Count : after + 1, _archiveBridgeHint);
        RefreshArchiveBridgeHint();
    }

    /// <summary>Called when an Add starts: remembers whether the profile had any download before it.</summary>
    private void NoteAdmissionStart() => _admissionStartedEmpty = UrlList.Count == 0 && BoardsList.Count == 0;

    /// <summary>Called with the finished Add results: the first successful Add of a profile shows the line.</summary>
    private void OfferArchiveBridgeHint(int added)
    {
        if (added <= 0 || !_admissionStartedEmpty || _config.ArchiveBridgeHintSeen || _isClosing) return;
        _archiveBridgeHintVisible = true;
        RefreshArchiveBridgeHint();
    }

    private void RefreshArchiveBridgeHint()
    {
        if (_archiveBridgeHint == null || _archiveBridgeHintText == null || _archiveBridgeHintOpen == null || _archiveBridgeHintDismiss == null) return;
        if (_config.ArchiveBridgeHintSeen) _archiveBridgeHintVisible = false;
        _archiveBridgeHint.IsVisible = _archiveBridgeHintVisible;
        string archive = CommandCatalog.Label("navigation.archive")
            + (CommandCatalog.ShortcutDisplay("navigation.archive") is { } shortcut ? " (" + shortcut + ")" : string.Empty);
        _archiveBridgeHintText.Text = Texts.T("downloads.archiveHint", archive);
        _archiveBridgeHintOpen.Content = Texts.T("downloads.archiveHint.open");
        _archiveBridgeHintDismiss.Content = Texts.T("downloads.archiveHint.dismiss");
        CommandBindings.EnsureIconBeforeText(_archiveBridgeHintOpen, "arrow-right");
        CommandBindings.EnsureIconBeforeText(_archiveBridgeHintDismiss, "check");
        AutomationProperties.SetName(_archiveBridgeHint, _archiveBridgeHintText.Text);
    }

    /// <summary>The hint is answered: hidden now, and never shown again for this profile.</summary>
    private void MarkArchiveBridgeHintSeen()
    {
        if (_archiveBridgeHintVisible) { _archiveBridgeHintVisible = false; RefreshArchiveBridgeHint(); }
        if (_config.ArchiveBridgeHintSeen || _isClosing) return;
        _config.ArchiveBridgeHintSeen = true; // the live copy at once; the write follows through the profile's preference path
        _ = SaveArchiveBridgeHintSeenAsync();
    }

    private async System.Threading.Tasks.Task SaveArchiveBridgeHintSeenAsync()
    {
        try { await _session.SaveViewPreferencesAsync(config => config.ArchiveBridgeHintSeen = true); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Log.Warning(error, "The Archive hint answer could not be stored; it may show once more."); }
    }

    /// <summary>
    /// With no thread and no board in the list, Search, Columns, Copy URLs and Details have nothing to act on: the toolbar
    /// shrinks to the empty-state guide. A running search keeps its tools, so a filter that hides everything stays usable.
    /// </summary>
    private void RefreshDownloadsToolbarEmptiness(bool searchActive)
    {
        bool hasRows = UrlList.Count > 0 || BoardsList.Count > 0 || _startupPreviewShown;
        bool tools = hasRows || searchActive || _downloadSearchOpen;
        foreach (string name in new[] { "DownloadSearchButton", "DownloadsColumnsButton", "CopyAllUrlsButton" })
            WorkspaceControl<Button>(name).IsVisible = tools;
        // The inspector's own close button remains when it is already open.
        DetailsToggleButton.IsVisible = tools || WorkspaceInspector.IsVisible;
        WorkspaceListActions.IsVisible = tools || DetailsToggleButton.IsVisible;
    }
}
