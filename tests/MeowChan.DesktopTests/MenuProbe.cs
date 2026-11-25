using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

internal static class MenuProbe
{
    // W112-21 supplies this internal accessor. Resolve it when present so this test helper also runs before that merge.
    private static readonly Func<MenuItem, string?>? ReadIconKey = typeof(CommandBindings).Assembly
        .GetType("MeowChan.Desktop.Controls.MenuIcons")?
        .GetMethod("KeyOf", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(MenuItem)], null)?
        .CreateDelegate<Func<MenuItem, string?>>();

    internal static IReadOnlyList<Entry> Entries(ContextMenu menu) => ReadEntries(menu);

    private static List<Entry> ReadEntries(ItemsControl menu)
    {
        List<Entry> entries = [];
        int section = 0;
        foreach (object? row in menu.Items)
        {
            if (row is Separator) section++;
            if (row is not MenuItem item) continue;
            string? id = item.Tag as string ?? CommandBindings.ActionId(item) ?? AutomationProperties.GetAutomationId(item);
            bool enabled = item.IsEffectivelyEnabled;
            string? reason = enabled ? null : AutomationProperties.GetHelpText(item);
            entries.Add(new(id, section, enabled, reason, ReadIconKey?.Invoke(item), item, ReadEntries(item)));
        }
        return entries;
    }

    internal sealed record Entry(string? Id, int Section, bool Enabled, string? DisabledReason, string? IconKey,
        MenuItem Item, IReadOnlyList<Entry> Children)
    {
        internal bool HasIcon(string key) => ReadIconKey != null ? IconKey == key
            : Item.Icon is Border { OpacityMask: ImageBrush { Source: { } mask } } && ReferenceEquals(mask, CommandBindings.IconMask(key));

        internal void Activate() => ContextGestures.Click(Item);
    }
}
