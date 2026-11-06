using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Testing;
using Microsoft.Data.Sqlite;

using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// ROOT HOOK ONLY: await FilenameSearchChecks.RunAsync(mainWindow) after Ready.
// No Program dispatch supplied. Uses existing MediaNativeInput authorization gates; no routed
// event fallback for native keyboard checks or personal display. Unicode checks explicitly use
// routed TextInput and isolated fixture Paste to exercise TextBox insertion, not OS Unicode typing. Synthetic DB writes below are fixture creation ONLY.
internal static class FilenameSearchChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Needle = "w19filename";
    private const int Total = 205;

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint h && h != 0,
            "Requires actual visible MainWindow owner after Ready.");
        string previousLanguage = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("filename-search-native-");
        try
        {
            ArchiveFilenameHit[] expected = await SeedAsync(profile);
            await PagingSelectionLanguageLayoutAsync(owner, profile, expected);
            await UnicodeInputAsync(owner, profile);
            await LateFactoryAsync(owner, profile, close: false);
            await LateFactoryAsync(owner, profile, close: true);
            await LateFactoryAsync(owner, profile, close: false, cancel: true);
            Console.WriteLine("SMOKE filename-search complete: all helper assertions reached; synthetic metadata only, no current-byte/network/import claim.");
        }
        finally
        {
            Texts.SetLanguage(previousLanguage);
            owner.Activate();
            // Retain isolated synthetic evidence; do not recursively clean anything or print paths.
        }
    }

    // Scoped fixture clipboard: never read, save, restore or write the personal OS clipboard.
    // X11 resolves Clipboard from this service; fail closed elsewhere before attempting Paste.
    private static async Task UnicodeInputAsync(MainWindow owner, string profile)
    {
        Require(OperatingSystem.IsLinux(), "Isolated Paste fixture requires Linux/X11 smoke host.");
        using IDisposable scope = AvaloniaLocator.EnterScope();
        var clipboard = new UnicodeClipboard();
        AvaloniaLocator.CurrentMutable.Bind<IClipboard>().ToConstant(clipboard);
        int factories = 0;
        var dialog = new FilenameSearchDialog(token =>
        {
            Interlocked.Increment(ref factories);
            return ArchiveReader.OpenAsync(profile, token);
        });
        Task<ArchiveFilenameHit?> shown = dialog.ShowDialog<ArchiveFilenameHit?>(owner);
        try
        {
            await VisibleAsync(dialog);
            Require(ReferenceEquals(dialog.Clipboard, clipboard), "Fixture clipboard not installed; Paste forbidden.");
            TextBox query = C<TextBox>(dialog, "FilenameSearchQuery");
            Require(query.MaxLength == 0, "Destructive TextBox length limit reintroduced.");
            using var input = new MediaNativeInput(dialog);
            dialog.Activate(); await UntilAsync(() => dialog.IsActive);
            foreach (bool paste in new[] { false, true })
            {
                async Task InsertAsync(string text)
                {
                    Require(query.Focus(), "Unicode input cannot focus query.");
                    if (paste)
                    {
                        clipboard.Text = text;
                        // Actual TextBox clipboard insertion path (async void in Avalonia).
                        query.Paste();
                    }
                    else query.RaiseEvent(new TextInputEventArgs
                    { RoutedEvent = InputElement.TextInputEvent, Text = text });
                    await PumpAsync();
                }
                // Only empty reset uses .Text; all payload insertion uses the control pipeline.
                query.Text = ""; await PumpAsync();
                string ascii = new('a', 511);
                await InsertAsync(ascii); await UntilAsync(() => query.Text == ascii);
                query.CaretIndex = ascii.Length;
                query.SelectionStart = query.SelectionEnd = ascii.Length;
                string over = ascii + "😀";
                await InsertAsync("😀"); await UntilAsync(() => query.Text == over);
                Require(WellFormed(query.Text!), "Insertion split a surrogate pair.");
                int before = Volatile.Read(ref factories);
                await ClickAsync(input, dialog, "FilenameSearchSubmit");
                await UntilAsync(() => dialog.Session.Current.StatusKey == "filename.search.invalid");
                Require(query.Text == over && dialog.Session.Current.Text == over
                    && Volatile.Read(ref factories) == before, "513-unit text was truncated or handed to reader.");

                // Replace one selected ASCII unit in an actual 512-unit control value.
                query.Text = ""; await PumpAsync();
                string boundary = new('a', 512);
                await InsertAsync(boundary); await UntilAsync(() => query.Text == boundary);
                query.SelectionStart = 511; query.SelectionEnd = 512;
                await InsertAsync("😀"); await UntilAsync(() => query.Text == over);
                await ClickAsync(input, dialog, "FilenameSearchSubmit");
                await UntilAsync(() => dialog.Session.Current.StatusKey == "filename.search.invalid");
                Require(WellFormed(query.Text!) && Volatile.Read(ref factories) == before,
                    "Selection replacement split Unicode or acquired a reader for overlength text.");
                // Replace two ASCII units: exactly 512 UTF16 units, including the intact pair.
                query.Text = ""; await PumpAsync();
                await InsertAsync(boundary); await UntilAsync(() => query.Text == boundary);
                query.SelectionStart = 510; query.SelectionEnd = 512;
                string replaced = new string('a', 510) + "😀";
                await InsertAsync("😀"); await UntilAsync(() => query.Text == replaced);
                await ClickAsync(input, dialog, "FilenameSearchSubmit"); await ReadyAsync(dialog, 0, 0, 0);
                Require(dialog.Session.Current.Text == replaced && WellFormed(replaced)
                    && Volatile.Read(ref factories) == before + 1, "Valid selection replacement changed at handoff.");

                query.Text = ""; await PumpAsync();
                string emojis = string.Concat(Enumerable.Repeat("😀", 256));
                await InsertAsync(emojis); await UntilAsync(() => query.Text == emojis);
                await ClickAsync(input, dialog, "FilenameSearchSubmit"); await ReadyAsync(dialog, 0, 0, 0);
                Require(emojis.Length == 512 && dialog.Session.Current.Text == emojis && WellFormed(query.Text!)
                    && Volatile.Read(ref factories) == before + 2, "256 emoji were changed/rejected at handoff.");
            }
            Require(clipboard.Reads > 0, "Paste clipboard oracle was vacuous.");
            Console.WriteLine("SMOKE filename-search unicode-input: TextInput+fixture Paste preserve 511ASCII+emoji, selection replacement, 256emoji; invalid text never acquires reader.");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(Deadline);
            await dialog.Session.CloseAsync().WaitAsync(Deadline);
        }
    }

    private static bool WellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    private sealed class UnicodeClipboard : IClipboard
    {
        internal string Text = "";
        internal int Reads;
        public Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            Reads++;
            DataTransfer data = new();
            data.Add(DataTransferItem.Create(DataFormat.Text, Text));
            return Task.FromResult<IAsyncDataTransfer?>(data);
        }
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => TryGetDataAsync();
        public Task SetDataAsync(IAsyncDataTransfer? dataTransfer) => throw new InvalidOperationException("Unexpected fixture clipboard write.");
        public Task ClearAsync() => throw new InvalidOperationException("Unexpected fixture clipboard clear.");
        public Task FlushAsync() => throw new InvalidOperationException("Unexpected fixture clipboard flush.");
    }

    private static async Task PagingSelectionLanguageLayoutAsync(MainWindow owner, string profile, ArchiveFilenameHit[] expected)
    {
        Texts.SetLanguage("en");
        int factories = 0;
        var dialog = new FilenameSearchDialog(token =>
        {
            Interlocked.Increment(ref factories);
            return ArchiveReader.OpenAsync(profile, token);
        });
        Task<ArchiveFilenameHit?> shown = dialog.ShowDialog<ArchiveFilenameHit?>(owner);
        try
        {
            await VisibleAsync(dialog);
            Require(ReferenceEquals(dialog.Owner, owner), "Dialog is not owned by actual MainWindow.");
            TextBox query = C<TextBox>(dialog, "FilenameSearchQuery");
            ListBox list = C<ListBox>(dialog, "FilenameSearchResults");
            Require(query.IsFocused, "Opened did not focus actual query box.");
            Require(query.MaxLength == 0, "TextBox must not truncate UTF16 before contract validation.");
            Require(C<WindowChromeBar>(dialog, "FilenameSearchChrome").Bounds.Height == 48
                && dialog.WindowDecorations == WindowDecorations.BorderOnly, "Not app-owned chrome.");
            query.Text = Needle;
            await PumpAsync();
            Require(Volatile.Read(ref factories) == 0 && list.Items.Count == 0,
                "Construction/TextChanged implicitly searched.");
            using var input = new MediaNativeInput(dialog);
            dialog.Activate(); await UntilAsync(() => dialog.IsActive);
            Require(query.Focus(), "Query cannot receive keyboard focus.");
            await input.KeyAsync(MediaNativeInput.Enter); // OS input, not direct method/routed event
            await ReadyAsync(dialog, 0, 100, Total);
            Require(Volatile.Read(ref factories) == 1, "Explicit Enter did not issue exactly one reader request.");
            Require(!shown.IsCompleted && !C<Button>(dialog, "FilenameSearchSelect").IsEnabled,
                "Search automatically selected/returned a hit.");
            query.Text = " " + Needle + " "; await PumpAsync();
            Require(Volatile.Read(ref factories) == 1 && list.Items.Count == 0, "Query edit implicitly searched or retained rows.");
            await ClickAsync(input, dialog, "FilenameSearchSubmit"); await ReadyAsync(dialog, 0, 0, 0);
            Require(query.Text == " " + Needle + " ", "Actual search trimmed the literal input.");
            query.Text = Needle; await PumpAsync();
            Require(Volatile.Read(ref factories) == 2, "Second edit implicitly searched.");
            await ClickAsync(input, dialog, "FilenameSearchSubmit"); await ReadyAsync(dialog, 0, 100, Total);
            Require(Volatile.Read(ref factories) == 3, "Explicit search clicks did not each issue one request.");
            var observed = new List<ArchiveFilenameHit>(dialog.Session.Current.Rows);
            CheckPage(expected.Take(100).ToArray(), dialog);
            await CheckRenderedFirstAsync(dialog, expected[0]);
            await ClickAsync(input, dialog, "FilenameSearchNext");
            await ReadyAsync(dialog, 100, 100, Total);
            observed.AddRange(dialog.Session.Current.Rows);
            CheckPage(expected.Skip(100).Take(100).ToArray(), dialog);
            await ClickAsync(input, dialog, "FilenameSearchNext");
            await ReadyAsync(dialog, 200, 5, Total);
            observed.AddRange(dialog.Session.Current.Rows);
            CheckPage(expected.Skip(200).ToArray(), dialog);
            Require(observed.SequenceEqual(expected) && !C<Button>(dialog, "FilenameSearchNext").IsEnabled,
                "Three UI pages lost/duplicated identities or enabled another end page.");
            await ClickAsync(input, dialog, "FilenameSearchPrevious"); await ReadyAsync(dialog, 100, 100, Total);
            await ClickAsync(input, dialog, "FilenameSearchPrevious"); await ReadyAsync(dialog, 0, 100, Total);
            Console.WriteLine("SMOKE filename-search paging: real synthetic DB205, 100/100/5 rows, count+rows, exact identities, previous/end checked.");

            list.SelectedIndex = 0; list.ScrollIntoView(list.SelectedItem!); await PumpAsync();
            ArchiveFilenameHit exact = dialog.Session.Current.Rows[0];
            Require(C<Button>(dialog, "FilenameSearchSelect").IsEnabled, "Explicit list selection did not enable return.");
            int beforeLanguages = Volatile.Read(ref factories);
            foreach (string language in new[] { "de", "es", "en" })
            {
                Texts.SetLanguage(language);
                await UntilAsync(() => dialog.Title == ShippedFilenameText("filename.search.title", language));
                await PumpAsync();
                Require(query.Text == Needle && Volatile.Read(ref factories) == beforeLanguages,
                    "Language refresh changed query or issued a new search.");
                Require(ReferenceEquals(exact, dialog.Session.Current.Rows[0]) && list.SelectedIndex == 0
                    && C<Button>(dialog, "FilenameSearchSelect").IsEnabled, "Language refresh lost exact selection.");
                Require(C<Button>(dialog, "FilenameSearchSubmit").Content?.ToString() ==
                    ShippedFilenameText("filename.search.search", language), "Search label differs from shipped catalog/pack.");
                Require(C<TextBlock>(dialog, "FilenameSearchStatus").Text ==
                    ShippedFilenameText("filename.search.ready", language, Total, 100, 0), "Count label differs from shipped catalog/pack.");
                await CheckRenderedFirstAsync(dialog, exact);
            }
            Console.WriteLine("SMOKE filename-search localization: actual EN/DE/ES bundled translations refresh, no extra search, exact selection retained checked.");

            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                await UntilAsync(() => dialog.ActualThemeVariant == theme);
                await PumpAsync();
                Require(dialog.Background is ISolidColorBrush { Color.A: 255 }, "Dialog theme background unresolved/nonopaque.");
                foreach ((double width, double height) in new[] { (820d, 680d), (520d, 600d), (360d, 520d) })
                {
                    dialog.Width = width; dialog.Height = height; await PumpAsync();
                    CheckLayout(dialog);
                }
            }
            dialog.Width = 820; dialog.Height = 680; await PumpAsync();
            Require(query.Focus(), "Cannot focus input before native Tab check.");
            await input.KeyAsync(MediaNativeInput.Tab);
            Require(C<Button>(dialog, "FilenameSearchSubmit").IsFocused,
                "Native Tab did not navigate query → Search.");
            Console.WriteLine("SMOKE filename-search layout-keyboard: Light/Dark, 820x680/520x600/360x520 client control reachability, native Enter/Tab checked; no minimum380 claim.");
            await ClickAsync(input, dialog, "FilenameSearchSelect");
            ArchiveFilenameHit? returned = await shown.WaitAsync(Deadline);
            Require(ReferenceEquals(exact, returned), "ShowDialog nullable return is not the exact selected ArchiveFilenameHit.");
            await dialog.TerminateAsync().WaitAsync(Deadline);
            Console.WriteLine("SMOKE filename-search exact-return: explicit Select returned same nullable hit instance, no autoopen/current-byte claim.");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(Deadline);
            await dialog.TerminateAsync().WaitAsync(Deadline);
        }
    }

    // Delay only the EXISTING reader factory; no private-field replacement or new product hook.
    // Returned reader is real and owns a real DB. It intentionally ignores cancellation until
    // the factory returns; production must then reject it before search/publication and dispose it.
    private static async Task LateFactoryAsync(MainWindow owner, string profile, bool close, bool cancel = false)
    {
        var entered = Signal<CancellationToken>(); var release = Signal<bool>(); var returned = Signal<ArchiveReader>();
        int calls = 0;
        async Task<ArchiveReader> Factory(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult(token);
                await release.Task.ConfigureAwait(false);
                ArchiveReader reader = await ArchiveReader.OpenAsync(profile, CancellationToken.None).ConfigureAwait(false);
                returned.TrySetResult(reader);
                return reader;
            }
            return await ArchiveReader.OpenAsync(profile, token).ConfigureAwait(false);
        }
        var dialog = new FilenameSearchDialog(Factory);
        Task<ArchiveFilenameHit?> shown = dialog.ShowDialog<ArchiveFilenameHit?>(owner);
        Task? drain = null;
        try
        {
            await VisibleAsync(dialog);
            using var input = new MediaNativeInput(dialog);
            dialog.Activate(); await UntilAsync(() => dialog.IsActive);
            var query = C<TextBox>(dialog, "FilenameSearchQuery");
            query.Text = Needle;
            await ClickAsync(input, dialog, "FilenameSearchSubmit");
            CancellationToken old = await entered.Task.WaitAsync(Deadline);
            await UntilAsync(() => C<Button>(dialog, "FilenameSearchCancel").IsEnabled);
            if (close)
            {
                Require(query.Focus(), "Cannot focus query for native Escape-close.");
                await input.KeyAsync(MediaNativeInput.Escape);
                Require(await shown.WaitAsync(Deadline) == null, "Escape returned metadata instead of null.");
                drain = dialog.TerminateAsync();
                Require(!drain.IsCompleted && old.IsCancellationRequested && dialog.Session.Current.Closed,
                    "Close did not seal/cancel pending factory or drain is vacuous.");
            }
            else if (cancel)
            {
                await ClickAsync(input, dialog, "FilenameSearchCancel");
                Require(old.IsCancellationRequested && dialog.Session.Current.StatusKey == "filename.search.canceled",
                    "Actual Cancel button did not invalidate the pending request.");
            }
            else
            {
                query.Text = "w19-no-such-name"; // real TextChanged invalidates old request synchronously
                Require(old.IsCancellationRequested && dialog.Session.Current.Rows.IsEmpty,
                    "Changing old query did not cancel and clear the current page.");
                await ClickAsync(input, dialog, "FilenameSearchSubmit");
                await ReadyAsync(dialog, 0, 0, 0);
            }
            release.TrySetResult(true);
            ArchiveReader lateReader = await returned.Task.WaitAsync(Deadline);
            await DisposedAsync(lateReader);
            // Drains BOTH pending requests and proves that the canceled reader cannot publish later.
            // For open/edit case, terminal drain is done only AFTER observing stable visible result.
            if (close) await drain!.WaitAsync(Deadline);
            else
            {
                await UntilAsync(() => !dialog.Session.Current.Busy);
                await PumpAsync();
                Require(dialog.Session.Current.Total == 0 && C<ListBox>(dialog, "FilenameSearchResults").Items.Count == 0
                    && (cancel ? dialog.Session.Current.StatusKey == "filename.search.canceled"
                        : dialog.Session.Current.Text == "w19-no-such-name" && dialog.Session.Current.StatusKey == "filename.search.ready"),
                    "Late old factory rendered hits over the canceled/new empty query result.");
                await ClickAsync(input, dialog, "FilenameSearchClose");
                Require(await shown.WaitAsync(Deadline) == null, "Close button did not return null.");
                await dialog.TerminateAsync().WaitAsync(Deadline);
            }
            string oldTitle = dialog.Title ?? "";
            Texts.SetLanguage(Texts.Language == "de" ? "en" : "de"); await PumpAsync();
            Require(dialog.Title == oldTitle && dialog.Session.Current.Rows.IsEmpty,
                "Closed dialog responded to language/late completion.");
            Console.WriteLine(close
                ? "SMOKE filename-search close-late: native Escape, nullable null, token canceled, delayed real reader drained, no closed language refresh checked."
                : cancel ? "SMOKE filename-search cancel-late: actual Cancel, token cancellation, delayed real reader disposal, empty visible result and nullable null checked."
                : "SMOKE filename-search edit-late: actual TextChanged cancels old factory, explicit new search wins, delayed real reader cannot replace rows; Close returns null checked.");
        }
        finally
        {
            release.TrySetResult(true);
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(Deadline);
            await dialog.TerminateAsync().WaitAsync(Deadline);
        }
    }

    // Bounded observation of the real returned reader's public lifetime, not reflection/hooks.
    // At most a temporary catalog read before disposal; never a media/file/network access.
    // A disposed reader must reject this existing search API with ObjectDisposedException.
    private static async Task DisposedAsync(ArchiveReader reader)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < end)
        {
            try { await reader.SearchFilenamesAsync(new ArchiveFilenameSearch(Needle), limit: 1).WaitAsync(Deadline); }
            catch (ObjectDisposedException) { return; }
            await Task.Delay(20);
        }
        throw new TimeoutException("Canceled/closed factory reader was not disposed by product operation.");
    }

    private static async Task VisibleAsync(FilenameSearchDialog dialog)
    {
        await UntilAsync(() => dialog.IsVisible && dialog.TryGetPlatformHandle()?.Handle is nint h && h != 0);
        await PumpAsync();
        Require(dialog.ClientSize.Width > 0 && dialog.ClientSize.Height > 0, "Native dialog has no client layout.");
    }
    private static async Task ReadyAsync(FilenameSearchDialog dialog, int offset, int count, long total)
    {
        await UntilAsync(() => dialog.Session.Current is { Busy: false, StatusKey: "filename.search.ready" } state
            && state.Offset == offset && state.Rows.Length == count && state.Total == total
            && C<ListBox>(dialog, "FilenameSearchResults").Items.Count == count
            && C<TextBlock>(dialog, "FilenameSearchStatus").Text == FilenameSearchCatalog.T("filename.search.ready", total, count, offset));
    }
    private static void CheckPage(ArchiveFilenameHit[] expected, FilenameSearchDialog dialog)
    {
        Require(dialog.Session.Current.Rows.SequenceEqual(expected), "Real DB UI page metadata does not match exact fixture identities/revisions.");
        var list = C<ListBox>(dialog, "FilenameSearchResults");
        Require(list.Items.Count == expected.Length && list.Items.Cast<object>().Select(row => row.ToString()).SequenceEqual(
            expected.Select(hit => FilenameSearchDialog.DescribeHit(hit, (key, args) => FilenameSearchCatalog.T(key, args)))),
            "Actual bound row labels omit/alter captured identity metadata.");
    }
    private static async Task CheckRenderedFirstAsync(FilenameSearchDialog dialog, ArchiveFilenameHit hit)
    {
        var list = C<ListBox>(dialog, "FilenameSearchResults");
        list.ScrollIntoView(list.Items[0]!); await PumpAsync();
        string label = FilenameSearchDialog.DescribeHit(hit, (key, args) => FilenameSearchCatalog.T(key, args));
        await UntilAsync(() => list.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));
        Require(label.Contains(hit.OriginalName, StringComparison.Ordinal) && label.Contains(hit.Thread.Source, StringComparison.Ordinal)
            && label.Contains(hit.Thread.Board, StringComparison.Ordinal) && label.Contains(hit.Thread.Thread!, StringComparison.Ordinal)
            && label.Contains(hit.Post, StringComparison.Ordinal) && label.Contains(hit.Variant, StringComparison.Ordinal), "Rendered identity coverage is incomplete.");
        Require(label.Contains(hit.AttachmentRevisionKey ?? FilenameSearchCatalog.T("filename.search.unknown"), StringComparison.Ordinal)
            && label.Contains("7", StringComparison.Ordinal)
            && label.Contains(FilenameSearchCatalog.T(hit.PostIsDeleted ? "filename.search.deleted" : "filename.search.notDeleted"), StringComparison.Ordinal),
            "Rendered revision/deletion/unknown claims missing.");
        Require(!label.Contains("PATH-ONLY-MARKER", StringComparison.Ordinal) && !label.Contains("URL-ONLY-MARKER", StringComparison.Ordinal),
            "A filesystem/acquisition reference leaked into display.");
    }
    // Independent shipped-resource oracle, not FilenameSearchCatalog.Resolve or the live Texts resolver.
    // Missing keys fail: ES is now a real bundled translation, not an English-fallback expectation.
    private static string ShippedFilenameText(string key, string language, params object?[] args)
    {
        string format;
        if (language is "en" or "de")
        {
            var catalog = language == "de" ? BuiltInCatalog.German : BuiltInCatalog.English;
            if (!catalog.TryGetValue(key, out string? value))
                throw new InvalidOperationException("Missing shipped filename key: " + key);
            format = value;
        }
        else if (language == "es")
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
                "MeowChan.Desktop.Localization.Packs.es.json")
                ?? throw new InvalidOperationException("Missing shipped Spanish pack.");
            using var pack = System.Text.Json.JsonDocument.Parse(stream);
            format = pack.RootElement.GetProperty("strings").GetProperty(key).GetString()
                ?? throw new InvalidOperationException("Missing shipped Spanish filename value.");
        }
        else throw new InvalidOperationException("Unsupported shipped filename oracle language.");
        return string.Format(System.Globalization.CultureInfo.GetCultureInfo(language), format, args);
    }

    private static void CheckLayout(FilenameSearchDialog dialog)
    {
        dialog.UpdateLayout();
        foreach (string name in new[] { "FilenameSearchChrome", "FilenameSearchQuery", "FilenameSearchResults", "FilenameSearchSubmit",
            "FilenameSearchCancel", "FilenameSearchPrevious", "FilenameSearchNext", "FilenameSearchSelect", "FilenameSearchClose" })
        {
            var control = C<Control>(dialog, name);
            Point point = control.TranslatePoint(default, dialog) ?? throw new InvalidOperationException("Unattached control: " + name);
            Require(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                && point.X >= -1 && point.Y >= -1 && point.X + control.Bounds.Width <= dialog.ClientSize.Width + 1
                && point.Y + control.Bounds.Height <= dialog.ClientSize.Height + 1, "Control clipped/outside client: " + name);
        }
    }
    private static async Task ClickAsync(MediaNativeInput input, FilenameSearchDialog dialog, string name)
    {
        Button button = C<Button>(dialog, name); Require(button.IsEffectivelyEnabled, "Disabled action: " + name);
        await input.ClickAsync(button);
    }
    private static T C<T>(Control dialog, string name) where T : Control
    {
        // Code-built dialogs do not establish a XAML NameScope. Inspect actual attached controls;
        // never create substitutes or silently choose the first of duplicate named elements.
        Control[] matches = dialog.GetVisualDescendants().OfType<Control>().Prepend(dialog)
            .Where(control => string.Equals(control.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Expected exactly one actual filename dialog control: " + name
                + "; found " + matches.Length);
        return matches[0] is T typed ? typed
            : throw new InvalidOperationException("Wrong actual filename dialog control type: " + name);
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Filename search smoke condition timed out.");
            await Task.Delay(20);
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task<ArchiveFilenameHit[]> SeedAsync(string profile)
    {
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        { await using var catalog = await ArchiveCatalog.OpenAsync(profile, lease); }
        using var connection = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction();
        var expected = new List<ArchiveFilenameHit>();
        for (int i = 0; i < Total; i++)
        {
            ResourceId id = new(i % 2 == 0 ? "synthetic-A" : "synthetic-B", new[] { "g", "x", "y" }[i % 3], (1000 + i % 5).ToString());
            string postId = (2000 + i).ToString(); string name = Needle + "-" + i.ToString("D4") + ".png";
            string variant = i % 2 == 0 ? "preview" : "original";
            string? revision = i % 3 == 0 ? null : "synthetic-revision-" + i;
            bool deleted = i % 4 == 0;
            using (var thread = ArchiveSql.Command(connection, transaction, """
                INSERT OR IGNORE INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                VALUES($source,$board,$thread,'https://example.invalid/URL-ONLY-MARKER','synthetic','PATH-ONLY-MARKER',2,0,'2026-11-05T00:00:00.0000000+00:00',7)
                """))
            { ArchiveSql.AddId(thread, id); thread.ExecuteNonQuery(); }
            using (var post = ArchiveSql.Command(connection, transaction, """
                INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,is_deleted) VALUES($source,$board,$thread,$post,$ordinal,0,$deleted)
                """))
            {
                ArchiveSql.AddId(post, id); post.Parameters.AddWithValue("$post", postId);
                post.Parameters.AddWithValue("$ordinal", i); post.Parameters.AddWithValue("$deleted", deleted ? 1 : 0); post.ExecuteNonQuery();
            }
            using (var file = ArchiveSql.Command(connection, transaction, """
                INSERT INTO archive_attachments(source,board,thread,post,position,variant,original_name,planned_path,url,is_deleted,attachment_revision_key)
                VALUES($source,$board,$thread,$post,0,$variant,$name,'PATH-ONLY-MARKER','https://example.invalid/URL-ONLY-MARKER',$deleted,$revision)
                """))
            {
                ArchiveSql.AddId(file, id); file.Parameters.AddWithValue("$post", postId); file.Parameters.AddWithValue("$variant", variant);
                file.Parameters.AddWithValue("$name", name); file.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
                file.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value); file.ExecuteNonQuery();
            }
            expected.Add(new(id, postId, 0, revision, variant, name, deleted, deleted, 7));
        }
        transaction.Commit();
        return expected.OrderBy(hit => hit.Thread.Source, StringComparer.Ordinal).ThenBy(hit => hit.Thread.Board, StringComparer.Ordinal)
            .ThenBy(hit => hit.Thread.Thread, StringComparer.Ordinal).ThenBy(hit => hit.Post, StringComparer.Ordinal).ThenBy(hit => hit.Position).ToArray();
    }
}
