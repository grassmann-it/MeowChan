using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly NameScope _exportNames = new();
    private Border? _exportCard;
    private Control? _exportOrigin;
    private TopLevel? _exportTopLevel;
    // Programmatically built controls live in their own scope. The XAML page
    // scope is already complete when this lazy dialog is first opened.
    internal T? FindControl<T>(string name) where T : Control =>
        _exportNames.Find<T>(name) ?? ControlExtensions.FindControl<T>(this, name);
    private bool _exportWasUpdating;
    private void ShowExportDialog()
    {
        bool opening = !ArchiveExportDialog.IsVisible;
        ArchiveExportDialog.IsVisible = true;
        // R13: the extras section starts in its view's state each time the dialog opens; a switch inside the open dialog keeps it.
        if (opening && _exportCard != null) ApplyExportOptionsView();
        ResizeExportDialog();
        if (_exportCard != null) MeowChan.Desktop.Commands.LegacyCommandAdapters.Register(_exportCard);
        if (_exportTopLevel != null) return;
        TopLevel? top = _exportTopLevel = TopLevel.GetTopLevel(ArchiveExportDialog) ?? TopLevel.GetTopLevel(this);
        top?.AddHandler(PointerPressedEvent, ExportModalPointer, RoutingStrategies.Tunnel);
        top?.AddHandler(KeyDownEvent, ExportModalKey, RoutingStrategies.Tunnel);
    }
    private WorkView? _exportOptionsView;

    // Format, scope, target and media stay direct in every view; updating a previous export folds (WorkViewPolicy.Dialogs).
    // R13: the section takes its view's state when the dialog opens and when the view changes; R6: an update opens it.
    private void ApplyExportOptionsView()
    {
        _exportOptionsView = ArchiveWorkView;
        _exportWasUpdating = UpdatingExport;
        WorkViewPresenter.ApplyExpander(Control<Expander>("ArchiveExportMoreOptions"), ArchiveWorkView, viewChanged: true,
            nonDefaultState: _exportWasUpdating);
    }

    private void RefreshExportOptionsForView()
    {
        if (_exportCard != null && ArchiveExportDialog.IsVisible && _exportOptionsView != ArchiveWorkView) ApplyExportOptionsView();
    }

    // R6: an update in progress (its backup choice and the replaced target) is shown, whatever the view folded.
    private void OpenExportOptionsForUpdate()
    {
        bool updating = UpdatingExport;
        if (updating && !_exportWasUpdating) Control<Expander>("ArchiveExportMoreOptions").IsExpanded = true;
        _exportWasUpdating = updating;
    }

    private void EndExportDialog()
    {
        if (_exportCard != null)
        {
            Control<Ursa.Controls.LoadingIcon>("ArchiveExportLoading").IsLoading = false;
            Control<Ursa.Controls.LoadingIcon>("ArchiveExportLoading").IsVisible = false;
        }
        _exportLookupCancellation?.Cancel();
        _exportTopLevel?.RemoveHandler(PointerPressedEvent, ExportModalPointer);
        _exportTopLevel?.RemoveHandler(KeyDownEvent, ExportModalKey);
        _exportTopLevel = null;
    }
    private T ExportName<T>(T control, string name) where T : Control
    {
        control.Name = name;
        _exportNames.Register(name, control);
        return control;
    }
    private void EnsureExportDialog()
    {
        if (_exportCard != null) return;
        StackPanel content = new() { Spacing = 12 };
        TextBlock title = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportTitle");
        title.Classes.Add("page-title");
        TextBlock compactScope = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportScopeCompact");
        compactScope.Classes.Add("section-title");
        TextBlock summary = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportSummary");
        summary.Classes.Add("workspace-meta");
        content.Children.Add(ExportName(new Expander { Content = summary, HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportScopeDetails"));
        TextBlock hint = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportHint");
        hint.Classes.Add("workspace-help"); content.Children.Add(hint);
        TextBlock targetLabel = ExportName(new TextBlock(), "ArchiveExportTargetLabel");
        targetLabel.Classes.Add("section-title"); content.Children.Add(targetLabel);
        TextBox destination = ExportName(new TextBox { MaxLength = 32768, HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportDestination");
        Avalonia.Automation.AutomationProperties.SetLabeledBy(destination, targetLabel); content.Children.Add(destination);
        destination.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) OnExportDestinationEdited(destination); };
        Button browse = ExportName(new Button(), "ArchiveExportBrowse"); browse.Click += OnExportBrowse; content.Children.Add(browse);
        content.Children.Add(ExportName(new CheckBox { IsChecked = true }, "ArchiveExportMedia"));
        // Target, media and the primary actions stay direct; updating a previous export (known exports, update mode,
        // older exports, backup) is one extras section that folds in Barebones and Simple (plan AP5 §1.14).
        StackPanel extras = new() { Spacing = 8 };
        extras.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportHistoryLabel"));
        ComboBox previous = ExportName(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportHistory");
        previous.SelectionChanged += (_, _) => UpdateExportMode();
        extras.Children.Add(previous);
        WrapPanel modes = new() { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 6 };
        RadioButton fresh = ExportName(new RadioButton { GroupName = "exportMode", IsChecked = true }, "ArchiveExportNew");
        RadioButton update = ExportName(new RadioButton { GroupName = "exportMode" }, "ArchiveExportUpdate");
        fresh.IsCheckedChanged += (_, _) => UpdateExportMode();
        update.IsCheckedChanged += (_, _) => UpdateExportMode();
        modes.Children.Add(fresh); modes.Children.Add(update); extras.Children.Add(modes);
        // Vertical actions inherit the finite scroll-content width; wrapping a
        // row of buttons would not constrain an individual long label.
        StackPanel historyActions = new() { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        Button prior = ExportName(new Button(), "ArchiveExportPrevious"); prior.Click += OnExportPrevious;
        Button associate = ExportName(new Button(), "ArchiveExportAssociate"); associate.Click += OnExportAssociate;
        historyActions.Children.Add(prior); historyActions.Children.Add(associate); extras.Children.Add(historyActions);
        StackPanel legacyContent = new() { Spacing = 6 };
        Button legacyFind = ExportName(new Button(), "ArchiveExportLegacyFind"); legacyFind.Click += OnExportLegacyFind; legacyContent.Children.Add(legacyFind);
        ComboBox legacy = ExportName(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportLegacy");
        legacy.SelectionChanged += (_, _) => UpdateExportActions(); legacyContent.Children.Add(legacy);
        Button legacyAssociate = ExportName(new Button(), "ArchiveExportLegacyAssociate"); legacyAssociate.Click += OnExportLegacyAssociate; legacyContent.Children.Add(legacyAssociate);
        extras.Children.Add(ExportName(new Expander { Content = legacyContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch }, "ArchiveExportLegacySection"));
        extras.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportBackupHint"));
        CheckBox backup = ExportName(new CheckBox(), "ArchiveExportBackup"); extras.Children.Add(backup);
        Expander moreOptions = ExportName(new Expander { Content = extras, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch }, "ArchiveExportMoreOptions");
        WorkViewElement.SetId(moreOptions, "dialogs.export.options");
        content.Children.Add(moreOptions);
        Grid statusLine = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        statusLine.Children.Add(ExportName(new Ursa.Controls.LoadingIcon { Width = 16, Height = 16, IsVisible = false, Focusable = false }, "ArchiveExportLoading"));
        TextBlock status = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportStatus");
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        statusLine.Children.Add(status); Grid.SetColumn(status, 1);
        TextBlock error = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportError");
        error.Classes.Add("workspace-error");
        Avalonia.Automation.AutomationProperties.SetLiveSetting(error, Avalonia.Automation.AutomationLiveSetting.Assertive);
        MeowChan.Desktop.Controls.WorkspaceBanner errorBanner = ExportName(new MeowChan.Desktop.Controls.WorkspaceBanner { Type = Avalonia.Controls.Notifications.NotificationType.Error,
            IsVisible = false,
            Child = new ScrollViewer { Content = error, MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }, "ArchiveExportErrorBanner");
        TextBlock fixedScope = ExportName(new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, "ArchiveExportFixedScope");
        StackPanel header = new() { Spacing = 8 };
        // MD4 overlay header: the title on the left, the cross alone at the top right. The cross is the footer Close's twin: it
        // runs the same OnExportClose, so while an export runs it cancels it, exactly as the footer button and Escape do.
        Button headerClose = ExportName(new Button(), "ArchiveExportHeaderClose"); headerClose.Click += OnExportClose;
        header.Children.Add(MeowChan.Desktop.Controls.OverlayFrame.Header(title, headerClose, Texts.T("action.close")));
        header.Children.Add(BuildExportChoices()); header.Children.Add(compactScope); header.Children.Add(fixedScope);
        WrapPanel actions = new() { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 6 };
        Button close = ExportName(new Button(), "ArchiveExportClose"); close.Click += OnExportClose;
        Button open = ExportName(new Button(), "ArchiveExportOpen"); open.Click += OnExportOpen;
        Button start = ExportName(new Button(), "ArchiveExportStart"); start.Click += OnExportStart;
        close.Classes.Add("subtle"); start.Classes.Add("primary"); actions.Classes.Add("workspace-actions");
        actions.Children.Add(close); actions.Children.Add(open); actions.Children.Add(start);
        StackPanel footerContent = new() { Spacing = 12 };
        footerContent.Children.Add(new ScrollViewer { Content = statusLine, MaxHeight = 140, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        footerContent.Children.Add(actions);
        Border footer = new() { Child = footerContent }; footer.Classes.Add("workspace-dialog-footer");
        content.Margin = new Thickness(16, 0, 16, 12);
        // Fixed scope header, local error strip above the scrolling options, actions and status in the footer.
        Grid body = MeowChan.Desktop.Controls.DialogFrame.Build(header, content, footer, errorBanner);
        _exportCard = new Border { Child = body, MaxWidth = 680, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        NameScope.SetNameScope(_exportCard, _exportNames);
        _exportNames.Complete();
        _exportCard.Classes.Add("workspace-section-flush"); // the DialogFrame inside carries its own insets
        KeyboardNavigation.SetTabNavigation(_exportCard, KeyboardNavigationMode.Cycle);
        _exportCard.AddHandler(KeyDownEvent, OnExportKeyDown, RoutingStrategies.Tunnel);
        Border overlay = ArchiveExportDialog;
        overlay.Child = _exportCard;
        // The reader and its overlay may be hosted outside this (hidden) page.
        overlay.SizeChanged += (_, _) => ResizeExportDialog();
        ResizeExportDialog();
    }
    private void ResizeExportDialog()
    {
        if (_exportCard == null) return;
        Border overlay = ArchiveExportDialog;
        double available = overlay.Bounds.Height;
        if (available <= 0) available = (overlay.Parent as Control)?.Bounds.Height ?? 0;
        if (available <= 0) available = TopLevel.GetTopLevel(overlay)?.ClientSize.Height ?? 0;
        _exportCard.MaxHeight = Math.Max(100, available - 48);
    }
    private void ExportModalPointer(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control control && _exportCard != null && !IsExportChild(control)) e.Handled = true;
    }
    private bool IsExportChild(Control control)
    {
        for (StyledElement? current = control; current != null; current = current.Parent)
            if (ReferenceEquals(current, _exportCard)) return true;
        return false;
    }
    private void ExportModalKey(object? sender, KeyEventArgs e)
    {
        if (MeowChan.Desktop.Controls.OverlayFrame.TryHandleEscape(e, () => OnExportClose(sender, e))) return;
        if (e.Source is Control control && !IsExportChild(control))
        { e.Handled = true; Control<Button>("ArchiveExportClose").Focus(); }
    }
    // Keep the same text presenter on language refresh. Stretch through the
    // vertical parent and button presenter, leaving height free for every line.
    internal static void ApplyExportActionLabel(Button button, string label)
    {
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (button.Content is not TextBlock text)
        {
            text = new TextBlock();
            button.Content = text;
        }
        text.Text = label;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        Avalonia.Automation.AutomationProperties.SetName(button, label);
    }
    private void LocalizeExport()
    {
        if (_exportCard == null) return;
        RenderExportStatusText();
        RenderExportErrorText();
        UpdateExportSummary();
        Control<TextBlock>("ArchiveExportTargetLabel").Text = Texts.T("export.target");
        Control<Expander>("ArchiveExportScopeDetails").Header = Texts.T("export.scopeDetails");
        Control<TextBlock>("ArchiveExportTitle").Text = Texts.T("export.dialog.title");
        Control<TextBlock>("ArchiveExportHint").Text = Texts.T(_exportFormat == ExportFormat.LinkList ? "export.link.hint"
            : _exportAllResults ? "export.resultsHint" : IsCollectionExport ? "export.collectionHint" : "export.hint");
        Control<TextBlock>("ArchiveExportHistoryLabel").Text = Texts.T(IsCollectionExport ? "export.collectionKnown" : "export.known");
        Control<RadioButton>("ArchiveExportNew").Content = Texts.T("export.new");
        Control<RadioButton>("ArchiveExportUpdate").Content = Texts.T("export.update");
        Control<Expander>("ArchiveExportMoreOptions").Header = Texts.T("workview.moreOptions");
        Control<Expander>("ArchiveExportLegacySection").Header = Texts.T("export.older");
        ApplyExportActionLabel(Control<Button>("ArchiveExportLegacyFind"), Texts.T("export.legacyFind"));
        ApplyExportActionLabel(Control<Button>("ArchiveExportLegacyAssociate"), Texts.T("export.legacyAssociate"));
        ApplyExportActionLabel(Control<Button>("ArchiveExportPrevious"), Texts.T("export.previous"));
        ApplyExportActionLabel(Control<Button>("ArchiveExportAssociate"), Texts.T("export.associate"));
        Control<CheckBox>("ArchiveExportBackup").Content = Texts.T("export.preserve");
        Control<TextBlock>("ArchiveExportBackupHint").Text = Texts.T("export.backupHint");
        Control<CheckBox>("ArchiveExportMedia").Content = Texts.T("archive.exportMedia");
        Control<Button>("ArchiveExportBrowse").Content = Texts.T("archive.exportBrowse");
        Control<Button>("ArchiveExportClose").Content = Texts.T(_exportRunning ? "archive.exportCancel" : "action.close");
        MeowChan.Desktop.Controls.OverlayFrame.MakeClose(Control<Button>("ArchiveExportHeaderClose"), Texts.T(_exportRunning ? "archive.exportCancel" : "action.close"));
        Control<Button>("ArchiveExportOpen").Content = Texts.T("action.openFolder");
        ToolTip.SetTip(Control<Button>("ArchiveExportOpen"), Texts.T("export.openHint")); ToolTip.SetShowOnDisabled(Control<Button>("ArchiveExportOpen"), true);
        Control<Button>("ArchiveExportStart").Content = Texts.T(_exportFormat == ExportFormat.LinkList ? "export.link.review" : "archive.exportStart");
    }
}
