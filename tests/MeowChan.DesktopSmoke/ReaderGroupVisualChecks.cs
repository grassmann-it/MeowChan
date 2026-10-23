using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>Bounded visual evidence from the actual group dialogs and missing-target reader state.</summary>
internal static class ReaderGroupVisualChecks
{
    private sealed record Scenario(string Mode, string Language, string Theme, bool Wide)
    {
        internal double Width => Wide ? 1200 : Mode == "Barebones" ? 640 : 760;
        internal double Height => Wide ? 800 : Mode == "Barebones" ? 480 : 500;
        internal string Stem(Window window) => "smoke-reader-groups-" + Mode.ToLowerInvariant() + "-" + Language + "-"
            + Theme.ToLowerInvariant() + "-" + (Wide ? "wide" : "minimum") + "-scale-"
            + window.RenderScaling.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', 'p');
    }

    internal static Task CaptureManagerMatrixAsync(MainWindow owner, DesktopSessionHost host, ReaderGroupsDialogOrigin origin) =>
        MatrixAsync(owner, async scenario =>
        {
            ReaderGroupsDialog dialog = new(host, origin)
            { Width = scenario.Wide ? 1040 : 720, Height = scenario.Wide ? 720 : 620 };
            Task<ReaderGroupReadRequest?> result = dialog.ShowDialog<ReaderGroupReadRequest?>(owner);
            try
            {
                await Program.WaitUntilAsync(() => dialog.CanExecute("reader-groups.read"));
                Require(dialog.Title == (scenario.Language == "de" ? "Readergruppen" : "Reader groups"), "Manager title is not localized.");
                Require(dialog.ActualThemeVariant.ToString() == scenario.Theme, "Manager theme did not follow the actual application setting.");
                await SettleAsync(dialog);
                TraceManager(dialog, "ready");
                AssertCommandButtons(dialog);
                Require(dialog.Bounds.Width <= owner.Bounds.Width + 1 && dialog.Bounds.Height <= owner.Bounds.Height + 1,
                    "Manager exceeds the tested owner's client bounds.");
                ListBox groups = dialog.FindControl<ListBox>("Groups")!;
                groups.BringIntoView(); groups.Focus(NavigationMethod.Tab);
                await SettleAsync(dialog);
                Button[] footer = dialog.FindControl<WrapPanel>("GroupFooter")!.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
                Require(footer.Length >= 4, "Manager lost persistent save/read/reload/close actions.");
                Capture(dialog, scenario, "manager", new Control[] { groups, dialog.FindControl<TextBlock>("GroupMessage")! }.Concat(footer).ToArray());

                ListBox members = dialog.FindControl<ListBox>("GroupMembers")!;
                Require(members.ItemCount == 2 && members.SelectedIndex == 1, "Visual setup lost the saved membership order/selection.");
                members.BringIntoView(); members.Focus(NavigationMethod.Tab);
                await SettleAsync(dialog);
                Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(members)), "Members list lacks an accessible name.");
                Capture(dialog, scenario, "members", new Control[] { members }.Concat(footer).ToArray());

                // The list itself can fit while its editing actions are below the scroll fold.
                // Reach those real controls without changing membership or order, then measure
                // the scrolled viewport rather than treating IsEffectivelyVisible as sufficient.
                Button[] memberActions = dialog.FindControl<WrapPanel>("MemberActions")!.Children.OfType<Button>().ToArray();
                Require(memberActions.Length == 3, "Member editing actions are incomplete.");
                dialog.Activate();
                await Program.WaitUntilAsync(() => dialog.IsActive);
                foreach (Button action in memberActions.Where(button => button.IsEffectivelyEnabled))
                {
                    Require(action.Focus(NavigationMethod.Tab), "Member action cannot receive keyboard focus: " + action.Name);
                    action.BringIntoView();
                    await SettleAsync(dialog);
                    Require(action.IsKeyboardFocusWithin && FullyVisible(dialog, action),
                        "Focused member action is outside its scroll viewport: " + action.Name);
                }
                Button focusedMemberAction = memberActions.Single(button => button.IsKeyboardFocusWithin);
                Capture(dialog, scenario, "member-actions", memberActions.Cast<Control>().Concat(footer).ToArray());
                Require(focusedMemberAction.IsKeyboardFocusWithin && members.ItemCount == 2 && members.SelectedIndex == 1,
                    "Member action presentation changed the saved order/selection or lost focus.");

                Button delete = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "GroupCommand_delete");
                delete.BringIntoView(); delete.Focus(NavigationMethod.Tab);
                await SettleAsync(dialog);
                TraceManager(dialog, "before-delete");
                AssertCommandButtons(dialog);
                Require(FullyVisible(dialog, delete), "Delete confirmation entry cannot be reached by scrolling.");
                Click(delete);
                Window? prompt = null;
                await Program.WaitUntilAsync(() => (prompt = dialog.OwnedWindows.FirstOrDefault(window => window.IsVisible)) != null);
                Window confirmation = prompt!;
                await SettleAsync(confirmation);
                Button[] choices = confirmation.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();
                Require(choices.Length == 2, "Group confirmation must expose exactly confirm and cancel.");
                Button cancel = choices.Single(button => Equals(button.Content, scenario.Language == "de" ? "Abbrechen" : "Cancel"));
                Require(ReferenceEquals(confirmation.FocusManager?.GetFocusedElement(), cancel), "Confirmation did not initially focus its safe cancel action.");
                TextBlock message = confirmation.GetVisualDescendants().OfType<TextBlock>().First(text =>
                    text.Text?.Contains(scenario.Language == "de" ? "Zuordnungen" : "memberships", StringComparison.Ordinal) == true);
                Capture(confirmation, scenario, "confirmation", new Control[] { message }.Concat(choices).ToArray());
                Click(cancel);
                await Program.WaitUntilAsync(() => !confirmation.IsVisible && dialog.CanExecute("reader-groups.read"));
                AssertCommandButtons(dialog);
                Require(members.ItemCount == 2, "Canceling visual confirmation changed group membership.");
            }
            catch
            {
                TryFailureDiagnostic(() => TraceManager(dialog, "failure"), "manager state");
                TryFailureDiagnostic(() => Program.CaptureWindow(dialog, scenario.Stem(dialog) + "-manager-failure.png"), "manager capture");
                throw;
            }
            finally
            {
                foreach (Window prompt in dialog.OwnedWindows.Where(window => window.IsVisible).ToArray()) prompt.Close(false);
                dialog.Close();
                await result;
            }
        });

    internal static async Task CaptureMissingMatrixAsync(MainWindow owner, Window reader, ArchivePage page)
    {
        Size originalSize = reader.Bounds.Size;
        try
        {
            await MatrixAsync(owner, async scenario =>
        {
            reader.Width = scenario.Wide ? 1040 : scenario.Width;
            reader.Height = scenario.Wide ? 720 : scenario.Height;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(reader, reader.Width, reader.Height));
            // Reattempt the same absent next target through the real reader executor so its current-language
            // explanation is regenerated; membership and the displayed origin remain untouched.
            ResourceId? origin = page.CurrentThread?.Summary.Id;
            await page.NavigateReaderAsync(0, 1);
            Require(page.CurrentThread?.Summary.Id == origin && page.CanSkipUnavailableGroupMember,
                "Missing-target visual scenario changed the reader origin or lost explicit skip.");
            TextBlock message = page.FindControl<TextBlock>("ReaderGroupAvailabilityMessage")!;
            Require(message.Text?.Contains(scenario.Language == "de" ? "nicht verfügbar" : "unavailable", StringComparison.Ordinal) == true,
                "Missing-target explanation did not use the current language.");
            Button skip = page.FindControl<Button>("ReaderSkipUnavailableGroupMember")!;
            reader.Activate(); skip.Focus(NavigationMethod.Tab);
            await SettleAsync(reader);
            Capture(reader, scenario, "missing-target", [message, skip, page.FindControl<Button>("ArchiveCloseReader")!,
                page.FindControl<Button>("ReaderPreviousThread")!, page.FindControl<Button>("ReaderNextThread")!]);
            });
        }
        finally
        {
            reader.Width = originalSize.Width; reader.Height = originalSize.Height;
            await SettleAsync(reader);
        }
    }

    private static async Task MatrixAsync(MainWindow owner, Func<Scenario, Task> scene)
    {
        Config original = owner.SessionHost.Config.Clone();
        Size originalSize = owner.Bounds.Size;
        WindowState originalState = owner.WindowState;
        MainWindow.WorkspacePage originalPage = owner.SelectedPage;
        try
        {
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(owner, mode);
                foreach (string theme in new[] { "Light", "Dark" })
                foreach (string language in new[] { "en", "de" })
                {
                    await SetPresentationAsync(owner, language, theme);
                    foreach (bool wide in mode == "Advanced" ? new[] { false, true } : new[] { false })
                    {
                        Scenario scenario = new(mode, language, theme, wide);
                        owner.WindowState = WindowState.Normal;
                        owner.Width = scenario.Width; owner.Height = scenario.Height;
                        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(owner, scenario.Width, scenario.Height));
                        await SettleAsync(owner);
                        Require(owner.SessionHost.Config.WorkspaceMode == mode && Texts.Language == language,
                            "Matrix label differs from actual active mode/language.");
                        await scene(scenario);
                    }
                }
            }
        }
        finally
        {
            await SetPresentationAsync(owner, original.Language, original.Theme);
            await PresentationChecks.SwitchModeAsync(owner, original.WorkspaceMode);
            owner.Width = originalSize.Width; owner.Height = originalSize.Height; owner.WindowState = originalState;
            Control? navigation = owner.FindControl<Button>(originalPage + "NavigationButton");
            if (navigation?.IsEffectivelyVisible == true) Click((Button)navigation);
            else if (owner.FindControl<MenuItem>(originalPage + "MenuItem") is MenuItem menu)
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await SettleAsync(owner);
            Require(owner.SessionHost.Config.WorkspaceMode == original.WorkspaceMode
                && owner.SessionHost.Config.Language == original.Language && owner.SessionHost.Config.Theme == original.Theme,
                "Visual matrix did not restore the original mode/language/theme settings.");
        }
    }

    private static async Task SetPresentationAsync(MainWindow owner, string language, string theme)
    {
        if (owner.SessionHost.Config.Language == language && owner.SessionHost.Config.Theme == theme) return;
        owner.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => owner.SettingsPageVisible);
        SettingsEditor settings = owner.SettingsEditor;
        settings.FindControl<ComboBox>("LanguageInput")!.SelectedItem = settings.LanguageChoices.Single(choice => choice.Code == language);
        settings.FindControl<ComboBox>("ThemeInput")!.SelectedItem = settings.ThemeChoices.Single(choice => choice.Value == theme);
        Click(settings.FindControl<Button>("SaveSettingsButton")!);
        await Program.WaitUntilAsync(() => !settings.IsBusy && owner.SessionHost.Config.Language == language
            && owner.SessionHost.Config.Theme == theme && Texts.Language == language);
        Click(owner.FindControl<Button>("DownloadsNavigationButton")!);
        await Program.WaitUntilAsync(() => !owner.SettingsPageVisible);
    }

    private static void Capture(Window window, Scenario scenario, string scene, IReadOnlyList<Control> controls)
    {
        string stem = scenario.Stem(window) + "-" + scene;
        // Write both artifacts before assertions so a clipping failure still has inspectable evidence.
        Program.CaptureWindow(window, stem + ".png");
        var geometry = controls.Select(control =>
        {
            Point? position = control.TranslatePoint(default, window);
            return new { name = control.Name ?? control.GetType().Name, type = control.GetType().Name,
                x = position?.X, y = position?.Y, width = control.Bounds.Width, height = control.Bounds.Height,
                fullyVisible = FullyVisible(window, control), enabled = control.IsEffectivelyEnabled,
                accessibleName = AutomationProperties.GetName(control),
                focused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), control),
                keyboardFocusWithin = control.IsKeyboardFocusWithin,
                labels = control is Button ? control.GetVisualDescendants().OfType<TextBlock>()
                    .Where(label => label.IsEffectivelyVisible && !string.IsNullOrEmpty(label.Text))
                    .Select(label => new { text = label.Text, width = label.Bounds.Width, height = label.Bounds.Height,
                        textWidth = label.TextLayout.WidthIncludingTrailingWhitespace, textHeight = label.TextLayout.Height,
                        fits = FullyVisible(window, label)
                            && label.TextLayout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1
                            && label.TextLayout.Height <= label.Bounds.Height + 1 }).ToArray() : null };
        }).ToArray();
        string metadata = JsonSerializer.Serialize(new
        {
            scene, scenario.Mode, scenario.Language, scenario.Theme, scenario.Wide,
            actualTheme = window.ActualThemeVariant.ToString(), actualLanguage = Texts.Language,
            requestedOwnerWidth = scenario.Width, requestedOwnerHeight = scenario.Height,
            actualClientWidth = window.ClientSize.Width, actualClientHeight = window.ClientSize.Height,
            renderScaling = window.RenderScaling, requestedScale = Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"),
            pixelWidth = (int)Math.Ceiling(window.Bounds.Width * window.RenderScaling),
            pixelHeight = (int)Math.Ceiling(window.Bounds.Height * window.RenderScaling), controls = geometry
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, stem + ".json"), metadata);
        Console.WriteLine("SMOKE group visual geometry: " + stem + " " + metadata);
        Require(window.ActualThemeVariant.ToString() == scenario.Theme && Texts.Language == scenario.Language,
            stem + ": actual theme/language do not match scenario.");
        Require(geometry.All(item => item.fullyVisible), stem + ": a required control is clipped; inspect PNG/JSON.");
        Require(geometry.All(item => item.labels == null || item.labels.All(label => label.fits)),
            stem + ": a required button label is clipped; inspect PNG/JSON.");
        Button[] footerButtons = controls.OfType<Button>().Where(button => button.Parent is WrapPanel { Name: "GroupFooter" }).ToArray();
        for (int left = 0; left < footerButtons.Length; left++)
        for (int right = left + 1; right < footerButtons.Length; right++)
        {
            Rect a = BoundsIn(window, footerButtons[left]), b = BoundsIn(window, footerButtons[right]);
            Rect overlap = a.Intersect(b);
            Require(overlap.Width <= 1 || overlap.Height <= 1, stem + ": footer controls overlap.");
        }
    }

    private static Rect BoundsIn(Window window, Control control) =>
        control.TranslatePoint(default, window) is Point point ? new Rect(point, control.Bounds.Size) : default;

    private static void AssertCommandButtons(ReaderGroupsDialog dialog)
    {
        foreach (Button button in dialog.GetVisualDescendants().OfType<Button>().Where(button => button.Command != null))
            Require(button.IsEffectivelyEnabled == button.Command!.CanExecute(button.CommandParameter),
                "Group command enabled state is stale: " + button.Name);
    }

    private static void TraceManager(ReaderGroupsDialog dialog, string stage)
    {
        static object WindowStateOf(Window window) => new { window.Title, window.IsActive, window.IsEnabled,
            window.IsEffectivelyEnabled, bounds = window.Bounds.ToString(), client = window.ClientSize.ToString(),
            window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight,
            focused = (window.FocusManager?.GetFocusedElement() as Control)?.Name,
            focusedType = window.FocusManager?.GetFocusedElement()?.GetType().Name };
        Console.WriteLine("SMOKE group manager state: " + JsonSerializer.Serialize(new
        {
            stage, window = WindowStateOf(dialog), owner = dialog.Owner is Window owner ? WindowStateOf(owner) : null,
            controls = dialog.GetVisualDescendants().OfType<Control>()
                .Where(control => control is ListBox || control is Button { Command: not null })
                .Select(control => new { control.Name, control.IsVisible, control.IsEnabled, control.IsEffectivelyEnabled,
                    bounds = BoundsIn(dialog, control).ToString(), fullyVisible = FullyVisible(dialog, control),
                    selectedIndex = (control as ListBox)?.SelectedIndex, itemCount = (control as ListBox)?.ItemCount,
                    commandCanExecute = control is Button button ? button.Command?.CanExecute(button.CommandParameter) : null })
        }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    private static void TryFailureDiagnostic(Action diagnostic, string name)
    {
        try { diagnostic(); }
        catch (Exception exception)
        {
            // Each diagnostic is independent; neither may replace the original test exception.
            try { Console.Error.WriteLine("SMOKE group failure diagnostic failed (" + name + "): " + exception); }
            catch { /* Preserve the original failure even if the output stream is unavailable. */ }
        }
    }

    private static bool FullyVisible(Window window, Control control)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        Rect bounds = BoundsIn(window, control);
        Rect visible = bounds.Intersect(new Rect(window.Bounds.Size));
        // Testing only window bounds would incorrectly count offscreen scrolled editors as visible.
        foreach (ScrollContentPresenter viewport in control.GetVisualAncestors().OfType<ScrollContentPresenter>())
            if (viewport.TranslatePoint(default, window) is Point point)
                visible = visible.Intersect(new Rect(point, viewport.Bounds.Size));
        return visible.Width >= bounds.Width - 1 && visible.Height >= bounds.Height - 1;
    }

    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }
    private static void Click(Button button) => new ButtonAutomationPeer(button).Invoke();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
