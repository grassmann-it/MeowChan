using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan;

public partial class PluginsPage
{
    private sealed class PluginToolViewState
    {
        internal string Url = string.Empty;
        internal PluginProbePresentation? Result;
        internal IReadOnlyList<PluginBuildDiagnostic> Diagnostics = Array.Empty<PluginBuildDiagnostic>();
    }

    // Detached, display-only data: never retain a worker result, snapshot, source
    // collection or test authorization. Raw preview text keeps the existing first
    // 100-entry bound and the validated SDK metadata/text limits; it is not translated.
    internal sealed class PluginProbePresentation
    {
        internal const int MaximumPreviewEntries = 100;
        internal enum ResultKind { Thread, Catalog, Failure, Deferred, NotFound, Unchanged }

        internal ResultKind Kind { get; }
        internal int EntryCount { get; }
        internal long AttachmentCount { get; }
        internal DateTimeOffset? RetryAt { get; }
        internal int? HttpStatusCode { get; }
        internal string RawContent { get; }
        internal bool PreviewLimited { get; }

        private PluginProbePresentation(ResultKind kind, string rawContent = "", int entryCount = 0,
            long attachmentCount = 0, DateTimeOffset? retryAt = null, int? httpStatusCode = null,
            bool previewLimited = false)
        {
            Kind = kind; RawContent = rawContent; EntryCount = entryCount;
            AttachmentCount = attachmentCount; RetryAt = retryAt; HttpStatusCode = httpStatusCode;
            PreviewLimited = previewLimited;
        }

        // Called only for the already validated result of the captured probe.
        // Materialize the bounded raw projection now, not a closure over result.
        internal static PluginProbePresentation Capture(object result) => result switch
        {
            MeowChan.PluginSdk.ThreadSnapshot thread => new(ResultKind.Thread,
                string.Join("\n", thread.Snapshot.Posts.Take(MaximumPreviewEntries).Select(post =>
                    post.PostId + ": " + post.Subject + " " + post.Text + "\n"
                    + string.Join("\n", post.Attachments?.Select(asset =>
                        "  " + asset.MediaId + " · " + asset.OriginalFileName + " · " + asset.Url) ?? Array.Empty<string>()))),
                entryCount: thread.Snapshot.Posts.Count,
                attachmentCount: thread.Snapshot.Posts.Sum(post => (long)(post.Attachments?.Count ?? 0)),
                previewLimited: thread.Snapshot.Posts.Count > MaximumPreviewEntries),
            CatalogSnapshot catalog => new(ResultKind.Catalog,
                string.Join("\n", catalog.Snapshot.Threads.Take(MaximumPreviewEntries).Select(thread =>
                    thread.Id.ThreadId + " · " + thread.Title + " · " + thread.Url)),
                entryCount: catalog.Snapshot.Threads.Count),
            ThreadFailure failure => new(ResultKind.Failure, failure.Failure.Code + ": " + failure.Failure.Message),
            CatalogFailure failure => new(ResultKind.Failure, failure.Failure.Code + ": " + failure.Failure.Message),
            ThreadDeferred deferred => new(ResultKind.Deferred, deferred.ReasonCode, retryAt: deferred.RetryAt),
            CatalogDeferred deferred => new(ResultKind.Deferred, deferred.ReasonCode, retryAt: deferred.RetryAt),
            ThreadNotFound missing => new(ResultKind.NotFound, httpStatusCode: missing.HttpStatusCode),
            ThreadUnchanged or CatalogUnchanged => new(ResultKind.Unchanged),
            _ => throw new InvalidOperationException("Unexpected plugin probe result.")
        };

        internal string Render() => Kind switch
        {
            ResultKind.Thread => Texts.T("plugins.threadResult", EntryCount, AttachmentCount) + "\n" + RawContent
                + (PreviewLimited ? "\n" + Texts.T("plugins.previewLimited") : string.Empty),
            ResultKind.Catalog => Texts.T("plugins.catalogResult", EntryCount) + "\n" + RawContent,
            ResultKind.Failure => RawContent,
            ResultKind.Deferred => Texts.T("plugins.deferred", RetryAt, RawContent),
            ResultKind.NotFound => Texts.T("plugins.notFound", HttpStatusCode),
            ResultKind.Unchanged => Texts.T("plugins.unchanged"),
            _ => throw new InvalidOperationException("Unexpected plugin probe presentation.")
        };
    }

    private readonly Dictionary<string, Dictionary<string, bool>> _pluginExpansionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginToolViewState> _pluginToolStates = new(StringComparer.Ordinal);
    private string? _pluginToolKey;
    private bool _restoringPluginTools;
    private bool _editorOptionsLoaded;

    private void InitializePluginDetails()
    {
        TrackExpansion(C<Expander>("PluginDeveloperExpander"), "developer-details");
        TrackExpansion(C<Expander>("PluginInfoExpander"), "information");
        TrackExpansion(C<Expander>("PluginGrantExpander"), "grants");
        TrackExpansion(C<Expander>("PluginEditorExpander"), "editor-options");
        C<TextBox>("PluginProbeUrl").TextChanging += (_, _) => CapturePluginProbeState();

        void TrackExpansion(Expander expander, string id)
        {
            expander.PropertyChanged += (_, args) =>
            {
                if (args.Property != Expander.IsExpandedProperty || _restoringPluginTools || _pluginToolKey is not { } key) return;
                ExpansionState(key)[id] = expander.IsExpanded;
                QueuePluginNavigationVisibility();
            };
        }
    }

    private Dictionary<string, bool> ExpansionState(string key)
    {
        if (!_pluginExpansionStates.TryGetValue(key, out var state))
            _pluginExpansionStates.Add(key, state = new(StringComparer.Ordinal));
        return state;
    }

