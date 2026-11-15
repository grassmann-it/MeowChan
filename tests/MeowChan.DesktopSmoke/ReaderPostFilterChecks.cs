using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// Post filters (plan P2 part B) in a private profile with a detached reader: "Post filters…" in the View menu, a rule as
// placeholder, highlight and hide (with replies), "Hide post" with its replies from the post menu, a reveal of a hidden
// post, the quote preview note, the reply card note, "Show post again", the dialog, and the store after a reopen.
internal static partial class ReaderPostFilterChecks
{
    private const string ThreadId = "700";

    // 700 opening post; 701 "placeholder me", 702 "highlight me", 703 "remove me", 704 quotes 703, 705 quotes 701,
    // 706 manual target, 707 quotes 706, 708 quotes 707, 709.. filler.
    private static string Quote(string target) => "<a href=\"#p" + target + "\" class=\"quotelink\">&gt;&gt;" + target + "</a> ";

    private static CapturedPost[] Posts() => Enumerable.Range(0, 30).Select(index =>
    {
        string id = (700 + index).ToString();
        string html = index switch
        {
            1 => "<p>Please placeholder me.</p>",
            2 => "<p>Highlight me, I am important.</p>",
            3 => "<p>Remove me, I am spam.</p>",
            4 => Quote("703") + "a reply to the spam",
            5 => Quote("701") + "a reply to the placeholder",
            6 => "<p>Hide me by hand.</p>",
            7 => Quote("706") + "reply to the hand-hidden post",
            8 => Quote("707") + "reply to that reply",
            _ => "<p>Post filter filler " + id + ".</p>"
        };
        return new CapturedPost(id, index, PostFieldPresence.All, PostedAt: DateTimeOffset.UnixEpoch.AddMinutes(index), Html: html);
    }).ToArray();

