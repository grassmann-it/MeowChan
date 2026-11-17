using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// Export archive (archive.transfer-export): scope, media choice, the fixed limits of the transfer format, a create-only
/// destination and the result. Modelled on the transfer import dialog: header with the task, error strip, body with the
/// choices and counts, footer with the polite state line above the actions and Close right-most. The window never opens a
/// reader, a stream or the destination itself; counting and exporting go through the host delegates off the UI thread.
/// </summary>
internal sealed class ArchiveTransferExportDialog : Window, IDisposable
{
    private readonly Func<ArchiveTransferExportRequest, CancellationToken, Task<ArchiveTransferExportCount>> _count;
    private readonly Func<ArchiveTransferExportRequest, string, IProgress<ArchiveTransferExportProgress>, CancellationToken,
        Task<ArchiveTransferExportResult>> _export;
    private readonly Func<string, Task<string?>> _chooseFile;
    private readonly Action<string>? _openFolder;
    private readonly IReadOnlyList<ResourceId> _selected;
    private readonly Func<DateTime> _today;
    private readonly CancellationTokenSource _lifetime;
    private readonly TextBlock _intro = new() { Name = "ArchiveTransferExportIntro", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _scopeLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _scope = new() { Name = "ArchiveTransferExportScope", MinWidth = 260 };
    private readonly TextBlock _mediaLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _media = new() { Name = "ArchiveTransferExportMedia", MinWidth = 260 };
    private readonly TextBlock _mediaHelp = new() { Name = "ArchiveTransferExportMediaHelp", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { Name = "ArchiveTransferExportSummary", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _limits = new() { Name = "ArchiveTransferExportLimits", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _overLimit = new() { Name = "ArchiveTransferExportOverLimit", TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _full = new() { Name = "ArchiveTransferExportFull" };
    private readonly TextBlock _fullHelp = new() { Name = "ArchiveTransferExportFullHelp", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _destinationLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _destination = new() { Name = "ArchiveTransferExportDestination", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _result = new() { Name = "ArchiveTransferExportResult", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { Name = "ArchiveTransferExportState", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Name = "ArchiveTransferExportError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _choose = new() { Name = "ArchiveTransferExportChoose" };
    private readonly Button _cancel = new() { Name = "ArchiveTransferExportCancel" };
    private readonly Button _start = new() { Name = "ArchiveTransferExportStart" };
    private readonly Button _folder = new() { Name = "ArchiveTransferExportOpenFolder" };
    private readonly Button _close = new() { Name = "ArchiveTransferExportClose" };
    private CancellationTokenSource? _operation;
    private ArchiveTransferExportCount? _counted;
    private ArchiveTransferExportResult? _last;
    private ArchiveTransferExportProgress? _progress;
    private string? _target;
    private string _stateKey = "transfer.export.idle";
    private string? _errorKey;
    private bool _counting, _running, _picking, _closed, _localizing;
    private int _countGeneration;
    private Task _completion = Task.CompletedTask;

    internal ArchiveTransferExportDialog(DesktopSessionHost host, IReadOnlyList<ResourceId> selected,
        Func<string, Task<string?>> chooseFile, Action<string>? openFolder, CancellationToken lifetime = default)
        : this((request, token) => host.CountArchiveTransferExportAsync(request, token),
            (request, target, progress, token) => host.ExportArchiveTransferAsync(request, target, progress, token),
            selected, chooseFile, openFolder, () => DateTime.Now, lifetime) { }

    // Delegates let host-free tests drive the window with controlled results and cancellation points.
    internal ArchiveTransferExportDialog(
        Func<ArchiveTransferExportRequest, CancellationToken, Task<ArchiveTransferExportCount>> count,
        Func<ArchiveTransferExportRequest, string, IProgress<ArchiveTransferExportProgress>, CancellationToken, Task<ArchiveTransferExportResult>> export,
        IReadOnlyList<ResourceId> selected, Func<string, Task<string?>> chooseFile, Action<string>? openFolder,
        Func<DateTime>? today = null, CancellationToken lifetime = default)
    {
        _count = count ?? throw new ArgumentNullException(nameof(count));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _selected = (selected ?? throw new ArgumentNullException(nameof(selected))).Distinct().ToArray();
        _chooseFile = chooseFile ?? throw new ArgumentNullException(nameof(chooseFile));
        _openFolder = openFolder;
        _today = today ?? (() => DateTime.Now);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Name = "ArchiveTransferExportDialog";
        // Modals plan MD2: a form (M); the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        _intro.Classes.Add("workspace-help");
        _scopeLabel.Classes.Add("section-title"); _mediaLabel.Classes.Add("section-title"); _destinationLabel.Classes.Add("section-title");
        _mediaHelp.Classes.Add("workspace-help"); _limits.Classes.Add("workspace-help"); _fullHelp.Classes.Add("workspace-help");
        _summary.Classes.Add("workspace-meta"); _destination.Classes.Add("workspace-meta"); _state.Classes.Add("workspace-meta");
        _overLimit.Classes.Add("workspace-warning");
        _start.Classes.Add("primary"); _close.Classes.Add("subtle");
        // I1b: the secondary tools carry a catalog icon before their text (Export, Cancel and Close stay text).
        IconButtons.WithText(_choose, "folder"); IconButtons.WithText(_folder, "folder");
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _scope.SelectionChanged += (_, _) => { if (!_localizing) OnChoiceChanged(); };
        _media.SelectionChanged += (_, _) => { if (!_localizing) OnChoiceChanged(); };
        // The count does not depend on splitting; only the limits that block Export do.
        _full.IsCheckedChanged += (_, _) => { if (!_localizing && !_running) { _last = null; Render(); } };
        _choose.Click += async (_, _) => await ChooseAsync();
        _start.Click += async (_, _) => await StartAsync();
        _cancel.Click += (_, _) => CancelOperation();
        _folder.Click += (_, _) => OpenFolder();
        _close.Click += (_, _) => Close();

        StackPanel header = new() { Spacing = 8, Children = { _intro } };
        StackPanel body = new() { Spacing = 8, Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_scopeLabel); body.Children.Add(_scope);
        body.Children.Add(_mediaLabel); body.Children.Add(_media); body.Children.Add(_mediaHelp);
        body.Children.Add(_summary); body.Children.Add(_overLimit); body.Children.Add(_limits);
        body.Children.Add(_full); body.Children.Add(_fullHelp);
        body.Children.Add(_destinationLabel); body.Children.Add(_destination); body.Children.Add(_result);
        Border footer = DialogFooter.Build(_close, new Control[] { _choose, _cancel, _folder, _start }, _state, statusAbove: true);
        DialogWindow.Frame(this, DialogFrame.Build(header, body, footer, DialogFrame.ErrorStrip(_error)), modal: true);

        // Escape first cancels a running count or export, then closes.
        DialogEscape.Attach(this, () =>
        {
            if (!_counting && !_running) return false;
            CancelOperation(); return true;
        });
        Closed += (_, _) => Terminate();
        Texts.Changed += OnLanguageChanged;
        ApplyLabels();
        _ = RecountAsync();
    }

    internal Button ChooseButton => _choose;
    internal Button StartButton => _start;
    internal Button CancelButton => _cancel;
    internal Button OpenFolderButton => _folder;
    internal Button CloseButton => _close;
    internal ComboBox ScopeBox => _scope;
    internal ComboBox MediaBox => _media;
    internal CheckBox FullExportBox => _full;
    internal TextBlock SummaryText => _summary;
    internal TextBlock OverLimitText => _overLimit;
    internal TextBlock LimitsText => _limits;
    internal TextBlock StateText => _state;
    internal TextBlock ErrorText => _error;
    internal TextBlock ResultText => _result;
    internal TextBlock DestinationText => _destination;
    internal ArchiveTransferExportResult? LastResult => _last;
    internal ArchiveTransferExportCount? Counted => _counted;
    /// <summary>The running count or export; the page awaits it after the window closed so the host operation has ended.</summary>
    internal Task Completion => _completion;
    internal Task ChooseForTestsAsync() => ChooseAsync();
    internal Task StartForTestsAsync() => StartAsync();
    internal Task RecountForTestsAsync() => RecountAsync();

    internal ArchiveTransferExportRequest Request => new(SelectedScopeIsSelection ? _selected : null, SelectedMedia, FullExport);
    private bool FullExport => _full.IsChecked == true;
    private bool SelectedScopeIsSelection => _selected.Count > 0 && _scope.SelectedIndex == 0;
    private ArchiveTransferExportMedia SelectedMedia => _media.SelectedIndex switch
    {
        1 => ArchiveTransferExportMedia.Originals,
        2 => ArchiveTransferExportMedia.Everything,
        _ => ArchiveTransferExportMedia.MetadataOnly
    };

    internal void Terminate() => Dispose();

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Texts.Changed -= OnLanguageChanged;
        try { _operation?.Cancel(); }
        finally
        {
            try { _lifetime.Cancel(); }
            finally { _lifetime.Dispose(); }
        }
        // Only child tokens are handed to workers. Each operation disposes its own source after it exits.
    }

    private void OnChoiceChanged()
    {
        if (_closed || _running) return;
        _last = null; _errorKey = null;
        _ = RecountAsync();
    }

    private async Task RecountAsync()
    {
        if (_closed || _running) return;
        int generation = ++_countGeneration;
        _operation?.Cancel();
        using CancellationTokenSource operation = _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            _counting = true; _counted = null; _stateKey = "transfer.export.counting";
            Render();
            // The host admits one count at a time; let the cancelled previous count unwind instead of being refused as busy.
            Task previous = _completion;
            if (!previous.IsCompleted) { try { await previous; } catch { /* Superseded. */ } }
            if (_closed || generation != _countGeneration) return;
            Task<ArchiveTransferExportCount> pending;
            try { pending = _count(Request, operation.Token); }
            catch (Exception) { pending = Task.FromException<ArchiveTransferExportCount>(new InvalidOperationException()); }
            Task tracked = pending;
            _completion = tracked;
            try
            {
                ArchiveTransferExportCount counted = await pending;
                if (_closed || generation != _countGeneration) return;
                _counted = counted;
                _errorKey = counted.IsEmpty ? "transfer.export.error.empty" : null;
                _stateKey = counted.WithinLimits ? (_target == null ? "transfer.export.idle" : "transfer.export.ready") : "transfer.export.idle";
            }
            catch (OperationCanceledException) when (generation != _countGeneration || _closed || operation.IsCancellationRequested)
            {
                if (_closed || generation != _countGeneration) return;
                _stateKey = "transfer.export.idle";
            }
            catch (Exception)
            {
                if (_closed || generation != _countGeneration) return;
                _errorKey = "transfer.export.error.count"; _stateKey = "transfer.export.idle";
            }
            finally
            {
                if (generation == _countGeneration) _counting = false;
                if (!_closed && generation == _countGeneration) Render();
            }
        }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
        }
    }

    private async Task ChooseAsync()
    {
        if (_closed || _running || _picking) return;
        _picking = true;
        Render();
        try
        {
            string? path;
            try { path = await _chooseFile(ArchiveTransferExportDestination.DefaultFileName(_today())); }
            catch (Exception) { path = null; }
            if (_closed || string.IsNullOrWhiteSpace(path)) return;
            _last = null;
            string? full = ArchiveTransferExportDestination.Normalize(path);
            if (full == null) { _target = null; _errorKey = "transfer.export.error.destination"; }
            else if (ArchiveTransferExportDestination.Exists(full)) { _target = null; _errorKey = "transfer.export.error.exists"; }
            else { _target = full; _errorKey = _counted?.IsEmpty == true ? "transfer.export.error.empty" : null; }
            if (_counted?.WithinLimits == true) _stateKey = _target == null ? "transfer.export.idle" : "transfer.export.ready";
        }
        finally
        {
            _picking = false;
            if (!_closed) Render();
        }
    }

    private async Task StartAsync()
    {
        if (!CanStart || _target is not { } target) return;
        using CancellationTokenSource operation = _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            _running = true; _last = null; _errorKey = null; _progress = null; _stateKey = "transfer.export.reading";
            Render();
            Progress<ArchiveTransferExportProgress> progress = new(report =>
            {
                if (_closed || !_running) return;
                _progress = report;
                _stateKey = report.Phase switch
                {
                    ArchiveTransferExportPhase.Counting => "transfer.export.counting",
                    ArchiveTransferExportPhase.Reading => "transfer.export.reading",
                    _ => "transfer.export.writing"
                };
                Render();
            });
            Task<ArchiveTransferExportResult> pending;
            try { pending = _export(Request, target, progress, operation.Token); }
            catch (Exception) { pending = Task.FromResult(new ArchiveTransferExportResult(ArchiveTransferExportError.WriteFailure)); }
            _completion = pending;
            ArchiveTransferExportResult result;
            try { result = await pending; }
            catch (OperationCanceledException) { result = new(ArchiveTransferExportError.Canceled); }
            catch (Exception) { result = new(ArchiveTransferExportError.WriteFailure); }
            _running = false;
            if (_closed) return;
            _last = result;
            if (result.Count is { } counted) _counted = counted;
            switch (result.Error)
            {
                case ArchiveTransferExportError.None:
                    _stateKey = "transfer.export.completed"; _errorKey = null;
                    // The same name cannot be used again; the next export needs a new file name.
                    _target = null;
                    break;
                case ArchiveTransferExportError.Canceled:
                    _stateKey = "transfer.export.canceled"; _errorKey = null;
                    break;
                default:
                    _stateKey = "transfer.export.failed"; _errorKey = ErrorKey(result.Error);
                    if (result.Error is ArchiveTransferExportError.DestinationExists or ArchiveTransferExportError.InvalidDestination
                        || result.Parts.Count > 0) _target = null;
                    break;
            }
            Render();
        }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
        }
    }

    private void CancelOperation()
    {
        if (_closed) return;
        if (!_counting && !_running) return;
        try { _operation?.Cancel(); } catch (ObjectDisposedException) { }
        if (_counting) { _countGeneration++; _counting = false; _stateKey = "transfer.export.idle"; Render(); }
    }

    private void OpenFolder()
    {
        if (_closed || _last?.Parts.FirstOrDefault()?.Path is not { } path) return;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory != null) _openFolder?.Invoke(directory);
        }
        catch (Exception) { _errorKey = "transfer.export.error.openFolder"; Render(); }
    }

    private bool CanStart => !_closed && !_running && !_counting && !_picking && _target != null
        && _counted is { IsEmpty: false, MissingThreads: 0 } counted && (counted.WithinLimits || FullExport);

    internal static string ErrorKey(ArchiveTransferExportError error) => error switch
    {
        ArchiveTransferExportError.DestinationExists => "transfer.export.error.exists",
        ArchiveTransferExportError.InvalidDestination => "transfer.export.error.destination",
        ArchiveTransferExportError.StaleSnapshot => "transfer.export.error.stale",
        ArchiveTransferExportError.SelectionLimit => "transfer.export.error.limit",
        ArchiveTransferExportError.ValidationFailed => "transfer.export.error.validation",
        ArchiveTransferExportError.MediaUnavailable => "transfer.export.error.media",
        ArchiveTransferExportError.ReadFailure => "transfer.export.error.read",
        ArchiveTransferExportError.Busy => "transfer.export.error.busy",
        ArchiveTransferExportError.ProfileUnavailable => "transfer.export.error.profile",
        ArchiveTransferExportError.EmptySelection => "transfer.export.error.empty",
        _ => "transfer.export.error.write"
    };

    /// <summary>The over-limit explanation of a count, or empty when it is within the limits.</summary>
    internal static string DescribeOverLimit(ArchiveTransferExportCount? count)
    {
        if (count == null || count.IsEmpty || count.WithinLimits) return string.Empty;
        List<string> parts = new();
        if (count.ThreadsOverLimit) parts.Add(Texts.T("transfer.export.overThreads", count.Threads, ArchiveTransferExportCount.MaxThreads));
        if (count.PostsOverLimit) parts.Add(Texts.T("transfer.export.overPosts", count.Posts, ArchiveTransferExportCount.MaxPosts));
        if (count.MediaDirectoriesOverLimit) parts.Add(Texts.T("transfer.export.overFolders", ArchiveTransferExportCount.MaxMediaDirectories));
        return Texts.T("transfer.export.overLimit", string.Join(" · ", parts));
    }

    internal static string DescribeCount(ArchiveTransferExportCount count, ArchiveTransferExportMedia media)
    {
        string folders = count.MediaDirectories == null ? Texts.T("transfer.export.foldersUnknown")
            : count.MediaDirectoriesTruncated ? Texts.T("transfer.export.foldersOver", ArchiveTransferExportCount.MaxMediaDirectories)
            : count.MediaDirectories.Value.ToString("N0", Texts.Culture);
        List<string> lines = new() { Texts.T("transfer.export.count", count.Threads, count.Posts, folders) };
        if (count.MissingThreads > 0) lines.Add(Texts.T("transfer.export.missing", count.MissingThreads));
        if (media != ArchiveTransferExportMedia.MetadataOnly && count.MediaDirectories != null && !count.MediaDirectoriesTruncated)
            lines.Add(count.EstimatedMediaBytes is { } bytes
                ? Texts.T("transfer.export.estimate", SizeText.Format(bytes), count.EstimatedMediaFiles ?? 0)
                : Texts.T("transfer.export.estimateUnknown"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The warning under the count: what one package cannot hold, or (Full export) that several packages will be written.
    /// Empty when one package is expected to fit.
    /// </summary>
    internal static string DescribeLimitState(ArchiveTransferExportCount? count, ArchiveTransferExportMedia media, bool full)
    {
        if (count == null || count.IsEmpty) return string.Empty;
        bool tooLarge = media != ArchiveTransferExportMedia.MetadataOnly && count.LikelyTooLargeForOnePackage;
        if (full) return !count.WithinLimits || tooLarge ? Texts.T("transfer.export.splitNote") : string.Empty;
        if (!count.WithinLimits) return DescribeOverLimit(count);
        return tooLarge ? Texts.T("transfer.export.tooLarge", SizeText.Format(ArchiveTransferExportCount.MaxMediaBytes),
            ArchiveTransferExportCount.MaxMediaFiles) : string.Empty;
    }

    internal static string DescribeResult(ArchiveTransferExportResult result)
    {
        List<string> lines = new();
        if (result.Succeeded && result.Parts.Count == 1)
            lines.Add(Texts.T("transfer.export.result", result.Parts[0].Threads, SizeText.Format(result.Parts[0].Bytes), result.Parts[0].Path));
        else if (result.Parts.Count > 0)
        {
            lines.Add(Texts.T(result.Succeeded ? "transfer.export.resultParts" : "transfer.export.partsKept", result.Parts.Count,
                result.Parts.Sum(part => part.Threads), SizeText.Format(result.Parts.Sum(part => part.Bytes))));
            lines.AddRange(result.Parts.Select(part => part.Path));
        }
        if (result.WithoutMedia.Count > 0)
            lines.Add(Texts.T("transfer.export.withoutMedia", string.Join(", ", result.WithoutMedia.Select(Identity))));
        if (result.Skipped.Count > 0)
            lines.Add(Texts.T("transfer.export.skipped", string.Join(", ", result.Skipped.Select(skip => Identity(skip.Thread)))));
        if (result.StageRetained) lines.Add(Texts.T("transfer.export.stageRetained"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Identity(ResourceId id) => id.Source + "/" + id.Board + "/" + id.Thread;

    private void Render()
    {
        if (_closed) return;
        Title = Texts.T("transfer.export.title");
        _intro.Text = Texts.T("transfer.export.intro");
        _mediaHelp.Text = Texts.T(SelectedMedia switch
        {
            ArchiveTransferExportMedia.Originals => "transfer.export.mediaOriginalsHelp",
            ArchiveTransferExportMedia.Everything => "transfer.export.mediaEverythingHelp",
            _ => "transfer.export.mediaMetadataHelp"
        });
        _limits.Text = Texts.T("transfer.export.limits", ArchiveTransferExportCount.MaxThreads, ArchiveTransferExportCount.MaxPosts,
            ArchiveTransferExportCount.MaxAttachments, ArchiveTransferExportCount.MaxMediaDirectories,
            ArchiveTransferExportCount.MaxMediaFiles, SizeText.Format(ArchiveTransferExportCount.MaxMediaBytes));
        _fullHelp.Text = Texts.T("transfer.export.fullHelp");
        _summary.Text = _counted == null ? string.Empty : DescribeCount(_counted, SelectedMedia);
        _overLimit.Text = DescribeLimitState(_counted, SelectedMedia, FullExport);
        _overLimit.IsVisible = _overLimit.Text.Length > 0;
        _destinationLabel.Text = Texts.T("transfer.export.destination");
        _destination.Text = _target ?? Texts.T("transfer.export.noDestination");
        string state = _stateKey == "transfer.export.writing" && _progress?.PayloadBytes is > 0 and var bytes
            ? Texts.T("transfer.export.writingSize", SizeText.Format(bytes)) : Texts.T(_stateKey);
        _state.Text = _running && _progress is { Packages: > 1 } step && _stateKey != "transfer.export.counting"
            ? Texts.T("transfer.export.packageProgress", step.Package, step.Packages, state) : state;
        _error.Text = _errorKey == null ? string.Empty : Texts.T(_errorKey);
        _result.Text = _last == null ? string.Empty : DescribeResult(_last);
        _result.IsVisible = _result.Text.Length > 0;
        bool idle = !_running && !_picking;
        _scope.IsEnabled = idle && _selected.Count > 0;
        _media.IsEnabled = idle;
        _full.IsEnabled = idle;
        _choose.IsEnabled = idle;
        _cancel.IsEnabled = _running || _counting;
        _start.IsEnabled = CanStart;
        _folder.IsEnabled = idle && _last is { Parts.Count: > 0 } && _openFolder != null;
        _folder.IsVisible = _last is { Parts.Count: > 0 };
    }

    private void SetButtonLabel(Button button, string key)
    {
        string text = Texts.T(key);
        button.Content = text; // WithText draws the icon before this string; the string stays the content.
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, text);
    }

    private void ApplyLabels()
    {
        if (_closed) return;
        _localizing = true;
        try
        {
            int scope = _scope.SelectedIndex, media = _media.SelectedIndex;
            _scopeLabel.Text = Texts.T("transfer.export.scope");
            _scope.ItemsSource = _selected.Count > 0
                ? new[] { Texts.T("transfer.export.scopeSelected", _selected.Count), Texts.T("transfer.export.scopeAll") }
                : new[] { Texts.T("transfer.export.scopeAll") };
            _scope.SelectedIndex = _selected.Count > 0 && scope is 0 or 1 ? scope : 0;
            AutomationProperties.SetName(_scope, _scopeLabel.Text);
            _mediaLabel.Text = Texts.T("transfer.export.media");
            _media.ItemsSource = new[] { Texts.T("transfer.export.mediaMetadata"), Texts.T("transfer.export.mediaOriginals"),
                Texts.T("transfer.export.mediaEverything") };
            _media.SelectedIndex = media is 0 or 1 or 2 ? media : 0;
            AutomationProperties.SetName(_media, _mediaLabel.Text);
            _full.Content = Texts.T("transfer.export.full");
            AutomationProperties.SetName(_full, Texts.T("transfer.export.full"));
            SetButtonLabel(_choose, "transfer.export.choose");
            SetButtonLabel(_cancel, "transfer.export.cancel");
            SetButtonLabel(_start, "transfer.export.start");
            SetButtonLabel(_folder, "transfer.export.openFolder");
            SetButtonLabel(_close, "action.close");
        }
        finally { _localizing = false; }
        Render();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLabels);
        else ApplyLabels();
    }
}
