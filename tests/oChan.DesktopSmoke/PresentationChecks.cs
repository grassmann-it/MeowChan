using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using oChan.Desktop;
using oChan.Desktop.Localization;

namespace oChan.DesktopSmoke;

internal static class PresentationChecks
{
    internal static async Task RunPreferencesAsync(MainWindow window)
    {
        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        Button add = window.FindControl<Button>("AddUrlButton")!;
        Config initial = Config.LoadConfig();
        Require(initial.WorkspaceMode == "Simple" && Mode(modes) == "Simple", "A new profile did not open in Simple view.");
        Require(initial.Theme == "Dark" && initial.Language == "en", "A new profile did not use the English dark presentation.");
        Require(window.FindControl<Button>("ThemeToggleButton")!.Content as string == "Light",
            "The dark view's theme action did not display a translated Light label.");
        const string draft = "https://unsupported.invalid/draft-one\nhttps://unsupported.invalid/draft-two";
        input.Text = draft;
        string englishAdd = add.Content?.ToString() ?? string.Empty;
        using ThreadRow retained = new ThreadRow(SnapshotFixtures.Thread(301, "301", "Presentation selection fixture", completed: 2));
        using BoardRow board = new BoardRow(SnapshotFixtures.Board(302));
        window.UrlList.Add(retained);
        window.BoardsList.Add(board);
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        grid.SelectedItem = retained;

        await SwitchModeAsync(window, "Advanced");
        double advancedWidth = window.Bounds.Width;
        await SwitchModeAsync(window, "Barebones");
        await WaitForLayoutAsync(window, () => window.Bounds.Width < advancedWidth && InViewport(window, input)
            && InViewport(window, add) && InViewport(window, modes)
            && InViewport(window, window.FindControl<MenuItem>("WorkspaceMenuItem")!));
        Require(!window.FindControl<Border>("WorkspaceInspector")!.IsVisible,
            "Barebones opened with the large inspector visible.");
        Require(input.Text == draft && window.UrlList.Count == 1 && window.BoardsList.Count == 1
            && ReferenceEquals(grid.SelectedItem, retained),
            "Changing view changed the draft input, displayed watches or selection.");
        Program.CaptureWindow(window, "smoke-barebones.png");

        await SwitchModeAsync(window, "Simple");
        Click(window.FindControl<Button>("ThemeToggleButton")!);
        await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == "Light"
            && Application.Current?.RequestedThemeVariant == ThemeVariant.Light && add.IsEnabled);
        Require(window.FindControl<Button>("ThemeToggleButton")!.Content as string == "Dark",
            "The light view's theme action did not display a translated Dark label.");
        Require(input.Text == draft, "Changing theme discarded the pending URLs.");
        Program.CaptureWindow(window, "smoke-simple-light.png");

        await ChangeLanguageAsync(window, "de");
        await Program.WaitUntilAsync(() => add.Content?.ToString() != englishAdd);
        Require(!string.IsNullOrWhiteSpace(add.Content?.ToString()) && input.Text == draft && Mode(modes) == "Simple",
            "Changing language did not translate intake or discarded pending view state.");
        Require(window.FindControl<Button>("ThemeToggleButton")!.Content as string == "Dunkel",
            "The German theme action exposed a missing translation key or an English label.");
        Program.CaptureWindow(window, "smoke-simple-german.png");
        await ChangeLanguageAsync(window, "en");
        await Program.WaitUntilAsync(() => add.Content?.ToString() == englishAdd);