    private sealed class StoreService(PostFilterStore store) : IPostFilterService
    {
        public Task<PostFilterSnapshot> LoadAsync(CancellationToken token) => Task.Run(() => store.Load(), token);
        public Task<PostFilterSnapshot> RecoverAsync(long revision, CancellationToken token) => Task.Run(() => store.Recover(revision, token), token);
        public Task<PostFilterSnapshot> AddRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) =>
            Task.Run(() => store.AddRule(id, draft, revision, token), token);
        public Task<PostFilterSnapshot> ReplaceRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) =>
            Task.Run(() => store.ReplaceRule(id, draft, revision, token), token);
        public Task<PostFilterSnapshot> SetRuleEnabledAsync(Guid id, bool enabled, long revision, CancellationToken token) =>
            Task.Run(() => store.SetRuleEnabled(id, enabled, revision, token), token);
        public Task<PostFilterSnapshot> RemoveRuleAsync(Guid id, long revision, CancellationToken token) => Task.Run(() => store.RemoveRule(id, revision, token), token);
        public Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, long revision, CancellationToken token) =>
            Task.Run(() => store.SetHiddenPostsAction(action, revision, token), token);
        public Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, DateTimeOffset added, long revision, CancellationToken token) =>
            Task.Run(() => store.HidePost(thread, post, replies, added, revision, token), token);
        public Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, long revision, CancellationToken token) =>
            Task.Run(() => store.UnhidePost(thread, post, revision, token), token);
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-post-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        ArchivePage? page = null;
        Window? window = null;
        try
        {
            ResourceId id = new("4chan", "g", ThreadId);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + ThreadId, "Post filter " + ThreadId,
                ThreadSourceState.Active, DateTimeOffset.UtcNow, profile, Posts()));
            PostFilterSession filters = new(new StoreService(new PostFilterStore(profile, lease)));
            await filters.LoadAsync();
            page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            page.PostFilters = filters;
            Control pane = page.DetachReader();
            window = new Window { Width = 1440, Height = 900, Content = pane, Title = "Private post filters" };
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow[] Rows() => list.ItemsSource?.Cast<ArchivePostRow>().ToArray() ?? [];
            ArchivePostRow? Row(string post) => Rows().FirstOrDefault(row => row.Id == post);
            await Program.WaitUntilAsync(() => !page.IsBusy && Rows().Length == 30, () => "rows " + Rows().Length);
            Require(page.Projection is { IsIdentity: true }, "An empty filter store changed the view.");

            // "Post filters…" is a plain action row of the settings popover (RL1; formerly an entry of the View menu).
            Require(page.ReaderViewOptionIds.Contains("reader.post-filters"), "Post filters… is not a view option.");
            QuickSettingsSurface popover = await ReaderSettingsProbe.OpenAsync(page);
            Require(ReaderSettingsProbe.Row(popover, "reader.post-filters") is Button { IsEffectivelyVisible: true, IsEnabled: true },
                "Post filters… is not an enabled action row of the settings popover.");
            await ReaderSettingsProbe.CloseAsync(page);

            async Task SettleAsync()
            {
                await Program.WaitUntilAsync(() => !page.IsProjectionPending, () => "post filter view pending");
                await page.WaitForReaderViewAsync();
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            }

            // Placeholder, highlight and hide with replies.
            await filters.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "PLACEHOLDER me", PostFilterAction.Stub, false));
            await filters.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "highlight me", PostFilterAction.Highlight, false, "4chan", "g"));
            await filters.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "remove me", PostFilterAction.Hide, true, "4chan"));
            await SettleAsync();
            Require(Row("701") is { IsStub: true } stub && stub.StubText.Contains("PLACEHOLDER me"), "The placeholder rule did not apply: " + Row("701")?.StubText);
            Require(Row("702") is { IsHighlighted: true, IsStub: false }, "The highlight rule did not apply.");
            Require(Row("703") == null && Row("704") == null, "The hide rule with replies left a post.");
            Require(Row("705") is { IsStub: false }, "A reply to a placeholder was hidden without “replies”.");
            Require(page.Projection is { HiddenCount: 2, StubCount: 1 }, "Counts " + page.Projection?.HiddenCount + "/" + page.Projection?.StubCount);
            Require(page.FindControl<TextBlock>("ArchiveReaderStatus")!.Text!.Contains(Texts.Count("reader.view.hiddenCount", 3, 3)), "The status does not count the hidden posts.");
            await CaptureThemesAsync(window, "rules");

            // The quote preview of a hidden post says so; a reply card says "hidden".
            ArchiveQuoteRow quote = Row("705")!.Quotes.Single();
            Require(page.QuotePreviewRow == null, "A preview is open before the check.");
            ArchiveQuoteRow hiddenQuote = new(new ArchiveQuote(new ArchivePostId(id, "704"), new ArchivePostId(id, "703"), ">>703", null));
            page.RequestQuotePreview(hiddenQuote, list, default, immediate: true);
            await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "quote preview not open");
            Require(page.QuotePreviewShowsFilterNote, "The quote preview of a hidden post has no note.");
            page.CloseQuotePreview();
            page.RequestQuotePreview(quote, list, default, immediate: true);
            await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "second quote preview not open");
            Require(page.QuotePreviewShowsFilterNote, "The quote preview of a placeholder has no note.");
            page.CloseQuotePreview();

            // An explicit jump reveals the hidden post.
            await page.RevealPostAsync("703");
            await SettleAsync();
            Require(Row("703") is { IsRevealed: true }, "Revealing the hidden post failed.");

            // "Hide post and its replies" from the post menu; default action: placeholder.
            ArchivePostRow target = Row("706")!;
            ContextMenu menu = page.BuildPostMenu(target, list);
            Require(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "post.hide")), "The post menu has no Hide post.");
            MenuItem withReplies = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "post.hide-replies"));
            withReplies.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await page.PostFilterTask;
            await SettleAsync();
            Require(filters.IsHidden(id, "706"), "Hide post did not store the post.");
            Require(Row("706") is { IsStub: true } hidden && hidden.StubText.Contains(Texts.T("postFilter.reason.single")), "The hand-hidden post is no placeholder.");
            Require(Row("707") is { IsStub: true } && Row("708") is { IsStub: true }, "Its replies were not hidden transitively.");
            await CaptureThemesAsync(window, "hidden");

            // A reply card of a hidden reply shows "hidden" instead of its text (the archive counts it).
            ArchivePostRow parent = Row("706")!;
            await Program.WaitUntilAsync(() => parent.BacklinkCount != null || parent.HasRepliesError, () => "reply count of 706 missing");
            await page.SetRepliesExpandedAsync(parent, true);
            await Program.WaitUntilAsync(() => parent.Replies.Count > 0 || parent.HasRepliesError, () => "reply cards of 706 missing");
            Require(parent.Replies.Any(card => card.ReplyExcerpt == Texts.T("postFilter.hiddenReply")), "A hidden reply card still quotes the post.");

            // "Show post again".
            menu = page.BuildPostMenu(Row("706")!, list);
            Require(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "post.unhide")), "The post menu has no Show post again.");
            Require(await page.UnhidePostAsync(id, "706"), "Show post again failed.");
            await SettleAsync();
            Require(Row("706") is { IsStub: false } && Row("707") is { IsStub: false }, "The post is still hidden.");

            // The dialog lists the rules and closes again.
            Require(await page.HidePostAsync(id, "709", replies: false) && filters.IsHidden(id, "709"), "709 was not hidden.");
            Task dialog = page.OpenPostFiltersAsync();
            await Program.WaitUntilAsync(() => page.PostFilterWindow is { IsVisible: true } shown && !shown.Model.IsBusy, () => "post filter dialog not open");
            PostFilterDialog window2 = page.PostFilterWindow!;
            Require(window2.Model.Rules.Count == 3 && window2.Model.HiddenPosts.Count == 1, "The dialog does not list the store.");
            await Dispatcher.UIThread.InvokeAsync(window2.UpdateLayout, DispatcherPriority.Background);
            foreach ((ThemeVariant variant, string suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                await Dispatcher.UIThread.InvokeAsync(window2.UpdateLayout, DispatcherPriority.Background);
                Program.CaptureWindow(window2, "smoke-reader-p2-post-filter-dialog-" + suffix + ".png");
            }
            window2.Close();
            await dialog;

            // Everything is in the file: a fresh store reads it back.
            PostFilterSnapshot reopened = new PostFilterStore(profile, lease).Load();
            Require(reopened.Rules.Count == 3 && reopened.HiddenPosts.Count == 1 && reopened.HiddenPosts[0].Post == "709",
                "The store did not keep the rules and the hidden post.");
            // The HTML export reads the archive directly: the thread there still has every post.
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(profile, CancellationToken.None))
                Require((await reader.ReadThreadAsync(id, CancellationToken.None))?.PostCount == 30, "The archive lost posts.");
            Console.WriteLine("SMOKE reader post filter: View menu entry, placeholder, highlight, hide with replies, quote preview note, reveal, hide post with replies, reply card note, show again, dialog and store reopen passed.");
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = theme;
            page?.EndLifetime();
            window?.Close();
            await catalog.DisposeAsync();
            lease.Dispose();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CaptureThemesAsync(Window window, string name)
    {
        foreach ((ThemeVariant variant, string suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Program.CaptureWindow(window, "smoke-reader-p2-post-filter-" + name + "-" + suffix + ".png");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
