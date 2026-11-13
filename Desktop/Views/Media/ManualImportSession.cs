using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>Where the import dialog stands.</summary>
internal enum ManualImportStage { Selecting, Checking, Ready, Importing, Done }

/// <summary>
/// The import dialog's model, without UI: the selection (files and folders), the two switches, the check (restarted on
/// every change, cancellable), the preview counts, the batch name and tags, the copy (cancellable after the current file)
/// and the summary. The host functions are injected, so tests drive it without a window or a profile.
/// </summary>
internal sealed class ManualImportSession
{
    internal delegate Task<ManualImportPlan> PlanFunction(ManualImportRequest request, IProgress<ManualImportCheckProgress> progress,
        CancellationToken token);

    internal delegate Task<ManualImportOutcome> RunFunction(ManualImportPlan plan, ManualImportOptions options,
        IProgress<ManualImportCopyProgress> progress, CancellationToken token);

    private readonly PlanFunction _plan;
    private readonly RunFunction _run;
    private readonly List<string> _roots = new();
    private readonly List<string> _tags = new();
    private CancellationTokenSource? _work;
    private int _generation;
    private bool _includeSubfolders = true;
    private bool _includeOtherFiles;

    internal ManualImportSession(PlanFunction plan, RunFunction run, DateTimeOffset now)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        BatchName = DefaultBatchName(now);
    }

    internal event Action? Updated;

    internal IReadOnlyList<string> Roots => _roots;
    internal IReadOnlyList<string> Tags => _tags;
    internal ManualImportStage Stage { get; private set; } = ManualImportStage.Selecting;
    internal ManualImportPlan? Plan { get; private set; }
    internal ManualImportCheckProgress? CheckProgress { get; private set; }
    internal ManualImportCopyProgress? CopyProgress { get; private set; }
    internal ManualImportOutcome? Outcome { get; private set; }
    /// <summary>A localization key explaining the last refusal or failure; null when there is none.</summary>
    internal string? MessageKey { get; private set; }
    internal object[] MessageArguments { get; private set; } = Array.Empty<object>();
    internal string BatchName { get; set; }
    internal bool Busy => Stage is ManualImportStage.Checking or ManualImportStage.Importing;
    /// <summary>The task of the current or last check/import; tests await it.</summary>
    internal Task WorkTask { get; private set; } = Task.CompletedTask;

    internal bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set { if (_includeSubfolders == value || Busy && Stage == ManualImportStage.Importing) return; _includeSubfolders = value; Recheck(); }
    }

    internal bool IncludeOtherFiles
    {
        get => _includeOtherFiles;
        set { if (_includeOtherFiles == value || Busy && Stage == ManualImportStage.Importing) return; _includeOtherFiles = value; Recheck(); }
    }

    internal int NewCount => Plan?.Count(ManualImportDisposition.New) ?? 0;
    internal int LinkedCount => (Plan?.Count(ManualImportDisposition.AlreadyInLibrary) ?? 0) + (Plan?.Count(ManualImportDisposition.DuplicateInSelection) ?? 0);
    internal int UnsupportedCount => Plan?.Items.Count(item => item.Disposition is ManualImportDisposition.Unsupported
        or ManualImportDisposition.OtherFilesOff or ManualImportDisposition.TooLarge or ManualImportDisposition.Skipped) ?? 0;
    internal int UnreadableCount => Plan?.Count(ManualImportDisposition.Unreadable) ?? 0;

    internal bool BatchNameValid => ManualImportCatalogStore.ValidName((BatchName ?? string.Empty).Trim(), ManualImportCatalogStore.MaximumBatchNameLength);

    internal bool CanImport => Stage == ManualImportStage.Ready && Plan is { Status: ManualImportPlanStatus.Ready }
        && NewCount + LinkedCount > 0 && BatchNameValid;

    internal static string DefaultBatchName(DateTimeOffset now) =>
        Texts.T("import.batch.default", now.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    /// <summary>Adds files or folders (each once) and checks the selection again.</summary>
    internal void AddRoots(IEnumerable<string> paths)
    {
        if (Stage is ManualImportStage.Importing or ManualImportStage.Done) return;
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        bool changed = false;
        foreach (string path in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (_roots.Contains(full, comparer)) continue;
            _roots.Add(full);
            changed = true;
        }
        if (changed) Recheck();
    }

    internal void RemoveRoot(int index)
    {
        if (Stage is ManualImportStage.Importing or ManualImportStage.Done || index < 0 || index >= _roots.Count) return;
        _roots.RemoveAt(index);
        Recheck();
    }

    /// <summary>Adds a tag name for the new files of the batch; false (with a message) for an invalid name.</summary>
    internal bool AddTag(string? text)
    {
        if (Stage is ManualImportStage.Importing or ManualImportStage.Done) return false;
        if (!PrivateTagName.TryCreate(text, out PrivateTagName? name, out _) || name == null)
        {
            SetMessage("import.tags.invalid");
            Changed();
            return false;
        }
        if (!_tags.Any(tag => PrivateTagName.TryCreate(tag, out PrivateTagName? known, out _) && known!.Key == name.Key))
        {
            if (_tags.Count >= ManualImportCatalogStore.MaximumTags) { SetMessage("import.tags.tooMany", ManualImportCatalogStore.MaximumTags); Changed(); return false; }
            _tags.Add(name.Display);
        }
        SetMessage(null);
        Changed();
        return true;
    }

    internal void RemoveTag(string tag)
    {
        if (Stage is ManualImportStage.Importing or ManualImportStage.Done) return;
        if (_tags.Remove(tag)) Changed();
    }

    /// <summary>Stops the running check or, during the copy, stops after the current file. Finished files stay.</summary>
    internal void Cancel() => _work?.Cancel();

    private void Recheck()
    {
        _work?.Cancel();
        int generation = ++_generation;
        Plan = null;
        CheckProgress = null;
        SetMessage(null);
        if (_roots.Count == 0) { Stage = ManualImportStage.Selecting; Changed(); WorkTask = Task.CompletedTask; return; }
        CancellationTokenSource work = new();
        _work = work;
        Stage = ManualImportStage.Checking;
        Changed();
        WorkTask = CheckAsync(generation, work);
    }

    private async Task CheckAsync(int generation, CancellationTokenSource work)
    {
        ManualImportRequest request = new(_roots.ToArray(), _includeSubfolders, _includeOtherFiles);
        Progress<ManualImportCheckProgress> progress = new(value => { if (generation == _generation) { CheckProgress = value; Changed(); } });
        try
        {
            ManualImportPlan plan = await _plan(request, progress, work.Token);
            if (generation != _generation) return;
            Plan = plan;
            Stage = ManualImportStage.Ready;
            SetMessage(plan.Status switch
            {
                ManualImportPlanStatus.TooManyFiles => "import.limit.files",
                ManualImportPlanStatus.BatchTooLarge => "import.limit.batch",
                _ => plan.NewCountOrLinked() == 0 ? "import.nothing" : null
            }, plan.Status == ManualImportPlanStatus.TooManyFiles ? new object[] { plan.ImportableFiles - 1 } : Array.Empty<object>());
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            if (generation != _generation) return;
            Stage = ManualImportStage.Selecting;
            SetMessage("import.check.cancelled");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ManualImportException
            or InvalidOperationException or ObjectDisposedException)
        {
            if (generation != _generation) return;
            Stage = ManualImportStage.Selecting;
            SetMessage("import.check.failed");
        }
        finally
        {
            if (ReferenceEquals(_work, work) && generation == _generation) _work = null;
            work.Dispose();
            if (generation == _generation) Changed();
        }
    }

    /// <summary>Copies the new files and applies the tags; ends in <see cref="ManualImportStage.Done"/> or back in Ready with a message.</summary>
    internal Task ImportAsync()
    {
        if (!CanImport) return Task.CompletedTask;
        ManualImportPlan plan = Plan!;
        CancellationTokenSource work = new();
        _work = work;
        int generation = ++_generation;
        Stage = ManualImportStage.Importing;
        CopyProgress = null;
        SetMessage(null);
        Changed();
        return WorkTask = RunAsync(plan, generation, work);
    }

    private async Task RunAsync(ManualImportPlan plan, int generation, CancellationTokenSource work)
    {
        Progress<ManualImportCopyProgress> progress = new(value => { if (generation == _generation) { CopyProgress = value; Changed(); } });
        try
        {
            Outcome = await _run(plan, new ManualImportOptions(BatchName.Trim(), _tags.ToArray()), progress, work.Token);
            Stage = ManualImportStage.Done;
        }
        catch (ManualImportException error)
        {
            Stage = ManualImportStage.Ready;
            SetMessage(MessageOf(error.Error));
        }
        catch (DesktopSessionBusyException)
        {
            Stage = ManualImportStage.Ready;
            SetMessage("import.busy");
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            // Cancelled before the batch was planned: nothing was written.
            Stage = ManualImportStage.Ready;
            SetMessage("import.run.cancelled");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or ObjectDisposedException)
        {
            Stage = ManualImportStage.Ready;
            SetMessage("import.run.failed");
        }
        finally
        {
            if (ReferenceEquals(_work, work)) _work = null;
            work.Dispose();
            Changed();
        }
    }

    internal static string MessageOf(ManualImportError error) => error switch
    {
        ManualImportError.CatalogDamaged => "import.error.damaged",
        ManualImportError.CatalogNewer => "import.error.newer",
        ManualImportError.TooManyImportedFiles => "import.error.total",
        ManualImportError.CatalogTooLarge => "import.error.catalogSize",
        ManualImportError.NotEnoughSpace => "import.error.space",
        ManualImportError.TooManyFiles => "import.limit.files",
        ManualImportError.BatchTooLarge => "import.limit.batch",
        ManualImportError.Conflict => "import.busy",
        _ => "import.run.failed"
    };

    private void SetMessage(string? key, params object[] arguments)
    {
        MessageKey = key;
        MessageArguments = arguments ?? Array.Empty<object>();
    }

    private void Changed() => Updated?.Invoke();

    /// <summary>The status word of one row (text, never only a color).</summary>
    internal static string StatusText(ManualImportCandidate item) => item.Disposition switch
    {
        ManualImportDisposition.New => Texts.T("import.row.new"),
        ManualImportDisposition.AlreadyInLibrary => Texts.T("import.row.existing", item.ExistingOccurrences),
        ManualImportDisposition.DuplicateInSelection => Texts.T("import.row.twice"),
        ManualImportDisposition.Unsupported => Texts.T("import.row.unsupported"),
        ManualImportDisposition.OtherFilesOff => Texts.T("import.row.otherOff"),
        ManualImportDisposition.TooLarge => Texts.T("import.row.tooLarge"),
        ManualImportDisposition.Unreadable => Texts.T("import.row.unreadable"),
        _ => Texts.T("import.row.skipped")
    };

    internal static string KindText(SavedMediaKind kind) => kind switch
    {
        SavedMediaKind.Image => Texts.T("import.kind.image"),
        SavedMediaKind.Video => Texts.T("import.kind.video"),
        SavedMediaKind.Other => Texts.T("import.kind.other"),
        _ => Texts.T("import.kind.unknown")
    };

    internal static string Size(long bytes) => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", Texts.Culture) + " GB"
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.0", Texts.Culture) + " MB"
        : bytes >= 1L << 10 ? (bytes / (double)(1L << 10)).ToString("0", Texts.Culture) + " KB"
        : bytes.ToString(Texts.Culture) + " B";
}

internal static class ManualImportPlanText
{
    internal static int NewCountOrLinked(this ManualImportPlan plan) => plan.Items.Count(item => item.Importable && item.Sha256 != null);
}
