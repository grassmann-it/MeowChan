using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        this.FindControl<Button>("ClearSessionFeedbackButton")!.Content = Texts.T("feedback.clear");
        this.FindControl<Button>("CopySessionFeedbackButton")!.Content = Texts.T("feedback.copy");
        AutomationProperties.SetName(grid, Texts.T("feedback.title"));
        AutomationProperties.SetName(this.FindControl<TextBox>("SessionFeedbackDetails")!, Texts.T("settings.errorDetails"));
        RefreshFeedbackSelection();
    }
    private void OnFeedbackSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshFeedbackSelection();
    private void RefreshFeedbackSelection()
    {
        SessionFeedback? entry = this.FindControl<DataGrid>("SessionFeedbackGrid")!.SelectedItem as SessionFeedback;
        this.FindControl<TextBox>("SessionFeedbackDetails")!.Text = entry?.DetailText ?? string.Empty;
        this.FindControl<Button>("CopySessionFeedbackButton")!.IsEnabled = _copy != null && entry != null;
    }
    private void OnClearFeedback(object? sender, RoutedEventArgs e) { if (!_ended) _feedback?.Clear(); }
    private async void OnCopyFeedback(object? sender, RoutedEventArgs e)
    {
        if (_ended || _copy == null || this.FindControl<DataGrid>("SessionFeedbackGrid")!.SelectedItem is not SessionFeedback entry) return;
        try { await _copy(entry.DetailText); }
        catch (Exception)
        {
            if (_ended) return;
            string message = Texts.T("clipboard.writeFailed");
            TextBlock text = this.FindControl<TextBlock>("DiagnosticsActionError")!;
            AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Assertive);
            text.Text = message;
            _feedback?.Record(FeedbackCategory.Error, "diagnostics.copy-message", entry.Id.ToString(), message);
        }
    }
}
