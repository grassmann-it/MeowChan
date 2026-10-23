using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaRatingChecks
{
    private static async Task SelectPresentationMode(string mode)
    {
        ComboBox modes = _window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        await Program.WaitUntilAsync(() => modes.IsEffectivelyVisible && modes.IsEffectivelyEnabled);
        Log("Apply presentation workspace mode " + mode,
            "actual footer ComboBox selection property invokes production SaveAppearanceAsync", modes);
        modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == mode);
        // A direct SessionHost settings write does not apply MainWindow appearance/minima.
        // Use its real footer handler and wait for the UI mode, not only the stored mode.
        await Program.WaitUntilAsync(() => _window.SessionHost.Config.WorkspaceMode == mode
            && (modes.SelectedItem as ComboBoxItem)?.Tag as string == mode
            && Field<string?>(_window, "_appliedWorkspaceMode") == mode
            && !Field<bool>(_window, "_updatingWorkspacePreferences")
            && _window.FindControl<Button>("AddUrlButton")!.IsEnabled);
    }

    private static async Task PreparePresentationCell(string mode, string language, ThemeVariant theme, int width, int height)
    {
        int transitionRecords = 0;
        string stage = "before mode selection";
        void Snapshot(string reason, Exception? error = null)
        {
            ComboBox modes = _window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
            var snapshot = new
            {
                sequence = Evidence.Count + 1, presentationReadiness = reason, stage,
                requested = new { mode, language, theme = theme.ToString(), width, height },
                actual = new
                {
                    mode = _window.SessionHost.Config.WorkspaceMode,
                    selectedMode = (modes.SelectedItem as ComboBoxItem)?.Tag as string,
                    appliedMode = Field<string?>(_window, "_appliedWorkspaceMode"),
                    applyingWorkspace = Field<bool>(_window, "_updatingWorkspacePreferences"),
                    minimumCorrectionQueued = Field<bool>(_window, "_minimumSizeCorrectionQueued"),
                    configuredLanguage = _window.SessionHost.Config.Language, language = Texts.Language,
                    configuredTheme = _window.SessionHost.Config.Theme,
                    appRequestedTheme = Application.Current?.RequestedThemeVariant?.ToString(),
                    appActualTheme = Application.Current?.ActualThemeVariant.ToString(),
                    windowRequestedTheme = _window.RequestedThemeVariant?.ToString(),
                    windowActualTheme = _window.ActualThemeVariant.ToString(),
                    _window.IsLoaded, _window.IsVisible, _window.IsActive, _window.IsEnabled,
                    _window.IsKeyboardFocusWithin, focus = Focus(_window),
                    _window.Width, _window.Height, _window.MinWidth, _window.MinHeight,
                    maxWidth = _window.MaxWidth.ToString(CultureInfo.InvariantCulture),
                    maxHeight = _window.MaxHeight.ToString(CultureInfo.InvariantCulture), _window.CanResize,
                    windowState = _window.WindowState.ToString(), sizeToContent = _window.SizeToContent.ToString(),
                    bounds = _window.Bounds.ToString(), client = _window.ClientSize.ToString(),
                    frame = _window.FrameSize?.ToString(), position = _window.Position.ToString(),
                    scale = _window.RenderScaling,
                    workingArea = _window.Screens.ScreenFromWindow(_window)?.WorkingArea.ToString(),
                    ownedWindows = _window.OwnedWindows.Select(window => new
                    { type = window.GetType().Name, window.IsVisible, window.IsActive, window.IsEnabled,
                        correctOwner = ReferenceEquals(window.Owner, _window), focus = Focus(window) }).ToArray()
                },
                gates = new
                {
                    active = _window.IsActive, themeMatches = _window.ActualThemeVariant == theme,
                    widthMatches = Math.Abs(_window.ClientSize.Width - width) <= 1,
                    heightMatches = Math.Abs(_window.ClientSize.Height - height) <= 1,
                    modeMatches = _window.SessionHost.Config.WorkspaceMode == mode,
                    languageMatches = Texts.Language == language
                },
                error = error?.ToString(),
                scope = "presentation owner readiness and actual geometry; no native-input or human acceptance claim"
            };
            Evidence.Add(snapshot);
            Console.WriteLine("URSA16 presentation-readiness: " + JsonSerializer.Serialize(snapshot));
            SaveEvidence(error == null ? "running presentation readiness" : "failed presentation readiness");
        }
        void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (transitionRecords >= 48) return;
            if (args.Property.Name is "ClientSize" or "Bounds" or "MinWidth" or "MinHeight" or "Width" or "Height"
                or "IsActive" or "IsVisible" or "WindowState" or "ActualThemeVariant")
            {
                transitionRecords++;
                Snapshot("owner property changed: " + args.Property.Name);
            }
        }
        _window.PropertyChanged += Changed;
        try
        {
            Snapshot("entry");
            await SelectPresentationMode(mode);
            stage = "mode applied"; Snapshot("after actual footer mode application");
            Log("Set presentation language/theme " + language + "/" + theme,
                "fixture view-preference commit and actual language/theme application");
            await _window.SessionHost.SaveViewPreferencesAsync(config =>
            { config.Language = language; config.Theme = theme.ToString(); });
            TabControl readerTabs = CurrentReaderTabs();
            object? chosenReaderTab = readerTabs.SelectedItem;
            string previousLanguage = Texts.Language;
            bool languageChanges = Texts.Language != language;
            ComboBox versionScope = _page.FindControl<ComboBox>("ArchiveVersionScope")!;
            ComboBox mediaSort = _page.FindControl<ComboBox>("ArchiveMediaSort")!;
            int chosenVersionScope = versionScope.SelectedIndex, chosenMediaSort = mediaSort.SelectedIndex;
            bool chosenAllVersions = Field<bool>(_page, "_allVersions");
            int chosenSort = Field<int>(_page, "_mediaSort");
            ListBox previousTiles = await CurrentMediaTilesAsync();
            var previousMediaKeys = previousTiles.Items.Cast<ArchiveAssetRow>().Select(KeyOf).ToArray();
            Require(chosenVersionScope == 1 && chosenAllVersions && chosenMediaSort == 2 && chosenSort == 2
                && previousMediaKeys.Length == 20,
                "Language preservation must start with all twenty versions and the chosen non-default sort");
            ReaderTreeSnapshot("before presentation language change");
            Require((chosenReaderTab as Control)?.Name == "ArchiveMediaTab",
                "Presentation language change must start on the actual media tab");
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await Dispatcher.UIThread.InvokeAsync(_window.UpdateLayout, DispatcherPriority.Render);
            ReaderTreeSnapshot("after presentation language change");
            Require(ReferenceEquals(CurrentReaderTabs(), readerTabs)
                && ReferenceEquals(readerTabs.SelectedItem, chosenReaderTab),
                "Language change must preserve the chosen media tab and actual reader instance");
            ListBox currentTiles = await CurrentMediaTilesAsync();
            Require(currentTiles.IsLoaded && ReferenceEquals(TopLevel.GetTopLevel(currentTiles), _window),
                "Language change must keep actual media tiles attached to the owner");
            var currentMediaKeys = currentTiles.Items.Cast<ArchiveAssetRow>().Select(KeyOf).ToArray();
            bool choicesPreserved = versionScope.SelectedIndex == chosenVersionScope
                && Field<bool>(_page, "_allVersions") == chosenAllVersions
                && mediaSort.SelectedIndex == chosenMediaSort && Field<int>(_page, "_mediaSort") == chosenSort;
            bool identitiesAndOrderPreserved = previousMediaKeys.SequenceEqual(currentMediaKeys);
            Evidence.Add(new { sequence = Evidence.Count + 1, mediaLanguagePreservation = "actual version scope, sort and ordered exact identities",
                previousLanguage, language, languageChanges, choicesPreserved, identitiesAndOrderPreserved,
                sameAttachedTiles = ReferenceEquals(previousTiles, currentTiles),
                before = new { versionScope = chosenVersionScope, allVersions = chosenAllVersions,
                    mediaSort = chosenMediaSort, sort = chosenSort, mediaKeys = previousMediaKeys },
                after = new { versionScope = versionScope.SelectedIndex, allVersions = Field<bool>(_page, "_allVersions"),
                    mediaSort = mediaSort.SelectedIndex, sort = Field<int>(_page, "_mediaSort"), mediaKeys = currentMediaKeys } });
            SaveEvidence("running presentation version/sort/identity preservation");
            Require(choicesPreserved && identitiesAndOrderPreserved && ReferenceEquals(previousTiles, currentTiles),
                "Language change must preserve chosen version scope, non-default sort and all ordered exact media identities");
            Evidence.Add(new { sequence = Evidence.Count + 1,
                assertion = "Language application preserves actual media tab and attached tiles", result = true,
                languageChanges, language, selectedTab = (readerTabs.SelectedItem as Control)?.Name });
            SaveEvidence("running presentation language preservation");
            stage = "before requested resize"; Snapshot("before resize and activation");
            _window.Width = width; _window.Height = height;
            _window.Activate();
            await Dispatcher.UIThread.InvokeAsync(_window.UpdateLayout, DispatcherPriority.Render);
            stage = "waiting for original owner gate"; Snapshot("after resize and activation request");
            await Program.WaitUntilAsync(() => _window.IsActive && _window.ActualThemeVariant == theme
                && Math.Abs(_window.ClientSize.Width - width) <= 1 && Math.Abs(_window.ClientSize.Height - height) <= 1);
            Require(_window.SessionHost.Config.WorkspaceMode == mode && Texts.Language == language,
                "Actual presentation cell state differs");
            stage = "ready"; Snapshot("original owner gate passed");
        }
        catch (Exception error)
        {
            // Diagnostics are best effort; preserve the original readiness exception and stack.
            try { Snapshot("readiness failure", error); } catch (Exception) { }
            try { ReaderTreeSnapshot("presentation language/readiness failure", error); } catch (Exception) { }
            try
            {
                if (_window.IsVisible) Program.CaptureWindow(_window, "smoke-rating-presentation-readiness-failure.png");
            }
            catch (Exception) { }
            throw;
        }
        finally { _window.PropertyChanged -= Changed; }
    }

    private static async Task PreparePresentationDialog(Window dialog, Control editor, string cell)
    {
        int transitions = 0;
        void Snapshot(string stage, Exception? error = null)
        {
            var snapshot = new
            {
                sequence = Evidence.Count + 1, presentationDialogReadiness = stage, cell,
                dialog = new
                {
                    type = dialog.GetType().Name, dialog.IsLoaded, dialog.IsVisible, dialog.IsActive,
                    dialog.IsEnabled, dialog.IsKeyboardFocusWithin, focus = Focus(dialog),
                    bounds = dialog.Bounds.ToString(), client = dialog.ClientSize.ToString(),
                    windowState = dialog.WindowState.ToString(), actualTheme = dialog.ActualThemeVariant.ToString(),
                    correctOwner = ReferenceEquals(dialog.Owner, _window),
                    ownerListsDialog = _window.OwnedWindows.Contains(dialog)
                },
                editor = new
                {
                    editor.Name, editor.IsLoaded, editor.IsVisible, editor.IsEffectivelyVisible,
                    editor.IsEnabled, editor.IsEffectivelyEnabled, editor.Focusable, editor.IsFocused,
                    editor.IsKeyboardFocusWithin, bounds = editor.Bounds.ToString(),
                    topLevelType = TopLevel.GetTopLevel(editor)?.GetType().Name,
                    ownTopLevel = ReferenceEquals(TopLevel.GetTopLevel(editor), dialog),
                    actualFocus = ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), editor)
                },
                owner = new
                {
                    _window.IsVisible, _window.IsActive, _window.IsEnabled, focus = Focus(_window),
                    bounds = _window.Bounds.ToString(), actualTheme = _window.ActualThemeVariant.ToString(),
                    ownedWindows = _window.OwnedWindows.Select(window => new
                    { type = window.GetType().Name, window.IsVisible, window.IsActive, window.IsEnabled,
                        focus = Focus(window), isTarget = ReferenceEquals(window, dialog) }).ToArray()
                },
                gates = new
                {
                    active = dialog.IsActive, editorVisible = editor.IsEffectivelyVisible,
                    editorPositiveWidth = editor.Bounds.Width > 0
                },
                error = error?.ToString(),
                scope = "actual presentation dialog activation/focus and ownership; not native input or human acceptance"
            };
            Evidence.Add(snapshot);
            Console.WriteLine("URSA16 presentation-dialog-readiness: " + JsonSerializer.Serialize(snapshot));
            SaveEvidence(error == null ? "running presentation dialog readiness" : "failed presentation dialog readiness");
        }
        void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (transitions >= 24) return;
            if (args.Property.Name is "IsActive" or "IsVisible" or "IsEnabled" or "IsKeyboardFocusWithin"
                or "Bounds" or "ClientSize" or "WindowState")
            {
                transitions++;
                Snapshot("dialog property changed: " + args.Property.Name);
            }
        }
        dialog.PropertyChanged += Changed;
        try
        {
            Snapshot("before activation");
            Require(ReferenceEquals(dialog.Owner, _window) && _window.OwnedWindows.Contains(dialog),
                "Presentation rating dialog must belong to the actual owner");
            Require(ReferenceEquals(TopLevel.GetTopLevel(editor), dialog),
                "Presentation rating editor must belong to the actual dialog TopLevel");
            Log("Activate the actual presentation rating dialog", "Window.Activate request; actual IsActive gate retained", editor);
            dialog.Activate();
            Snapshot("after activation request");
            await Program.WaitUntilAsync(() => dialog.IsActive && editor.IsEffectivelyVisible && editor.Bounds.Width > 0);
            Snapshot("original dialog gate passed");
            Require(editor.Focus(NavigationMethod.Tab) && ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), editor),
                "Presentation star editor must own actual focus");
            Snapshot("actual editor focus confirmed");
        }
        catch (Exception error)
        {
            // Try both diagnostics independently, without replacing the original failure.
            try { Snapshot("readiness or focus failure", error); } catch (Exception) { }
            try
            {
                if (dialog.IsVisible)
                    Program.CaptureWindow(dialog, "smoke-rating-matrix-" + cell + "-dialog-readiness-failure.png");
            }
            catch (Exception) { }
            throw;
        }
        finally { dialog.PropertyChanged -= Changed; }
    }
}
