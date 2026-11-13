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
    private Border[]? _lifecycleChips;

    /// <summary>
    /// Installed → Enabled → Active (after restart) as three chips and the one next action as a sentence. A pure display of the
    /// facts the status line and buttons already use: it adds no action, so approval, consent, activation, SDK transition and
    /// migration keep their own buttons and confirmations.
    /// </summary>
    private void RenderLifecycle(PluginCatalogEntry entry, PluginEntryPresentation presentation)
    {
        WrapPanel panel = C<WrapPanel>("PluginStepChips");
        if (_lifecycleChips == null)
        {
            _lifecycleChips = new[] { "PluginStepInstalled", "PluginStepEnabled", "PluginStepActive" }
                .Select(name => WorkspaceChips.CreateBadge(name, string.Empty)).ToArray();
            foreach (Border chip in _lifecycleChips) panel.Children.Add(chip);
        }
        PluginLifecycleView view = PluginLifecycleSteps.Create(entry, presentation);
        string[] names = new[]
        {
            Texts.T("plugins.step.installed"), Texts.T("plugins.step.enabled"),
            Texts.T(view.ActiveNow ? "plugins.step.active" : "plugins.step.activeRestart")
        };
        string[] spoken = new string[3];
        for (int index = 0; index < 3; index++)
        {
            PluginStepState state = view.Steps[index].State;
            Border chip = _lifecycleChips[index];
            ((TextBlock)chip.Child!).Text = state switch
            {
                PluginStepState.Done => "✓ ",
                PluginStepState.Next => "→ ",
                PluginStepState.Problem => "! ",
                _ => "○ "
            } + names[index];
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
        }
        AutomationProperties.SetName(panel, Texts.T("plugins.step.group") + ": " + string.Join("; ", spoken));

        string? next = view.Next switch
        {
            PluginNextAction.FileMissing => Texts.T("plugins.step.next.missing", Texts.T("plugins.add")),
            PluginNextAction.FixProblem => Texts.T("plugins.step.next.fix"),
            PluginNextAction.RestartToRegister => Texts.T("plugins.step.next.restart"),
            PluginNextAction.Enable => Texts.T("plugins.step.next.enable", CommandCatalog.Label("plugins.enable")),
            PluginNextAction.SelectBuild => Texts.T("plugins.step.next.select", CommandCatalog.Label("plugins.select-candidate")),
            PluginNextAction.RestartToUse => Texts.T("plugins.step.next.restartSaved"),
            PluginNextAction.ApproveSdkTransition => Texts.T("plugins.step.next.sdk", Texts.T("plugins.sdkTransition.action")),
            PluginNextAction.SavedChangesApplyAtRestart => Texts.T("plugins.step.next.saved"),
            _ => null
        };
        TextBlock line = C<TextBlock>("PluginLifecycleNext");
        line.Text = next ?? string.Empty;
        line.IsVisible = next != null;
        AutomationProperties.SetName(line, next ?? string.Empty);
    }
}
