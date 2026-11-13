using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Ratings;

namespace MeowChan.DesktopSmoke;

// Ratings group: single-asset rating dialog and the ratings manager. Save/remove/load
// failures appear in the local error strip; the action footer stays fixed.
internal static partial class DialogFrameChecks
{
    private static readonly TimeSpan RatingDeadline = TimeSpan.FromSeconds(20);

    private static async partial Task RunRatingsAsync(MainWindow owner)
    {
        string language = Texts.Language;
        try
        {
            // The error state at the minimum size is the tightest layout (strip + wrapped footer); German and the other
            // longest packs must keep the footer inside the window as well.
            foreach (string frameLanguage in new[] { "en", "de", "ru", "fr" })
            {
                if (!DialogGallery.Active || frameLanguage == "en") Texts.SetLanguage(frameLanguage);
                await RatingDialogFrameAsync(owner);
            }
            if (!DialogGallery.Active) Texts.SetLanguage("en");
            await RatingsManagerFrameAsync(owner);
            await RatingsManagerLoadErrorFrameAsync(owner);
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task RatingDialogFrameAsync(MainWindow owner)
    {
        const string raw = "Synthetic provider failure with a deliberately long detail so the local strip has to wrap "
            + "over several lines at the minimum dialog width without pushing the action footer out of the window.";
        int calls = 0;
        MediaRatingDialog dialog = new(RatingFrameKey("dialog"), 2, _ =>
        {
            calls++;
            return Task.FromException(new InvalidOperationException(raw));
        });
        Task shown = dialog.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => dialog.IsVisible);
            await Settle();
            await VerifyFrameAsync(dialog, "ratings/dialog normal", "ratings-dialog-normal");
            Ursa.Controls.Rating rating = Part<Ursa.Controls.Rating>(dialog, "MediaRatingEditor", "rating dialog");
            Require(rating.Focus(), "rating dialog: editor did not accept focus.");
            RatingKey(rating, Key.Right);
            RatingKey(rating, Key.Enter);
            await Program.WaitUntilAsync(() => calls == 1 && Part<Button>(dialog, "MediaRatingRetry", "rating dialog").IsVisible);
            string expected = MediaRatingText.FormatError(Texts.T("media.rating.error.save"), raw);
            await VerifyErrorStripAsync(dialog, "ratings/dialog error normal", expected);
            Capture(dialog, "ratings-dialog-error");
            await RatingResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
            await VerifyErrorStripAsync(dialog, "ratings/dialog error minimum", expected);
            await VerifyFrameAsync(dialog, "ratings/dialog error minimum footer", "ratings-dialog-minimum");
            Require(Part<Button>(dialog, "MediaRatingClose", "rating dialog").IsEffectivelyEnabled && calls == 1,
                "rating dialog: layout changed busy state or retried.");
            RatingKey(dialog, Key.Escape);
            await shown.WaitAsync(RatingDeadline);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(RatingDeadline);
        }
    }

    private static async Task RatingsManagerFrameAsync(MainWindow owner)
    {
        MediaRatingEntry[] entries = Enumerable.Range(0, 40).Select(index => new MediaRatingEntry(RatingFrameKey("m" + index), 1 + index % 5)).ToArray();
        MediaRatingsManagerDialog manager = new(RatingFrameHost(entries, disposed: false));
        Task shown = manager.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => manager.IsVisible);
            ListBox list = Part<ListBox>(manager, "MediaRatingsList", "ratings manager");
            await Program.WaitUntilAsync(() => list.ItemCount == entries.Length);
            list.SelectedIndex = 0;
            await Settle();
            Require(Find<Control>(manager, DialogFrame.ErrorStripName) is { IsVisible: false },
                "ratings manager: error strip visible without an error.");
            await VerifyFrameAsync(manager, "ratings/manager normal", "ratings-manager-normal");
            await RatingResizeAsync(manager, manager.MinWidth, manager.MinHeight);
            await VerifyFrameAsync(manager, "ratings/manager minimum", "ratings-manager-minimum");
            Require(Part<TextBox>(manager, "MediaRatingsIdentity", "ratings manager").IsEffectivelyVisible
                && Part<Button>(manager, "MediaRatingsRemove", "ratings manager").IsEffectivelyVisible,
                "ratings manager: identity or remove action hidden at minimum size.");
            RatingKey(manager, Key.Escape);
            await shown.WaitAsync(RatingDeadline);
        }
        finally
        {
            if (manager.IsVisible) manager.Close();
            await shown.WaitAsync(RatingDeadline);
        }
    }

    private static async Task RatingsManagerLoadErrorFrameAsync(MainWindow owner)
    {
        // The inert host is marked disposed, so the open-time read fails without touching any store.
        MediaRatingsManagerDialog manager = new(RatingFrameHost(Array.Empty<MediaRatingEntry>(), disposed: true));
        Task shown = manager.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => manager.IsVisible
                && !string.IsNullOrEmpty(Part<TextBlock>(manager, "MediaRatingsError", "ratings manager error").Text));
            string label = Texts.T("media.rating.error.load");
            await VerifyErrorStripAsync(manager, "ratings/manager load error normal", label);
            Capture(manager, "ratings-manager-error");
            await RatingResizeAsync(manager, manager.MinWidth, manager.MinHeight);
            await VerifyErrorStripAsync(manager, "ratings/manager load error minimum", label);
            RatingKey(manager, Key.Escape);
            await shown.WaitAsync(RatingDeadline);
        }
        finally
        {
            if (manager.IsVisible) manager.Close();
            await shown.WaitAsync(RatingDeadline);
        }
    }

    // Same inert synthetic host as RatingLanguageRefreshChecks: only GetMediaRatings runs.
    private static DesktopSessionHost RatingFrameHost(IReadOnlyList<MediaRatingEntry> entries, bool disposed)
    {
        DesktopSessionHost host = (DesktopSessionHost)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSessionHost));
        RatingFrameField(host, "_ratingEntries").SetValue(host, entries);
        RatingFrameField(host, "_disposed").SetValue(host, disposed);
        return host;
    }

    private static FieldInfo RatingFrameField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(owner.GetType().Name + " field missing: " + name);

    private static FavoriteAssetKey RatingFrameKey(string suffix) =>
        new(new("synthetic", "board", suffix), "media-" + suffix, "original", new string('a', 64));

    private static void RatingKey(Control target, Key key) => target.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None
    });

    private static async Task RatingResizeAsync(Window dialog, double width, double height)
    {
        dialog.Width = width; dialog.Height = height;
        await Program.WaitUntilAsync(() => Math.Abs(dialog.ClientSize.Width - width) <= 1 && Math.Abs(dialog.ClientSize.Height - height) <= 1);
        await Settle();
    }
}
