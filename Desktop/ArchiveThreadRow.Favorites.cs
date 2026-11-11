using System;
using System.ComponentModel;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed partial record ArchiveThreadRow : INotifyPropertyChanged
{
    private string? _threadFavoriteLanguage;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool? ThreadFavorite { get; private set; }
    public bool CanToggleThreadFavorite { get; private set; }
    public string? ThreadFavoriteError { get; private set; }
    public Func<Task>? ToggleThreadFavoriteAsync { get; set; }
    public string ThreadFavoriteLabel => ThreadFavoriteText.Action(ThreadFavorite == true);

    internal void SetThreadFavorite(bool? marked, bool available, string? error)
    {
        if (ThreadFavorite == marked && CanToggleThreadFavorite == available
            && ThreadFavoriteError == error && _threadFavoriteLanguage == Texts.Language) return;
        ThreadFavorite = marked;
        CanToggleThreadFavorite = available;
        ThreadFavoriteError = error;
        _threadFavoriteLanguage = Texts.Language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThreadFavorite)));
    }
}

internal static class ThreadFavoriteText
{
    internal static string Action(bool marked) => Texts.T(marked ? "archive.threadFavorite.remove" : "archive.threadFavorite.add");
    internal static string Hint(bool marked) => Action(marked) + " · " + Texts.T("archive.threadFavorite.hint");
    internal static string LoadError(string details) => Texts.T("archive.threadFavorite.loadError", details);
    internal static string SaveError(ResourceId id, string details) =>
        Texts.T("archive.threadFavorite.saveError", id.Source, id.Board, id.Thread, details);
}
