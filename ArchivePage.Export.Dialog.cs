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
    private void EndExportDialog()
    {
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
        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(ExportName(new TextBlock { FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, "ArchiveExportTitle"));
        content.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportSummary"));
        content.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportHint"));
        content.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportHistoryLabel"));
        ComboBox previous = ExportName(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportHistory");
        previous.SelectionChanged += (_, _) => UpdateExportMode();
        content.Children.Add(previous);
        WrapPanel modes = new() { Orientation = Orientation.Horizontal };
        RadioButton fresh = ExportName(new RadioButton { GroupName = "exportMode", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) }, "ArchiveExportNew");
        RadioButton update = ExportName(new RadioButton { GroupName = "exportMode" }, "ArchiveExportUpdate");
        fresh.IsCheckedChanged += (_, _) => UpdateExportMode();
        update.IsCheckedChanged += (_, _) => UpdateExportMode();
        modes.Children.Add(fresh); modes.Children.Add(update); content.Children.Add(modes);
        WrapPanel historyActions = new();
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
        content.Children.Add(ExportName(new TextBox { MaxLength = 32768, HorizontalAlignment = HorizontalAlignment.Stretch }, "ArchiveExportDestination"));
        Button browse = ExportName(new Button(), "ArchiveExportBrowse"); browse.Click += OnExportBrowse; content.Children.Add(browse);
        content.Children.Add(ExportName(new CheckBox { IsChecked = true }, "ArchiveExportMedia"));
        content.Children.Add(ExportName(new TextBlock { TextWrapping = TextWrapping.Wrap }, "ArchiveExportStatus"));
        Grid body = new() { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 10 };
        body.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        WrapPanel actions = new() { HorizontalAlignment = HorizontalAlignment.Right };
        Button close = ExportName(new Button(), "ArchiveExportClose"); close.Click += OnExportClose;
        Button open = ExportName(new Button(), "ArchiveExportOpen"); open.Click += OnExportOpen;
        Button start = ExportName(new Button(), "ArchiveExportStart"); start.Click += OnExportStart;
        actions.Children.Add(close); actions.Children.Add(open); actions.Children.Add(start); Grid.SetRow(actions, 1); body.Children.Add(actions);
        _exportCard = new Border { Child = body, MaxWidth = 680, Padding = new Thickness(16), Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
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
        Control<TextBlock>("ArchiveExportTitle").Text = Texts.T("archive.export");
        Control<TextBlock>("ArchiveExportHint").Text = Texts.T(IsCollectionExport ? "export.collectionHint" : "export.hint");
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
