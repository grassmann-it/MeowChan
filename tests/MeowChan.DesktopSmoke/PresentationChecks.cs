using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class PresentationChecks
{
    internal static async Task RunPreferencesAsync(MainWindow window)
    {
        await CommandButtonContrastChecks.RunAsync(window);
        await WindowChromeChecks.RunAsync(window);
        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        Button add = window.FindControl<Button>("AddUrlButton")!;
        Config initial = Config.LoadConfig();
        Require(modes.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string)
            .SequenceEqual(new[] { "Barebones", "Simple", "Advanced" }),
            "The view choices were not ordered Barebones, Simple, Advanced.");
        Require(initial.WorkspaceMode == "Barebones" && Mode(modes) == "Barebones", "A new profile did not open in Barebones view.");
        TextBlock version = window.FindControl<TextBlock>("WorkspaceVersion")!;
        Require(version.IsVisible && !string.IsNullOrWhiteSpace(version.Text),
            "Barebones did not display the application version in the title bar.");
        Require(initial.Theme == "Dark" && initial.Language == "en", "A new profile did not use the English dark presentation.");
        Require(Avalonia.Automation.AutomationProperties.GetName(window.FindControl<Button>("ThemeToggleButton")!) == "Switch to light theme",
            "The dark view's theme action did not display a translated Light label.");
        const string draft = "https://unsupported.invalid/draft-one\nhttps://unsupported.invalid/draft-two";
        input.Text = draft;
        string englishAdd = Texts.T("action.add");
        RequireIconLabel(add, englishAdd);
        using ThreadRow retained = new ThreadRow(SnapshotFixtures.Thread(301, "301", "Presentation selection fixture", completed: 2));
        using BoardRow board = new BoardRow(SnapshotFixtures.Board(302));
        window.UrlList.Add(retained);
        window.BoardsList.Add(board);
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        await Program.WaitUntilAsync(() => window.VisibleThreads.Contains(retained)
            && grid.CollectionView.Cast<ThreadRow>().Contains(retained));
        grid.SelectedItem = retained;
        await Program.WaitUntilAsync(() => ReferenceEquals(grid.SelectedItem, retained) && grid.SelectedItems.Contains(retained));

        await CheckModeColumnProfilesAsync(window, grid);

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
        Require(Avalonia.Automation.AutomationProperties.GetName(window.FindControl<Button>("ThemeToggleButton")!) == "Switch to dark theme",
            "The light view's theme action did not display a translated Dark label.");
        Require(input.Text == draft, "Changing theme discarded the pending URLs.");
        Program.CaptureWindow(window, "smoke-simple-light.png");

        await ChangeLanguageAsync(window, "de");
        await Program.WaitUntilAsync(() => HasIconLabel(add, Texts.T("action.add"))
            && Avalonia.Automation.AutomationProperties.GetName(add) != englishAdd);
        Require(Avalonia.Automation.AutomationProperties.GetName(window.FindControl<Button>("WindowMinimizeButton")!) == "Minimieren",
            "The caption did not update its accessible label after changing language.");
        Require(HasIconLabel(add, Texts.T("action.add")) && input.Text == draft && Mode(modes) == "Simple",
            "Changing language did not translate intake or discarded pending view state.");
        Require(Avalonia.Automation.AutomationProperties.GetName(window.FindControl<Button>("ThemeToggleButton")!) == "Zum dunklen Design wechseln",
            "The German theme action exposed a missing translation key or an English label.");
        Program.CaptureWindow(window, "smoke-simple-german.png");
        await ChangeLanguageAsync(window, "en");
        await Program.WaitUntilAsync(() => HasIconLabel(add, englishAdd));

        Click(window.FindControl<Button>("ThemeToggleButton")!);
        await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == "Dark"
            && Application.Current?.RequestedThemeVariant == ThemeVariant.Dark && add.IsEnabled);
        await SwitchModeAsync(window, "Advanced");
        Require(input.Text == draft && ReferenceEquals(grid.SelectedItem, retained),
            "The presentation roundtrip discarded a pending URL batch or selection.");
        await CheckCompactAdmissionMatrixAsync(window, retained);
        await SwitchModeAsync(window, "Advanced");
        input.Text = string.Empty;
        window.UrlList.Remove(retained);
        window.BoardsList.Remove(board);
        await R08dPresentationChecks.RunAsync(window);
        Console.WriteLine("SMOKE presentation: Barebones default; Simple/Advanced/Barebones persisted; compact controls reachable; Light/Dark and EN/DE roundtrip retained pending input and selected passive row.");
    }

    private static async Task CheckCompactAdmissionMatrixAsync(MainWindow window, ThreadRow selected)
    {
        Config original = Config.LoadConfig();
        foreach (string theme in new[] { "Dark", "Light" })
        {
            if (Config.LoadConfig().Theme != theme)
            {
                Click(window.FindControl<Button>("ThemeToggleButton")!);
                await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == theme);
            }
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await SwitchModeAsync(window, mode);
                foreach (string language in new[] { "en", "de" })
                {
                    await ChangeLanguageAsync(window, language);
                    await CheckSmallWindowAsync(window, selected, mode == "Barebones" ? 640 : 760,
                        mode == "Barebones" ? 480 : 500, "admission-" + theme + "-" + mode + "-" + language);
                }
            }
        }
        await SwitchModeAsync(window, original.WorkspaceMode);
        await ChangeLanguageAsync(window, original.Language);
        if (Config.LoadConfig().Theme != original.Theme)
        {
            Click(window.FindControl<Button>("ThemeToggleButton")!);
            await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == original.Theme);
        }
        Console.WriteLine("SMOKE compact admission matrix: six rejected URLs, cause/example and whole row visible, complete scrollable results, details and preserved input in Dark/Light × EN/DE × B/S/A minima.");
    }

    private static async Task CheckModeColumnProfilesAsync(MainWindow window, DataGrid grid)
    {
        Button columnsButton = window.FindControl<Button>("DownloadsColumnsButton")!;
        ActionToolBar toolbar = window.FindControl<ActionToolBar>("WorkspaceListActions")!;
        async Task OpenColumnsAsync()
        {
            window.UpdateLayout();
            if (toolbar.OverflowHost?.Children.Contains(columnsButton) == true)
            {
                Require(toolbar.More?.IsEffectivelyVisible == true, "The columns overflow action is not reachable.");
                toolbar.More!.IsChecked = true;
            }
            await Program.WaitUntilAsync(() => columnsButton.IsEffectivelyVisible && columnsButton.IsEffectivelyEnabled);
            SmokeActionClick.Execute(columnsButton);
        }
        DataGridColumn completion = grid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.Completion));
        Require(completion.IsVisible && !grid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.Status)).IsVisible,
            "Barebones download columns did not start with compact defaults.");
        await OpenColumnsAsync();
        ContextMenu menu = columnsButton.ContextMenu ?? throw new InvalidOperationException("Download columns menu did not open.");
        MenuItem completionItem = menu.Items.OfType<MenuItem>().ElementAt(grid.Columns.IndexOf(completion));
        Require(completionItem.IsChecked && completionItem.ToggleType == MenuItemToggleType.CheckBox,
            "Download columns menu did not expose checked native items.");
        completionItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => !completion.IsVisible &&
            Config.LoadConfig().GetLayoutProfile("Barebones").ColumnVisibility.TryGetValue("ThreadsDataGrid.Completion", out bool visible) && !visible);
        menu.Close();
        if (toolbar.More != null) toolbar.More.IsChecked = false;

        await SwitchModeAsync(window, "Advanced");
        Require(completion.IsVisible && grid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.Status)).IsVisible,
            "The Advanced table inherited Barebones column visibility.");
        await SwitchModeAsync(window, "Barebones");
        Require(!completion.IsVisible, "The Barebones column choice did not return after switching modes.");

        await OpenColumnsAsync();
        MenuItem reset = columnsButton.ContextMenu!.Items.OfType<MenuItem>().Last();
        reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => completion.IsVisible &&
            !Config.LoadConfig().GetLayoutProfile("Barebones").ColumnVisibility.ContainsKey("ThreadsDataGrid.Completion"));
        columnsButton.ContextMenu?.Close();
        if (toolbar.More != null) toolbar.More.IsChecked = false;
        Config restarted = Config.LoadConfig();
        Require(restarted.WorkspaceMode == "Barebones" &&
            !restarted.GetLayoutProfile("Advanced").ColumnVisibility.ContainsKey("ThreadsDataGrid.Completion"),
            "A reset or mode switch leaked download columns into another profile on restart.");
    }

    private static async Task CheckSmallWindowAsync(MainWindow window, ThreadRow selected, double width, double height, string scenario)
    {
        // Mode changes request native resizes asynchronously. Capture the restore
        // size only after that request has reached the window manager.
        await WaitForLayoutAsync(window, () => window.WindowState != WindowState.Normal
            || (Math.Abs(window.Bounds.Width - window.Width) <= 1
                && Math.Abs(window.Bounds.Height - window.Height) <= 1));
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
                && MatchesRequestedSize(window, width, height)
                && InViewport(window, input) && InViewport(window, add));
            if (window.Screens.ScreenFromWindow(window) is { } screen && window.FrameSize is Size frame)
                Require(frame.Width * window.RenderScaling <= screen.WorkingArea.Width + 1
                    && frame.Height * window.RenderScaling <= screen.WorkingArea.Height + 1,
                    "The small native window frame exceeds the working area.");
            Console.WriteLine($"SMOKE {scenario} native size: requested={width}×{height}; actual={window.Bounds.Size}; frame={window.FrameSize}; scale={window.RenderScaling}.");
            Require(!inspector.IsVisible, "A small window reserved table space for the initially closed inspector.");
            await WindowChromeChecks.CheckReachableAsync(window);

            string rejectedInput = string.Join(Environment.NewLine, Enumerable.Range(1, 6)
                .Select(index => "https://unsupported.invalid/small-window-" + index));
            input.Text = rejectedInput;
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
            SelectableTextBlock repair = window.FindControl<SelectableTextBlock>("AdmissionRepairText")!;
            ScrollViewer repairViewport = window.FindControl<ScrollViewer>("AdmissionRepairScroll")!;
            TextBox report = results.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "AdmissionReport");
            SelectableTextBlock sources = resultViewport.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Single(block => block.Name == "AdmissionSourcesHelp");
            Require(input.Text == rejectedInput, "The small-window rejection discarded the editable URL batch.");
            string repairText = repair.Text ?? string.Empty;
            string repairPrefix = Texts.T("admission.repairUnsupported") + Environment.NewLine
                + Texts.T("admission.repairFormat", string.Empty);
            Require(repairText.StartsWith(repairPrefix, StringComparison.Ordinal),
                "The small-window repair did not retain its complete immediate cause and localized URL-example label.");
            // An unknown host can use any registered source's valid example. Plugin
            // activation order must not turn one particular source into a UI contract.
            string exampleUrl = repairText[repairPrefix.Length..];
            ProviderDescriptor[] providers = window.SessionHost.GetProviderDescriptors().ToArray();
            BoardAdmissionTarget? example = window.SessionHost.ClassifyAdmission(exampleUrl);
            Require(Uri.TryCreate(exampleUrl, UriKind.Absolute, out Uri? exampleUri)
                && providers.Any(provider => provider.SupportedHosts.Prepend(provider.BaseUri.Host)
                    .Contains(exampleUri.Host, StringComparer.OrdinalIgnoreCase))
                && example != null && example.Url == exampleUrl,
                "The displayed repair example was not a canonical URL accepted by a registered source: " + exampleUrl);
            Require(InViewport(window, repairViewport) && FitsVertically(repair, repairViewport),
                "The small-window repair clipped the immediate cause or URL example.");
            Require(Enumerable.Range(1, 6).All(index => report.Text?.Contains("https://unsupported.invalid/small-window-" + index, StringComparison.Ordinal) == true)
                && sources.Text?.Contains("https://boards.4chan.org/", StringComparison.Ordinal) == true
                && sources.Text.Contains("https://8kun.top/", StringComparison.Ordinal)
                && providers.All(provider => sources.Text.Contains(provider.DisplayName + " (" + provider.BaseUri.AbsoluteUri + ")", StringComparison.Ordinal)),
                "The scrollable results lost rejected URLs or the separate registered-source help.");
            await WaitForLayoutAsync(window, () => InViewport(window, input) && InViewport(window, add)
                && grid.IsEffectivelyVisible && grid.Bounds.Height >= grid.RowHeight
                && IsBelow(results, window.FindControl<Border>("DownloadsPanel")!, window)
                && grid.GetVisualDescendants().OfType<DataGridRow>().Any(row => row.IsEffectivelyVisible
                    && ReferenceEquals(row.DataContext, selected) && FitsVertically(row, grid)));
            double tableHeight = grid.Bounds.Height;
            Console.WriteLine($"SMOKE {scenario} compact admission: repair={repairViewport.Bounds.Height}; results={resultViewport.Bounds.Height}; table={tableHeight}; row={grid.GetVisualDescendants().OfType<DataGridRow>().Single(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, selected)).Bounds.Height}.");
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
            Console.WriteLine("SMOKE " + scenario + " compact admission gates: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                inputInViewport = InViewport(window, input), addInViewport = InViewport(window, add),
                grid.IsEffectivelyVisible, gridHeight = grid.Bounds.Height.ToString(), rowHeight = grid.RowHeight.ToString(),
                gridAtLeastOneRow = grid.Bounds.Height >= grid.RowHeight,
                resultsBeforeDownloads = IsBelow(results, window.FindControl<Border>("DownloadsPanel")!, window),
                selectedMatches = ReferenceEquals(grid.SelectedItem, selected),
                selectedRows = grid.GetVisualDescendants().OfType<DataGridRow>()
                    .Where(row => ReferenceEquals(row.DataContext, selected)).Select(row => new
                    {
                        row.IsEffectivelyVisible, bounds = row.Bounds.ToString(),
                        origin = row.TranslatePoint(default, window)?.ToString(), fitsVertically = FitsVertically(row, grid),
                        ancestors = row.GetVisualAncestors().OfType<Control>().Select(control => new
                        { type = control.GetType().Name, control.Name, bounds = control.Bounds.ToString(),
                            control.ClipToBounds, clip = control.Clip?.Bounds.ToString() }).ToArray()
                    }).ToArray()
            }));
            foreach (string name in new[] { "WorkspaceHeader", "WorkspacePages", "ShellActionErrorBanner", "WorkspaceBody", "WorkspaceIntakeCard",
                "WorkspaceContentGrid", "UrlInput", "AddUrlButton", "AdmissionResultsPanel",
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

    internal static bool MatchesRequestedSize(Window window, double width, double height)
    {
        // Win32's BorderOnly minimum-track calculation includes the top resize
        // border even when the extended client area occupies most of that border.
        // At MinHeight/MinWidth Windows can therefore clamp a request slightly
        // above the logical minimum. Bound that allowance by the measured native
        // frame inset; ordinary resizes and every non-Windows size stay exact.
        Size client = window.Bounds.Size;
        Size frame = window.FrameSize ?? client;
        bool Matches(double actual, double requested, double minimum, double inset) =>
            Math.Abs(actual - requested) <= 1
            || (OperatingSystem.IsWindows() && Math.Abs(requested - minimum) <= 0.01
                && actual >= requested && actual - requested <= Math.Max(0, inset) + 1);
        return Matches(client.Width, width, window.MinWidth, frame.Width - client.Width)
            && Matches(client.Height, height, window.MinHeight, frame.Height - client.Height);
    }

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
            && window.FindControl<SelectableTextBlock>("InspectorError")!.Text == thread.IssueDetails
            && window.FindControl<SelectableTextBlock>("InspectorContext")!.Text == thread.ContextDetails,
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
        bool changed = Mode(modes) != mode;
        modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == mode);
        await Program.WaitUntilAsync(() => Config.LoadConfig().WorkspaceMode == mode && Mode(modes) == mode
            && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
        window.UpdateLayout();
        if (changed)
            await WaitForLayoutAsync(window, () => window.WindowState != WindowState.Normal
                || (MatchesRequestedSize(window, window.Width, window.Height)
                    && window.Screens.ScreenFromWindow(window) is { } screen
                    && window.FrameSize is Size frame
                    && frame.Width * window.RenderScaling <= screen.WorkingArea.Width + 1
                    && frame.Height * window.RenderScaling <= screen.WorkingArea.Height + 1));
    }

    private static async Task ChangeLanguageAsync(MainWindow window, string code)
    {
        window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        SettingsEditor settings = window.SettingsEditor;
        settings.FindControl<ComboBox>("LanguageInput")!.SelectedItem = settings.LanguageChoices.Single(choice => choice.Code == code);
        Click(settings.FindControl<Button>("SaveSettingsButton")!);
        await Program.WaitUntilAsync(() => !settings.IsBusy && Config.LoadConfig().Language == code
            && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
        window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
    }

    private static string? Mode(ComboBox modes) => (modes.SelectedItem as ComboBoxItem)?.Tag as string;

    private static bool HasIconLabel(Button button, string expected) =>
        button.Content is StackPanel panel && panel.Children.Count == 2
        && panel.Children[0] is Control
        && panel.Children[1] is TextBlock label && label.Text == expected
        && Avalonia.Automation.AutomationProperties.GetName(button) == expected;

    private static void RequireIconLabel(Button button, string expected) =>
        Require(HasIconLabel(button, expected), "Add URL lost its icon, translated label, or accessibility name.");

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
