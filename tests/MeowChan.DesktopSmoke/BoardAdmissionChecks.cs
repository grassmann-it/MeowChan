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
            CheckBox removed = modal.GetVisualDescendants().OfType<CheckBox>().First(check => check.Name == "BoardAdmissionInclude"
                && check.Content is TextBlock text && text.Text!.Contains("#3", StringComparison.Ordinal));
            removed.IsChecked = true;
            await Program.WaitUntilAsync(() => summary.Text == Texts.T("wizard.summary", 1, 2));
            Program.CaptureWindow(modal, "smoke-board-admission-" + language + ".png");
            if (previews != loadedPreviews) throw new InvalidOperationException("Rule changes unexpectedly reloaded the catalog.");
            Click(modal.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BoardAdmissionCancel"));
            if (await result != null) throw new InvalidOperationException("Cancel committed a detached wizard draft.");
        }
        Texts.SetLanguage("en");
        var all = new BoardAdmissionWindow(urls, _ => saved, Preview, showWizard: true);
        Task<BoardAdmissionSelection?> allResult = all.ShowDialog<BoardAdmissionSelection?>(owner);
        await Program.WaitUntilAsync(() => all.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "BoardAdmissionAll"));
        Click(all.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BoardAdmissionAll"));
        var accepted = await allResult ?? throw new InvalidOperationException("Monitor all did not return a selection.");
        if (accepted.DirectFuture || accepted.Policies.Count != 2 || accepted.Policies.Values.Any(policy => policy.Rules.Count != 0)
            || accepted.Policies[board].Exceptions[board + "thread/2"] != ThreadAdmissionException.Exclude)
            throw new InvalidOperationException("Monitor all lost exclusions or silently changed the future default.");
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
