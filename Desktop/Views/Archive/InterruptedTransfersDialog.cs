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
    private readonly WindowChromeBar _chrome = new();
    private readonly TextBlock _heading = new() { Name = "InterruptedTransfersHeading", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _explanation = new() { Name = "InterruptedTransfersExplanation", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _rows = new() { Name = "InterruptedTransfersRows", Spacing = 6 };
    private readonly Button _discard = new() { Name = "InterruptedTransfersDiscard" };
    private readonly Button _keep = new() { Name = "InterruptedTransfersKeep" };
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _confirming;

    internal InterruptedTransfersDialog(IReadOnlyList<InterruptedNativeMediaOperation> operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        Name = "InterruptedTransfersDialog";
        Width = 640; Height = 440; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = WorkspaceMetrics.TitleBarHeight;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _heading.Classes.Add("section-title");
        _explanation.Classes.Add("workspace-meta");
        AutomationProperties.SetLiveSetting(_discard, AutomationLiveSetting.Polite);
        _discard.Click += (_, _) => OnDiscard();
        _keep.Click += (_, _) => Close();
        // Header: title and what is at stake; body: the interrupted operations; footer: Discard/Keep.
        StackPanel header = new() { Spacing = 12 };
        header.Children.Add(_heading); header.Children.Add(_explanation);
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_rows);
        WrapPanel actions = new() { ItemSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_discard); actions.Children.Add(_keep);
        Border footer = new() { Child = actions, Margin = new Thickness(16, 0, 16, 16) };
        footer.Classes.Add("workspace-dialog-footer");
        Grid layout = new() { RowDefinitions = WorkspaceMetrics.TitleBarRows("*") };
        layout.Children.Add(_chrome);
        Grid frame = DialogFrame.Build(header, body, footer);
        layout.Children.Add(frame); Grid.SetRow(frame, 1);
        Content = layout;
        Opened += (_, _) => { _chrome.Attach(this); _keep.Focus(); };
        Closed += (_, _) => { Texts.Changed -= OnLanguageChanged; _decision.TrySetResult(false); };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_confirming) { _confirming = false; Render(); } else Close();
        }, RoutingStrategies.Tunnel);
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
        _heading.Text = Texts.T("transfer.interrupted.title");
        Title = _heading.Text;
        _explanation.Text = Texts.T("transfer.interrupted.explanation");
        long bytes = _operations.Sum(operation => operation.CandidateBytes);
        _discard.Content = _confirming
            ? Texts.T("transfer.interrupted.confirm", _operations.Count, bytes) : Texts.T("transfer.interrupted.discard");
        _keep.Content = Texts.T("transfer.interrupted.keep");
        _rows.Children.Clear();
        foreach (InterruptedNativeMediaOperation operation in _operations)
        {
            string resource = operation.Resource.Source + "/" + operation.Resource.Board + "/" + operation.Resource.Thread;
            string at = operation.ImportedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            _rows.Children.Add(new TextBlock
            {
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = Texts.T(operation.Merge ? "transfer.interrupted.row.merge" : "transfer.interrupted.row.import",
                    resource, at, operation.CandidateBytes)
            });
        }
    }
}
