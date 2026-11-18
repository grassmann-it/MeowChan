using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MeowChan.Archive;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// Shown during startup when native imports or merges stopped before their commit (crash, power loss,
/// full disk). Nothing of them was published. Discarding needs a second press that names count and size;
/// Keep, Escape or closing leaves everything as it is, and startup then stops with the recovery error.
/// </summary>
internal sealed class InterruptedTransfersDialog : Window
{
    private readonly IReadOnlyList<InterruptedNativeMediaOperation> _operations;
    private readonly TextBlock _explanation = new() { Name = "InterruptedTransfersExplanation", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _rows = new() { Name = "InterruptedTransfersRows", Spacing = 8 };
    private readonly Button _discard = new() { Name = "InterruptedTransfersDiscard" };
    private readonly Button _keep = new() { Name = "InterruptedTransfersKeep" };
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _confirming;

    internal InterruptedTransfersDialog(IReadOnlyList<InterruptedNativeMediaOperation> operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        Name = "InterruptedTransfersDialog";
        // Modals plan MD2: a modal form-step dialog; the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _explanation.Classes.Add("workspace-meta");
        AutomationProperties.SetLiveSetting(_discard, AutomationLiveSetting.Polite);
        _discard.Click += (_, _) => OnDiscard();
        _keep.Click += (_, _) => Close();
        // Header: what is at stake; body: the interrupted operations; footer: Discard, then Keep (the safe way out) right-most.
        StackPanel header = new() { Spacing = 12 };
        header.Children.Add(_explanation);
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_rows);
        DialogWindow.Frame(this, DialogFrame.Build(header, body, DialogFooter.Build(_keep, new Control[] { _discard })), modal: true);
        Opened += (_, _) => _keep.Focus();
        Closed += (_, _) => { Texts.Changed -= OnLanguageChanged; _decision.TrySetResult(false); };
        // Escape first withdraws the second press, then closes like Keep.
        DialogEscape.Attach(this, () =>
        {
            if (!_confirming) return false;
            _confirming = false; Render(); return true;
        });
        Texts.Changed += OnLanguageChanged;
        Render();
    }

    /// <summary>True only after the confirmed second press; false for Keep, Escape or closing.</summary>
    internal Task<bool> Decision => _decision.Task;

    private void OnLanguageChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Render);

    private void OnDiscard()
    {
        if (!_confirming) { _confirming = true; Render(); return; }
        _decision.TrySetResult(true);
        Close();
    }

    private void Render()
    {
        Title = Texts.T("transfer.interrupted.title");
        _explanation.Text = Texts.T("transfer.interrupted.explanation");
        long bytes = _operations.Sum(operation => operation.CandidateBytes);
        _discard.Content = _confirming
            ? Texts.T("transfer.interrupted.confirm", _operations.Count, bytes) : Texts.T("transfer.interrupted.discard");
        _keep.Content = Texts.T("transfer.interrupted.keep");
        // The second press renames Discard to what it will delete; the accessible names follow the visible words.
        AutomationProperties.SetName(_discard, (string)_discard.Content);
        AutomationProperties.SetName(_keep, (string)_keep.Content);
        AutomationProperties.SetName(_rows, Texts.T("transfer.interrupted.list"));
        _rows.Children.Clear();
        foreach (InterruptedNativeMediaOperation operation in _operations)
        {
            string resource = operation.Resource.Source + "/" + operation.Resource.Board + "/" + operation.Resource.Thread;
            string at = operation.ImportedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            TextBlock row = new()
            {
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = Texts.T(operation.Merge ? "transfer.interrupted.row.merge" : "transfer.interrupted.row.import",
                    resource, at, operation.CandidateBytes)
            };
            // Each entry is named by its own line (what, where, when, how much), never by the list.
            AutomationProperties.SetName(row, row.Text);
            _rows.Children.Add(row);
        }
    }
}
