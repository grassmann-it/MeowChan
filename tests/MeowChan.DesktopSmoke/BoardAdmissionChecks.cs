using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class BoardAdmissionChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        const string board = "https://boards.4chan.org/g/";
        const string other = "https://8kun.top/tech/";
        string[] urls = [board, other];
        int previews = 0;
        BoardAdmissionPolicy saved = new([], new Dictionary<string, ThreadAdmissionException>
        { [board + "thread/2"] = ThreadAdmissionException.Exclude });
        Task<BoardAdmissionPreview> Preview(string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); previews++;
            return Task.FromResult(new BoardAdmissionPreview(url, "Offline fixture", saved, new BoardCatalogThread[] {
                new("1", url + "thread/1", "Cats and dogs", "A readable opening post"),
                new("2", url + "thread/2", "Dogs", "No cats here"),
                new("3", url + "thread/3", "Removed thread", "cat", Suppressed: true) }));
        }
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            var modal = new BoardAdmissionWindow(urls, _ => saved, Preview, showWizard: true);
            Task<BoardAdmissionSelection?> result = modal.ShowDialog<BoardAdmissionSelection?>(owner);
            await Program.WaitUntilAsync(() => modal.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "BoardAdmissionApply" && button.IsEnabled));
            var tabs = modal.GetVisualDescendants().OfType<TabControl>().Single();
            if (tabs.ItemCount != 2) throw new InvalidOperationException("Pasted boards are not grouped in one modal.");
            int loadedPreviews = previews;
            Button add = modal.GetVisualDescendants().OfType<Button>().First(button => button.Name == "BoardAdmissionAddRule");
            Click(add);
            TextBox search = modal.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "BoardAdmissionRuleText");
            search.Text = "cat";
            TextBlock summary = modal.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "BoardAdmissionSummary");
            await Program.WaitUntilAsync(() => summary.Text == Texts.T("wizard.summary", 0, 3));
            var list = modal.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "BoardAdmissionPreview");
            // Catalog rows are virtualized. Scroll to the removed item and await
            // realization instead of assuming every catalog row has a visual.
            list.ScrollIntoView(list.Items[2]!);
            CheckBox? Removed() => modal.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(check => check.Name == "BoardAdmissionInclude"
                && check.Content is TextBlock text && text.Text!.Contains("#3", StringComparison.Ordinal));
            await Program.WaitUntilAsync(() => Removed() != null);
            CheckBox removed = Removed()!;
            removed.IsChecked = true;
            await Program.WaitUntilAsync(() => summary.Text == Texts.T("wizard.summary", 1, 2));
            Program.CaptureWindow(modal, "smoke-board-admission-" + language + ".png");
            if (previews != loadedPreviews) throw new InvalidOperationException("Rule changes unexpectedly reloaded the catalog.");
            Click(modal.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BoardAdmissionCancel"));
            if (await result != null) throw new InvalidOperationException("Cancel committed a detached wizard draft.");
        }
        Texts.SetLanguage("en");
        await CheckRemovedMatchesAsync(owner);
        await BoardAdmissionContractChecks.RunAsync(owner);
    }
    private static async Task CheckRemovedMatchesAsync(MainWindow owner)
    {
        const string board = "https://boards.4chan.org/g/";
        var policy = new BoardAdmissionPolicy([new(ThreadTextField.Title, ThreadTextAction.Include, "/dpt/")]);
        Task<BoardAdmissionPreview> Preview(string url, CancellationToken token) => Task.FromResult(new BoardAdmissionPreview(url, "Technology", policy, [
            new("1", board + "thread/1", "Sticky", string.Join('\n', Enumerable.Repeat("Long sticky text", 30))),
            new("2", board + "thread/2", "/dpt/ - Daily Programming Thread", "What are you working on?", Suppressed: true),
            new("3", board + "thread/3", "/dpt/ - Daily Programming Thread", "Previous thread", Suppressed: true),
            new("4", board + "thread/4", "/sqt/", "Programming: >>>/g/dpt")]));
        var modal = new BoardAdmissionWindow([board], _ => policy, Preview, showWizard: true);
        Task<BoardAdmissionSelection?> result = modal.ShowDialog<BoardAdmissionSelection?>(owner);
        await Program.WaitUntilAsync(() => modal.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "BoardAdmissionApply" && button.IsEnabled));
        TextBlock Matches() => modal.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "BoardAdmissionMatches");
        await Program.WaitUntilAsync(() => Matches().Text == Texts.T("wizard.matches", 2, 2));
        ListBox preview = modal.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "BoardAdmissionPreview");
        object[] removed = UdBoardVirtualizedRows.ExactRemovedTargets(preview, board);
        await UdBoardVirtualizedRows.RealizeAsync(modal, preview, removed[0], "initial-excluded");
        await UdBoardVirtualizedRows.RealizeAsync(modal, preview, removed[1], "initial-excluded");
        CheckBox first = await UdBoardVirtualizedRows.RealizeAsync(modal, preview, removed[0], "before-include");
        UdBoardVirtualizedRows.PointerClick(modal, first, removed[0]);
        await Program.WaitUntilAsync(() => Matches().Text == Texts.T("wizard.matches", 2, 1));
        if (first.IsChecked != true || !UdBoardVirtualizedRows.Included(removed[0]) || UdBoardVirtualizedRows.Included(removed[1]) || !first.IsFocused)
            throw new InvalidOperationException("Exact row2 click did not include only row2 and retain checkbox focus.");
        UdBoardVirtualizedRows.Trace(modal, preview, removed[0], first, "after-include");
        CheckBox second = await UdBoardVirtualizedRows.RealizeAsync(modal, preview, removed[1], "before-include");
        UdBoardVirtualizedRows.PointerClick(modal, second, removed[1]);
        await Program.WaitUntilAsync(() => Matches().Text == Texts.T("wizard.matches", 2, 0)
            && UdBoardVirtualizedRows.Included(removed[0]) && UdBoardVirtualizedRows.Included(removed[1]) && second.IsChecked == true && second.IsFocused);
        UdBoardVirtualizedRows.Trace(modal, preview, removed[1], second, "after-include");
        // Restore only row3's excluded state to retain the original separate bulk-reinclude contract.
        second = await UdBoardVirtualizedRows.RealizeAsync(modal, preview, removed[1], "before-restoring-bulk-precondition");
        UdBoardVirtualizedRows.PointerClick(modal, second, removed[1]);
        await Program.WaitUntilAsync(() => Matches().Text == Texts.T("wizard.matches", 2, 1)
            && UdBoardVirtualizedRows.Included(removed[0]) && !UdBoardVirtualizedRows.Included(removed[1]) && second.IsChecked == false && second.IsFocused);
        UdBoardVirtualizedRows.Trace(modal, preview, removed[1], second, "restored-original-bulk-precondition");
        Button reinclude = modal.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BoardAdmissionReinclude");
        if (!reinclude.IsEnabled) throw new InvalidOperationException("Matching excluded threads cannot be reactivated together.");
        Click(reinclude);
        await Program.WaitUntilAsync(() => Matches().Text == Texts.T("wizard.matches", 2, 0) && !reinclude.IsEnabled);
        Program.CaptureWindow(modal, "smoke-board-admission-removed-matches.png");
        Click(modal.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BoardAdmissionApply"));
        var accepted = await result ?? throw new InvalidOperationException("Explicit reactivation was not accepted.");
        if (accepted.Reactivations == null || accepted.Reactivations[board].Length != 2)
            throw new InvalidOperationException("Matching removed thread did not retain its explicit reactivation.");
        if (!accepted.Reactivations[board].OrderBy(url => url, StringComparer.Ordinal)
            .SequenceEqual(new[] { board + "thread/2", board + "thread/3" }))
            throw new InvalidOperationException("Bulk acceptance did not retain exactly the two clicked row identities2/3.");
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
