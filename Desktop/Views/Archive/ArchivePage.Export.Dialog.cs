using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

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
    private void ShowExportDialog()
    {
        Control<Border>("ArchiveExportDialog").IsVisible = true;
        ResizeExportDialog();
        if (_exportCard != null) MeowChan.Desktop.Commands.LegacyCommandAdapters.Register(_exportCard);
        if (_exportTopLevel != null) return;
        TopLevel? top = _exportTopLevel = TopLevel.GetTopLevel(Control<Border>("ArchiveExportDialog")) ?? TopLevel.GetTopLevel(this);
        top?.AddHandler(PointerPressedEvent, ExportModalPointer, RoutingStrategies.Tunnel);
        top?.AddHandler(KeyDownEvent, ExportModalKey, RoutingStrategies.Tunnel);
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
        content.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportHistoryLabel"));
        ComboBox previous = ExportName(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportHistory");
        previous.SelectionChanged += (_, _) => UpdateExportMode();
        content.Children.Add(previous);
        WrapPanel modes = new() { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 6 };
        RadioButton fresh = ExportName(new RadioButton { GroupName = "exportMode", IsChecked = true }, "ArchiveExportNew");
        RadioButton update = ExportName(new RadioButton { GroupName = "exportMode" }, "ArchiveExportUpdate");
        fresh.IsCheckedChanged += (_, _) => UpdateExportMode();
        update.IsCheckedChanged += (_, _) => UpdateExportMode();
        modes.Children.Add(fresh); modes.Children.Add(update); content.Children.Add(modes);
        WrapPanel historyActions = new() { ItemSpacing = 8, LineSpacing = 6 };
        Button prior = ExportName(new Button(), "ArchiveExportPrevious"); prior.Click += OnExportPrevious;
        Button associate = ExportName(new Button(), "ArchiveExportAssociate"); associate.Click += OnExportAssociate;
        historyActions.Children.Add(prior); historyActions.Children.Add(associate); content.Children.Add(historyActions);
        StackPanel legacyContent = new() { Spacing = 6 };
        Button legacyFind = ExportName(new Button(), "ArchiveExportLegacyFind"); legacyFind.Click += OnExportLegacyFind; legacyContent.Children.Add(legacyFind);
        ComboBox legacy = ExportName(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportLegacy");
        legacy.SelectionChanged += (_, _) => UpdateExportActions(); legacyContent.Children.Add(legacy);
        Button legacyAssociate = ExportName(new Button(), "ArchiveExportLegacyAssociate"); legacyAssociate.Click += OnExportLegacyAssociate; legacyContent.Children.Add(legacyAssociate);
        content.Children.Add(ExportName(new Expander { Content = legacyContent, HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportLegacySection"));
        content.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportBackupHint"));
        CheckBox backup = ExportName(new CheckBox(), "ArchiveExportBackup"); content.Children.Add(backup);
        TextBox destination = ExportName(new TextBox { MaxLength = 32768, HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportDestination");
        Avalonia.Automation.AutomationProperties.SetLabeledBy(destination, targetLabel); content.Children.Add(destination);
        Button browse = ExportName(new Button(), "ArchiveExportBrowse"); browse.Click += OnExportBrowse; content.Children.Add(browse);
        TextBlock optionsLabel = ExportName(new TextBlock(), "ArchiveExportOptionsLabel");
        optionsLabel.Classes.Add("section-title"); content.Children.Add(optionsLabel);
        content.Children.Add(ExportName(new CheckBox { IsChecked = true }, "ArchiveExportMedia"));
        Grid statusLine = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        statusLine.Children.Add(ExportName(new Ursa.Controls.LoadingIcon { Width = 16, Height = 16, IsVisible = false, Focusable = false }, "ArchiveExportLoading"));
        TextBlock status = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportStatus");
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        statusLine.Children.Add(status); Grid.SetColumn(status, 1);
        TextBlock error = ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportError");
        error.Classes.Add("workspace-error");
        Avalonia.Automation.AutomationProperties.SetLiveSetting(error, Avalonia.Automation.AutomationLiveSetting.Assertive);
        Ursa.Controls.Banner errorBanner = ExportName(new Ursa.Controls.Banner { Type = Avalonia.Controls.Notifications.NotificationType.Error,
            CanClose = false, IsVisible = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = error, MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }, "ArchiveExportErrorBanner");
        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10 };
        TextBlock fixedScope = ExportName(new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, "ArchiveExportFixedScope");
        StackPanel header = new() { Spacing = 8 };
        header.Children.Add(title); header.Children.Add(compactScope); header.Children.Add(fixedScope);
        body.Children.Add(header);
        ScrollViewer scroll = new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        WrapPanel actions = new() { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 6 };
        Button close = ExportName(new Button(), "ArchiveExportClose"); close.Click += OnExportClose;
        Button open = ExportName(new Button(), "ArchiveExportOpen"); open.Click += OnExportOpen;
        Button start = ExportName(new Button(), "ArchiveExportStart"); start.Click += OnExportStart;
        close.Classes.Add("subtle"); start.Classes.Add("primary"); actions.Classes.Add("workspace-actions");
        actions.Children.Add(close); actions.Children.Add(open); actions.Children.Add(start);
        StackPanel outcome = new() { Spacing = 8 };
        outcome.Children.Add(statusLine); outcome.Children.Add(errorBanner);
        StackPanel footerContent = new() { Spacing = 12 };
        footerContent.Children.Add(new ScrollViewer { Content = outcome, MaxHeight = 140, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        footerContent.Children.Add(actions);
        Border footer = new() { Child = footerContent }; footer.Classes.Add("workspace-dialog-footer");
        Grid.SetRow(footer, 2); body.Children.Add(footer);
        header.Margin = new Thickness(16, 16, 16, 0); scroll.Margin = new Thickness(16, 0);
        _exportCard = new Border { Child = body, MaxWidth = 680, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        NameScope.SetNameScope(_exportCard, _exportNames);
        _exportNames.Complete();
        _exportCard.Classes.Add("panel");
        KeyboardNavigation.SetTabNavigation(_exportCard, KeyboardNavigationMode.Cycle);
        _exportCard.AddHandler(KeyDownEvent, OnExportKeyDown, RoutingStrategies.Tunnel);
        Border overlay = Control<Border>("ArchiveExportDialog");
        overlay.Child = _exportCard;
        // The reader and its overlay may be hosted outside this (hidden) page.
        overlay.SizeChanged += (_, _) => ResizeExportDialog();
        ResizeExportDialog();
    }
    private void ResizeExportDialog()
    {
        if (_exportCard == null) return;
        Border overlay = Control<Border>("ArchiveExportDialog");
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
        if (e.Key == Key.Escape) { e.Handled = true; OnExportClose(sender, e); return; }
        if (e.Source is Control control && !IsExportChild(control))
        { e.Handled = true; Control<Button>("ArchiveExportClose").Focus(); }
    }
    private void LocalizeExport()
    {
        if (_exportCard == null) return;
        UpdateExportSummary();
        Control<TextBlock>("ArchiveExportTargetLabel").Text = Texts.T("export.target");
        Control<TextBlock>("ArchiveExportOptionsLabel").Text = Texts.T("export.options");
        Control<Expander>("ArchiveExportScopeDetails").Header = Texts.T("export.scopeDetails");
        Control<TextBlock>("ArchiveExportTitle").Text = Texts.T(_exportAllResults ? "export.resultsTitle" : "archive.export");
        Control<TextBlock>("ArchiveExportHint").Text = Texts.T(_exportAllResults ? "export.resultsHint" : IsCollectionExport ? "export.collectionHint" : "export.hint");
        Control<TextBlock>("ArchiveExportHistoryLabel").Text = Texts.T(IsCollectionExport ? "export.collectionKnown" : "export.known");
        Control<RadioButton>("ArchiveExportNew").Content = Texts.T("export.new");
        Control<RadioButton>("ArchiveExportUpdate").Content = Texts.T("export.update");
        Control<Expander>("ArchiveExportLegacySection").Header = Texts.T("export.older");
        Control<Button>("ArchiveExportLegacyFind").Content = Texts.T("export.legacyFind");
        Control<Button>("ArchiveExportLegacyAssociate").Content = Texts.T("export.legacyAssociate");
        Control<Button>("ArchiveExportPrevious").Content = Texts.T("export.previous");
        Control<Button>("ArchiveExportAssociate").Content = Texts.T("export.associate");
        Control<CheckBox>("ArchiveExportBackup").Content = Texts.T("export.preserve");
        Control<TextBlock>("ArchiveExportBackupHint").Text = Texts.T("export.backupHint");
        Control<CheckBox>("ArchiveExportMedia").Content = Texts.T("archive.exportMedia");
        Control<Button>("ArchiveExportBrowse").Content = Texts.T("archive.exportBrowse");
        Control<Button>("ArchiveExportClose").Content = Texts.T(_exportRunning ? "archive.exportCancel" : "action.close");
        Control<Button>("ArchiveExportOpen").Content = Texts.T("action.openFolder");
        ToolTip.SetTip(Control<Button>("ArchiveExportOpen"), Texts.T("export.openHint"));
        Control<Button>("ArchiveExportStart").Content = Texts.T("archive.exportStart");
    }
}
