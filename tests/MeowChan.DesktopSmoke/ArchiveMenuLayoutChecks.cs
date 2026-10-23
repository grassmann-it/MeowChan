using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveMenuLayoutChecks
{
    internal static async Task RunAsync(ArchivePage page, DataGrid table)
    {
        MainWindow window = (MainWindow)TopLevel.GetTopLevel(page)!;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string originalLanguage = Texts.Language;
        double originalWidth = window.Width;
        List<string> failures = new();
        try
        {
            // Keep both layouts in list-only mode. Switching from a wide card
            // preview to a narrow table otherwise intentionally keeps its reader.
            window.Width = 980;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 980) < 1);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Program.WaitUntilAsync(() => !page.IsBusy);
                foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current.RequestedThemeVariant = theme;
                    await Program.WaitUntilAsync(() => window.ActualThemeVariant == theme);
                    foreach (bool cards in new[] { false, true })
                    {
                        string scene = $"{language}-{theme.ToString().ToLowerInvariant()}-{(cards ? "cards" : "table")}-first";
                        Console.WriteLine("SMOKE archive menu opening " + scene);
                        string layout = cards ? "List" : "Table";
                        SmokeActionClick.Execute(page.FindControl<Button>(cards ? "ArchiveLayoutList" : "ArchiveLayoutTable")!);
                        Control list = cards ? page.FindControl<ListBox>("ArchiveThreads")! : table;
                        await Program.WaitUntilAsync(() => !page.IsBusy && list.IsEffectivelyVisible
                            && window.SessionHost.Config.GetLayoutProfile(window.SessionHost.Config.WorkspaceMode).ArchiveThreadLayout == layout);
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        ArchiveThreadRow[] rows = table.ItemsSource.Cast<ArchiveThreadRow>().Take(2).ToArray();
                        Control first = await RowAsync(window, list, rows[0]);
                        await SelectionChecks.PressAsync(window, first);
                        await Program.WaitUntilAsync(() => !page.IsBusy && table.SelectedItems.Count == 1);
                        Control second = await RowAsync(window, list, rows[1]);
                        await SelectionChecks.PressAsync(window, second, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
                        await Program.WaitUntilAsync(() => !page.IsBusy && table.SelectedItems.Count == 2);
                        // Exercise both product entry paths. Dark uses the routed
                        // keyboard request; Light uses a native selected-row right click.
                        if (theme == ThemeVariant.Dark)
                        {
                            list.Focus();
                            list.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                        }
                        else await SelectionChecks.PressAsync(window, second, right: true);
                        ContextMenu? menu = null;
                        await Program.WaitUntilAsync(() =>
                        {
                            menu = cards ? list.GetVisualDescendants().OfType<Control>()
                                .Select(control => control.ContextMenu).OfType<ContextMenu>().FirstOrDefault(candidate => candidate.IsOpen)
                                : table.ContextMenu;
                            return menu?.IsOpen == true;
                        });
                        try { await CheckBoundsAsync(menu!, scene); }
                        catch (TimeoutException) { failures.Add(scene); }
                        finally
                        {
                            CaptureMenu(menu!, scene);
                            menu!.Close();
                            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        }
                    }
                }
            }
        }
        finally
        {
            window.Width = originalWidth;
            Application.Current.RequestedThemeVariant = originalTheme;
            Texts.SetLanguage(originalLanguage);
            SmokeActionClick.Execute(page.FindControl<Button>("ArchiveLayoutTable")!);
            await Program.WaitUntilAsync(() => !page.IsBusy && table.IsEffectivelyVisible
                && window.SessionHost.Config.GetLayoutProfile(window.SessionHost.Config.WorkspaceMode).ArchiveThreadLayout == "Table");
        }
        if (failures.Count > 0) throw new InvalidOperationException("Archive menu bounds failed: " + string.Join(", ", failures));
    }

    private static async Task<Control> RowAsync(MainWindow window, Control list, ArchiveThreadRow row)
    {
        if (list is ListBox cards) return await SelectionChecks.CardForAsync(window, cards, row.Summary.Id);
        DataGrid table = (DataGrid)list;
        table.ScrollIntoView(row, null);
        await SelectionChecks.WaitRow(table, row);
        return SelectionChecks.Row(table, row);
    }

    internal static async Task CheckBoundsAsync(ContextMenu menu, string scene)
    {
        string geometry = string.Empty;
        try
        {
            await Program.WaitUntilAsync(() =>
            {
                menu.UpdateLayout();
                TopLevel? popup = TopLevel.GetTopLevel(menu);
                MenuItem[] items = menu.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();
                geometry = $"{scene}: menu={menu.Bounds}; popup={popup?.Bounds}; items={items.Length}; " +
                    string.Join("; ", items.Select(item => $"{(item.Header is TextBlock text ? text.Text : item.Header)}: {item.Bounds}, at={item.TranslatePoint(default, menu)}, visible={item.IsEffectivelyVisible}"));
                return items.Length >= 12 && menu.IsEffectivelyVisible && menu.Bounds.Width > 0 && menu.Bounds.Height > 0
                    && popup != null && items.All(item => Fits(item, menu) && Fits(item, popup));
            });
            Console.WriteLine("SMOKE archive menu full bounds PASS " + geometry);
        }
        catch (TimeoutException)
        {
            Console.WriteLine("SMOKE archive menu full bounds FAIL " + geometry);
            throw;
        }
    }

    private static void CaptureMenu(ContextMenu menu, string scene)
    {
        if (menu.Bounds.Width <= 0 || menu.Bounds.Height <= 0) return;
        double scale = TopLevel.GetTopLevel(menu)?.RenderScaling ?? 1;
        using RenderTargetBitmap image = new(PixelSize.FromSize(menu.Bounds.Size, scale), new Vector(96 * scale, 96 * scale));
        image.Render(menu);
        image.Save(Path.Combine(AppContext.BaseDirectory, "smoke-archive-menu-" + scene + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static bool Fits(Control item, Control container)
    {
        Point? origin = item.TranslatePoint(default, container);
        return item.IsEffectivelyVisible && item.Bounds.Width > 0 && item.Bounds.Height > 0 && origin.HasValue
            && origin.Value.X >= -1 && origin.Value.Y >= -1
            && origin.Value.X + item.Bounds.Width <= container.Bounds.Width + 1
            && origin.Value.Y + item.Bounds.Height <= container.Bounds.Height + 1;
    }
}
