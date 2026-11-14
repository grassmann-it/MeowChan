using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class DiagnosticsPage
{
    private SessionFeedbackStore? _feedback;
    internal void AttachFeedback(SessionFeedbackStore feedback)
    {
        if (_feedback != null) _feedback.Changed -= RefreshFeedback;
        _feedback = feedback;
        _feedback.Changed += RefreshFeedback;
        RefreshFeedback();
    }
    private void RefreshFeedback()
    {
        if (_ended) return;
        DataGrid grid = this.FindControl<DataGrid>("SessionFeedbackGrid")!;
        long? selected = (grid.SelectedItem as SessionFeedback)?.Id;
        SessionFeedback[] entries = _feedback?.Entries.ToArray() ?? Array.Empty<SessionFeedback>();
        grid.ItemsSource = entries;
        grid.SelectedItem = entries.FirstOrDefault(entry => entry.Id == selected);
        grid.Columns[0].Header = Texts.T("feedback.time");
        grid.Columns[1].Header = Texts.T("diagnostics.category");
        grid.Columns[2].Header = Texts.T("diagnostics.message");
        this.FindControl<Expander>("SessionFeedbackSection")!.Header = Texts.T("feedback.title") + " (" + entries.Length + ")";
        this.FindControl<TextBlock>("SessionFeedbackEmpty")!.Text = Texts.T("feedback.empty");
        this.FindControl<TextBlock>("SessionFeedbackEmpty")!.IsVisible = entries.Length == 0;
        this.FindControl<Button>("ClearSessionFeedbackButton")!.IsEnabled = entries.Length != 0;
        ApplyFeedbackButtonLabels();
        AutomationProperties.SetName(grid, Texts.T("feedback.title"));
        AutomationProperties.SetName(this.FindControl<TextBox>("SessionFeedbackDetails")!, Texts.T("settings.errorDetails"));
        RefreshFeedbackSelection();
    }
    // Copy (one copy button per context), "mark as read" (it only changes how the row is presented; nothing is hidden or
    // removed) and clear: icon and text each, names from the catalog.
    private void ApplyFeedbackButtonLabels()
    {
        Button copy = this.FindControl<Button>("CopySessionFeedbackButton")!;
        SetIconLabel(copy, "copy", Texts.T("feedback.copy"));
        ToolTip.SetTip(copy, Texts.T("diagnostics.copyPrivacy"));
        AutomationProperties.SetHelpText(copy, Texts.T("diagnostics.copyPrivacy"));
        SetIconLabel(this.FindControl<Button>("DismissSessionFeedbackButton")!, "check", Texts.T("feedback.markRead"));
        SetIconLabel(this.FindControl<Button>("ClearSessionFeedbackButton")!, "x-circle", Texts.T("feedback.clear"));
    }

    /// <summary>A button of icon and text (catalog icon by file name): the content is built once, a language change only renames it.</summary>
    private static void SetIconLabel(Button button, string icon, string label)
    {
        button.Classes.Set("command-icon", true);
        if (button.Content is StackPanel { Tag: string current, Children: [_, TextBlock text] } && current == icon)
        { if (text.Text != label) text.Text = label; }
        else
            button.Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 5,
                Tag = icon,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children = { CommandBindings.NamedIcon(icon), new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } }
            };
        AutomationProperties.SetName(button, label);
    }

    private void OnFeedbackSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshFeedbackSelection();
    private void RefreshFeedbackSelection()
    {
        SessionFeedback? entry = this.FindControl<DataGrid>("SessionFeedbackGrid")!.SelectedItem as SessionFeedback;
        this.FindControl<TextBox>("SessionFeedbackDetails")!.Text = entry?.DetailText ?? string.Empty;
        this.FindControl<Button>("CopySessionFeedbackButton")!.IsEnabled = !_ended && _copy != null && entry != null;
        this.FindControl<Button>("DismissSessionFeedbackButton")!.IsEnabled = !_ended && entry != null
            && _feedback != null && !_feedback.IsDismissed(entry);
        RenderDiagnosticActionFeedback();
    }
    private void OnDismissFeedback(object? sender, RoutedEventArgs e)
    {
        if (_ended || _feedback == null
            || this.FindControl<DataGrid>("SessionFeedbackGrid")!.SelectedItem is not SessionFeedback entry) return;
        // Acknowledgement changes presentation only: retain the row, details,
        // timestamp and repeat count. Dismiss intentionally emits no Changed event.
        _feedback.Dismiss(entry);
        RefreshFeedbackSelection();
    }
    private void OnClearFeedback(object? sender, RoutedEventArgs e) { if (!_ended) _feedback?.Clear(); }
    private async void OnCopyFeedback(object? sender, RoutedEventArgs e)
    {
        if (_ended || _copy == null || this.FindControl<DataGrid>("SessionFeedbackGrid")!.SelectedItem is not SessionFeedback entry) return;
        int generation = BeginDiagnosticAction();
        try { await _copy(entry.DetailText); }
        catch (Exception)
        {
            if (_ended) return;
            string message = Texts.T("clipboard.writeFailed");
            ShowDiagnosticActionFeedback(generation, new(ActionContextKind.SessionFeedback, FeedbackId: entry.Id), message);
            _feedback?.Record(FeedbackCategory.Error, "diagnostics.copy-message", entry.Id.ToString(), message);
        }
    }
}
