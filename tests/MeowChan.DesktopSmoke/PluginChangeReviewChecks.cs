using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Opt-in synthetic native helper for the parent smoke registry. It deliberately does not
/// register itself: the catalog and dialog wiring must be registered by the parent first.
/// </summary>
internal static class PluginChangeReviewChecks
{
    internal static void AssertSyntheticEditorSurface(Window owner)
    {
        SettingsSchema schema = new("1", [new("limit", SettingScope.Plugin, SettingKind.Integer, "Limit", IsRequired: true)]);
        PluginSettingsDraft draft = new(schema, SettingScope.Plugin, new("1", [new("limit", SettingKind.Integer, Integer: 1)]));
        PluginSettingsEditor editor = new(draft, "synthetic-change-review");
        owner.Content = editor;
        owner.UpdateLayout();
        TextBox field = (TextBox)editor.FindField("limit")!;
        Require(field.IsEffectivelyVisible, "Synthetic change-review editor field is not visible.");
        Require(AutomationProperties.GetAutomationId(field)?.StartsWith("PluginField:", StringComparison.Ordinal) == true,
            "Synthetic change-review editor field has no stable automation identity.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
