using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Ratings;

namespace MeowChan.DesktopSmoke;

// Root hook: await RatingLanguageRefreshChecks.RunAsync(owner) on the native UI thread.
// This is real Avalonia window/control coverage with managed routed input. It does not
// claim OS keyboard, native accessibility, translation quality, profile, media or network coverage.
internal static class RatingLanguageRefreshChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly string[] Languages = ["en", "de", "es", "fr", "ru"];

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string originalLanguage = Texts.Language;
        try
        {
            foreach (string language in Languages)
                Require(Texts.Languages.Any(item => item.Code == language), "Bundled rating language is unavailable: " + language);

            await DialogAsync(owner);
            await ManagerAsync(owner);
            Console.WriteLine("SMOKE rating language refresh: MediaRatingDialog and MediaRatingsManagerDialog retained synthetic draft/selection/focus, busy state and provider-call count across EN/DE/ES/FR/RU; managed routed input only; no profile/media/network.");
        }
        finally
        {
            Texts.SetLanguage(originalLanguage);
            await SettleAsync();
        }
    }

    private static async Task DialogAsync(MainWindow owner)
    {
        FavoriteAssetKey key = SyntheticKey("dialog");
        Texts.SetLanguage("en");
        int calls = 0;
        int? requested = null;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaRatingDialog dialog = new(key, 2, value =>
        {
            calls++;
            requested = value;
            return release.Task;
        });
        Task result = dialog.ShowDialog(owner);
        Exception? failureBeforeCleanup = null;
        try
        {
            await WaitUntilAsync(() => dialog.IsVisible);
            Ursa.Controls.Rating rating = Need<Ursa.Controls.Rating>(dialog, "MediaRatingEditor");
            TextBlock valueText = Need<TextBlock>(dialog, "MediaRatingValue");
            TextBlock help = Private<TextBlock>(dialog, "_help");
            Require(rating.Focus(), "Rating editor did not accept focus.");
            RaiseKey(rating, Key.Right);
            await SettleAsync(dialog);
            Require(rating.Value == 3 && valueText.Text == MediaRatingText.KeyboardValue(3),
                "Keyboard draft was not materialized before the pending save.");
            Control focused = Focused(dialog);
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                await SettleAsync(dialog);
                CheckDialogLabels(dialog, rating, 2, MediaRatingText.KeyboardValue(3));
                Require(calls == 0 && rating.Value == 3 && !IsBusy(dialog)
                    && ReferenceEquals(Focused(dialog), focused),
                    language + ": uncommitted keyboard draft/focus changed or a save started.");
            }
            RaiseKey(rating, Key.Enter);
            await WaitUntilAsync(() => calls == 1);
            Require(requested == 3 && !release.Task.IsCompleted && IsBusy(dialog),
                "Exactly one controlled save call must remain pending and mark the dialog busy.");
            // Disabling the editor for Save may intentionally move focus. Snapshot
            // after that transition; language changes must not move it again.
            object? pendingFocus = dialog.FocusManager?.GetFocusedElement();
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                await SettleAsync(dialog);
                CheckDialogLabels(dialog, rating, 2, MediaRatingText.KeyboardValue(3));
                Require(calls == 1 && IsBusy(dialog) && rating.Value == 3
                    && valueText.Text == MediaRatingText.KeyboardValue(3)
                    && ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), pendingFocus),
                    "Dialog language refresh changed pending draft, busy state, focus or call count in " + language + ".");
                Require(valueText.Text == MediaRatingText.KeyboardValue(3),
                    "Dialog value did not use the current catalog in " + language + ".");
                Require(help.Text == Texts.T("media.rating.help"),
                    "Dialog help was stale during pending refresh in " + language + ".");
            }
            const string raw = "Provider literal: media.rating.error.save {0} <raw> ü 日本\nunaltered payload";
            release.SetException(new InvalidOperationException(raw));
            await WaitUntilAsync(() => !IsBusy(dialog) && rating.IsEffectivelyEnabled);
            Require(rating.Focus(), "Failed-save editor did not accept focus.");
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                await SettleAsync(dialog);
                CheckDialogLabels(dialog, rating, 2, MediaRatingText.LocalizedValue(2));
                string expected = MediaRatingText.FormatError(Texts.T("media.rating.error.save"), raw);
                Require(Need<TextBlock>(dialog, "MediaRatingError").Text == expected
                    && Private<string>(dialog, "_errorDetails") == raw
                    && Need<Button>(dialog, "MediaRatingRetry").IsVisible,
                    language + ": error wrapper is stale or raw provider payload changed.");
                Require(calls == 1 && requested == 3 && rating.Value == 2 && !IsBusy(dialog)
                    && ReferenceEquals(Focused(dialog), rating),
                    language + ": failed-save refresh committed/retried or altered restored value/focus.");
            }
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                release.TrySetResult();
                await WaitUntilAsync(() => !IsBusy(dialog));
                if (dialog.IsVisible) dialog.Close();
                await result.WaitAsync(Deadline);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in RatingLanguageRefreshChecks: " + exception));
        }
    }

    private static async Task ManagerAsync(MainWindow owner)
    {
        Texts.SetLanguage("en");
        MediaRatingEntry[] entries =
        [
            new(SyntheticKey("manager-1"), 4),
            new(SyntheticKey("manager-2"), 1)
        ];
        DesktopSessionHost host = SyntheticHost(entries);
        MediaRatingsManagerDialog manager = new(host);
        Task result = manager.ShowDialog(owner);
        try
        {
            await WaitUntilAsync(() => manager.IsVisible);
            ListBox list = Need<ListBox>(manager, "MediaRatingsList");
            TextBox search = Need<TextBox>(manager, "MediaRatingsSearch");
            await WaitUntilAsync(() => list.ItemCount == entries.Length);
            object selected = list.Items.Cast<object>().First();
            list.SelectedItem = selected;
            Require(search.Focus(), "Ratings manager search did not accept focus.");
            search.CaretIndex = 0;
            search.SelectionStart = search.SelectionEnd = 0;
            Control focused = Focused(manager);
            string searchText = search.Text ?? string.Empty;
            await SettleAsync(manager);
            MediaRatingEntry selectedEntry = (MediaRatingEntry)selected.GetType().GetProperty("Entry")!.GetValue(selected)!;
            int count = list.ItemCount;
            object[] originalRows = list.Items.Cast<object>().ToArray();
            // A post-open read tripwire: a refresh which incorrectly reloads from
            // this inert host fails GetMediaRatings instead of touching any store.
            SetPrivate(host, "_disposed", true);
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                await SettleAsync(manager);
                Require(list.ItemCount == count && ReferenceEquals(list.SelectedItem, selected)
                    && ReferenceEquals(Focused(manager), focused) && (search.Text ?? string.Empty) == searchText
                    && !IsBusy(manager),
                    "Manager language refresh changed selection, focus, search or busy state in " + language + ".");
                CheckManagerLabels(manager, entries.Length);
                Require(list.Items.Cast<object>().Zip(originalRows).All(pair => ReferenceEquals(pair.First, pair.Second))
                    && string.IsNullOrEmpty(Private<TextBlock>(manager, "_error").Text),
                    language + ": manager reloaded/refiltered rows or tried to read the host again.");
                foreach (object row in originalRows)
                {
                    MediaRatingEntry entry = (MediaRatingEntry)row.GetType().GetProperty("Entry")!.GetValue(row)!;
                    Require(MaterializedValue(list, row) == MediaRatingText.LocalizedValue(entry.Value),
                        "Manager materialized row value is stale in " + language + ".");
                    Require(list.GetVisualDescendants().OfType<StackPanel>()
                        .Any(panel => AutomationProperties.GetName(panel) == MediaRatingText.Row(entry)),
                        "Manager materialized row accessibility name is stale in " + language + ".");
                }
                Require(Need<TextBox>(manager, "MediaRatingsIdentity").Text == MediaRatingText.Row(selectedEntry),
                    language + ": selected identity/row text is stale.");
            }
            // Actual confirmation state, but deliberately never ConfirmAsync: this
            // manager API has no synthetic commit callback and must not write a host.
            SmokeActionClick.Execute(Private<Button>(manager, "_remove"));
            await SettleAsync(manager);
            object? frozen = Field(manager, "_frozen").GetValue(manager);
            Require(Equals(frozen, selectedEntry), "Manager remove did not freeze the synthetic selected entry.");
            // BeginRemove captures the current focus without focusing the command
            // button. EndConfirmation restores this origin when still reachable;
            // only its fallback targets _remove and then _search, never _list.
            Require(ReferenceEquals(Field(manager, "_confirmationOrigin").GetValue(manager), search)
                && ReferenceEquals(focused, search),
                "Confirmation did not capture the deliberately focused search as its return target.");
            Control confirmationFocus = Focused(manager);
            Require(ReferenceEquals(confirmationFocus, Private<Button>(manager, "_cancel")),
                "Confirmation did not focus its Cancel action.");
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                await SettleAsync(manager);
                CheckManagerLabels(manager, entries.Length);
                Require(Equals(Field(manager, "_frozen").GetValue(manager), frozen)
                    && ReferenceEquals(list.SelectedItem, selected)
                    && ReferenceEquals(Focused(manager), confirmationFocus)
                    && ReferenceEquals(Field(manager, "_confirmationOrigin").GetValue(manager), search)
                    && !list.IsEnabled && !search.IsEnabled && !IsBusy(manager),
                    language + ": refresh lost confirmation, selection, focus or enabled blocked input.");
                Require(Need<TextBox>(manager, "MediaRatingsIdentity").Text
                    == Texts.T("media.rating.confirmRemove") + "\n" + MediaRatingText.Row(selectedEntry),
                    language + ": frozen confirmation identity is stale.");
            }
            SmokeActionClick.Execute(Private<Button>(manager, "_cancel"));
            await SettleAsync(manager);
            Require(Field(manager, "_frozen").GetValue(manager) == null
                && Field(manager, "_confirmationOrigin").GetValue(manager) == null
                && ReferenceEquals(list.SelectedItem, selected)
                && list.IsEnabled && search.IsEffectivelyEnabled
                && ReferenceEquals(Focused(manager), search),
                "Cancel did not clear confirmation, retain selection and restore captured search focus.");
        }
        finally
        {
            if (manager.IsVisible) manager.Close();
            await result.WaitAsync(Deadline);
        }
    }

    private static void CheckDialogLabels(MediaRatingDialog dialog, Ursa.Controls.Rating rating,
        int confirmed, string currentValue)
    {
        Require(dialog.Title == MediaRatingText.LocalizedLabel
            && Private<TextBlock>(dialog, "_help").Text == Texts.T("media.rating.help")
            && Need<TextBlock>(dialog, "MediaRatingConfirmed").Text == MediaRatingText.LocalizedSaved(confirmed)
            && Need<TextBlock>(dialog, "MediaRatingValue").Text == currentValue
            && AutomationProperties.GetName(rating) == MediaRatingText.AccessibleValue(currentValue),
            Texts.Language + ": dialog visible/accessibility value or help is stale.");
        string keyboardHelp = Texts.T("media.rating.keyboardHelp");
        Require((AutomationProperties.GetHelpText(rating) ?? "").Contains(keyboardHelp, StringComparison.Ordinal)
            && (ToolTip.GetTip(rating)?.ToString() ?? "").Contains(keyboardHelp, StringComparison.Ordinal),
            Texts.Language + ": keyboard help is stale.");
    }

    private static void CheckManagerLabels(MediaRatingsManagerDialog manager, int count)
    {
        Require(manager.Title == MeowChan.Desktop.Commands.CommandCatalog.Label("media.rating.manage")
            && Need<TextBox>(manager, "MediaRatingsSearch").PlaceholderText == Texts.T("media.rating.search")
            && AutomationProperties.GetName(Need<TextBox>(manager, "MediaRatingsSearch")) == Texts.T("media.rating.search")
            && Need<TextBlock>(manager, "MediaRatingsSummary").Text == Texts.T("media.rating.summary", count, count)
            && Equals(Private<Button>(manager, "_confirm").Content, Texts.T("media.rating.removeThis"))
            && Equals(Private<Button>(manager, "_cancel").Content, Texts.T("media.rating.cancel"))
            && Equals(Private<Button>(manager, "_close").Content, Texts.T("media.rating.close")),
            Texts.Language + ": manager summary/search/action presentation is stale.");
    }

    // Only GetMediaRatings runs against this inert synthetic host. Its method reads
    // _disposed and _ratingEntries; no constructor, lease, filesystem or real session
    // is used. Never Dispose or call any mutator on this uninitialized test object.
    private static DesktopSessionHost SyntheticHost(IReadOnlyList<MediaRatingEntry> entries)
    {
        DesktopSessionHost host = (DesktopSessionHost)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSessionHost));
        Field(host, "_ratingEntries").SetValue(host, entries);
        Field(host, "_disposed").SetValue(host, false);
        return host;
    }

    private static FavoriteAssetKey SyntheticKey(string suffix) =>
        new(new("synthetic", "board", suffix), "media-" + suffix, "original", new string('a', 64));

    private static string MaterializedValue(ListBox list, object row)
    {
        ListBoxItem panel = list.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, row) || ReferenceEquals(item.Content, row))
            ?? throw new InvalidOperationException("Synthetic rating row was not materialized.");
        return panel.GetVisualDescendants().OfType<TextBlock>().First().Text ?? string.Empty;
    }

    private static void RaiseKey(Control target, Key key) => target.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None
    });

    private static Control Focused(Window window) => window.FocusManager?.GetFocusedElement() as Control
        ?? throw new InvalidOperationException("Rating window lost its focused control.");

    private static bool IsBusy(object dialog) => Private<bool>(dialog, "_busy");
    private static T Private<T>(object owner, string name) => (T)Field(owner, name).GetValue(owner)!;
    private static void SetPrivate(object owner, string name, object value) => Field(owner, name).SetValue(owner, value);
    private static FieldInfo Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(owner.GetType().Name + " field missing: " + name);

    private static T Need<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Missing materialized rating control: " + name);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Rating language refresh fixture timed out.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }
    }

    private static async Task SettleAsync(Window? window = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
