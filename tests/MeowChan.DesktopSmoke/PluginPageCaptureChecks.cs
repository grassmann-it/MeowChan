using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Evidence only: the merged Plugins page over the passive UX fixture at 1440 x 920, 900 x 760 and 860 x 760 (one pane), Dark and Light, EN and DE, so its look
/// can be compared with the Sources page (smoke-sources-wide-*). Nothing is compiled, described, started or probed; the checks are
/// limited to passive presentation, dropdown/layer navigation and the selected plugin's visible state action.
/// </summary>
internal static class PluginPageCaptureChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(Config.GetConfigDirectory(), "plugin-page-captures"));
        SourcesPage sources = MergedSourcesSmoke.Create(fixture.Services, out PluginsPage page);
        ContentControl host = window.FindControl<ContentControl>("SourcesPageHost")!;
        object? retained = host.Content;
        string language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        host.Content = sources;
        try
        {
            MergedSourcesSmoke.OpenPlugins(window);
            await PresentationChecks.SwitchModeAsync(window, "Simple");
            await page.ActivateAsync();
            foreach ((double captureWidth, double captureHeight, string size) in new[] { (1440d, 920d, "1440"), (900d, 760d, "900"), (860d, 760d, "860") })
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                string suffix = variant.Key!.ToString()!.ToLowerInvariant();
                Application.Current.RequestedThemeVariant = variant;
                window.WindowState = WindowState.Normal; window.Width = captureWidth; window.Height = captureHeight;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, captureWidth, captureHeight)
                    && window.ActualThemeVariant == variant);
                foreach (string captureLanguage in new[] { "de", "en" })
                {
                    Texts.SetLanguage(captureLanguage);
                    await page.RefreshAsync();
                    if (size == "1440" && captureLanguage == "de")
                    {
                        // Fresh merged entry now selects the first plugin passively; it must not enable/start/save it.
                        SourcesPage freshSources = MergedSourcesSmoke.Create(fixture.Services, out PluginsPage fresh);
                        byte[] beforeOpening = MeowChan.PluginSdk.SdkJson.Serialize(await fixture.Store.ReadAsync());
                        host.Content = freshSources;
                        try
                        {
                            await fresh.ActivateAsync();
                            Require(fresh.Selected != null && fresh.FindControl<SelectionList>("PluginCards")!.SelectedItem != null,
                                "A fresh merged catalog did not select its first plugin.");
                            byte[] afterOpening = MeowChan.PluginSdk.SdkJson.Serialize(await fixture.Store.ReadAsync());
                            Require(beforeOpening.SequenceEqual(afterOpening)
                                && fixture.ForbiddenCalls == 0, "Passive initial selection mutated the profile or executed plugin code.");
                            await Capture(window, fresh, fresh.Selected!.Key, "smoke-sources-page-" + size + "-initial-all-" + captureLanguage + "-" + suffix);
                        }
                        finally { freshSources.EndLifetime(); host.Content = sources; }
                        await page.RefreshAsync();
                    }
                    await CaptureSourceLayersAsync(window, sources, size + "-" + captureLanguage + "-" + suffix);
                    MergedSourcesSmoke.Filter(page, SourcesPluginsFilter.AllPlugins);
                    await Capture(window, page, PluginUxFixture.SourceKey, "smoke-sources-page-" + size + "-source-" + captureLanguage + "-" + suffix);
                    // The steps of an installed, enabled and active plugin: all three done, nothing left to do.
                    PluginAddChecks.RequireSteps(page, new[] { PluginStepState.Done, PluginStepState.Done, PluginStepState.Done }, null, "source plugin " + captureLanguage + "/" + suffix);
                    if (captureLanguage == "de")
                        await Capture(window, page, PluginUxFixture.FilterKey, "smoke-sources-page-" + size + "-filter-" + captureLanguage + "-" + suffix);
                    if (captureLanguage == "de")
                        // A disabled plugin: installed, "Enable plugin" is the next step, active comes after it.
                        PluginAddChecks.RequireSteps(page, new[] { PluginStepState.Done, PluginStepState.Next, PluginStepState.Later },
                            Texts.T("plugins.step.next.enable", MeowChan.Desktop.Commands.CommandCatalog.Label("plugins.enable")), "filter plugin " + suffix);
                    if (size == "1440")
                    {
                        // A file the app has not registered yet (just added): installed, and the restart is the next step.
                        await Capture(window, page, PluginUxFixture.UnknownKey, "smoke-sources-page-" + size + "-new-" + captureLanguage + "-" + suffix);
                        PluginAddChecks.RequireSteps(page, new[] { PluginStepState.Done, PluginStepState.Next, PluginStepState.Later }, Texts.T("plugins.step.next.restart"), "new file " + captureLanguage + "/" + suffix);
                    }
                    if (size == "860" && captureLanguage == "de")
                    {
                        // Below the 886 dip switch point: one pane; Back returns to the list.
                        Require(page.FindControl<Button>("PluginBack")!.IsEffectivelyVisible && !page.FindControl<Grid>("PluginListPane")!.IsVisible,
                            "At 860 dip the Plugins page was not in its one-pane layout.");
                        page.FindControl<Button>("PluginBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Capture(window, page, null, "smoke-sources-page-" + size + "-list-" + captureLanguage + "-" + suffix);
                    }
                    if (size == "1440" && captureLanguage == "de")
                    {
                        // The image-filter category: the list shows only the filter plugin, the details stay with the choice.
                        MergedSourcesSmoke.Filter(page, SourcesPluginsFilter.ImageFilters);
                        await Capture(window, page, PluginUxFixture.FilterKey, "smoke-sources-page-" + size + "-category-" + captureLanguage + "-" + suffix);
                        Require(page.FindControl<SelectionList>("PluginCards")!.Items.Count == 1 && page.Selected?.IsFilter == true,
                            "The Image filters dropdown included a source-only or not-yet-described plugin.");
                        MergedSourcesSmoke.Filter(page, SourcesPluginsFilter.AllPlugins);
                    }
                }
            }
            await CaptureDiagnosticsFilterRowAsync(window);
            await PluginGroupChecks.RunAsync(window);
            Require(fixture.ForbiddenCalls == 0, "The capture pass compiled, described, started or probed a plugin.");
            Console.WriteLine("PLUGIN_PAGE_CAPTURES PASS: 1440x920, 900x760 and 860x760 (one pane), Dark and Light, EN and DE; evidence only.");
        }
        finally
        {
            sources.EndLifetime(); host.Content = retained;
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
        }
    }

    private static async Task CaptureSourceLayersAsync(MainWindow window, SourcesPage sources, string cell)
    {
        PluginsPage management = sources.PluginManagement;
        // One registered source plus the described, unregistered source tests the active/inactive union in the real list.
        sources.Refresh(new[] { new ProviderDescriptor("capture-active", "Registered capture source", new Uri("https://capture.example.test/"),
            ProviderCapabilities.ThreadWatch, new[] { "capture.example.test" }, "fixture", false) }, window.SessionHost.GetView(), true);
        MergedSourcesSmoke.Filter(management, SourcesPluginsFilter.Sources);
        SelectionList rows = sources.FindControl<SelectionList>("SourceCards")!;
        Require(rows.Items.Count == 2 && ((SelectionListItem)rows.Items[0]!).Content is StackPanel active
            && active.Children.OfType<TextBlock>().First().Text == "Registered capture source",
            "The Sources dropdown did not put the registered source before the inactive described source.");
        SelectionListItem inactive = (SelectionListItem)rows.Items[1]!;
        Require(((StackPanel)inactive.Content!).Children.OfType<WrapPanel>().Single().Children.OfType<Border>()
            .Any(chip => chip.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("sources.badge.status.inactive"))),
            "The described but unregistered source lost its inactive chip.");
        sources.SelectSource(PluginUxFixture.SourceId);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        Require(sources.FindControl<TextBlock>("SourceHeading")!.IsEffectivelyVisible && !management.ShowingSourcePluginDetails,
            "Sources did not start at its source overview layer.");
        Program.CaptureWindow(window, "smoke-sources-page-" + cell + "-overview.png");
        sources.FindControl<Button>("SourcePluginButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => management.ShowingSourcePluginDetails
            && management.FindControl<TextBlock>("PluginHeading")!.IsFocused);
        Require(management.Selected?.Key == PluginUxFixture.SourceKey && management.Filter == SourcesPluginsFilter.Sources
            && !management.FindControl<Grid>("PluginListPane")!.IsEffectivelyVisible,
            "Plugin details escaped the selected source or opened a separate workspace/list.");
        Program.CaptureWindow(window, "smoke-sources-page-" + cell + "-plugin-details.png");
        management.FindControl<Button>("PluginBack")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !management.ShowingSourcePluginDetails
            && sources.FindControl<TextBlock>("SourceHeading")!.IsEffectivelyVisible);
        Require(window.SelectedPage == MainWindow.WorkspacePage.Sources
            && ReferenceEquals(window.FindControl<ContentControl>("SourcesPageHost")!.Content, sources),
            "Back from plugin details left the actual merged Sources host.");
    }

    // The Diagnostics filter row mixes a combo box, a text box, two checkboxes and a button: the checkboxes sit on the
    // row's centre line (shared WorkspaceStyles rule for horizontal rows), checked to the pixel and captured in both themes.
    private static async Task CaptureDiagnosticsFilterRowAsync(MainWindow window)
    {
        // Diagnostics is a segment of the Status page.
        if (!window.ShowStatus(StatusSegment.Diagnostics)) throw new InvalidOperationException("The diagnostics segment cannot be opened.");
        window.Width = 1440; window.Height = 920;
        // Barebones starts with the filter group closed (work views); this check is about the open row.
        if (window.GetVisualDescendants().OfType<Expander>().FirstOrDefault(control => control.Name == "DiagnosticFiltersSection") is { } filters)
            filters.IsExpanded = true;
        Control text = null!, problems = null!, waiting = null!;
        await Program.WaitUntilAsync(() =>
        {
            text = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(control => control.Name == "DiagnosticTextFilter")!;
            problems = window.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(control => control.Name == "DiagnosticProblemsOnly")!;
            waiting = window.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(control => control.Name == "DiagnosticIncludeWaiting")!;
            return text?.IsEffectivelyVisible == true && problems != null && waiting != null
                && PresentationChecks.MatchesRequestedSize(window, 1440, 920);
        });
        foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            text.BringIntoView();
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == variant);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            double Centre(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height / 2), window)!.Value.Y;
            foreach (Control box in new[] { problems, waiting })
                Require(box.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Center && Math.Abs(Centre(box) - Centre(text)) <= 1,
                    $"The Diagnostics filter checkbox {box.Name} is not centred on the filter row: {Centre(box)} vs {Centre(text)}.");
            Program.CaptureWindow(window, "smoke-diagnostics-filter-row-" + variant.Key!.ToString()!.ToLowerInvariant() + ".png");
        }
    }

    private static async Task Capture(MainWindow window, PluginsPage page, string? key, string name)
    {
        if (key != null)
        {
            page.Select(key);
            page.FindControl<ScrollViewer>("PluginDetailsPane")!.Offset = default;
        }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (key != null)
            Require(page.Selected?.Key == key && page.FindControl<TextBlock>("PluginHeading")!.IsEffectivelyVisible
                && page.FindControl<Button>("PluginEnabled")!.IsEffectivelyVisible,
                "The selected plugin's heading or state action was not shown for the capture " + name + ".");
        // A generated single-line settings field has the workspace input height, as the category box above the list.
        double inputHeight = page.FindControl<ComboBox>("PluginCategory")!.Bounds.Height;
        foreach (TextBox field in page.GetVisualDescendants().OfType<TextBox>()
            .Where(field => field.Name?.StartsWith("PluginField_", StringComparison.Ordinal) == true && field.IsEffectivelyVisible && !field.AcceptsReturn))
            Require(field.Bounds.Height >= inputHeight - 1,
                $"The plugin setting field {field.Name} is {field.Bounds.Height} dip high, the workspace inputs {inputHeight} dip ({name}).");
        Program.CaptureWindow(window, name + ".png");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
