using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;
using Status = MeowChan.MediaHashSearchOperation.Status;

namespace MeowChan.DesktopSmoke;

// ROOT HOOK ONLY: await MediaHashSearchChecks.RunAsync(mainWindow);
// UI thread, real themed MainWindow after Ready, isolated native-allowed runner.
// No Program/dispatch edits. No routed-click/RaiseEvent/operation-method fallback.
internal static class MediaHashSearchChecks
{
    private const string Marker = "SMOKE HASH_SEARCH_NATIVE_GP37 ";
    private const int Matches = 137;
    private static readonly string Hash = "0" + new string('a', 63);
    private static readonly string Generation = new('d', 64);

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        NativeEnvironment();
        Require(owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
            "Real visible MainWindow owner with native handle required.");
        Require(Application.Current != null, "Real themed Application required.");
        foreach (string code in new[] { "en", "de", "es" })
            Require(Texts.Languages.Any(language => language.Code == code), "Runner must initialize bundled language " + code);
        string language = Texts.Language;
        ThemeVariant? applicationTheme = Application.Current!.RequestedThemeVariant;
        ThemeVariant? ownerTheme = owner.RequestedThemeVariant;
        // Exactly ONE snapshot shared by all real dialog instances; no reader/store/host APIs.
        // Full distinct IDs, deliberately non-existent paths and private-looking metadata.
        MediaAssetReference[] assets = Enumerable.Range(0, Matches).Select(index => new MediaAssetReference(
            "asset-" + index.ToString("x64", CultureInfo.InvariantCulture),
            "media-" + index.ToString("x64", CultureInfo.InvariantCulture),
            index % 2 == 0 ? "original" : "preview", "HASH_PRIVATE_requested.bin",
            "/HASH_PRIVATE/never-opened/" + index.ToString(CultureInfo.InvariantCulture),
            7, Hash, "https://HASH_PRIVATE.invalid/?token=HASH_SECRET", "HASH_PRIVATE_original.bin", index % 3 == 0)).ToArray();
        MediaAssetSnapshot snapshot = new("/HASH_PRIVATE/never-opened", MediaManifestStatus.Available,
            4, Generation, assets, "HASH_PRIVATE warning");
        Require(snapshot.Count == Matches, "Synthetic snapshot construction lost references.");
        Console.WriteLine(Marker + "BEGIN one_snapshot=137 same_sha256 distinct_full_ids variants_and_history no_file_reader");
        try
        {
            await SelectionJourneyAsync(owner, snapshot, assets, keyboard: true);
            await SelectionJourneyAsync(owner, snapshot, assets, keyboard: false);
            await ParentCancelAsync(owner, snapshot);
            await EscapeAsync(owner, snapshot);
            Require(snapshot.Generation == Generation && snapshot.Count == Matches
                && snapshot.GetPage(0, 100).Zip(assets.Take(100)).All(pair => ReferenceEquals(pair.First, pair.Second)),
                "Dialog journey changed the captured snapshot.");
            Console.WriteLine(Marker + "COMPLETE_ASSERTIONS_PASSED owner_modal native_enter_pointer_escape nullable_returns live_en_de_es_actualbundled no_navigation_no_rehash");
        }
        catch
        {
            Console.WriteLine(Marker + "FAILED no_completion_claim; inspect preceding phase marker; no synthetic fallback");
            throw;
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current!.RequestedThemeVariant = applicationTheme;
            owner.RequestedThemeVariant = ownerTheme;
            // Memory-only presentation restoration; no SaveViewPreferences/Config/profile writes.
        }
    }

    private static async Task SelectionJourneyAsync(MainWindow owner, MediaAssetSnapshot snapshot,
        MediaAssetReference[] assets, bool keyboard)
    {
        string scene = keyboard ? "keyboard-history-page2" : "pointer-preview-page1";
        var dialog = new MediaHashSearchDialog(snapshot);
        Task<MediaAssetReference?>? returned = null;
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            Texts.SetLanguage("en");
            returned = dialog.ShowDialog<MediaAssetReference?>(owner);
            using MediaNativeInput native = await StageAsync(owner, dialog, scene);
            TextBox input = Named<TextBox>(dialog, "MediaHashSearchInput");
            Button search = Named<Button>(dialog, "MediaHashSearchStart");
            Require(dialog.Operation.Current.Status == Status.Ready && dialog.Operation.Current.Request == 0
                && Rows(dialog).Length == 0 && !returned.IsCompleted, "Opening automatically searched/selected.");
            Require(dialog.WindowDecorations == WindowDecorations.BorderOnly
                && dialog.ExtendClientAreaToDecorationsHint && Math.Abs(dialog.ExtendClientAreaTitleBarHeightHint - 48) < 0.01,
                "Production window chrome policy differs.");
            Require(Named<WindowChromeBar>(dialog, "MediaHashSearchChrome").Bounds.Height == 48,
                "Real WindowChromeBar is absent or not laid out.");

            if (keyboard)
            {
                foreach (string invalid in new[] { new string('a', 32), " " + Hash, "sha256:" + Hash, Hash[..63] + "Ａ" })
                {
                    SetFixtureText(dialog, input, invalid);
                    var draft = dialog.Operation.Current;
                    await ReachAsync(dialog, input, scene + " invalid-input");
                    await native.ClickAsync(input);
                    await FocusedAsync(dialog, input, "native-pointer-input-focus");
                    // No IsDefault action in this UI: Enter in TextBox must not search implicitly.
                    await native.KeyAsync(MediaNativeInput.Enter);
                    await SettleAsync(dialog);
                    Require(dialog.Operation.Current.Request == draft.Request && dialog.Operation.Current.Status == Status.Ready,
                        "Typing/Enter in input implicitly dispatched a query.");
                    await ReachAsync(dialog, search, scene + " invalid-search");
                    Require(search.Focus(), "Cannot stage real Search button focus.");
                    await FocusedAsync(dialog, search, "staged-search-focus");
                    await native.KeyAsync(MediaNativeInput.Enter);
                    await WaitAsync(() => dialog.Operation.Current.Status == Status.Invalid
                        && Named<TextBlock>(dialog, "MediaHashSearchStatus").Text == Expected("mediahash.invalid", "en"), "invalid-visible");
                    Require(Rows(dialog).Length == 0 && !returned.IsCompleted, "Invalid hash produced a result/return.");
                }
                Console.WriteLine(Marker + "INVALID_EXPLICIT_ENTER_OK md5_whitespace_prefix_unicode input_enter_no_autosearch");
            }

            SetFixtureText(dialog, input, Hash.ToUpperInvariant());
            var ready = dialog.Operation.Current;
            if (keyboard) await PresentationAsync(owner, dialog, input, ready, withResults: false);
            await ResizeAsync(dialog, 760, 640);
            Texts.SetLanguage("en"); await SettleAsync(dialog);
            await ReachAsync(dialog, input, "valid-input");
            await native.ClickAsync(input);
            await FocusedAsync(dialog, input, "valid-native-input-focus");
            // Real native Tab, not programmatic focus, from input to explicit search.
            await native.KeyAsync(MediaNativeInput.Tab);
            await FocusedAsync(dialog, search, "native-tab-search-focus");
            await ReachAsync(dialog, search, "valid-search");
            await native.KeyAsync(MediaNativeInput.Enter);
            await WaitAsync(() => dialog.Operation.Current.Status == Status.Available
                && dialog.Operation.Current.Count == Matches && Rows(dialog).Length == 100
                && Named<TextBlock>(dialog, "MediaHashSearchStatus").Text == Expected("mediahash.matches", "en", Matches), "query100-rendered");
            Require(!returned.IsCompleted, "Search automatically selected a reference.");
            CheckPage(dialog, assets, 0, 100);
            if (keyboard) await PresentationAsync(owner, dialog, input, dialog.Operation.Current, withResults: true);
            await ResizeAsync(dialog, 760, 640);
            Texts.SetLanguage("en"); await SettleAsync(dialog);
            // Back/forward uses actual production footer buttons and native pointer events.
            Button previous = Named<Button>(dialog, "MediaHashSearchPrevious");
            Button next = Named<Button>(dialog, "MediaHashSearchNext");
            Require(!previous.IsEnabled && next.IsEnabled, "First-page bounds incorrect.");
            await ReachAsync(dialog, next, "next-footer"); await native.ClickAsync(next);
            await WaitAsync(() => dialog.Operation.Current.Offset == 100 && Rows(dialog).Length == 37, "query37-rendered");
            CheckPage(dialog, assets, 100, 37);
            Require(previous.IsEnabled && !next.IsEnabled, "Last-page bounds incorrect.");
            Require(Named<TextBlock>(dialog, "MediaHashSearchPage").Text == Expected("mediahash.page", "en", 101, Matches, Matches),
                "Last page count label is not captured 101–137 of137.");
            await ReachAsync(dialog, previous, "previous-footer"); await native.ClickAsync(previous);
            await WaitAsync(() => dialog.Operation.Current.Offset == 0 && Rows(dialog).Length == 100, "back100-rendered");
            CheckPage(dialog, assets, 0, 100);
            int selectedIndex;
            if (keyboard)
            {
                await ReachAsync(dialog, next, "return-next-footer");
                await native.ClickAsync(next);
                await WaitAsync(() => dialog.Operation.Current.Offset == 100 && Rows(dialog).Length == 37, "return37-rendered");
                selectedIndex = 133; // Full distinct ID, preview, historical IsCurrent=false.
            }
            else selectedIndex = 1; // Different full ID, preview, historical, on page1.
            var view = dialog.Operation.Current;
            Button row = Rows(dialog)[selectedIndex - view.Offset];
            MediaAssetReference expected = assets[selectedIndex];
            await ReachAsync(dialog, row, "exact-row");
            CheckCompleteLabel(row, MediaHashSearchDialog.RowLabel(expected), "exact-row-full-id");
            Require(!expected.IsCurrent && expected.Variant == "preview", "Selection fixture is not historical preview.");
            if (keyboard)
            {
                Require(row.Focus(), "Cannot stage real historical row focus.");
                await FocusedAsync(dialog, row, "staged-exact-row-focus");
                await native.KeyAsync(MediaNativeInput.Enter);
            }
            else await native.ClickAsync(row);
            await WaitAsync(() => closed && returned.IsCompleted, "exact-row-modal-return");
            MediaAssetReference? selected = await returned.WaitAsync(TimeSpan.FromSeconds(20));
            Require(ReferenceEquals(expected, selected), "Modal return is null/different object or reconstructed identity.");
            await dialog.TerminateAsync();
            Require(dialog.Operation.Current.Status == Status.Closed, "Selected dialog did not terminate.");
            await WaitAsync(() => owner.IsActive, "natural-owner-return"); // No owner.Activate() as an assertion shortcut.
            Console.WriteLine(Marker + "SELECT_EXACT_REFERENCE_OK scene=" + scene + " page_sizes=100_37 native=" + (keyboard ? "Enter(staged_row_focus)" : "pointer")
                + " historical_preview nullable_result_nonnull backend=" + native.Backend);
        }
        finally
        {
            if (!closed) dialog.Close();
            await dialog.TerminateAsync();
            if (returned != null) await returned.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private static async Task ParentCancelAsync(MainWindow owner, MediaAssetSnapshot snapshot)
    {
        using var parent = new CancellationTokenSource();
        var dialog = new MediaHashSearchDialog(snapshot, parent.Token);
        Task<MediaAssetReference?>? returned = null;
        try
        {
            Texts.SetLanguage("en");
            returned = dialog.ShowDialog<MediaAssetReference?>(owner);
            using MediaNativeInput native = await StageAsync(owner, dialog, "parent-cancel");
            TextBox input = Named<TextBox>(dialog, "MediaHashSearchInput");
            SetFixtureText(dialog, input, Hash);
            Button search = Named<Button>(dialog, "MediaHashSearchStart");
            await ReachAsync(dialog, search, "cancel-search"); await native.ClickAsync(search);
            await WaitAsync(() => dialog.Operation.Current.Status == Status.Available && Rows(dialog).Length == 100, "cancel-published-results");
            var old = dialog.Operation.Current;
            Button staleRow = Rows(dialog)[0]; // Real old control, never invoke its event handler directly.
            parent.Cancel();
            await WaitAsync(() => dialog.Operation.Current.Status == Status.Canceled && Rows(dialog).Length == 0
                && Named<TextBlock>(dialog, "MediaHashSearchStatus").Text == Expected("mediahash.canceled", "en"), "parent-canceled-visible");
            Require(dialog.Operation.Current.Request != old.Request && TopLevel.GetTopLevel(staleRow) == null
                && !returned.IsCompleted, "Parent cancellation kept rows/selection or returned an asset.");
            Button close = Named<Button>(dialog, "MediaHashSearchClose");
            await ReachAsync(dialog, close, "canceled-close");
            Require(close.Focus(), "Cannot stage canceled Close button focus.");
            await FocusedAsync(dialog, close, "staged-cancel-close-focus");
            await native.KeyAsync(MediaNativeInput.Enter);
            Require(await returned.WaitAsync(TimeSpan.FromSeconds(20)) == null, "Canceled modal returned an asset.");
            await dialog.TerminateAsync();
            await WaitAsync(() => owner.IsActive, "cancel-natural-owner-return");
            Console.WriteLine(Marker + "PARENT_CANCEL_PUBLISHED_OK rows_revoked native_enter_close nullable_return_null not_inflight_race_claim");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await dialog.TerminateAsync();
            if (returned != null) await returned.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private static async Task EscapeAsync(MainWindow owner, MediaAssetSnapshot snapshot)
    {
        var dialog = new MediaHashSearchDialog(snapshot);
        Task<MediaAssetReference?>? returned = null;
        try
        {
            returned = dialog.ShowDialog<MediaAssetReference?>(owner);
            using MediaNativeInput native = await StageAsync(owner, dialog, "escape-draft");
            TextBox input = Named<TextBox>(dialog, "MediaHashSearchInput");
            SetFixtureText(dialog, input, Hash);
            var before = dialog.Operation.Current;
            Require(before.Status == Status.Ready && before.Page.Length == 0, "Draft unexpectedly searched.");
            await ReachAsync(dialog, input, "escape-input"); await native.ClickAsync(input);
            await FocusedAsync(dialog, input, "escape-textbox-focus");
            await native.KeyAsync(MediaNativeInput.Escape);
            Require(await returned.WaitAsync(TimeSpan.FromSeconds(20)) == null, "Escape draft returned an asset.");
            Task first = dialog.TerminateAsync();
            Require(ReferenceEquals(first, dialog.TerminateAsync()), "Close drain is not idempotent.");
            await first;
            Require(dialog.Operation.Current.Status == Status.Closed && dialog.Operation.Current.Page.Length == 0,
                "Closed draft retained results.");
            await WaitAsync(() => owner.IsActive, "escape-natural-owner-return");
            Console.WriteLine(Marker + "ESCAPE_DRAFT_OK native_textbox_escape nullable_return_null no_search idempotent_drain");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await dialog.TerminateAsync();
            if (returned != null) await returned.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private static async Task PresentationAsync(MainWindow owner, MediaHashSearchDialog dialog, TextBox input,
        MediaHashSearchOperation.View baseline, bool withResults)
    {
        foreach (string code in new[] { "en", "de", "es" })
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (var size in new[] { new Size(760, 640), new Size(360, 360) })
        {
            Texts.SetLanguage(code);
            Application.Current!.RequestedThemeVariant = theme;
            owner.RequestedThemeVariant = theme; // Actual owner, memory-only; no new host/window/theme surrogate.
            await ResizeAsync(dialog, size.Width, size.Height);
            await WaitAsync(() => dialog.Title == Expected("mediahash.title", code)
                && owner.ActualThemeVariant == theme && dialog.ActualThemeVariant == theme
                && Named<TextBlock>(dialog, "MediaHashSearchScope").Text == Expected("mediahash.scope", code)
                && Equals(Named<Button>(dialog, "MediaHashSearchStart").Content, Expected("mediahash.search", code)), "live-language-theme");
            Require(Equals(dialog.Operation.Current, baseline), "Presentation started a query or changed captured results.");
            Require(input.Text == Hash.ToUpperInvariant(), "Presentation changed the untrimmed hash draft.");
            Require(Named<TextBlock>(dialog, "MediaHashSearchGeneration").Text == Expected("mediahash.generation", code, Generation),
                "Captured generation changed or is not fully visible text.");
            Require(dialog.Background is ISolidColorBrush { Color.A: 255 }, "Dialog lacks an opaque real theme background.");
            Border caption = dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("shell-bar"));
            Require(caption.Background is ISolidColorBrush { Color.A: 255 }, "Real caption theme surface is not opaque.");
            Require(Named<TextBlock>(dialog, "WindowCaptionTitle").Text == dialog.Title,
                "Real WindowChrome title did not follow locale.");
            await ReachAsync(dialog, input, "presentation-input");
            Require(input.Focus(), "Cannot stage input focus in real dialog.");
            await FocusedAsync(dialog, input, "presentation-input-focus");
            await SettleAsync(dialog);
            Require(Equals(dialog.Operation.Current, baseline), "Focus/scroll unexpectedly searched.");
            foreach (string name in new[] { "MediaHashSearchPrevious", "MediaHashSearchNext", "MediaHashSearchClose" })
            {
                Button footer = Named<Button>(dialog, name);
                Require(Fits(footer, dialog), "Real footer action outside client: " + name);
                CheckCompleteLabel(footer, footer.Content?.ToString() ?? "", "footer-" + code);
            }
            Button search = Named<Button>(dialog, "MediaHashSearchStart");
            await ReachAsync(dialog, search, "presentation-search");
            CheckCompleteLabel(search, Expected("mediahash.search", code), "search-" + code);
            Require(AutomationProperties.GetName(input) == Expected("mediahash.input", code), "Input accessible label did not refresh.");
            Require(AutomationProperties.GetName(search) == Expected("mediahash.search", code), "Search accessible label did not refresh.");
            if (withResults)
            {
                Require(Rows(dialog).Length == 100, "Locale/theme lost the current 100-row page.");
                for (int index = 0; index < 100; index++)
                {
                    string expected = MediaHashSearchDialog.RowLabel(baseline.Page[index]);
                    Require(RowText(Rows(dialog)[index]) == expected, "Full row text or variant/current localization changed.");
                }
            }
            Require(!dialog.GetVisualDescendants().OfType<TextBlock>().Any(label =>
                (label.Text ?? "").Contains("HASH_PRIVATE", StringComparison.Ordinal) || (label.Text ?? "").Contains("HASH_SECRET", StringComparison.Ordinal)),
                "A private path/source/filename/warning was projected into visible text.");
            Console.WriteLine(Marker + "PRESENTATION_CELL_OK state=" + (withResults ? "results" : "draft")
                + " locale=" + code + (code == "es" ? "(actualbundled_hash_catalog)" : "")
                + " theme=" + theme + " client=" + dialog.ClientSize + " request=" + baseline.Request
                + " input_focus_staged footer_glyphs_complete no_new_query no_minimum_long_row_fit_claim");
        }
    }

    private static void SetFixtureText(MediaHashSearchDialog dialog, TextBox input, string value)
    {
        // Explicit harness fixture injection, NOT OS typing/clipboard/IME evidence.
        input.Text = value;
        Require(input.Text == value && dialog.Operation.Current.Status == Status.Ready && Rows(dialog).Length == 0,
            "Editing trimmed text, kept old rows or automatically started a scan.");
    }
    private static void CheckPage(MediaHashSearchDialog dialog, MediaAssetReference[] expected, int offset, int count)
    {
        var view = dialog.Operation.Current;
        Button[] rows = Rows(dialog);
        Require(view.Offset == offset && view.Count == Matches && view.Page.Length == count && rows.Length == count,
            "Page bounds/count mismatch.");
        for (int index = 0; index < count; index++)
        {
            MediaAssetReference asset = expected[offset + index];
            Require(ReferenceEquals(asset, view.Page[index]), "Equal hash collapsed/reconstructed/reordered an asset.");
            string label = RowText(rows[index]);
            Require(label == MediaHashSearchDialog.RowLabel(asset) && label.Contains(asset.AssetId, StringComparison.Ordinal)
                && label.Contains(asset.MediaIdentifier, StringComparison.Ordinal) && label.Contains(asset.Variant, StringComparison.Ordinal),
                "Full IDs/variant/captured IsCurrent not preserved in row text.");
        }
        Console.WriteLine(Marker + "PAGE_EXACT_OK offset=" + offset + " count=" + count + " total=" + Matches + " no_dedup_same_object_refs");
    }
    private static async Task<MediaNativeInput> StageAsync(MainWindow owner, MediaHashSearchDialog dialog, string scene)
    {
        using var map = new PrivateMapProbe();
        await WaitAsync(() => dialog.IsVisible && owner.OwnedWindows.Contains(dialog)
            && dialog.TryGetPlatformHandle()?.Handle is nint handle && handle != 0, scene + " owned-native");
        await SettleAsync(dialog);
        await WaitAsync(() => dialog.ClientSize.Width > 0 && dialog.ClientSize.Height > 0 && map.IsViewable(dialog), scene + " mapped-native");
        var native = new MediaNativeInput(dialog);
        try
        {
            dialog.Activate(); // Explicit harness map/activation staging, not product initial-focus evidence.
            await WaitAsync(() => dialog.IsActive, scene + " active-native");
            await SettleAsync(dialog);
            Console.WriteLine(Marker + "STAGED scene=" + scene + " actual_MainWindow_owner native_handle theme=" + dialog.ActualThemeVariant + " backend=" + native.Backend);
            return native;
        }
        catch { native.Dispose(); throw; }
    }
    private static async Task ResizeAsync(Window dialog, double width, double height)
    {
        dialog.Width = width; dialog.Height = height;
        await WaitAsync(() => Math.Abs(dialog.ClientSize.Width - width) <= 1 && Math.Abs(dialog.ClientSize.Height - height) <= 1,
            "actual-client-" + width + "x" + height + " no-clamp-PASS");
        await SettleAsync(dialog);
    }
    private static async Task ReachAsync(Window dialog, Control control, string scene)
    {
        control.BringIntoView();
        await SettleAsync(dialog);
        await WaitAsync(() =>
        {
            dialog.UpdateLayout();
            if (!control.IsEffectivelyVisible || !control.IsEffectivelyEnabled || !Fits(control, dialog)) return false;
            foreach (Control ancestor in control.GetVisualAncestors().OfType<Control>())
                if ((ancestor.ClipToBounds || ancestor is ScrollViewer) && !Fits(control, ancestor)) return false;
            Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), dialog);
            return center is Point point && dialog.InputHitTest(point) is Visual hit
                && (ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control));
        }, "reachable-full-control-" + scene);
    }
    private static bool Fits(Control child, Control container)
    {
        Point? origin = child.TranslatePoint(default, container);
        return child.Bounds.Width > 0 && child.Bounds.Height > 0 && origin is Point point
            && point.X >= -1 && point.Y >= -1 && point.X + child.Bounds.Width <= container.Bounds.Width + 1
            && point.Y + child.Bounds.Height <= container.Bounds.Height + 1;
    }
    private static void CheckCompleteLabel(Button button, string expected, string scene)
    {
        TextBlock label = button.Content as TextBlock ?? button.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == expected);
        Require(label.Text == expected && AutomationProperties.GetName(button) == expected && Fits(label, button),
            "Real complete label/name bounds mismatch: " + scene);
        var layout = label.TextLayout;
        string rendered = string.Concat(layout.TextLines.SelectMany(line => line.TextRuns)
            .Where(run => run is not TextEndOfLine).Select(run => run.Text.ToString()));
        Require(rendered == expected && layout.TextLines.All(line => !line.HasCollapsed)
            && layout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1 && layout.Height <= label.Bounds.Height + 1,
            "Real label shortened/ellipsized/clipped: " + scene);
    }
    private static Task FocusedAsync(Window dialog, Control control, string scene) => WaitAsync(() =>
        ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), control), scene);
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
    private static async Task WaitAsync(Func<bool> predicate, string scene)
    {
        try { await Program.WaitUntilAsync(predicate); }
        catch (Exception error) { throw new InvalidOperationException(Marker + "WAIT_FAILED " + scene, error); }
    }
    private static Button[] Rows(MediaHashSearchDialog dialog) => Named<StackPanel>(dialog, "MediaHashSearchResults").Children.OfType<Button>().ToArray();
    private static string RowText(Button row) => (row.Content as TextBlock)?.Text ?? "";
    private static T Named<T>(Window dialog, string name) where T : Control
    {
        // Code-built dialogs have no parent NameScope. Require a unique actual name BEFORE
        // checking its type, so a same-name control of another type cannot be hidden.
        Control[] matches = dialog.GetVisualDescendants().OfType<Control>().Prepend(dialog)
            .Where(control => string.Equals(control.Name, name, StringComparison.Ordinal)).ToArray();
        Require(matches.Length == 1, "Expected exactly one actual hash dialog control: " + name + "; found " + matches.Length);
        return matches[0] is T typed ? typed
            : throw new InvalidOperationException(Marker + "ASSERT_FAILED Wrong actual hash dialog control type: " + name);
    }
    private static string Expected(string key, string language, params object?[] args)
    {
        var entry = MediaHashSearchCatalog.Entries.Single(entry => entry.Key == key);
        // Independent shipped-resource oracle for ES, not Texts.T/the adapter under observation.
        string template = language == "es" ? SpanishHashTemplates.Value[key]
            : language == "de" ? entry.German : entry.English;
        return string.Format(CultureInfo.GetCultureInfo(language), template, args);
    }
    private static readonly Lazy<IReadOnlyDictionary<string, string>> SpanishHashTemplates = new(() =>
    {
        const string resource = "MeowChan.Desktop.Localization.Packs.es.json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(Marker + "ASSERT_FAILED shipped ES pack missing");
        using JsonDocument document = JsonDocument.Parse(stream);
        Require(document.RootElement.GetProperty("code").GetString() == "es", "Shipped ES pack code mismatch.");
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .Where(property => property.Name.StartsWith("mediahash.", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.Value.GetString()
                ?? throw new InvalidOperationException(Marker + "ASSERT_FAILED null ES hash template"), StringComparer.Ordinal);
    });
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(Marker + "ASSERT_FAILED " + message); }
    private static void NativeEnvironment()
    {
        bool linux = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        bool other = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") == "1";
        Require(linux || other, "Private Xvfb or explicitly allowed disposable native desktop required; no fallback.");
    }
    // Read-only X11 map barrier on the SAME isolated display allowed by MediaNativeInput.
    // Other platforms use their real handle/layout/IsActive gates; this does not grant permission.
    private sealed class PrivateMapProbe : IDisposable
    {
        private readonly nint _display;
        internal PrivateMapProbe()
        {
            if (!OperatingSystem.IsLinux()) return;
            NativeEnvironment();
            _display = XOpenDisplay(0);
            Require(_display != 0, "Cannot observe private X11 mapping.");
        }
        internal bool IsViewable(Window window)
        {
            if (_display == 0) return true;
            nint handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            return handle != 0 && XGetWindowAttributes(_display, handle, out var attributes) != 0 && attributes.MapState == 2;
        }
        public void Dispose() { if (_display != 0) XCloseDisplay(_display); }
        [StructLayout(LayoutKind.Sequential)]
        private struct XWindowAttributes
        {
            internal int X, Y, Width, Height, BorderWidth, Depth;
            internal nint Visual, Root;
            internal int Class, BitGravity, WinGravity, BackingStore;
            internal nuint BackingPlanes, BackingPixel;
            internal int SaveUnder;
            internal nint Colormap;
            internal int MapInstalled, MapState;
            internal nint AllEventMasks, YourEventMask, DoNotPropagateMask;
            internal int OverrideRedirect;
            internal nint Screen;
        }
        [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
        [DllImport("libX11.so.6")] private static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);
    }
}
