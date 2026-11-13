using System;
using System.IO;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>
/// The sentence shown when a private list (favorites, ratings, bookmarks, saved searches, reader groups, post filters) is full. It names
/// the setting and the page it is on; the stores themselves only know English messages.
/// </summary>
internal static class StoreLimitText
{
    /// <summary>The localized sentence for a refusal of a private list, or null when <paramref name="error"/> is something else.</summary>
    internal static string? Describe(Exception error)
    {
        if (!PrivateStoreLimit.TryDescribe(error, out PrivateStoreLimitKind kind, out long limit)) return null;
        string page = Texts.T("settings.storageLimits");
        return kind switch
        {
            PrivateStoreLimitKind.FavoriteEntries => Texts.Count("limit.favoriteEntries", limit, limit, Texts.T("budgets.favoriteEntries"), page),
            PrivateStoreLimitKind.FavoriteFile => Texts.T("limit.favoriteFile", SizeText.Format(limit, Texts.Culture), Texts.T("budgets.favoriteFile"), page),
            PrivateStoreLimitKind.Bookmarks => Texts.Count("limit.postBookmarks", limit, limit, Texts.T("budgets.postBookmarks"), page),
            PrivateStoreLimitKind.SavedSearches => Texts.Count("limit.savedSearches", limit, limit, Texts.T("budgets.savedSearches"), page),
            PrivateStoreLimitKind.ReaderGroups => Texts.Count("limit.readerGroups", limit, limit, Texts.T("budgets.readerGroups"), page),
            PrivateStoreLimitKind.PostFilterRules => Texts.Count("limit.postFilterRules", limit, limit, Texts.T("budgets.postFilterRules"), page),
            PrivateStoreLimitKind.HiddenPosts => Texts.Count("limit.hiddenPosts", limit, limit, Texts.T("budgets.hiddenPosts"), page),
            _ => error.Message
        };
    }

    /// <summary>The refusal with a localized message (the store's exception stays inner), or the error itself when it is no limit refusal.</summary>
    internal static Exception Localize(Exception error) => Describe(error) is { } text ? new InvalidDataException(text, error) : error;
}
