using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

internal static partial class PluginManagementChecks
{
    private static Task CheckFixedDraftFooterAsync(MainWindow window, PluginsPage page, PluginProfileStore profile) =>
        AtBarebonesMinimumAsync(window, async () =>
        {
            using MediaNativeInput input = new(window);
            ScrollViewer scroll = page.FindControl<ScrollViewer>("PluginDetailsPane")!;
            Border footer = page.FindControl<Border>("PluginDraftFooter")!;
            Button discard = page.FindControl<Button>("PluginDiscard")!;
            Button save = page.FindControl<Button>("PluginSave")!;
            TextBlock status = page.FindControl<TextBlock>("PluginDraftStatus")!;
            SelectableTextBlock reasons = page.FindControl<SelectableTextBlock>("PluginDraftReasons")!;
            TextBlock error = page.FindControl<TextBlock>("PluginPageError")!;

            foreach (string language in new[] { "en", "cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans", "de" })
            {
                Texts.SetLanguage(language);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Require(Texts.Language == language && VisibleFooter() && FullyVisibleWithin(status, footer) && FullyVisibleWithin(reasons, footer)
                    && reasons.Text?.Contains(Texts.T("plugins.reason.unchanged"), StringComparison.Ordinal) == true
                    && !save.IsEnabled, $"The unchanged draft reason or its actions are clipped or incorrect in {language}.");
            }

            PluginSettingsEditor editor = await PluginSettingsFormChecks.OpenManagedScopeAsync(page, SettingScope.Plugin);
            TextBox number = (TextBox)editor.FindField("number")!;
            TextBox lastField = (TextBox)editor.FindField("uri")!;
            number.Text = "1.5";
            scroll.Offset = default;
            window.UpdateLayout();
            Point initialFooter = footer.TranslatePoint(default, page)!.Value;
            Require(VisibleFooter() && !FullyVisibleWithin(lastField, scroll), "The long draft fixture did not begin above its last field.");
            await RevealAsync(window, lastField);
            Require(scroll.Offset.Y > 1 && VisibleFooter() &&
                Math.Abs(footer.TranslatePoint(default, page)!.Value.Y - initialFooter.Y) <= 1,
                "The draft footer moved when scrolling to the last settings field.");

            long revision = (await profile.ReadAsync()).Revision;
            Require(lastField.Focus(), "Could not focus the last settings field.");
            await TabToDiscardAsync();
            await input.KeyAsync(MediaNativeInput.Tab);
            Require(save.IsFocused && VisibleFooter(), "Tab did not move from the left Discard action to the right Save action.");
            await input.KeyAsync(MediaNativeInput.Space);
            await Program.WaitUntilAsync(() => number.IsFocused && FullyVisibleWithin(number, scroll));
            Require(VisibleFooter() && FullyVisibleWithin(error, page) && error.Text == Texts.T("plugins.error.fields")
                && number.Text == "1.5" && (await profile.ReadAsync()).Revision == revision,
                "An invalid save lost its message, footer, focused raw input or unchanged profile.");

            number.Text = "4";
            await TabToDiscardAsync();
            await input.KeyAsync(MediaNativeInput.Tab);
            Require(save.IsFocused, "The corrected draft could not be saved by keyboard.");
            await input.KeyAsync(MediaNativeInput.Space);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentDraft?.Dirty == false);
            PluginProfileSnapshot saved = await profile.ReadAsync();
            Require(saved.Revision > revision && saved.Plugins.Single().Pending?.Configuration.PluginSettings.Values
                .Single(value => value.Key == "number").Integer == 4, "Keyboard Save did not persist the corrected draft.");

            editor = await PluginSettingsFormChecks.OpenManagedScopeAsync(page, SettingScope.Plugin);
            number = (TextBox)editor.FindField("number")!;
            number.Text = "5";
            Require(number.Focus(), "Could not focus the changed setting before discarding.");
            await TabToDiscardAsync();
            await input.KeyAsync(MediaNativeInput.Space);
            PopConfirm confirmation = await WaitForOwnedDiscardPopupAsync(window, page, discard);
            Button confirm = confirmation.GetVisualDescendants().OfType<Popup>().Single(popup => popup.Name == PopConfirm.PART_Popup)
                .Child!.GetVisualDescendants().OfType<Button>().Single(button => button.Name == PopConfirm.PART_ConfirmButton);
            Require(confirm.Focus(), "The Discard confirmation could not receive keyboard focus.");
            await input.KeyAsync(MediaNativeInput.Space);
            await Program.WaitUntilAsync(() => !page.IsBusy && !page.IsDiscardConfirmationOpen && discard.IsFocused);
            Require(VisibleFooter() && page.CurrentDraft?.Plugin.Text("number") == "4"
                && (await profile.ReadAsync()).Revision == saved.Revision, "Keyboard Discard did not restore the saved draft and return focus.");

            Console.WriteLine($"PLUGIN DRAFT FOOTER PASS: {window.ClientSize} DIP, scale {window.RenderScaling}, 12 languages, last field, invalid save focus, corrected save and confirmed discard.");

            bool VisibleFooter()
            {
                Point? bodyTop = scroll.TranslatePoint(default, page);
                Point? footerTop = footer.TranslatePoint(default, page);
                Point? discardPosition = discard.TranslatePoint(default, footer);
                Point? savePosition = save.TranslatePoint(default, footer);
                return FullyVisibleWithin(footer, page) && FullyVisibleWithin(discard, footer) && FullyVisibleWithin(save, footer)
                    && bodyTop is { } body && footerTop is { } bottom && body.Y + scroll.Bounds.Height <= bottom.Y + 1
                    && discardPosition is { } left && savePosition is { } right && left.X + discard.Bounds.Width <= right.X + 1;
            }

            async Task TabToDiscardAsync()
            {
                for (int step = 0; step < 32 && !discard.IsFocused; step++)
                    await input.KeyAsync(MediaNativeInput.Tab);
                Require(discard.IsFocused && VisibleFooter(), "The fixed Discard action was not reachable in the detail tab order.");
            }
        });

    private static bool FullyVisibleWithin(Control control, Control container) =>
        control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
        && control.TranslatePoint(default, container) is { } point
        && point.X >= -1 && point.Y >= -1
        && point.X + control.Bounds.Width <= container.Bounds.Width + 1
        && point.Y + control.Bounds.Height <= container.Bounds.Height + 1;
}
