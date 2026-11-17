using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

public partial class PluginsPage
{


    /// <summary>
    /// Installed → Enabled → Active (after restart) as three chips and the one next action as a sentence. A pure display of the
    /// facts the status line and buttons already use: it adds no action, so approval, consent, activation, SDK transition and
    /// migration keep their own buttons and confirmations.
    /// </summary>
    private void RenderLifecycle(PluginCatalogEntry entry, PluginEntryPresentation presentation)
    {
        RenderLifecycleSteps(C<WrapPanel>("PluginStepChips"), entry, presentation);
        PluginLifecycleView view = PluginLifecycleSteps.Create(entry, presentation);
        string? next = view.Next switch
        {
            PluginNextAction.FileMissing => Texts.T("plugins.step.next.missing", Texts.T("plugins.add")),
            PluginNextAction.FixProblem => Texts.T("plugins.step.next.fix"),
            PluginNextAction.RestartToRegister => Texts.T("plugins.step.next.restart"),
            PluginNextAction.Enable => Texts.T("plugins.step.next.enable", CommandCatalog.Label("plugins.enable")),
            PluginNextAction.SelectBuild => Texts.T("plugins.step.next.select", CommandCatalog.Label("plugins.select-candidate")),
            PluginNextAction.RestartToUse => Texts.T("plugins.step.next.restartSaved"),
            PluginNextAction.ApproveSdkTransition => Texts.T("plugins.step.next.sdk", Texts.T("plugins.sdkTransition.action")),
            // A running build that fails with sdk-mismatch does not wait "because it comes from another folder": name the whole route.
            PluginNextAction.ReviewRepair when presentation.HasSdkMismatch => Texts.T("plugins.step.next.sdkMismatch", Texts.T("plugins.repair.review")),
            PluginNextAction.ReviewRepair => Texts.T("plugins.step.next.review", Texts.T("plugins.repair.review")),
            PluginNextAction.SavedChangesApplyAtRestart => Texts.T("plugins.step.next.saved"),
            _ => null
        };
        TextBlock line = C<TextBlock>("PluginLifecycleNext");
        line.Text = next ?? string.Empty;
        line.IsVisible = next != null;
        AutomationProperties.SetName(line, next ?? string.Empty);
    }
    private static void RenderLifecycleSteps(WrapPanel panel, PluginCatalogEntry entry, PluginEntryPresentation presentation)
    {
        if (panel.Children.Count == 0)
        {
            Border[] chips = new[] { "PluginStepInstalled", "PluginStepEnabled", "PluginStepActive" }
                .Select(name => WorkspaceChips.CreateBadge(name, string.Empty)).ToArray();
            foreach (Border chip in chips) panel.Children.Add(chip);
        }
        PluginLifecycleView view = PluginLifecycleSteps.Create(entry, presentation);
        string[] names = new[]
        {
            Texts.T("plugins.step.installed"), Texts.T("plugins.step.enabled"),
            Texts.T(view.ActiveNow ? "plugins.step.active" : "plugins.step.activeRestart")
        };
        string signature = Texts.Language + "|" + string.Join("|", view.Steps.Select((step, index) => step.State + ":" + names[index]));
        if (panel.Tag as string == signature) return;
        panel.Tag = signature;
        string[] spoken = new string[3];
        for (int index = 0; index < 3; index++)
        {
            PluginStepState state = view.Steps[index].State;
            Border chip = (Border)panel.Children[index];
            var label = new TextBlock { Text = names[index], Classes = { "chip-label" } };
            var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(state == PluginStepState.Problem ? WorkspaceIcons.Create(WorkspaceIcons.SeverityWarning)
                : CommandBindings.NamedIcon(state switch { PluginStepState.Done => "check", PluginStepState.Next => "arrow-right", _ => "circle" }));
            content.Children.Add(label);
            chip.Child = content;
            chip.Classes.Set("chip-step-done", state == PluginStepState.Done);
            chip.Classes.Set("chip-step-next", state == PluginStepState.Next);
            chip.Classes.Set("chip-warning", state == PluginStepState.Problem);
            chip.Classes.Set("chip-neutral", state == PluginStepState.Later);
            spoken[index] = Texts.T("plugins.step.name", index + 1, 3, names[index], Texts.T(state switch
            {
                PluginStepState.Done => "plugins.step.state.done",
                PluginStepState.Next => "plugins.step.state.next",
                PluginStepState.Problem => "plugins.step.state.problem",
                _ => "plugins.step.state.later"
            }));
            // The text leaf carries the spoken step/state; the drawn icon is not a font character.
            AutomationProperties.SetName(label, spoken[index]);
        }
        AutomationProperties.SetName(panel, Texts.T("plugins.step.group") + ": " + string.Join("; ", spoken));

    }

}
