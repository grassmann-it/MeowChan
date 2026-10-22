using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class BoardAdmissionContractChecks
{
    private const string Board = "https://boards.4chan.org/g/";
    private const string Other = "https://8kun.top/tech/";
    private static readonly string[] Urls = [Board, Other];

    internal static async Task RunAsync(MainWindow owner)
    {
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            await CheckScopeAsync(owner);
            await CheckFailureAndValidationAsync(owner);
            await CheckCancelAsync(owner, escape: false);
            await CheckCancelAsync(owner, escape: true);
        }
        Texts.SetLanguage("en");
        Console.WriteLine("SMOKE board admission contract: active draft only; exceptions/reactivations retained; explicit joint apply; loading/error/foreign empty-rule guards; empty catalog valid; retry retains drafts; Cancel/Escape discard future-direct setting.");
    }

    private static BoardAdmissionPolicy Saved(string url) => new(
        [new(ThreadTextField.Title, ThreadTextAction.Include, url == Board ? "cat" : "dog")],
        new Dictionary<string, ThreadAdmissionException>(StringComparer.Ordinal)
        {
            [url + "thread/2"] = ThreadAdmissionException.Exclude,
            [url + "thread/off-catalog"] = ThreadAdmissionException.Include
        });

    private static BoardAdmissionPreview Preview(string url) => new(url, "Offline catalog fixture", Saved(url),
    [
        new("1", url + "thread/1", "cat and dog", "First thread"),
        new("2", url + "thread/2", "other", "Individually excluded"),
        new("3", url + "thread/3", "cat and dog", "Explicitly reactivate me", Suppressed: true)
    ]);

    private static async Task CheckScopeAsync(MainWindow owner)
    {
        int previews = 0;
        Dictionary<string, BoardAdmissionPolicy> saved = Urls.ToDictionary(url => url, Saved);
        BoardAdmissionWindow modal = new(Urls, url => saved[url], (url, _) =>
        {
            previews++;
            return Task.FromResult(Preview(url));
        }, showWizard: true);
        Task<BoardAdmissionSelection?> result = modal.ShowDialog<BoardAdmissionSelection?>(owner);
        await Program.WaitUntilAsync(() => Find<Button>(modal, "BoardAdmissionApply").IsEnabled);
        foreach (int index in new[] { 0, 1 })
        {
            await SelectTabAsync(modal, index);
            Click(Find<Button>(modal, "BoardAdmissionReinclude"));
            await Program.WaitUntilAsync(() => Find<TextBlock>(modal, "BoardAdmissionMatches").Text == Texts.T("wizard.matches", 2, 0));
        }
        await SelectTabAsync(modal, 0);
        Button clear = Find<Button>(modal, "BoardAdmissionClearTextRules");
        Require(AutomationProperties.GetName(clear) == Texts.T("wizard.clearTextRules")
            && AutomationProperties.GetHelpText(clear) == Texts.T("wizard.clearTextRulesHint"), "Local clear action lacks its localized accessible scope.");
        clear.Focus(NavigationMethod.Tab);
        clear.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Program.WaitUntilAsync(() => !modal.GetVisualDescendants().OfType<TextBox>().Any(text => text.Name == "BoardAdmissionRuleText"));
        Require(!result.IsCompleted && modal.IsVisible && ReferenceEquals(modal.FocusManager!.GetFocusedElement(), clear),
            "Clearing rules closed the dialog or lost keyboard focus.");
        Require(previews == 2, "Local clear requested another catalog.");
        await SelectTabAsync(modal, 1);
        Require(Find<TextBox>(modal, "BoardAdmissionRuleText").Text == "dog", "Clearing the first board changed another board's rule.");
        await SelectTabAsync(modal, 0);
        Require(!modal.GetVisualDescendants().OfType<TextBox>().Any(text => text.Name == "BoardAdmissionRuleText"), "Tab switch restored cleared rules.");
        Click(Find<Button>(modal, "BoardAdmissionApply"));
        BoardAdmissionSelection accepted = await result ?? throw new InvalidOperationException("Explicit joint apply returned no selection.");
        Require(accepted.Policies.Count == 2 && !accepted.DirectFuture && accepted.Policies[Board].Rules.Count == 0
            && accepted.Policies[Other].Rules.SequenceEqual(saved[Other].Rules), "Joint apply changed the wrong drafts or future default.");
        foreach (string url in Urls)
        {
            Require(accepted.Policies[url].Exceptions[url + "thread/2"] == ThreadAdmissionException.Exclude
                && accepted.Policies[url].Exceptions[url + "thread/off-catalog"] == ThreadAdmissionException.Include
                && accepted.Policies[url].Exceptions[url + "thread/3"] == ThreadAdmissionException.Include
                && accepted.Reactivations![url].SequenceEqual([url + "thread/3"]), "Clear/apply lost individual exceptions or explicit reactivations.");
            Require(saved[url].Rules.Count == 1 && saved[url].Exceptions.Count == 2, "The dialog mutated an input policy.");
        }
    }

    private static async Task CheckFailureAndValidationAsync(MainWindow owner)
    {
        int requests = 0;
        TaskCompletionSource<BoardAdmissionPreview> firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<BoardAdmissionPreview> retryAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BoardAdmissionWindow modal = new(Urls, Saved, (url, _) => url == Board ? Task.FromResult(Preview(url))
            : ++requests == 1 ? firstAttempt.Task : retryAttempt.Task, showWizard: true);
        Task<BoardAdmissionSelection?> result = modal.ShowDialog<BoardAdmissionSelection?>(owner);
        await Program.WaitUntilAsync(() => requests == 1);
        Click(Find<Button>(modal, "BoardAdmissionReinclude"));
        Click(Find<Button>(modal, "BoardAdmissionClearTextRules"));
        Button apply = Find<Button>(modal, "BoardAdmissionApply");
        Require(!apply.IsEnabled, "A pending preview permitted joint apply.");
        Click(apply); // Even a direct routed command must not bypass the actual draft guard.
        Require(!result.IsCompleted, "Apply handler accepted an unloaded draft.");
        await SelectTabAsync(modal, 1);
        TextBox prepared = Find<TextBox>(modal, "BoardAdmissionRuleText");
        prepared.Text = "prepared while loading";
        firstAttempt.SetException(new IOException("Controlled catalog failure"));
        await Program.WaitUntilAsync(() => Find<Button>(modal, "BoardAdmissionRetry").IsVisible);
        Require(!apply.IsEnabled && Find<TextBlock>(modal, "BoardAdmissionStatus").Text!.Contains("Controlled catalog failure", StringComparison.Ordinal)
            && Find<TextBlock>(modal, "BoardAdmissionSummary").Text == string.Empty
            && Find<Button>(modal, "BoardAdmissionCancel").IsEnabled, "Failure did not retain retry/cancel or suggested a valid empty preview.");
        Program.CaptureWindow(modal, "smoke-board-admission-error-" + Texts.Language + ".png");
        Click(Find<Button>(modal, "BoardAdmissionRetry"));
        await Program.WaitUntilAsync(() => requests == 2);
        Require(prepared.Text == "prepared while loading" && !apply.IsEnabled, "Retry discarded rules or enabled premature apply.");
        retryAttempt.SetResult(new(Other, "Empty successful fixture", Saved(Other), []));
        await Program.WaitUntilAsync(() => apply.IsEnabled);
        Require(Find<TextBlock>(modal, "BoardAdmissionStatus").Text == Texts.T("wizard.empty")
            && prepared.Text == "prepared while loading", "Empty successful catalog was invalid or retry replaced the edited rule.");
        prepared.Text = string.Empty;
        await Program.WaitUntilAsync(() => !apply.IsEnabled);
        await SelectTabAsync(modal, 0);
        Click(Find<Button>(modal, "BoardAdmissionClearTextRules"));
        Click(apply);
        Require(!apply.IsEnabled && !result.IsCompleted && requests == 2, "Local clear bypassed another draft's invalid rule.");
        await SelectTabAsync(modal, 1);
        prepared.Text = "prepared while loading";
        await Program.WaitUntilAsync(() => apply.IsEnabled);
        Click(apply);
        BoardAdmissionSelection accepted = await result ?? throw new InvalidOperationException("Successful retry could not be accepted.");
        Require(accepted.Policies[Other].Rules.Single().Contains == "prepared while loading"
            && accepted.Policies[Other].Exceptions.Count == 2
            && accepted.Reactivations![Board].SequenceEqual([Board + "thread/3"]), "Retry/joint apply lost prepared rules, exceptions or reactivations.");
    }

    private static async Task CheckCancelAsync(MainWindow owner, bool escape)
    {
        int watches = owner.UrlList.Count, boards = owner.BoardsList.Count;
        bool showWizard = Config.LoadConfig().ShowBoardAdmissionWizard;
        BoardAdmissionWindow modal = new(Urls, Saved, (url, _) => Task.FromResult(Preview(url)), showWizard: true);
        Task<BoardAdmissionSelection?> result = modal.ShowDialog<BoardAdmissionSelection?>(owner);
        await Program.WaitUntilAsync(() => Find<Button>(modal, "BoardAdmissionApply").IsEnabled);
        Click(Find<Button>(modal, "BoardAdmissionReinclude"));
        Click(Find<Button>(modal, "BoardAdmissionClearTextRules"));
        Find<CheckBox>(modal, "BoardAdmissionDirectFuture").IsChecked = true;
        await SelectTabAsync(modal, 1);
        Find<TextBox>(modal, "BoardAdmissionRuleText").Text = "discard me";
        if (escape) modal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        else Click(Find<Button>(modal, "BoardAdmissionCancel"));
        Require(await result == null && owner.UrlList.Count == watches && owner.BoardsList.Count == boards
            && Config.LoadConfig().ShowBoardAdmissionWizard == showWizard, "Cancel/Escape committed watches or the future-direct setting.");
    }

    internal static async Task CheckPresentationMatrixAsync(MainWindow owner)
    {
        string originalMode = Config.LoadConfig().WorkspaceMode;
        string originalLanguage = Texts.Language;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        Size originalSize = owner.Bounds.Size;
        WindowState originalState = owner.WindowState;
        try
        {
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(owner, mode);
                owner.WindowState = WindowState.Normal;
                owner.Width = mode == "Barebones" ? 640 : 760;
                owner.Height = mode == "Barebones" ? 480 : 500;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(owner, owner.Width, owner.Height));
                foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (string language in new[] { "en", "de" })
                {
                    Application.Current.RequestedThemeVariant = theme;
                    Texts.SetLanguage(language);
                    BoardAdmissionWindow modal = new(Urls, Saved, (url, _) => Task.FromResult(Preview(url)), showWizard: true)
                    { Width = 640, Height = 600 };
                    Task<BoardAdmissionSelection?> result = modal.ShowDialog<BoardAdmissionSelection?>(owner);
                    await Program.WaitUntilAsync(() => Find<Button>(modal, "BoardAdmissionApply").IsEnabled);
                    modal.UpdateLayout();
                    Button clear = Find<Button>(modal, "BoardAdmissionClearTextRules");
                    Require(AutomationProperties.GetName(Find<TextBox>(modal, "BoardAdmissionRuleText")) == Texts.T("wizard.ruleText")
                        && AutomationProperties.GetName(Find<Button>(modal, "BoardAdmissionApply")) == Texts.T("wizard.apply"), "Rule input or joint apply lacks a localized accessible name.");
                    Require(InWindow(modal, Find<Button>(modal, "BoardAdmissionApply"))
                        && InWindow(modal, Find<Button>(modal, "BoardAdmissionCancel"))
                        && InWindow(modal, Find<CheckBox>(modal, "BoardAdmissionDirectFuture")), "Minimum dialog clips its commit/cancel controls.");
                    clear.BringIntoView();
                    clear.Focus(NavigationMethod.Tab);
                    await Program.WaitUntilAsync(() => ReferenceEquals(modal.FocusManager!.GetFocusedElement(), clear) && InWindow(modal, clear));
                    string stem = "smoke-board-admission-minimum-" + mode.ToLowerInvariant() + "-" + theme.Key!.ToString()!.ToLowerInvariant() + "-" + language;
                    Program.CaptureWindow(modal, stem + "-before.png");
                    clear.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                    await Program.WaitUntilAsync(() => !modal.GetVisualDescendants().OfType<TextBox>().Any(text => text.Name == "BoardAdmissionRuleText"));
                    Require(!result.IsCompleted && ReferenceEquals(modal.FocusManager.GetFocusedElement(), clear), "Minimum dialog lost local edit/focus semantics.");
                    Program.CaptureWindow(modal, stem + "-after.png");
                    ListBox list = Find<ListBox>(modal, "BoardAdmissionPreview");
                    list.ScrollIntoView(list.Items[0]!);
                    await Program.WaitUntilAsync(() => modal.GetVisualDescendants().OfType<CheckBox>().Any(check => check.Name == "BoardAdmissionInclude"));
                    CheckBox row = modal.GetVisualDescendants().OfType<CheckBox>().First(check => check.Name == "BoardAdmissionInclude");
                    row.Focus(NavigationMethod.Tab); row.BringIntoView();
                    await Program.WaitUntilAsync(() => InWindow(modal, row) && ReferenceEquals(modal.FocusManager.GetFocusedElement(), row));
                    Require(InWindow(modal, Find<Button>(modal, "BoardAdmissionApply")), "Scrolling to the preview moved joint apply out of view.");
                    Program.CaptureWindow(modal, stem + "-preview.png");
                    Click(Find<Button>(modal, "BoardAdmissionCancel"));
                    Require(await result == null, "Presentation cancellation returned a selection.");
                    Console.WriteLine($"SMOKE board dialog minimum: mode={mode}, theme={theme}, language={language}, actual={modal.Bounds.Size}; keyboard local edit and footer reachable.");
                }
            }
        }
        finally
        {
            Texts.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
            await PresentationChecks.SwitchModeAsync(owner, originalMode);
            owner.Width = originalSize.Width; owner.Height = originalSize.Height; owner.WindowState = originalState;
        }
    }

    private static async Task SelectTabAsync(BoardAdmissionWindow modal, int index)
    {
        Find<TabControl>(modal, "BoardAdmissionTabs").SelectedIndex = index;
        await Program.WaitUntilAsync(() =>
        {
            modal.UpdateLayout();
            return modal.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "BoardAdmissionClearTextRules");
        });
    }

    private static T Find<T>(Window modal, string name) where T : Control => modal.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static bool InWindow(Window modal, Control control)
    {
        Point? origin = control.TranslatePoint(default, modal);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && origin.HasValue
            && origin.Value.X >= -1 && origin.Value.Y >= -1
            && origin.Value.X + control.Bounds.Width <= modal.Bounds.Width + 1
            && origin.Value.Y + control.Bounds.Height <= modal.Bounds.Height + 1;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
