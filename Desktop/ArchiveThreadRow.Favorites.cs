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
    internal static string Get(string english, string german) => Texts.Language == "de" ? german : english;
    internal static string Action(bool marked) => marked
        ? Get("Remove thread favorite", "Threadfavorit entfernen")
        : Get("Favorite thread", "Thread als Favorit markieren");
    internal static string Hint(bool marked) => Action(marked) + " · " + Get(
        "Local thread mark in this profile; independent of media favorites.",
        "Lokale Threadmarkierung in diesem Profil; unabhängig von Medienfavoriten.");
    internal static string LoadError(string details) => Get(
        "Thread favorites could not be loaded. Refresh the archive to retry.",
        "Threadfavoriten konnten nicht geladen werden. Zum Wiederholen das Archiv aktualisieren.") + "\nDetails: " + details;
    internal static string SaveError(ResourceId id, string details) => Get(
        "Could not save the thread favorite", "Threadfavorit konnte nicht gespeichert werden")
        + " (" + id.Source + " /" + id.Board + "/ " + id.Thread + ").\nDetails: " + details;
}