    private PluginToolViewState ToolState(string key)
    {
        if (!_pluginToolStates.TryGetValue(key, out var state))
            _pluginToolStates.Add(key, state = new());
        return state;
    }

    private void CapturePluginProbeState()
    {
        if (_restoringPluginTools || _pluginToolKey is not { } key) return;
        PluginToolViewState state = ToolState(key);
        state.Url = C<TextBox>("PluginProbeUrl").Text ?? string.Empty;
    }

    // Selection and rebuild restore UI state, never an old test authorization or a configuration draft.
    private void RestorePluginToolState(PluginCatalogEntry entry)
    {
        CapturePluginProbeState();
        _pluginToolKey = entry.Key;
        _restoringPluginTools = true;
        try
        {
            if (!_editorOptionsLoaded)
            {
                PluginEditorOptions options = GetEditorOptions?.Invoke() ?? new(null, new[] { "{file}" });
                C<TextBox>("PluginEditorExecutable").Text = options.Executable;
                C<TextBox>("PluginEditorArguments").Text = string.Join(Environment.NewLine, options.Arguments);
                _editorOptionsLoaded = true;
            }
            var expansion = ExpansionState(entry.Key);
            // A disclosure the user has not touched for this plugin opens as the view says (Rights: open in Advanced).
            foreach (var item in new[] { ("PluginDeveloperExpander", "developer-details", "plugins.detail.developer"),
                ("PluginInfoExpander", "information", "plugins.detail.info"), ("PluginGrantExpander", "grants", "plugins.detail.grants"),
                ("PluginEditorExpander", "editor-options", (string?)null) })
                C<Expander>(item.Item1).IsExpanded = expansion.TryGetValue(item.Item2, out bool remembered) ? remembered
                    : item.Item3 != null && DefaultOpen(item.Item3);
            PluginToolViewState state = ToolState(entry.Key);
            C<TextBox>("PluginProbeUrl").Text = state.Url;
            RenderPluginProbeResult();
            RenderDiagnostics(state.Diagnostics, entry);
            C<TextBlock>("PluginTechnicalCopyStatus").Text = string.Empty;
        }
        finally { _restoringPluginTools = false; }
    }

    private void RenderPluginProbeResult()
    {
        if (_ended || _pluginToolKey is not { } key || key != _selectedKey) return;
        TextBox output = C<TextBox>("PluginProbeResult");
        string text = ToolState(key).Result?.Render() ?? string.Empty;
        if (output.Text == text) { RefreshWorkViewPresentation(); return; }
        int start = output.SelectionStart, end = output.SelectionEnd, caret = output.CaretIndex;
        output.Text = text;
        output.CaretIndex = Math.Clamp(caret, 0, text.Length);
        output.SelectionStart = Math.Clamp(start, 0, text.Length);
        output.SelectionEnd = Math.Clamp(end, 0, text.Length);
        RefreshWorkViewPresentation();
    }

    private void RenderTechnicalIdentity(PluginCatalogEntry entry)
    {
        Descriptions descriptions = C<Descriptions>("PluginIdentity");
        foreach (PluginTechnicalDetail detail in TechnicalDetails(entry))
        {
            if (!_identityRows.TryGetValue(detail.Key, out var row))
            {
                SelectableTextBlock value = new() { TextWrapping = TextWrapping.Wrap, Focusable = true };
                DescriptionsItem item = new() { Content = value };
                row = (item, value); _identityRows.Add(detail.Key, row); descriptions.Items.Add(item);
            }
            row.Item.Label = detail.Label;
            if (row.Value.Text != detail.Value) row.Value.Text = detail.Value;
            AutomationProperties.SetName(row.Value, detail.Label + ": " + detail.Value);
        }
    }

    private IReadOnlyList<PluginTechnicalDetail> TechnicalDetails(PluginCatalogEntry entry) => PluginTechnicalDetails.Create(entry,
        PluginEntryPresentation.Create(entry, _services?.GetProductionWorkerSnapshots?.Invoke(), CurrentDraft, _catalog?.Profile.Revision).Worker);

    private async void OnCopyTechnicalDetailsClick(object? sender, RoutedEventArgs args)
    {
        if (_ended || Selected is not { } entry) return;
        string key = entry.Key;
        string details = PluginTechnicalDetails.Format(TechnicalDetails(entry));
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException(Texts.T("plugins.clipboardUnavailable"));
            await clipboard.SetTextAsync(details);
            if (!_ended && Selected?.Key == key)
                C<TextBlock>("PluginTechnicalCopyStatus").Text = Texts.T("plugins.technicalDetailsCopied");
        }
        catch (Exception exception)
        {
            if (!_ended && Selected?.Key == key)
                C<TextBlock>("PluginTechnicalCopyStatus").Text = Texts.T("plugins.technicalDetailsCopyFailed", exception.Message);
        }
    }

    internal void OpenPluginDeveloperDetails()
    {
        if (!_ended && Selected is { } entry && Included(entry))
            RevealDeveloperArea();
    }

    private void FocusPluginBuildDiagnostics()
    {
        string? key = _selectedKey;
        long generation = _operationGeneration;
        if (_ended || _busy || key == null) return;
        OpenPluginDeveloperDetails();
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || _busy || _selectedKey != key || generation != _operationGeneration || TopLevel.GetTopLevel(this) == null) return;
            StackPanel diagnostics = C<StackPanel>("PluginBuildDiagnostics");
            if (diagnostics.Children.Count == 0) return;
            TopLevel.GetTopLevel(this)!.UpdateLayout();
            diagnostics.Focusable = true;
            AutomationProperties.SetName(diagnostics, Texts.T("plugins.buildDiagnostics"));
            diagnostics.BringIntoView(); diagnostics.Focus();
        }, DispatcherPriority.Loaded);
    }
}
