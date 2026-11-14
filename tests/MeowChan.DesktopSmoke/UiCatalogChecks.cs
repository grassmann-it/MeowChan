using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Registry smoke "ui-catalog" (evidence run, not a check): the pages, page modes, overlays, popovers, flyouts and context menus of the REAL
// main window over the offline showcase archive (ShowcaseData, passive synthetic download rows, in-memory Booru ports), rendered per
// Light/Dark x EN/DE at one standard window size (pages also at the narrow minimum size) into one flat directory with manifest.json
// and ONE index.html grouped by the catalog areas (UiCatalog). Dialog windows come from the dialog-gallery run (MEOWCHAN_UI_CATALOG_DIALOGS).
// Every scene is isolated: a failure is listed under "Nicht erfasst" and the run goes on. Nothing is confirmed, saved or sent: every
// surface is closed again with its own cancel path; no network, no real profile.
//   MEOWCHAN_UI_CATALOG_OUTPUT   output directory (the registry run needs it)
//   MEOWCHAN_UI_CATALOG_SCENES   comma separated scene names (or prefixes ending in *) to run, default all
//   MEOWCHAN_UI_CATALOG_DIALOGS  output directory of a dialog-gallery run to merge into the index
//   MEOWCHAN_UI_CATALOG_ENTRIES  entries.json of the catalog (ids, areas, names, capture keys)
internal static partial class UiCatalogChecks
{
    internal const double StandardWidth = 1380, StandardHeight = 850, NarrowWidth = 760, NarrowHeight = 500;
    internal const string Standard = "standard 1380x850", Narrow = "narrow 760x500";

    private static readonly (string Language, string Theme)[] Cells = [("en", "Light"), ("en", "Dark"), ("de", "Light"), ("de", "Dark")];

    // A scene: a name, an optional cell restriction ("tier" scenes run in two cells only) and the run.
    private sealed record Scene(string Name, Func<MainWindow, Task> Run, bool TiersOnly = false, bool Once = false);

    private static string _baselineMode = "Simple";
    private static WorkViewBooruChecks? _booru;

