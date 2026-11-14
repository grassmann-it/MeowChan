using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// UX flows AP16: a large Add can leave a few rejected URLs between many accepted ones. Next to the results the user
// gets a "Problems only (n)" filter for the report and an action that puts the rejected URLs back into the Add field.
// Built in code beside the results panel (like the Archive hint), so the shared window layout stays untouched.
public partial class MainWindow
{
    private StackPanel? _admissionProblemTools;
    private CheckBox? _admissionProblemsOnlyBox;
    private Button? _admissionPutBackButton;
    private bool _admissionProblemsOnly;
    private string? _admissionSubmittedInput;
    private bool _admissionProblemToolsSyncing;

    internal StackPanel? AdmissionProblemToolsForTests => _admissionProblemTools;

    private static bool IsRejectedAdmission(AdmissionFeedback item) => item.Status is not (AdmissionStatus.Added or AdmissionStatus.AlreadyPresent);

    private void InitializeAdmissionProblemTools()
    {
        _admissionProblemsOnlyBox = new CheckBox { Name = "AdmissionProblemsOnly", VerticalAlignment = VerticalAlignment.Center };
        _admissionProblemsOnlyBox.IsCheckedChanged += (_, _) =>
        {
            if (_admissionProblemToolsSyncing) return;
            _admissionProblemsOnly = _admissionProblemsOnlyBox.IsChecked == true;
            // The report sits in the collapsed results expander: filtering without showing the report would look broken.
            if (_admissionProblemsOnly) AdmissionResultsExpander.IsExpanded = true;
            RefreshAdmissionResults();
        };
        _admissionPutBackButton = new Button { Name = "AdmissionPutBack", VerticalAlignment = VerticalAlignment.Center };
        _admissionPutBackButton.Classes.Add("subtle");
        _admissionPutBackButton.Click += (_, _) => PutRejectedBackIntoField();
        _admissionProblemTools = new StackPanel { Name = "AdmissionProblemTools", Orientation = Orientation.Horizontal, Spacing = 12, IsVisible = false };
        _admissionProblemTools.Children.Add(_admissionProblemsOnlyBox);
        _admissionProblemTools.Children.Add(_admissionPutBackButton);
        int after = WorkspaceIntakeStack.Children.IndexOf(AdmissionResultsPanel);
        WorkspaceIntakeStack.Children.Insert(after < 0 ? WorkspaceIntakeStack.Children.Count : after + 1, _admissionProblemTools);
    }

    /// <summary>Shows or hides the two tools for the finished results and refreshes their language.</summary>
    private void RefreshAdmissionProblemTools(int rejected, int total)
    {
        if (_admissionProblemTools == null || _admissionProblemsOnlyBox == null || _admissionPutBackButton == null) return;
        // A filter choice only lives while there is something to filter: new results start unfiltered.
        if (rejected == 0 || rejected >= total) _admissionProblemsOnly = false;
        _admissionProblemTools.IsVisible = rejected > 0;
        // Nothing to filter out when every result is a problem: the box would change nothing.
        _admissionProblemsOnlyBox.IsVisible = rejected > 0 && rejected < total;
        _admissionProblemToolsSyncing = true;
        try { _admissionProblemsOnlyBox.IsChecked = _admissionProblemsOnly && _admissionProblemsOnlyBox.IsVisible; }
        finally { _admissionProblemToolsSyncing = false; }
        _admissionProblemsOnlyBox.Content = Texts.T("admission.problemsOnly", rejected);
        AutomationProperties.SetName(_admissionProblemsOnlyBox, Texts.T("admission.problemsOnly", rejected));
        _admissionPutBackButton.Content = Texts.T("admission.putBack");
        AutomationProperties.SetName(_admissionPutBackButton, Texts.T("admission.putBack"));
    }

    private void PutRejectedBackIntoField()
    {
        if (_isClosing) return;
        IReadOnlyList<string> rejected = AdmissionProblemPlan.RejectedUrls(_admissionResults.Select(item => (item.Url, IsRejectedAdmission(item))));
        if (rejected.Count == 0) return;
        TextBox field = UrlInput;
        field.Text = AdmissionProblemPlan.FieldText(field.Text, _admissionSubmittedInput, rejected);
        field.Focus();
        field.CaretIndex = field.Text?.Length ?? 0;
    }
}
