using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace MeowChan.Desktop.Stats;

/// <summary>One table row of the statistics page. Updated in place so the grid keeps its rows, selection and scroll position.</summary>
internal sealed class StatisticsRow : INotifyPropertyChanged
{
    private string _name = string.Empty, _now = string.Empty, _session = string.Empty, _today = string.Empty, _retained = string.Empty,
        _diskWritten = string.Empty, _diskRead = string.Empty, _requests = string.Empty, _errors = string.Empty;

    internal StatisticsRow(IoStatsRowText text) { Key = text.Key; IsTotal = text.IsTotal; Update(text); }

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

    /// <summary>Takes the new texts; only properties that changed raise an event.</summary>
    internal void Update(IoStatsRowText text)
    {
        Name = text.Name; Now = text.Now; Session = text.Session; Today = text.Today; Retained = text.Retained;
        DiskWritten = text.DiskWritten; DiskRead = text.DiskRead; Requests = text.Requests; Errors = text.Errors;
    }

    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (string.Equals(field, value, System.StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