    internal static async Task RunAsync(MainWindow window)
    {
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_UI_CATALOG_OUTPUT") is { Length: > 0 } configured
            ? configured : Path.Combine(AppContext.BaseDirectory, "ui-catalog");
        string[] filter = (Environment.GetEnvironmentVariable("MEOWCHAN_UI_CATALOG_SCENES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Wanted(string name) => filter.Length == 0 || filter.Any(f => f.EndsWith('*') ? name.StartsWith(f[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        Scene[] scenes = [.. BuildScenes().Where(scene => Wanted(scene.Name))];
        Console.WriteLine("SMOKE ui catalog scenes: " + string.Join(", ", scenes.Select(scene => scene.Name)));
        string languageBefore = window.SessionHost.Config.Language, themeBefore = window.SessionHost.Config.Theme;
        string modeBefore = window.SessionHost.Config.WorkspaceMode;
        _baselineMode = "Simple"; // the page images show the middle tier; the Barebones and Advanced images are the tier scenes
        UiCatalog.Begin(output);
        SyntheticShellRows? rows = null;
        _booru = null;
        try
        {
            await PresentationChecks.SwitchModeAsync(window, _baselineMode);
            rows = await SyntheticShellRows.CreateAsync(window, WorkViewCaptures.DownloadEntries());
            if (scenes.Any(scene => scene.Name.StartsWith("booru", StringComparison.Ordinal)))
            {
                _booru = new WorkViewBooruChecks(window);
                await _booru.InitializeAsync();
            }
            for (int cell = 0; cell < Cells.Length; cell++)
            {
                (string language, string theme) = Cells[cell];
                if (Texts.Language != language) await FooterNavigationChecks.PersistLanguageAsync(window, language);
                await WorkViewCaptures.SetThemeAsync(window, theme);
                UiCatalog.Theme = theme;
                await WorkViewCaptures.ResizeAsync(window, StandardWidth, StandardHeight);
                foreach (Scene scene in scenes)
                {
                    // Tier scenes show the layout, not colour or language: two cells (EN Light, DE Dark) cover both.
                    if (scene.Once && cell != 0) continue;
                    if (scene.TiersOnly && !(language == "en" && theme == "Light") && !(language == "de" && theme == "Dark")) continue;
                    try
                    {
                        await scene.Run(window);
                        Console.WriteLine("SMOKE ui catalog scene OK: " + scene.Name + " " + theme + "/" + language);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine("SMOKE ui catalog scene FAILED: " + scene.Name + " " + theme + "/" + language + ": " + exception);
                        UiCatalog.Skip(scene.Name, exception.GetType().Name + ": " + exception.Message.Split('\n')[0]);
                    }
                    await ResetAsync(window);
                }
            }
        }
        finally
        {
            List<Exception> cleanup = [];
            async Task Restore(Func<Task> step) { try { await step(); } catch (Exception exception) { cleanup.Add(exception); } }
            await Restore(() => ResetAsync(window));
            await Restore(() => PresentationChecks.SwitchModeAsync(window, modeBefore));
            await Restore(() => WorkViewCaptures.SetThemeAsync(window, themeBefore));
            await Restore(() => FooterNavigationChecks.PersistLanguageAsync(window, languageBefore));
            if (rows != null) await Restore(() => rows.DisposeAsync().AsTask());
            if (_booru != null) await Restore(() => _booru.DisposeAsync().AsTask());
            await Restore(() => { UiCatalog.Finish("source-built harness run " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC"); return Task.CompletedTask; });
            if (cleanup.Count > 0) Console.WriteLine("SMOKE ui catalog cleanup problems: " + string.Join(" | ", cleanup.Select(e => e.Message)));
        }
        Require(UiCatalog.ShotCount > 0, "The UI catalog captured nothing.");
    }

    // Back to the bare main window on the Downloads page at the standard size: overlays, windows, menus, tier and size are undone.
    internal static async Task ResetAsync(MainWindow window)
    {
        for (int round = 0; round < 3 && window.OwnedWindows.Count > 0; round++)
        {
            foreach (Window owned in window.OwnedWindows.ToArray()) { try { owned.Close(); } catch (InvalidOperationException) { } }
            await Task.Delay(100);
        }
        await WorkViewCaptures.LeaveAsync(window);
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
        if (window.SessionHost.Config.WorkspaceMode != _baselineMode) await PresentationChecks.SwitchModeAsync(window, _baselineMode);
        if (Math.Abs(window.ClientSize.Width - StandardWidth) > 1 || Math.Abs(window.ClientSize.Height - StandardHeight) > 1)
            await WorkViewCaptures.ResizeAsync(window, StandardWidth, StandardHeight);
        // Popups of an earlier scene must not outlive it.
        foreach (ContextMenu menu in window.GetVisualDescendants().OfType<Control>().Select(c => c.ContextMenu).OfType<ContextMenu>().Where(m => m.IsOpen).ToArray()) menu.Close();
        if (window.SelectedPage != MainWindow.WorkspacePage.Downloads)
        {
            WorkViewCaptures.Click(window.FindControl<Button>("DownloadsNavigationButton")!);
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
        }
        window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
        await Settle(window, 150);
    }

    // ---- Helpers shared by the scene files ----

    internal static async Task Settle(Control root, int milliseconds = 350)
    {
        for (int pass = 0; pass < 3; pass++)
            await Dispatcher.UIThread.InvokeAsync(() => (TopLevel.GetTopLevel(root) as Layoutable ?? root).UpdateLayout(), DispatcherPriority.Background);
        await Task.Delay(milliseconds);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    internal static async Task Enter(MainWindow window, string page)
    {
        await WorkViewCaptures.EnterAsync(window, page);
        await WorkViewCaptures.SettleAsync(window, page);
    }

    /// <summary>Standard-size image of the window and, when asked, the narrow one (the page may relayout and is re-entered after each resize).</summary>
    internal static async Task PageAsync(MainWindow window, string key, string note = "", bool narrow = true, Func<Task>? reenter = null)
    {
        await Settle(window, 250);
        UiCatalog.Record(window, key, Standard, note);
        if (!narrow) return;
        await WorkViewCaptures.ResizeAsync(window, Math.Max(NarrowWidth, Math.Ceiling(window.MinWidth)), Math.Max(NarrowHeight, Math.Ceiling(window.MinHeight)));
        if (reenter != null) await reenter();
        await Settle(window, 450);
        UiCatalog.Record(window, key, Narrow, note);
        await WorkViewCaptures.ResizeAsync(window, StandardWidth, StandardHeight);
        if (reenter != null) await reenter();
        await Settle(window, 250);
    }

    /// <summary>A step inside a multi-capture scene: a failure is a gap of that capture only and the scene goes on.</summary>
    internal static async Task StepAsync(string key, Func<Task> step)
    {
        try { await step(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine("SMOKE ui catalog step FAILED: " + key + ": " + exception.Message);
            UiCatalog.Skip(key, exception.GetType().Name + ": " + exception.Message.Split('\n')[0]);
        }
    }

    internal static T? Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    internal static T Need<T>(Control root, string name) where T : Control =>
        Find<T>(root, name) ?? throw new InvalidOperationException("Missing control " + name);

    internal static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static void Press(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    internal static KeyModifiers Primary => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    internal static async Task UntilAsync(Func<bool> condition, string message, int seconds = 12)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(25);
        }
    }

    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>The separate top-level (popup root) a control inside a flyout, menu or drop-down lives in; the control itself when it is in the window.</summary>
    internal static Control SurfaceOf(Control inside, Window window)
    {
        TopLevel? top = TopLevel.GetTopLevel(inside);
        return top is Control surface && !ReferenceEquals(top, window) && surface.Bounds.Width > 0 ? surface : inside;
    }

    /// <summary>Opens a context menu the keyboard way (no pointer position), renders it as its own surface and closes it again.</summary>
    internal static async Task MenuAsync(MainWindow window, Control origin, string key, string note = "")
    {
        origin.RaiseEvent(new ContextRequestedEventArgs());
        ContextMenu? menu = null;
        await UntilAsync(() => (menu = origin.ContextMenu) is { IsOpen: true } && menu.Bounds.Width > 0 && menu.Bounds.Height > 0, key + ": the menu did not open", 6);
        await Settle(window, 200);
        UiCatalog.RecordSurface(menu!, key, "context menu", note);
        menu!.Close();
        await Settle(window, 120);
    }

    /// <summary>Renders an already built menu opened under <paramref name="anchor"/> and closes it.</summary>
    internal static async Task OpenedMenuAsync(MainWindow window, ContextMenu menu, Control anchor, string key, string note = "")
    {
        menu.Open(anchor);
        await UntilAsync(() => menu.IsOpen && menu.Bounds.Width > 0 && menu.Bounds.Height > 0, key + ": the menu did not open", 6);
        await Settle(window, 200);
        UiCatalog.RecordSurface(menu, key, "context menu", note);
        menu.Close();
        await Settle(window, 120);
    }

    /// <summary>Shows a flyout under <paramref name="anchor"/>, renders its presenter by itself and hides it.</summary>
    internal static async Task FlyoutAsync(MainWindow window, FlyoutBase flyout, Control anchor, Func<Control?> content, string key, string variant = "flyout", string note = "")
    {
        flyout.ShowAt(anchor);
        try
        {
            await UntilAsync(() => flyout.IsOpen && content() is { Bounds.Width: > 0 }, key + ": the flyout did not open", 6);
            await Settle(window, 250);
            Control inner = content()!;
            Control surface = inner.GetVisualAncestors().OfType<FlyoutPresenter>().FirstOrDefault() ?? inner;
            UiCatalog.RecordSurface(surface, key, variant, note);
        }
        finally { flyout.Hide(); await Settle(window, 120); }
    }

    private static Scene[] BuildScenes()
    {
        List<Scene> scenes = [];
        AddPageScenes(scenes);
        AddSurfaceScenes(scenes);
        AddTierScenes(scenes);
        return [.. scenes];
    }

    private static partial void AddPageScenes(List<Scene> scenes);
    private static partial void AddSurfaceScenes(List<Scene> scenes);
    private static partial void AddTierScenes(List<Scene> scenes);
}
