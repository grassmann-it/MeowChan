using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;
internal static partial class PluginManagementChecks
{
    private static async Task CheckDelayedDiscardFocusAsync(MainWindow window, PluginManagementServices original, string candidateId)
    {
        DelayedReadProfileStore profile = new(original.ProfileStore);
        PluginManagementServices delayed = new(profile, original.Compiler, original.Inventory, original.PluginDirectory,
            original.DescribeTestAsync, original.StartTestWorkerAsync, original.ProbeAsync);
        PluginsPage page = new(); page.Attach(delayed);
        ContentControl host = window.FindControl<ContentControl>("PluginsPageHost")!; object? retained = host.Content; host.Content = page;
        try
        {
            OpenPlugins(window); await page.ActivateAsync(); page.Select(candidateId);
            await Program.WaitUntilAsync(() => page.CurrentDraft != null);
            var draft = page.CurrentDraft!;
            draft.Plugin.Set("label", "delayed discard raw");
            Button trigger = page.FindControl<Button>("PluginDiscard")!;
            // A freshly hosted page can have a current draft before its presenter
            // has a visual tree. A real user action requires the actual trigger.
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return trigger.IsAttachedToVisualTree() && trigger.IsEffectivelyVisible && trigger.IsEffectivelyEnabled
                    && trigger.Bounds.Width > 0 && trigger.Bounds.Height > 0;
            });
            await RevealAsync(window, trigger);
            Require(trigger.Focus(NavigationMethod.Tab) && trigger.IsFocused, "Delayed-discard trigger could not receive actual focus.");
            page.RequestDiscardSelectedDraft();
            PopConfirm popup = await WaitForOwnedDiscardPopupAsync(window, page, trigger);
            TaskCompletionSource gate = profile.HoldReads();
            popup.ConfirmCommand!.Execute(null);
            Require(page.IsBusy && !trigger.IsEnabled && ReferenceEquals(page.CurrentDraft, draft),
                "Discard completed before the deliberately delayed profile reload or did not lock its trigger.");
            gate.SetResult();
            await Program.WaitUntilAsync(() => !page.IsBusy && !ReferenceEquals(page.CurrentDraft, draft));
            await Program.WaitUntilAsync(() => trigger.IsEffectivelyVisible && trigger.IsEnabled && ReferenceEquals(window.FocusManager?.GetFocusedElement(), trigger));
            Require(trigger.IsAttachedToVisualTree() && trigger.Bounds.Width > 0 && trigger.Bounds.Height > 0,
                "Async discard returned focus to a detached or invisible trigger.");
            page.CurrentDraft!.Plugin.Set("label", "cancel keeps raw");
            page.RequestDiscardSelectedDraft();
            PopConfirm cancelPopup = await WaitForOwnedDiscardPopupAsync(window, page, trigger);
            cancelPopup.CancelCommand!.Execute(null);
            await Program.WaitUntilAsync(() => ReferenceEquals(window.FocusManager?.GetFocusedElement(), trigger));
            Require(page.CurrentDraft.Plugin.Text("label") == "cancel keeps raw" && !page.IsBusy,
                "Cancelling discard lost raw text or started a profile reload.");
        }
        finally { profile.ReleaseReads(); page.EndLifetime(); host.Content = retained; }
    }
    private static async Task<PopConfirm> WaitForOwnedDiscardPopupAsync(MainWindow window, PluginsPage page, Button trigger)
    {
        PopConfirm? surface = null;
        Popup? popup = null;
        try
        {
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                // Ownership follows the actual trigger wrapper, not a page-wide
                // logical flag or an unrelated confirmation in another popup root.
                surface = trigger.GetVisualAncestors().OfType<PopConfirm>().SingleOrDefault();
                popup = surface?.GetVisualDescendants().OfType<Popup>().SingleOrDefault(value => value.Name == PopConfirm.PART_Popup);
                if (!page.IsDiscardConfirmationOpen || surface?.IsDropdownOpen != true || !surface.IsAttachedToVisualTree()
                    || !surface.IsEffectivelyVisible || popup?.IsOpen != true || popup.Child is not Control child
                    || !child.IsAttachedToVisualTree() || !child.IsEffectivelyVisible || child.Bounds.Width <= 0 || child.Bounds.Height <= 0) return false;
                Button[] actions = child.GetVisualDescendants().OfType<Button>().Where(value =>
                    value.Name is PopConfirm.PART_ConfirmButton or PopConfirm.PART_CancelButton).ToArray();
                return actions.Length == 2 && actions.All(value => value.IsAttachedToVisualTree() && value.IsEffectivelyVisible
                    && value.IsEffectivelyEnabled && value.Bounds.Width > 0 && value.Bounds.Height > 0);
            });
        }
        catch (TimeoutException exception)
        {
            string diagnostic = "logicalOpen=" + page.IsDiscardConfirmationOpen + "; pageAttached=" + page.IsAttachedToVisualTree()
                + "; pageBounds=" + page.Bounds + "; triggerAttached=" + trigger.IsAttachedToVisualTree()
                + "; triggerVisible=" + trigger.IsEffectivelyVisible + "; triggerBounds=" + trigger.Bounds
                + "; triggerFocused=" + trigger.IsFocused + "; triggerParent=" + trigger.Parent?.GetType().Name
                + "; ownedSurface=" + surface?.GetType().Name + "; surfaceOpen=" + surface?.IsDropdownOpen
                + "; popupOpen=" + popup?.IsOpen + "; childBounds=" + popup?.Child?.Bounds;
            Console.WriteLine("SMOKE delayed discard popup: " + diagnostic);
            throw new TimeoutException("Actual owned discard popup did not materialize: " + diagnostic, exception);
        }
        return surface!;
    }
    private sealed class DelayedReadProfileStore(IPluginProfileStore inner) : IPluginProfileStore
    {
        private TaskCompletionSource? _gate;
        internal TaskCompletionSource HoldReads() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseReads() { _gate?.TrySetResult(); _gate = null; }
        public async Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { Task? wait = _gate?.Task; if (wait != null) await wait.WaitAsync(cancellationToken); return await inner.ReadAsync(cancellationToken); }
        public event Action<PluginLifecycleChange>? LifecycleChanged { add => inner.LifecycleChanged += value; remove => inner.LifecycleChanged -= value; }
        public event Action<PluginGrantChange>? GrantChanged { add => inner.GrantChanged += value; remove => inner.GrantChanged -= value; }
        public Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision, CancellationToken cancellationToken = default) => inner.ObserveCandidateAsync(candidate, expectedRevision, cancellationToken);
        public Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision, CancellationToken cancellationToken = default) => inner.ApplyStartupAsync(candidates, expectedRevision, cancellationToken);
        public Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision, CancellationToken cancellationToken = default) => inner.SelectCandidateAsync(pluginId, candidateId, expectedRevision, cancellationToken);
        public Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration, long expectedRevision, CancellationToken cancellationToken = default) => inner.SaveConfigurationAsync(pluginId, candidateId, configuration, expectedRevision, cancellationToken);
        public Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision, CancellationToken cancellationToken = default) => inner.SelectRollbackAsync(pluginId, expectedRevision, cancellationToken);
        public Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default) => inner.SetEnabledAsync(pluginId, enabled, expectedRevision, cancellationToken);
        public Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope, FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default) => inner.SetGrantAsync(pluginId, filterKey, scope, access, expectedRevision, cancellationToken);
        public Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem, long expectedRevision, CancellationToken cancellationToken = default) => inner.ReportActivationFailureAsync(pluginId, executionRevision, problem, expectedRevision, cancellationToken);
    }
}
