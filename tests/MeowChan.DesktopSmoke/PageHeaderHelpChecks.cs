using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan.DesktopSmoke;

internal static class PageHeaderHelpChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        await using WorkViewBooruChecks booru = new(window);
        await booru.InitializeAsync();
        foreach (string language in new[] { "en", "de" })
        {
            await FooterNavigationChecks.PersistLanguageAsync(window, language);
            await SmokeNavigation.GoToAsync(window, MainWindow.WorkspacePage.Sources);
            SourcesPage sources = (SourcesPage)window.FindControl<ContentControl>("SourcesPageHost")!.Content!;
            await sources.ActivateAsync(SourcesPluginsFilter.Sources);
            await CheckSizesAsync(window, "sources", "PluginsMoreButton", "PluginsHelpButton");
            await sources.ActivateAsync(SourcesPluginsFilter.AllPlugins);
            await CheckSizesAsync(window, "plugins", "PluginsMoreButton", "PluginsHelpButton");

            await WorkViewBooruChecks.EnterAsync(window);
            // Loaded local watchers and catalog usage supply More entries in Barebones.
            await window.BooruPageForTests!.ActivateAsync();
            await CheckSizesAsync(window, "booru", "BooruMore", BooruWorkbenchView.SearchHelpName,
                BooruWorkbenchView.OpenWatchersName);
        }
    }

    private static async Task CheckSizesAsync(MainWindow window, string page, string moreId, string helpId, string? watchersId = null)
    {
        await Program.WaitUntilAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled && HasAutomationId(button, moreId)),
            () => $"{page}: More did not become usable.");
        foreach (double width in new[] { 1380, Math.Ceiling(window.MinWidth) })
        {
            await WorkViewCaptures.ResizeAsync(window, width, Math.Max(600, Math.Ceiling(window.MinHeight)));
            window.UpdateLayout();
            Check(window, page, moreId, helpId, watchersId);
            Program.CaptureWindow(window, $"smoke-header-help-{page}-{Texts.Language}-{width:0}.png");
        }
    }

    private static void Check(MainWindow window, string page, string moreId, string helpId, string? watchersId)
    {
        HelpButton help = window.GetVisualDescendants().OfType<HelpButton>()
            .Single(button => button.IsEffectivelyVisible && HasAutomationId(button, helpId));
        PageHeader header = help.GetVisualAncestors().OfType<PageHeader>().First();
        Button[] buttons = header.GetVisualDescendants().OfType<Button>().ToArray();
        Button more = buttons.Single(button => HasAutomationId(button, moreId));
        Button? watchers = watchersId == null ? null : buttons.Single(button => HasAutomationId(button, watchersId));
        Panel actions = header.Children.OfType<Panel>().Single(panel => PageHeader.GetRole(panel) == PageHeaderRole.Actions);

        Rect helpBounds = BoundsIn(help, header);
        Button rightmost = watchers ?? help;
        Rect rightmostBounds = BoundsIn(rightmost, header);
        Require(helpBounds.Width > 0, $"{page}: Help has no width.");
        Require(rightmost.IsEffectivelyVisible && rightmostBounds.Width > 0 && Math.Abs(header.Bounds.Width - rightmostBounds.Right) <= 0.5,
            $"{page}: {rightmost.Name} {rightmostBounds} is off the right edge of the {header.Bounds.Width} dip header.");
        Button lastAction = watchers == null ? more : help;
        Require(ReferenceEquals(actions.Children[^1], lastAction), $"{page}: {lastAction.Name} is no longer the final action.");
        Require(Array.IndexOf(buttons, more) < Array.IndexOf(buttons, help), $"{page}: More follows Help in the visual tree.");
        Require(more.IsEffectivelyVisible, $"{page}: More disappeared on resize.");

        Rect moreBounds = BoundsIn(more, header);
        Require(moreBounds.Right <= helpBounds.Left + 0.5 && moreBounds.Center.Y <= helpBounds.Center.Y + 0.5,
            $"{page}: More {moreBounds} and Help {helpBounds} are out of visual order.");
        if (watchers != null)
        {
            Require(Array.IndexOf(buttons, help) < Array.IndexOf(buttons, watchers), $"{page}: Help follows Watchers in the visual tree.");
            Require(helpBounds.Right <= rightmostBounds.Left + 0.5,
                $"{page}: Help {helpBounds} and Watchers {rightmostBounds} are out of visual order.");
        }

        IFocusManager focus = window.FocusManager!;
        IInputElement? previous = focus.GetFocusedElement();
        try
        {
            Require(more.Focus() && focus.TryMoveFocus(NavigationDirection.Next) && help.IsFocused,
                $"{page}: Tab from More did not reach Help.");
            if (watchers != null)
            {
                Require(focus.TryMoveFocus(NavigationDirection.Next) && watchers.IsFocused,
                    $"{page}: Tab from Help did not reach Watchers.");
                Require(focus.TryMoveFocus(NavigationDirection.Previous) && help.IsFocused,
                    $"{page}: Shift+Tab from Watchers did not reach Help.");
            }
            Require(focus.TryMoveFocus(NavigationDirection.Previous) && more.IsFocused,
                $"{page}: Shift+Tab from Help did not reach More.");
        }
        finally { previous?.Focus(); }
    }

    private static bool HasAutomationId(Control control, string id) =>
        ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId() == id;

    private static Rect BoundsIn(Control control, PageHeader header) => new(
        control.TranslatePoint(default, header) ?? throw new InvalidOperationException($"{control.Name} is outside the header."),
        control.Bounds.Size);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
