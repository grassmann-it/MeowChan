using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

internal static class MenuIconAudit
{
    internal sealed record Exception(string MenuId, string EntryId, string Reason);

    internal static void Verify(string menuId, IEnumerable<Control> controls, params Exception[] allowlist)
    {
        VerifyEntries(menuId, Entries(controls), allowlist);
    }

    internal static void VerifyNative(string menuId, IEnumerable<(string Id, NativeMenuItem Item)> rows, params Exception[] allowlist)
    {
        VerifyEntries(menuId, rows.Where(row => row.Item.IsVisible)
            .Select(row => new Entry(row.Id, null, row.Item.Icon != null)), allowlist);
    }

    private sealed record Entry(string Id, Control? Icon, bool HasNativeIcon = false);

    private static IEnumerable<Entry> Entries(IEnumerable<Control> controls, string parent = "")
    {
        int index = 0;
        foreach (Control control in controls.Where(control => control.IsVisible))
        {
            if (control is Separator) continue;
            switch (control)
            {
                case MenuItem menu:
                    string id = parent + (menu.Tag as string ?? CommandBindings.ActionId(menu) ?? menu.Name
                        ?? (menu.ToggleType == MenuItemToggleType.Radio ? menu.GroupName + "." + index : "row." + index));
                    yield return new(id, menu.Icon as Control);
                    foreach (Entry entry in Entries(menu.Items.OfType<Control>(), id + "/")) yield return entry;
                    break;
                case Button { Tag: GalleryViewer.OverflowItem row }:
                    yield return new(parent + row.Id, row.Icon.Content as Control);
                    break;
                case Button { Content: Grid grid, Tag: string layoutId }:
                    ContentControl slot = Assert.Single(grid.Children.OfType<ContentControl>(), item => Grid.GetColumn(item) == 1);
                    yield return new(parent + layoutId, slot.Content as Control);
                    break;
            }
            index++;
        }
    }

    private static void VerifyEntries(string menuId, IEnumerable<Entry> rows, Exception[] allowlist)
    {
        var exceptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Exception exception in allowlist)
        {
            string address = exception.MenuId + "/" + exception.EntryId;
            Assert.Equal(menuId, exception.MenuId);
            Assert.False(string.IsNullOrWhiteSpace(exception.EntryId), address + ": missing entry ID");
            Assert.False(string.IsNullOrWhiteSpace(exception.Reason), address + ": missing exception reason");
            Assert.True(exceptions.TryAdd(exception.EntryId, exception.Reason), address + ": duplicate exception");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Entry row in rows)
        {
            string address = menuId + "/" + row.Id;
            Assert.True(ids.Add(row.Id), address + ": duplicate entry ID");
            string? icon = MenuIcons.KeyOf(row.Icon);
            if (row.HasNativeIcon || icon != null)
            {
                Assert.False(exceptions.ContainsKey(row.Id), address + ": exception has an icon now");
                if (icon != null)
                {
                    Assert.True(AssetLoader.Exists(new Uri(CommandBindings.IconUri(icon))), address + ": unknown mask " + icon);
                    if (row.Icon is Border border && border.IsAttachedToVisualTree())
                        Assert.NotNull((border.OpacityMask as ImageBrush)?.Source);
                }
            }
            else
            {
                Assert.True(exceptions.Remove(row.Id), address + ": missing action icon");
            }
        }
        Assert.NotEmpty(ids);
        Assert.True(exceptions.Count == 0, menuId + ": stale exceptions for " + string.Join(", ", exceptions.Keys));
    }
}