        Click(window.FindControl<Button>("ThemeToggleButton")!);
        await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == "Dark"
            && Application.Current?.RequestedThemeVariant == ThemeVariant.Dark && add.IsEnabled);
        await SwitchModeAsync(window, "Advanced");
        Require(input.Text == draft && ReferenceEquals(grid.SelectedItem, retained),
            "The presentation roundtrip discarded a pending URL batch or selection.");
        await CheckSmallWindowAsync(window, retained, 760, 500, "small");
        await SwitchModeAsync(window, "Barebones");
        await CheckSmallWindowAsync(window, retained, 640, 480, "barebones-minimum");
        await SwitchModeAsync(window, "Advanced");
        input.Text = string.Empty;
        window.UrlList.Remove(retained);
        window.BoardsList.Remove(board);
        Console.WriteLine("SMOKE presentation: Simple default; Advanced/Barebones persisted; compact controls reachable; Light/Dark and EN/DE roundtrip retained pending input and selected passive row.");
    }

    private static async Task CheckSmallWindowAsync(MainWindow window, ThreadRow selected, double width, double height, string scenario)
    {
        double originalWidth = window.Width;
        double originalHeight = window.Height;
        WindowState originalState = window.WindowState;
        Size originalBounds = window.Bounds.Size;
        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        Button add = window.FindControl<Button>("AddUrlButton")!;
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        Border inspector = window.FindControl<Border>("WorkspaceInspector")!;
        Border results = window.FindControl<Border>("AdmissionResultsPanel")!;
        string? draft = input.Text;
        try
        {
            // On a scaled desktop the window manager may maximize the initial window.
            // Manual size requests only take effect after leaving that native state.
            window.WindowState = WindowState.Normal;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.Width = width;
            window.Height = height;
            await WaitForLayoutAsync(window, () => window.WindowState == WindowState.Normal
                && Math.Abs(window.Bounds.Width - width) <= 1 && Math.Abs(window.Bounds.Height - height) <= 1
                && InViewport(window, input) && InViewport(window, add));
            Require(!inspector.IsVisible, "A small window reserved table space for the initially closed inspector.");

            input.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 6)
                .Select(index => "https://unsupported.invalid/small-window-" + index));
            Expander resultExpander = results.Child as Expander
                ?? throw new InvalidOperationException("The URL results expander is missing.");
            resultExpander.IsExpanded = true;
            Click(add);
            ScrollViewer resultViewport = window.FindControl<ScrollViewer>("AdmissionResultsScroll")!;
            await WaitForLayoutAsync(window, () => add.IsEnabled && results.IsEffectivelyVisible && results.Bounds.Height > 0
                && resultViewport.Bounds.Height > 0 && InViewport(window, resultViewport)
                && FitsVertically(resultViewport, results)
                && results.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "AdmissionReport"
                    && box.IsEffectivelyVisible && box.Bounds.Height > 0 && !string.IsNullOrWhiteSpace(box.Text)));
            Require(resultExpander.IsExpanded, "The small-window fixture did not exercise expanded URL results.");
            await WaitForLayoutAsync(window, () => InViewport(window, input) && InViewport(window, add)
                && grid.IsEffectivelyVisible && grid.Bounds.Height >= grid.RowHeight
                && IsBelow(results, window.FindControl<Border>("DownloadsPanel")!, window)
                && grid.GetVisualDescendants().OfType<DataGridRow>().Any(row => row.IsEffectivelyVisible
                    && ReferenceEquals(row.DataContext, selected) && FitsVertically(row, grid)));
            double tableHeight = grid.Bounds.Height;
            Program.CaptureWindow(window, "smoke-" + scenario + "-window.png");

            Click(window.FindControl<Button>("DetailsToggleButton")!);
            Button close = window.FindControl<Button>("InspectorCloseButton")!;
            await WaitForLayoutAsync(window, () => inspector.IsEffectivelyVisible && InViewport(window, close)
                && inspector.Bounds.Height > 0 && FitsVertically(inspector, window));
            Require(ReferenceEquals(grid.SelectedItem, selected) && grid.Bounds.Height >= tableHeight - 1,
                "The small-window details drawer replaced the selected watch or consumed the table height.");
            Program.CaptureWindow(window, "smoke-" + scenario + "-details.png");
            Click(close);
            await Program.WaitUntilAsync(() => !inspector.IsVisible);
            Click(window.FindControl<Button>("DetailsToggleButton")!);
            await Program.WaitUntilAsync(() => inspector.IsVisible);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Program.WaitUntilAsync(() => !inspector.IsVisible);
            Require(ReferenceEquals(grid.SelectedItem, selected), "Closing compact details discarded the selection.");
            Console.WriteLine("SMOKE " + scenario + " window: " + width + "×" + height
                + " with expanded batch results retains intake and a visible table row; details Close/Escape remains reachable.");
        }
        catch
        {
            window.UpdateLayout();
            Console.WriteLine("SMOKE " + scenario + " layout failure: window=" + window.Bounds + "; state=" + window.WindowState
                + "; original state=" + originalState + "; requested=" + window.Width + "×" + window.Height
                + "; scale=" + window.RenderScaling);
            foreach (string name in new[] { "WorkspaceHeader", "WorkspaceBody", "UrlInput", "AddUrlButton", "AdmissionResultsPanel",
                "AdmissionResultsScroll", "DownloadsPanel", "DownloadsTabs", "ThreadsDataGrid", "WorkspaceInspector", "InspectorCloseButton" })
            {
                Control? control = window.FindControl<Control>(name);
                if (control != null) DumpLayout(window, name, control);
            }
            foreach (TextBox report in results.GetVisualDescendants().OfType<TextBox>().Where(box => box.Name == "AdmissionReport"))
                DumpLayout(window, "AdmissionReport", report);
            foreach (DataGridRow row in grid.GetVisualDescendants().OfType<DataGridRow>())
                DumpLayout(window, "DataGridRow " + (row.DataContext as ThreadRow)?.ThreadId, row);
            Program.CaptureWindow(window, "smoke-" + scenario + "-failure.png");
            throw;
        }
        finally
        {
            Expander? resultExpander = results.GetVisualDescendants().OfType<Expander>().FirstOrDefault();
            if (resultExpander != null)
                resultExpander.IsExpanded = false;
            input.Text = draft;
            window.WindowState = WindowState.Normal;
            window.Width = originalWidth;
            window.Height = originalHeight;
            window.WindowState = originalState;
            await WaitForLayoutAsync(window, () => window.WindowState == originalState
                && Math.Abs(window.Bounds.Width - originalBounds.Width) <= 1
                && Math.Abs(window.Bounds.Height - originalBounds.Height) <= 1);
        }
    }

    private static void DumpLayout(Window window, string name, Control control)
    {
        Console.WriteLine("SMOKE layout " + name + ": bounds=" + control.Bounds
            + "; origin=" + control.TranslatePoint(new Point(0, 0), window)
            + "; desired=" + control.DesiredSize + "; visible=" + control.IsEffectivelyVisible
            + "; enabled=" + control.IsEffectivelyEnabled);
    }

    private static bool FitsVertically(Control child, Control viewport)
    {
        Point? origin = child.TranslatePoint(new Point(0, 0), viewport);
        return origin.HasValue && child.Bounds.Height > 0 && origin.Value.Y >= -1
            && origin.Value.Y + child.Bounds.Height <= viewport.Bounds.Height + 1;
    }

    private static bool IsBelow(Control top, Control bottom, Window window)
    {
        Point? topOrigin = top.TranslatePoint(new Point(0, 0), window);
        Point? bottomOrigin = bottom.TranslatePoint(new Point(0, 0), window);
        return topOrigin.HasValue && bottomOrigin.HasValue
            && bottomOrigin.Value.Y >= topOrigin.Value.Y + top.Bounds.Height - 1;
    }

    private static Task WaitForLayoutAsync(Window window, Func<bool> condition) => Program.WaitUntilAsync(() =>
    {
        window.UpdateLayout();
        return condition();
    });

    internal static async Task CheckDetailsAsync(MainWindow window, ThreadRow thread)
    {
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
        grid.SelectedItem = thread;
        Border inspector = window.FindControl<Border>("WorkspaceInspector")!;
        if (!inspector.IsVisible)
            Click(window.FindControl<Button>("DetailsToggleButton")!);
        await Program.WaitUntilAsync(() => inspector.IsVisible
            && window.FindControl<SelectableTextBlock>("InspectorTitle")!.Text == thread.Title);
        Require(window.FindControl<SelectableTextBlock>("InspectorUrl")!.Text == thread.Url
            && window.FindControl<SelectableTextBlock>("InspectorDirectory")!.Text == thread.DownloadDirectory
            && window.FindControl<SelectableTextBlock>("InspectorError")!.Text == thread.ErrorDetails,
            "The inspector did not display the selected snapshot's exact URL, assigned directory and error.");
        Require(window.FindControl<SelectableTextBlock>("InspectorStatus")!.Text == thread.Status,
            "The inspector hid or changed the selected thread's retained status.");
        Require(window.FindControl<TextBlock>("WorkspaceCounts")!.Text == Texts.T("downloads.statusSummary", 1, 1, 3, 2),
            "The summary confused tracked watch counts with queued or running downloads.");
        Require(window.FindControl<TextBlock>("InspectorQueue")!.Text == Texts.T("detail.queue") + ": 3 / 2",
            "The inspector omitted typed queued and active counts.");
        Program.CaptureWindow(window, "smoke-inspector.png");
        Click(window.FindControl<Button>("DetailsToggleButton")!);
        await Program.WaitUntilAsync(() => !inspector.IsVisible);
        Require(ReferenceEquals(grid.SelectedItem, thread), "Closing details discarded the selected thread.");
        Console.WriteLine("SMOKE inspector: exact selected URL, assigned directory, status and retained error; closing preserves selection.");
    }

    internal static async Task CheckBoardDetailsAsync(MainWindow window, BoardRow board)
    {
        window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 1;
        DataGrid grid = window.FindControl<DataGrid>("BoardsDataGrid")!;
        grid.SelectedItem = board;
        Border inspector = window.FindControl<Border>("WorkspaceInspector")!;
        if (!inspector.IsVisible)
            Click(window.FindControl<Button>("DetailsToggleButton")!);
        await Program.WaitUntilAsync(() => window.FindControl<SelectableTextBlock>("InspectorTitle")!.Text == board.NiceName);
        Require(window.FindControl<SelectableTextBlock>("InspectorUrl")!.Text == board.Url
            && window.FindControl<SelectableTextBlock>("InspectorDirectory")!.Text == board.DownloadDirectory
            && window.FindControl<SelectableTextBlock>("InspectorStatus")!.Text == board.Status,
            "The board inspector reused a thread action target or displayed a different folder/status.");
        Require(!window.FindControl<StackPanel>("InspectorMediaSection")!.IsVisible,
            "The board inspector displayed the previous thread's media counts.");
        Click(window.FindControl<Button>("DetailsToggleButton")!);
        Console.WriteLine("SMOKE board inspector: selected board identity, assigned path and status; no stale thread media counts.");
    }

    internal static async Task SwitchModeAsync(MainWindow window, string mode)
    {
        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == mode);
        await Program.WaitUntilAsync(() => Config.LoadConfig().WorkspaceMode == mode && Mode(modes) == mode
            && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
        window.UpdateLayout();
    }

    private static async Task ChangeLanguageAsync(MainWindow window, string code)
    {
        window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<SettingsWindow>().Any(item => item.IsVisible));
        SettingsWindow settings = window.OwnedWindows.OfType<SettingsWindow>().Single(item => item.IsVisible);
        settings.FindControl<ComboBox>("LanguageInput")!.SelectedItem = settings.LanguageChoices.Single(choice => choice.Code == code);
        Click(settings.FindControl<Button>("SaveSettingsButton")!);
        await Program.WaitUntilAsync(() => !settings.IsVisible && Config.LoadConfig().Language == code
            && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
    }

    private static string? Mode(ComboBox modes) => (modes.SelectedItem as ComboBoxItem)?.Tag as string;

    private static bool InViewport(Window window, Control control)
    {
        Point? origin = control.TranslatePoint(new Point(0, 0), window);
        return control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Width > 0
            && control.Bounds.Height > 0 && origin.HasValue && origin.Value.X >= -1 && origin.Value.Y >= -1
            && origin.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && origin.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
