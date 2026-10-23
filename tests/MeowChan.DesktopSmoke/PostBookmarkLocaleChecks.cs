using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>Focused status-language regression; does not repeat the full bookmark comparison.</summary>
internal static class PostBookmarkLocaleChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-bookmark-locale-" + Guid.NewGuid().ToString("N"));
        string language = Texts.Language, mode = owner.SessionHost.Config.WorkspaceMode;
        string configLanguage = owner.SessionHost.Config.Language, configTheme = owner.SessionHost.Config.Theme;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = owner.Width, height = owner.Height;
        PostBookmarksDialog? dialog = null;
        DesktopSessionHost? host = null;
        var releaseOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool openEntered = false;
        var key = new PostBookmarkKey(new ResourceId("4chan", "g", "7200"), "7201");
        const string raw = "Reason A: investigate wording.\n\nReason B: keep for another project. 🙂";
        const string sourceTitle = "Original title bleibt unverändert 🙂";
        Directory.CreateDirectory(profile);
        try
        {
            new Config { DownloadsPaused = true, SaveUrlsOnExit = false }.SaveConfig(profile);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(key.Thread, "https://offline.invalid/g/thread/7200",
                    sourceTitle, ThreadSourceState.Archived, DateTimeOffset.UtcNow, Path.Combine(profile, "media"),
                    [new CapturedPost(key.Post, 0, PostFieldPresence.Html, Html: "<p>Locale fixture.</p>")]));
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
            await host.StartAsync();
            await host.SavePostBookmarkAsync(key, raw, 0, false);
            byte[] stored = await File.ReadAllBytesAsync(Path.Combine(profile, "post-bookmarks.json"));
            ComboBox modes = owner.FindControl<ComboBox>("WorkspaceModeComboBox")!;
            modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == "Barebones");
            await Program.WaitUntilAsync(() => owner.SessionHost.Config.WorkspaceMode == "Barebones");
            await SetLanguageAsync(owner, "en");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            owner.Width = 640; owner.Height = 480;
            dialog = new PostBookmarksDialog(host, async (_, _) => { openEntered = true; return await releaseOpen.Task; });
            dialog.Width = 640; dialog.Height = 480; dialog.Show(owner);
            await dialog.InitializeAsync();
            TextBox note = dialog.FindControl<TextBox>("Note")!;
            TextBlock status = dialog.FindControl<TextBlock>("Status")!;
            ListBox list = dialog.FindControl<ListBox>("Bookmarks")!;
            Button save = dialog.FindControl<Button>("Save")!, open = dialog.FindControl<Button>("OpenTarget")!;
            await Program.WaitUntilAsync(() => open.IsEnabled && status.Text == Texts.T("bookmarks.available", sourceTitle)
                && list.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("bookmarks.available", sourceTitle)));
            await SetLanguageAsync(owner, "de");
            Require(status.Text == Texts.T("bookmarks.available", sourceTitle) && note.Text == raw
                && list.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("bookmarks.available", sourceTitle)),
                "Available status did not relocalize while preserving source title and note.");

            await SetLanguageAsync(owner, "en");
            string invalid = raw + string.Concat(Enumerable.Repeat("🙂", 8001 - raw.EnumerateRunes().Count()));
            await EditAsync(dialog, invalid);
            Require(status.Text == Texts.T("bookmarks.invalidNote") && !save.Command!.CanExecute(null), "Invalid draft did not enter the real validation state.");
            foreach (string next in new[] { "de", "en", "de" })
            {
                await SetLanguageAsync(owner, next);
                Require(status.Text == Texts.T("bookmarks.invalidNote") && note.Text == invalid
                    && !save.IsEnabled && !save.Command!.CanExecute(null) && open.IsEnabled,
                    "Language change lost the invalid draft, validation status or command guards.");
            }
            await CaptureAsync(dialog, "smoke-bookmark-locale-invalid-de-light-barebones-minimum.png");
            await EditAsync(dialog, raw);
            await SetLanguageAsync(owner, "en");
            open.Command!.Execute(null);
            await Program.WaitUntilAsync(() => openEntered && !dialog.IsVisible);
            await SetLanguageAsync(owner, "de");
            releaseOpen.SetResult(false); // Actual asynchronous Open callback completes after the language changed.
            await Program.WaitUntilAsync(() => dialog.IsVisible && status.Text == Texts.T("bookmarks.missing") && open.IsEnabled);
            Require(note.Text == raw, "Delayed Open failure changed the raw note.");

            ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([key.Thread]);
            await host.DeleteArchivePermanentlyAsync(plan);
            dialog.InvalidateAvailability(); // Explicit actual target change, never a side effect of switching language.
            await Program.WaitUntilAsync(() => !open.IsEnabled && status.Text == Texts.T("bookmarks.missing")
                && list.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("bookmarks.missing")));
            foreach (string next in new[] { "en", "de", "en", "de" })
            {
                await SetLanguageAsync(owner, next);
                Require(status.Text == Texts.T("bookmarks.missing") && note.Text == raw
                    && list.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("bookmarks.missing"))
                    && !open.IsEnabled && !open.Command!.CanExecute(null) && save.IsEnabled && save.Command!.CanExecute(null),
                    "Language change lost the missing-target status, raw note or command guards.");
            }
            await CaptureAsync(dialog, "smoke-bookmark-locale-missing-de-light-barebones-minimum.png");
            byte[] after = await File.ReadAllBytesAsync(Path.Combine(profile, "post-bookmarks.json"));
            Require(stored.SequenceEqual(after), "Status language changes modified stored bookmark bytes.");
            Console.WriteLine("SMOKE bookmark locale: available title args, 8001 raw draft/disabled Save, delayed Open failure and exact missing target relocalized EN↔DE; bookmark bytes and command guards preserved.");
        }
        finally
        {
            releaseOpen.TrySetResult(false); dialog?.CloseForProfileEnd();
            if (host != null) await host.DisposeAsync();
            await owner.SessionHost.SaveViewPreferencesAsync(config => { config.Language = configLanguage; config.Theme = configTheme; });
            ComboBox modes = owner.FindControl<ComboBox>("WorkspaceModeComboBox")!;
            modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == mode);
            await Program.WaitUntilAsync(() => owner.SessionHost.Config.WorkspaceMode == mode);
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            owner.Width = width; owner.Height = height;
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }
    }

    private static async Task SetLanguageAsync(MainWindow owner, string language)
    {
        await owner.SessionHost.SaveViewPreferencesAsync(config => { config.Language = language; config.Theme = "Light"; });
        Texts.SetLanguage(language);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        Require(Texts.Language == language, "Fixture language request was reset.");
    }
    private static async Task EditAsync(PostBookmarksDialog dialog, string text)
    {
        TextBox note = dialog.FindControl<TextBox>("Note")!;
        bool changed = false;
        void Changed(object? sender, TextChangedEventArgs args) { if (note.Text == text) changed = true; }
        note.TextChanged += Changed;
        try { note.Text = text; await Program.WaitUntilAsync(() => changed); }
        finally { note.TextChanged -= Changed; }
    }
    private static async Task CaptureAsync(PostBookmarksDialog dialog, string file)
    {
        dialog.UpdateLayout(); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); dialog.UpdateLayout();
        Require(dialog.ActualThemeVariant == ThemeVariant.Light && Texts.Language == "de"
            && PresentationChecks.MatchesRequestedSize(dialog, 640, 480), "Targeted DE/Light minimum scene mismatch.");
        Program.CaptureWindow(dialog, file);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
