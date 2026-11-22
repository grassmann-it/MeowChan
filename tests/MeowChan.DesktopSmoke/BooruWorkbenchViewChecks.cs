using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Additional BP1 acceptance hook for the parent harness. Deliberately no navigation/registry changes:
// call await BooruWorkbenchViewChecks.RunAsync(owner) on the smoke UI thread after normal startup.
// Fake ports only; preview bytes are never produced or decoded by these checks.
internal static class BooruWorkbenchViewChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(Window owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        BooruUiHarness h = new();
        h.Previews.OnPreview = _ => null;
        h.Watchers.Seed(BooruUiFixtures.Watcher("bp1", "cat", clock: h.Clock));
        using BooruWorkbenchModel model = new(h.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        Window host = new() { Width = 1000, Height = 760, Content = view };
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pending = null;
        Exception? primaryFailure = null;
        try
        {
            Require(h.ProviderRequests == 0 && h.Previews.Requests == 0 && h.Watchers.Calls.Count == 0,
                "Constructing an unshown view read a port.");
            host.Show(owner);
            await view.ActivateAsync().WaitAsync(Deadline);
            await view.ActivateAsync().WaitAsync(Deadline);
            await SettleAsync();
            Require(h.Watchers.Calls.SequenceEqual(new[] { "list" }) && h.ProviderRequests == 0 && h.Previews.Requests == 0,
                "Activation did more than one local watcher-list read.");
            Require(Clock(view).IsEnabled, "Attached active view did not start its render-only clock.");

            view.ShowQuery("cat blue_eyes");
            Require(model.Search.View.Input == "cat blue_eyes" && h.ProviderRequests == 0,
                "ShowQuery lost its unlaid-out field value or sent a request.");
            Require(view.TryClearQuery() && !view.TryClearQuery() && model.Search.View.Input == string.Empty,
                "Clear must empty the draft once and leave it empty.");
            view.AddTagToSearch("cat");
            Require(model.Search.View.Input == "cat" && h.ProviderRequests == 0,
                "Adding a source tag must change only the draft.");

            TabControl tabs = Find<TabControl>(view, BooruWorkbenchView.TabsName);
            object? searchTab = tabs.SelectedItem;
            Find<Button>(view, BooruWorkbenchView.OpenWatchersName).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            model.Watchers.RequestRemoval("bp1");
            await Program.WaitUntilAsync(() => Has(view, BooruWorkbenchView.RemoveCancelPrefix + "bp1"),
                () => "Removal question did not render in the embedded host.");
            Require(view.TryCancelRemoval(), "Removal Escape hook did not take the safe answer.");
            await SettleAsync();
            Require(!Has(view, BooruWorkbenchView.RemoveConfirmPrefix + "bp1") && h.Watchers.Calls.SequenceEqual(new[] { "list" }),
                "Cancelling a removal changed the watcher port.");
            tabs.SelectedItem = searchTab;

            // A provider result arrives after detaching, while no Window owns the view. Reattaching
            // must preserve it, not dispose the model, repeat initialization or send another query.
            h.Search.OnSearch = async (query, page, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true,
                    BooruUiFixtures.Post("812", preview: false)));
            };
            pending = model.Search.SubmitAsync();
            await entered.Task.WaitAsync(Deadline);
            host.Content = null;
            Require(!Clock(view).IsEnabled && !host.GetVisualDescendants().Contains(view),
                "Detaching failed to suspend the clock or remove the view.");
            Require(Subscribed(model.Search, view) && Subscribed(model.Watchers, view),
                "Transient detach lost model subscriptions.");
            release.TrySetResult();
            await pending.WaitAsync(Deadline);
            await SettleAsync();
            Require(model.Search.View.Rows.Single().Post.PostId == "812", "A late result was lost while detached.");
            host.Content = view;
            await SettleAsync();
            Require(Has(view, "BooruResult:812") && Clock(view).IsEnabled, "Reattachment did not render the late result or resume the clock.");
            Require(h.Search.Requests == 1 && h.Previews.Requests == 0 && h.Queue.Loads.Count == 0
                && h.Watchers.Calls.SequenceEqual(new[] { "list" }), "Detach/reattach started unexpected work.");

            // Queue one render immediately before disposal too: that callback must see the closed guard.
            model.Search.SetInput("dog");
            host.Content = null;
            view.Dispose();
            view.Dispose();
            await SettleAsync();
            Require(!Clock(view).IsEnabled && !Subscribed(model.Search, view) && !Subscribed(model.Watchers, view),
                "Final Dispose left a clock or model subscription behind.");
            model.Search.SetInput("sky");
            Require(model.Search.View.Input == "sky" && model.Search.CanSubmit, "Disposing a borrowed model's view disposed the model.");

            using BooruWorkbenchView owned = new(h.Services);
            BooruWorkbenchModel ownedModel = owned.Model;
            owned.ShowQuery("cat");
            owned.Dispose();
            owned.Dispose();
            ownedModel.Search.SetInput("dog");
            Require(!ownedModel.Search.CanSubmit && ownedModel.Search.View.Input == "cat", "The owning view did not dispose its model.");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await SmokeCleanup.RunAsync(primaryFailure, async () =>
            {
                release.TrySetResult();
                try
                {
                    if (pending != null) await pending.WaitAsync(Deadline);
                }
                finally
                {
                    host.Content = null;
                    view.Dispose();
                    host.Close();
                    await SettleAsync();
                }
            }, failure => Console.Error.WriteLine("BP1 view cleanup: " + failure));
        }
    }

    private static DispatcherTimer Clock(BooruWorkbenchView view) =>
        (DispatcherTimer)typeof(BooruWorkbenchView).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static bool Subscribed(object model, object target) =>
        ((Delegate?)model.GetType().GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model))?
            .GetInvocationList().Any(handler => ReferenceEquals(handler.Target, target)) == true;

    private static bool Has(Control root, string name) => root.GetVisualDescendants().OfType<Control>().Any(control => control.Name == name);
    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
