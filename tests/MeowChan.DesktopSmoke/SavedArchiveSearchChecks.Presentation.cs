using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static partial class SavedArchiveSearchChecks
{
    // Representative live product dialogs only; save/reopen semantics belong to the existing pair.
    private static async Task PresentationAsync(MainWindow owner, ArchivePage page, string profile, Evidence log)
    {
        byte[] original = File.ReadAllBytes(SearchFile(profile));
        Config config = owner.SessionHost.Config;
        string language = config.Language, theme = config.Theme, mode = config.WorkspaceMode;
        ThemeVariant? requestedTheme = Application.Current!.RequestedThemeVariant;
        double width = owner.Width, height = owner.Height;
        WindowState windowState = owner.WindowState;
        const string rawDraft = "PRESENTATION draft B 🐾";
        await SetupAppliedAsync(page, Board, log);
        Edit(page.FindControl<TextBox>("ArchiveSearchText")!, rawDraft, log, "All presentation changes preserve this unsearched draft and stored A");
        string[] initialIds = Rows(page);
        int cells = 0;
        try
        {
            owner.WindowState = WindowState.Normal;
            foreach (string nextLanguage in new[] { "en", "de" })
            foreach (string nextTheme in new[] { "Light", "Dark" })
            foreach (string nextMode in new[] { "Barebones", "Simple", "Advanced" })
            {
                double minimumWidth = nextMode == "Barebones" ? 640 : 760;
                double minimumHeight = nextMode == "Barebones" ? 480 : 500;
                log.Note("presentation fixture setup", "Switch actual owner mode, persisted language/theme, then minimum client size", new
                { nextLanguage, nextTheme, nextMode, minimumWidth, minimumHeight });
                await PresentationChecks.SwitchModeAsync(owner, nextMode);
                await owner.SessionHost.SaveViewPreferencesAsync(candidate => { candidate.Language = nextLanguage; candidate.Theme = nextTheme; });
                Texts.SetLanguage(nextLanguage);
                ThemeVariant expectedTheme = nextTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                Application.Current.RequestedThemeVariant = expectedTheme;
                owner.Width = minimumWidth; owner.Height = minimumHeight;
                await SettlePresentationAsync(owner);
                await Program.WaitUntilAsync(() => Texts.Language == nextLanguage && owner.ActualThemeVariant == expectedTheme
                    && Math.Abs(owner.ClientSize.Width - minimumWidth) <= 1 && Math.Abs(owner.ClientSize.Height - minimumHeight) <= 1);
                Require(owner.SessionHost.Config.WorkspaceMode == nextMode, "Actual owner workspace mode differs from presentation case.");

                var manager = await OpenAsync(owner, page, false, log);
                try
                {
                    Select(manager, Names[0], log);
                    await CaptureAsync(manager, "manager", Control<ListBox>(manager, "SavedSearchList"),
                        [Control<ListBox>(manager, "SavedSearchList"), Control<Button>(manager, "SavedSearchApply"), Control<Button>(manager, "SavedSearchClose")]);
                    await ClickAsync(Control<Button>(manager, "SavedSearchApply"), log, "Reveal dirty-draft confirmation only; no Apply commit in presentation stage");
                    Require(Control<TextBlock>(manager, "SavedSearchPrompt").Text == Texts.T("savedSearch.prompt.Apply", Names[0]), "Dirty Apply presentation prompt differs from actual locale.");
                    await CaptureAsync(manager, "dirty-apply", Control<Button>(manager, "SavedSearchConfirm"),
                        [Control<TextBlock>(manager, "SavedSearchPrompt"), Control<Button>(manager, "SavedSearchConfirm"), Control<Button>(manager, "SavedSearchCancelEdit")]);
                    await ClickAsync(Control<Button>(manager, "SavedSearchCancelEdit"), log, "Cancel leaves raw B, current IDs and stored A unchanged");
                    await CloseAsync(manager, owner, log);
                }
                finally { if (manager.IsVisible) manager.CloseForLifetime(); }

                var nameDialog = await OpenAsync(owner, page, true, log);
                try
                {
                    const string rawName = "Unstored name 🐾";
                    Edit(Control<TextBox>(nameDialog, "SavedSearchName"), rawName, log, "Only demonstrate raw name presentation; do not write a definition");
                    Require(Control<TextBlock>(nameDialog, "SavedSearchPrompt").Text == Texts.T("savedSearch.prompt.Create", ""), "Save-name prompt differs from actual locale.");
                    await CaptureAsync(nameDialog, "name", Control<TextBox>(nameDialog, "SavedSearchName"),
                        [Control<TextBlock>(nameDialog, "SavedSearchPrompt"), Control<TextBox>(nameDialog, "SavedSearchName"), Control<Button>(nameDialog, "SavedSearchConfirm"), Control<Button>(nameDialog, "SavedSearchCancelEdit")]);
                    Require(Control<TextBox>(nameDialog, "SavedSearchName").Text == rawName, "Presentation changed raw unsaved name.");
                    await ClickAsync(Control<Button>(nameDialog, "SavedSearchCancelEdit"), log, "Ask before discarding the demonstration name");
                    Require(Control<TextBox>(nameDialog, "SavedSearchName").Text == rawName
                        && Control<TextBlock>(nameDialog, "SavedSearchPrompt").Text == Texts.T("savedSearch.prompt.Discard", ""), "Discard presentation lost raw name or localized prompt.");
                    await ClickAsync(Control<Button>(nameDialog, "SavedSearchConfirm"), log, "Explicitly discard demonstration name without writing");
                    await CloseAsync(nameDialog, owner, log);
                }
                finally { if (nameDialog.IsVisible) nameDialog.CloseForLifetime(); }
                CheckUnchanged(); cells++;
                log.Note("presentation cell complete", "Three real scenes captured; no functional Save/Apply or restart repeated", new
                { language = nextLanguage, theme = nextTheme, mode = nextMode, cells, sourceDigest = RuntimeSourceDigest });

                async Task CaptureAsync(SavedArchiveSearchesDialog dialog, string scene, Control focus, Control[] targets)
                {
                    await SettlePresentationAsync(owner, dialog);
                    await AssertFocusAsync(dialog, focus, log, "presentation " + scene);
                    Config persisted = Config.LoadConfig(profile);
                    string expectedClose = Texts.T("savedSearch.close");
                    string actualClose = Control<Button>(dialog, "SavedSearchClose").Content?.ToString() ?? "";
                    Require(Texts.Language == nextLanguage && dialog.ActualThemeVariant == expectedTheme && owner.ActualThemeVariant == expectedTheme
                        && owner.SessionHost.Config.Language == nextLanguage && owner.SessionHost.Config.Theme == nextTheme
                        && persisted.Language == nextLanguage && persisted.Theme == nextTheme && persisted.WorkspaceMode == nextMode,
                        "Actual/persisted presentation differs from requested case.");
                    Require(actualClose == expectedClose && dialog.Title == Texts.T("savedSearch.title"), "Actual dialog labels differ from requested language.");
                    Require(Math.Abs(owner.ClientSize.Width - minimumWidth) <= 1 && Math.Abs(owner.ClientSize.Height - minimumHeight) <= 1,
                        "Actual owner client size differs from the requested mode minimum.");
                    foreach (Control target in targets)
                    {
                        Point? at = target.TranslatePoint(default, dialog);
                        Require(target.IsEffectivelyVisible && target.Bounds.Width > 0 && target.Bounds.Height > 0 && at.HasValue
                            && at.Value.X >= -1 && at.Value.Y >= -1 && at.Value.X + target.Bounds.Width <= dialog.ClientSize.Width + 1
                            && at.Value.Y + target.Bounds.Height <= dialog.ClientSize.Height + 1, "Presentation target is outside the live dialog viewport: " + target.Name);
                    }
                    CheckUnchanged();
                    string stem = $"smoke-saved-search-{scene}-{nextLanguage}-{nextTheme.ToLowerInvariant()}-{nextMode.ToLowerInvariant()}-minimum.png";
                    Program.CaptureWindow(dialog, stem);
                    log.Note("presentation capture", "Actual labels/theme/focus and raw/store invariance checked before capture", new
                    {
                        scene, file = stem, requested = new { language = nextLanguage, theme = nextTheme, mode = nextMode, width = minimumWidth, height = minimumHeight },
                        actual = new { language = Texts.Language, ownerTheme = owner.ActualThemeVariant.ToString(), dialogTheme = dialog.ActualThemeVariant.ToString(),
                            mode = owner.SessionHost.Config.WorkspaceMode, ownerClient = owner.ClientSize.ToString(), dialogClient = dialog.ClientSize.ToString(),
                            scale = dialog.RenderScaling, closeLabel = actualClose, expectedClose, title = dialog.Title, focus = FocusLabel(dialog) },
                        rawName = Control<TextBox>(dialog, "SavedSearchName").Text, rawDraft, savedFileBefore = Hash(original), savedFileAfter = Hash(File.ReadAllBytes(SearchFile(profile))),
                        nativeAtImeOpen = true, humanVisualReview = "pending"
                    });
                }
            }
            Require(cells == 12, "Presentation matrix did not complete all twelve cells.");
        }
        finally
        {
            await owner.SessionHost.SaveViewPreferencesAsync(candidate => { candidate.Language = language; candidate.Theme = theme; });
            await PresentationChecks.SwitchModeAsync(owner, mode);
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = requestedTheme;
            owner.Width = width; owner.Height = height; owner.WindowState = windowState;
            await SettlePresentationAsync(owner);
            CheckUnchanged();
        }
        File.WriteAllText(Path.Combine(profile, "saved-search-presentation-complete.json"), JsonSerializer.Serialize(new
        { sourceDigest = RuntimeSourceDigest, implementationBasisDigest = ImplementationBasisDigest, fixtureDigest = FixtureDigest, savedFileHash = Hash(original), cells, captures = cells * 3, scale = owner.RenderScaling, result = "presentation assertions passed; functional pair not repeated" }));
        void CheckUnchanged()
        {
            Require(original.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Presentation mutated saved definitions.");
            Require(page.FindControl<TextBox>("ArchiveSearchText")!.Text == rawDraft && page.AppliedSearchSpec!.Equals(Board)
                && Rows(page).SequenceEqual(initialIds), "Presentation lost raw draft or changed the applied result.");
        }
    }
    private static async Task SettlePresentationAsync(Window owner, Window? dialog = null)
    {
        owner.UpdateLayout(); dialog?.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        owner.UpdateLayout(); dialog?.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        owner.UpdateLayout(); dialog?.UpdateLayout();
    }
}
