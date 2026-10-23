using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

// Fixture setup owns only the initial draft. Every edit below uses the actual
// styled trailing TextBox, including the first focus before a later Loaded turn.
internal static class RuleExtensionsLifetimeChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!;
        object? retained = host.Content;
        RuleExtensionsInput probe = new() { IsVisible = false };
        ExtensionTagDraft initial = new() { Rest = "image/jpeg" }; // Explicit fixture setup.
        probe.Bind(initial);
        try
        {
            host.Content = probe;
            await FlushAsync();
            probe.IsVisible = true;
            TextBox? input = null;
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                input = probe.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                return input?.IsAttachedToVisualTree() == true && input.IsEffectivelyVisible && input.Bounds.Width > 0;
            });
            Require(input!.Focus(NavigationMethod.Tab), "Late-materialized rule input could not take real focus.");
            // No dispatcher yield between first real focus and the first actual edit.
            input.Text = "JPG, WebM";
            Require(initial.Tags.SequenceEqual(new[] { ".jpg", ".webm" }) && initial.Rest == "",
                "First focused edit did not synchronously reach the tag draft: " + initial.Fingerprint);
            await FlushAsync();
            Require(initial.Tags.SequenceEqual(new[] { ".jpg", ".webm" }) && initial.Rest == "",
                "Later Loaded processing erased the first focused edit.");

            TextBox oldInput = input;
            host.Content = new Border();
            await FlushAsync();
            ExtensionTagDraft next = new() { Rest = "retained/invalid" }; // Explicit owner-switch fixture.
            probe.Bind(next);
            oldInput.Text = "detached-editor-must-not-edit-current-owner";
            probe.FlushPendingText();
            oldInput.Text = "detached-flush-must-not-resubscribe-old-editor";
            Require(next.Rest == "retained/invalid" && next.Tags.Count == 0,
                "Detached rule editor retained a subscription to a new draft owner.");
            host.Content = probe;
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                input = probe.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                return input?.IsAttachedToVisualTree() == true && input.IsEffectivelyVisible;
            });
            Require(input!.Focus(NavigationMethod.Tab) && input.Text == "retained/invalid",
                "Reattached editor did not restore its current owner's exact raw rest.");
            input.Text = "image/avif";
            await FlushAsync();
            Require(next.Rest == "image/avif", "Reattached first raw edit was lost or normalized destructively.");
            // Explicit fixture: a disabled owner's invalid rest is bound before
            // its trailing editor materializes, as in template creation on A.
            RuleExtensionsInput disabled = new() { IsVisible = false, IsEnabled = false };
            ExtensionTagDraft disabledDraft = new() { Rest = "image/jpeg" };
            disabled.Bind(disabledDraft); host.Content = disabled;
            await FlushAsync(); disabled.IsVisible = true;
            TextBox? disabledInput = null;
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                disabledInput = disabled.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                return disabledInput?.IsAttachedToVisualTree() == true && disabledInput.IsEffectivelyVisible;
            });
            string? beforeFlush = disabledInput!.Text;
            disabled.FlushPendingText();
            Require(disabledDraft.Rest == "image/jpeg" && disabledInput.Text == beforeFlush,
                "Unfocused disabled editor flushed uninitialized text over its owner's invalid rest, or rendered during Flush.");
            // Normal owner rebind on enable restores presentation, not a raw edit.
            disabled.IsEnabled = true; disabled.Bind(disabledDraft);
            Require(disabledInput.Focus(NavigationMethod.Tab) && disabledInput.Text == "image/jpeg",
                "Disabled late editor did not restore the current owner's retained raw rest.");
            disabledInput.Text = "";
            disabled.FlushPendingText();
            Require(disabledDraft.Rest == "", "An explicitly cleared initialized editor failed to commit empty raw text.");
            Console.WriteLine("SMOKE rule tags: first focus, late load, detached Flush ownership, disabled late Flush and explicit clear checks passed through actual TextBox edits.");
        }
        finally { host.Content = retained; await FlushAsync(); }
    }
    private static async Task FlushAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
