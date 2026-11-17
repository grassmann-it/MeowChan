using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Part of booru-workbench: in-memory PNG fixtures and ports only, no live network or local originals.
internal static class BooruResultViewerChecks
{
    internal static async Task RunAsync(Window owner)
    {
        BooruUiHarness h = new();
        h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("701"), BooruUiFixtures.Post("702"),
                BooruUiFixtures.Post("703", BooruOriginalAccess.Restricted, preview: false))));
        using BooruWorkbenchModel model = new(h.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        Window host = new() { Content = view, Width = 1000, Height = 760 };
        BooruResultViewerWindow? viewer = null;
        try
        {
            host.Show(owner);
            await view.ActivateAsync();
            view.ShowQuery("cat");
            await model.Search.SubmitAsync();
            await Program.WaitUntilAsync(() => model.Search.View.Rows.Count(row => row.Preview == BooruPreviewState.Ready) == 2);
            await SmokeDispatcher.SettleAsync();
            Click(Find<Button>(view, "BooruView:701"));
            viewer = host.OwnedWindows.OfType<BooruResultViewerWindow>().Single();
            await Program.WaitUntilAsync(() => Find<Image>(viewer, "GalleryImage").Source is Bitmap);
            Image image = Find<Image>(viewer, "GalleryImage");
            Require(image.Bounds.Width > 112 && image.Bounds.Height > 112, "The viewer did not enlarge the card preview.");
            Require(!Find<Control>(viewer, "GalleryOverlayControls").IsEffectivelyVisible,
                "Local-original gallery tools leaked into the Booru preview.");
            Require(h.Queue.Loads.Count == 0 && h.Search.Requests == 1 && h.Previews.Requests == 2,
                "Opening the preview fetched an original or repeated a provider request.");

            Send(image, Key.Space);
            Require(Find<CheckBox>(viewer, "BooruViewerSelect").IsChecked == true, "Space did not select the image.");
            Send(image, Key.Right);
            Require(viewer.CurrentPostId == "702" && Find<CheckBox>(viewer, "BooruViewerSelect").IsChecked == false,
                "Right did not advance independently of selection.");
            Send(image, Key.Enter);
            Send(image, Key.Left);
            Require(viewer.CurrentPostId == "701" && Find<CheckBox>(viewer, "BooruViewerSelect").IsChecked == true,
                "Selection was not retained when returning to the first image.");
            Send(image, Key.Left);
            Require(viewer.CurrentPostId == "701", "Previous wrapped past the first result.");
            Send(image, Key.Right);
            Send(image, Key.E, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
            await Program.WaitUntilAsync(() => h.Queue.Loads.Count == 1);
            Require(h.Queue.Loads.Single().PostId == "702", "The download key targeted a different image or the whole selection.");
            await Program.WaitUntilAsync(() => !Find<Button>(viewer, "BooruViewerDownload").IsEnabled);
            Send(image, Key.E, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
            Require(h.Queue.Loads.Count == 1, "Queued original was admitted twice.");
            Send(image, Key.Right);
            Require(viewer.CurrentPostId == "703" && !Find<Button>(viewer, "BooruViewerDownload").IsEnabled,
                "Restricted original was downloadable.");
            Send(image, Key.Right);
            Require(viewer.CurrentPostId == "703", "Next wrapped past the last result.");
            await SmokeDispatcher.SettleAsync();
            Require(Find<Image>(viewer, "GalleryImage").Source == null, "Missing preview retained the previous image.");
            Send(viewer, Key.Escape);
            await viewer.Cleanup;
            await SmokeDispatcher.SettleAsync();
            Require(!viewer.IsVisible && Find<CheckBox>(view, "BooruSelect:701").IsChecked == true
                && Find<CheckBox>(view, "BooruSelect:702").IsChecked == true,
                "Escape did not close or selection was lost from the result cards.");
            Require(host.FocusManager?.GetFocusedElement() is Control { Name: "BooruResult:703" },
                "Closing did not restore focus to the browsed result.");
            Click(Find<Button>(view, "BooruView:701"));
            viewer = host.OwnedWindows.OfType<BooruResultViewerWindow>().Single();
            Require(Find<CheckBox>(viewer, "BooruViewerSelect").IsChecked == true, "Reopening lost per-image selection.");
            Click(Find<Button>(viewer, "BooruViewerDownload"));
            await Program.WaitUntilAsync(() => h.Queue.Loads.Count == 2);
            Require(h.Queue.Loads.Last().PostId == "701", "Download button did not target the displayed result.");
            host.Content = null;
            await viewer.Cleanup;
            Require(!viewer.IsVisible, "Detaching the Booru page left a viewer alive.");
        }
        finally
        {
            viewer?.Close();
            if (viewer != null) await viewer.Cleanup;
            host.Content = null;
            host.Close();
        }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Send(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = source, Key = key, KeyModifiers = modifiers });
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
