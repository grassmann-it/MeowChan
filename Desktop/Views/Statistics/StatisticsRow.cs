using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Stats;

/// <summary>One table row of the statistics page. Updated in place so the grid keeps its rows, selection and scroll position.</summary>
internal sealed class StatisticsRow : INotifyPropertyChanged
{
    private string _name = string.Empty, _now = string.Empty, _session = string.Empty, _today = string.Empty, _retained = string.Empty,
        _diskWritten = string.Empty, _diskRead = string.Empty, _requests = string.Empty, _errors = string.Empty, _details = string.Empty;

    private long _errorCount;
    private string _errorsLinkText = string.Empty;

    internal StatisticsRow(IoStatsRowText text, int retentionDays = Config.StatsRetentionDaysDefault) { Key = text.Key; IsTotal = text.IsTotal; Update(text, retentionDays); }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal string Key { get; }
    internal bool IsTotal { get; }
    public FontWeight Weight => IsTotal ? FontWeight.SemiBold : FontWeight.Normal;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Now { get => _now; private set => Set(ref _now, value); }
    public string Session { get => _session; private set => Set(ref _session, value); }
    public string Today { get => _today; private set => Set(ref _today, value); }
    public string Retained { get => _retained; private set => Set(ref _retained, value); }
    public string DiskWritten { get => _diskWritten; private set => Set(ref _diskWritten, value); }
    public string DiskRead { get => _diskRead; private set => Set(ref _diskRead, value); }
    public string Requests { get => _requests; private set => Set(ref _requests, value); }
    public string Errors { get => _errors; private set => Set(ref _errors, value); }

    public string Details { get => _details; private set => Set(ref _details, value); }

    /// <summary>Whether the error count leads to the problems of this source in the diagnostics: the count is above zero and
    /// the row is a source the diagnostics know by id, or the total (all sources). The collective rows (other, local files,
    /// unknown) are no source id and have nowhere to go.</summary>
    public bool CanOpenDiagnostics => _errorCount > 0 && (IsTotal || IsSourceId(Key));

    /// <summary>The name and tooltip of the error link: where it leads.</summary>
    public string ErrorsLinkText { get => _errorsLinkText; private set => Set(ref _errorsLinkText, value); }

    /// <summary>The source id the diagnostics filter is set to; null for the total (all sources).</summary>
    internal string? DiagnosticsSourceId => IsTotal ? null : Key;

    internal static bool IsSourceId(string key) =>
        key.Length > 0 && key is not (IoStatsKeys.Other or IoStatsKeys.Unknown or MeowChan.Http.TransferMeter.LocalKey);

    /// <summary>Takes the new texts; only properties that changed raise an event.</summary>
    internal void Update(IoStatsRowText text, int retentionDays = Config.StatsRetentionDaysDefault)
    {
        bool couldOpen = CanOpenDiagnostics;
        _errorCount = text.ErrorCount;
        Name = text.Name; Now = text.Now; Session = text.Session; Today = text.Today; Retained = text.Retained;
        DiskWritten = text.DiskWritten; DiskRead = text.DiskRead; Requests = text.Requests; Errors = text.Errors;
        ErrorsLinkText = IsTotal ? Texts.T("stats.errors.openAll") : Texts.T("stats.errors.open", text.Name);
        if (couldOpen != CanOpenDiagnostics) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenDiagnostics)));
        Details = string.Join("\n",
            Texts.T("stats.col.today") + ": " + Today,
            Texts.Count("stats.col.retained", retentionDays) + ": " + Retained,
            Texts.T("stats.col.diskWritten") + ": " + DiskWritten,
            Texts.T("stats.col.diskRead") + ": " + DiskRead,
            Texts.T("stats.col.requests") + ": " + Requests);
    }

    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (string.Equals(field, value, System.StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
