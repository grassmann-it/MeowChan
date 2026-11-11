using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_PRIVATE_TAGS_ONLY. Real host, real store and actual page controls
// on a synthetic offline profile. Routed managed input is NOT OS keyboard input.
internal static class PrivateTagBarChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Tagged = new("4chan", "g", "1000");
    private static readonly ResourceId Plain = new("4chan", "g", "2000");

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint h && h != 0,
            "Private tags require a visible native application owner and real resources.");
        string language = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("private-tags-native-");
        try
        {
            new Config().SaveConfig(profile);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                await catalog.CaptureAsync(new CapturedThreadSnapshot(Tagged,
                    "https://boards.4chan.org/g/thread/1000", "/rpt/ - offline tag fixture",
                    ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile,
                    [new CapturedPost("1000", 0, PostFieldPresence.All, Html: "Welcome to <b>/RPT/</b> and /sdg/ general, see /usr/bin/env"),
                     new CapturedPost("1001", 1, PostFieldPresence.All, Html: "a reply naming /ignored/")]));
                await catalog.CaptureAsync(new CapturedThreadSnapshot(Plain,
                    "https://boards.4chan.org/g/thread/2000", "Plain offline fixture",
                    ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile,
                    [new CapturedPost("2000", 0, PostFieldPresence.All, Html: "no markers here")]));
            }
            Texts.SetLanguage("en");
            await JourneyAsync(owner, profile);
            Texts.SetLanguage("de");
            await RestartAndDamageAsync(owner, profile);
        }
        finally
        {
            Dispatcher.UIThread.VerifyAccess();
            Texts.SetLanguage(language);
            // Synthetic evidence retained, no user profile or global app setup touched.
        }
    }

    private sealed class Fixture(Window window, ArchivePage page, DesktopSessionHost host) : IAsyncDisposable
    {
        internal ArchivePage Page => page;
        internal DesktopSessionHost Host => host;
        internal T C<T>(string name) where T : Control => page.FindControl<T>(name)
            ?? throw new InvalidOperationException("Missing actual control " + name);
        internal void Click(string name) => Click(C<Button>(name));
        internal void Click(Button button)
        {
            Require(button.IsEffectivelyEnabled, "Disabled fixture action " + (button.Name ?? Label(button)));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        internal static string Label(Control control) => control is Button { Content: TextBlock text } ? text.Text ?? ""
            : control is Button button ? button.Content?.ToString() ?? "" : control is TextBlock block ? block.Text ?? "" : "";
        internal string[] Chips() => C<WrapPanel>("ReaderTagChips").Children.OfType<Button>().Select(Label).ToArray();
        internal Button[] Suggestions() => C<WrapPanel>("ReaderTagSuggestions").Children.OfType<Button>().ToArray();
        internal async Task SettleAsync()
        {
            await page.PrivateTagTask.WaitAsync(Deadline);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        internal PrivateTagStoreSnapshot Stored()
        {
            PrivateTagLoadSnapshot loaded = host.GetPrivateTagSnapshot();
            Require(loaded.State == PrivateTagLoadState.Ready, "Tag index is not readable: " + loaded.Error);
            return loaded.Snapshot!;
        }
        public async ValueTask DisposeAsync()
        {
            page.EndLifetime();
            window.Close();
            await host.DisposeAsync();
        }
    }

    private static async Task<Fixture> OpenAsync(MainWindow owner, string profile, ResourceId thread)
    {
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        ArchivePage page = new(host.OpenArchiveReaderAsync,
            (_, _) => throw new InvalidOperationException("No capture mutation permitted."),
            _ => throw new InvalidOperationException("No external launch permitted."));
        page.AttachPrivateTags(host);
        Window window = new() { Width = 1120, Height = 820, Content = page,
            Title = "Synthetic offline private tags", ShowInTaskbar = false };
        window.Show(owner);
        Require(window.TryGetPlatformHandle()?.Handle is nint h && h != 0, "Missing native fixture owner.");
        page.IsAvailable = true;
        await page.LoadThreadAsync(thread).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);
        Require(page.CurrentThread?.Summary.Id == thread, "Offline snapshot was not loaded through the catalog.");
        return new Fixture(window, page, host);
    }

    private static async Task JourneyAsync(MainWindow owner, string profile)
    {
        await using Fixture f = await OpenAsync(owner, profile, Tagged);
        Button open = f.C<Button>("ReaderTagsOpen");
        StackPanel bar = f.C<StackPanel>("ReaderTagBar");
        TextBox input = f.C<TextBox>("ReaderTagInput");
        Require(!bar.IsVisible && Equals(open.Content, "Tags") && open.IsEffectivelyEnabled, "Initial tag button state is wrong.");

        f.Click("ReaderTagsOpen");
        Require(bar.IsVisible && input.IsFocused, "Open did not show the bar and focus its input.");
        Require(f.Chips().Length == 0 && f.C<WrapPanel>("ReaderTagChips").Children.OfType<TextBlock>().Single().Text == Texts.T("tags.none"),
            "An untagged thread must say so.");
        // Title and first post only; duplicates collapse, paths and later posts are ignored.
        Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /rpt/", "+ /sdg/" }), "Unexpected suggestions.");
        Require(f.Stored().Tags.Count == 0 && !File.Exists(Path.Combine(profile, PrivateTagStore.FileName)),
            "Showing suggestions must not create or assign anything.");

        f.Click(f.Suggestions()[0]);
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(new[] { "/rpt/  ✕" }) && Equals(open.Content, "Tags (1)"), "Suggestion click did not tag the thread.");
        Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /sdg/" }), "An accepted suggestion must disappear.");
        PrivateTagStoreSnapshot stored = f.Stored();
        Require(stored.TagsOf(Tagged).Single().Name.Display == "/rpt/" && stored.TagsOf(Plain).Count == 0, "Store does not match the bar.");

        // Typed name with an underscore, committed with Enter from the text field.
        input.Focus();
        input.Text = "my_tag";
        var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        input.RaiseEvent(enter);
        Require(enter.Handled, "Enter in the tag field was not handled by the bar.");
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(new[] { "/rpt/  ✕", "my_tag  ✕" }), "Typed tag missing or underscore lost: " + string.Join("|", f.Chips()));
        Require(string.IsNullOrEmpty(input.Text) && input.IsFocused && Equals(open.Content, "Tags (2)"), "Add did not clear the field and keep focus.");

        Capture(f.Page, "bar");
        // The same name in another spelling is the same tag: no duplicate, no error.
        input.Text = "MY_TAG";
        f.Click("ReaderTagAdd");
        await f.SettleAsync();
        Require(f.Chips().Length == 2 && f.Stored().Tags.Count == 2, "Case variant created a duplicate tag.");

        long before = f.Stored().Revision;
        input.Text = "bad\tname";
        f.Click("ReaderTagAdd");
        await f.SettleAsync();
        TextBlock status = f.C<TextBlock>("ReaderTagStatus");
        Require(status.IsVisible && status.Text == Texts.T("tags.error.invalidName") && input.Text == "bad\tname",
            "Invalid name must be explained and the draft kept.");
        Require(f.Stored().Revision == before && f.Chips().Length == 2, "Invalid name changed the store.");
        Require(!status.Text!.Contains("bad", StringComparison.Ordinal), "Rejected input was echoed.");

        Button rpt = f.C<WrapPanel>("ReaderTagChips").Children.OfType<Button>().First();
        f.Click(rpt);
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(new[] { "my_tag  ✕" }) && Equals(open.Content, "Tags (1)"), "Chip click did not remove the tag.");
        Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /rpt/", "+ /sdg/" }), "Removed tag must be suggested again.");
        Require(f.Stored().Tags.Count == 2, "Removing an assignment must keep the tag itself.");

        // Applying a search clears the open thread; the bar hides with it rather than act on nothing.
        Require(await f.Page.ApplySearchSpecAsync(new ArchiveSearchSpec()), "List load failed.");
        Require(f.Page.CurrentThread == null && !bar.IsVisible && !open.IsEffectivelyEnabled, "Bar outlived its thread.");
        ArchiveThreadRow[] rows = f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().ToArray();
        ArchiveThreadRow taggedRow = rows.Single(row => row.Summary.Id == Tagged), plainRow = rows.Single(row => row.Summary.Id == Plain);
        Require(taggedRow.HasTags && taggedRow.TagsText == "Tags: my_tag" && !plainRow.HasTags, "List rows do not show thread tags.");
        // List rows follow changes made in the bar of the reopened thread.
        await f.Page.LoadThreadAsync(Tagged).WaitAsync(Deadline);
        await f.Page.MediaLoadTask.WaitAsync(Deadline);
        // The bar the user left open returns with the next opened thread, without taking focus.
        Require(bar.IsVisible && !input.IsFocused && f.Chips().SequenceEqual(new[] { "my_tag  \u2715" }), "Returned bar is not current.");
        f.Click(f.Suggestions()[1]);
        await f.SettleAsync();
        Require(taggedRow.TagsText == "Tags: my_tag, /sdg/" && !plainRow.HasTags, "List row did not follow the bar: " + taggedRow.TagsText);

        // Another thread: the bar follows it and carries neither draft nor message over.
        input.Text = "draft for the old thread";
        await f.Page.LoadThreadAsync(Plain).WaitAsync(Deadline);
        await f.Page.MediaLoadTask.WaitAsync(Deadline);
        Require(bar.IsVisible && f.Chips().Length == 0 && f.Suggestions().Length == 0 && string.IsNullOrEmpty(input.Text)
            && !status.IsVisible && Equals(open.Content, "Tags"), "Bar did not follow the thread switch cleanly.");
        Require(f.Stored().TagsOf(Plain).Count == 0, "Thread switch tagged the new thread.");

        input.Focus();
        var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        input.RaiseEvent(escape);
        Require(escape.Handled && !bar.IsVisible && open.IsFocused, "Escape did not close the bar and return focus.");
        Console.WriteLine("SMOKE private-tags journey [en]: suggestions, click-to-tag, typed Enter, case variant, invalid name, remove, list rows, thread switch, Escape checked.");
    }

    private static async Task RestartAndDamageAsync(MainWindow owner, string profile)
    {
        await using (Fixture f = await OpenAsync(owner, profile, Tagged))
        {
            // New host and page in German: persisted tags, localized labels.
            Button open = f.C<Button>("ReaderTagsOpen");
            Require(Equals(open.Content, Texts.T("tags.openCount", 2)), "Tags did not survive a restart: " + open.Content);
            f.Click("ReaderTagsOpen");
            Require(f.Chips().SequenceEqual(new[] { "my_tag  ✕", "/sdg/  ✕" }), "Restart lost or reordered tags.");
            Capture(f.Page, "bar-de");
            Require(Equals(f.C<Button>("ReaderTagAdd").Content, "Tag hinzufügen") && f.C<TextBox>("ReaderTagInput").Watermark == "Tagname",
                "German labels missing.");
            Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /rpt/" }), "Restart suggestions wrong.");
        }
        string file = Path.Combine(profile, PrivateTagStore.FileName);
        byte[] valid = File.ReadAllBytes(file);
        File.WriteAllText(file, "{bad");
        await using (Fixture f = await OpenAsync(owner, profile, Tagged))
        {
            f.Click("ReaderTagsOpen");
            TextBlock status = f.C<TextBlock>("ReaderTagStatus");
            Require(status.IsVisible && status.Text == Texts.T("tags.unavailable"), "Damaged index must be reported.");
            Require(!f.C<TextBox>("ReaderTagInput").IsEffectivelyEnabled && !f.C<Button>("ReaderTagAdd").IsEffectivelyEnabled
                && f.Chips().Length == 0 && f.Suggestions().Length == 0, "Damaged index must not be editable or look empty-but-usable.");
            Require(File.ReadAllText(file) == "{bad", "Damaged index was overwritten.");
        }
        File.WriteAllBytes(file, valid);
        Console.WriteLine("SMOKE private-tags restart [de]: persistence, German labels, damaged index read-only and preserved checked.");
    }

    // Optional visual evidence: MEOWCHAN_PRIVATE_TAGS_SCREENSHOTS names an existing directory.
    private static void Capture(Control control, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MEOWCHAN_PRIVATE_TAGS_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        control.UpdateLayout();
        Avalonia.PixelSize size = new((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height));
        if (size.Width <= 0 || size.Height <= 0) return;
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(size);
        bitmap.Render(control);
        bitmap.Save(Path.Combine(directory, "private-tags-" + name + ".png"));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
