using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;
using Serilog;

namespace MeowChan.Desktop.Views.Settings;

/// <summary>
/// TH3 + TH4: "Customize colors…" of Settings › Appearance. A modal dialog (modals plan MD1, size step XL) over a <see cref="ThemeConfiguratorModel"/>:
/// six accent presets, the Light | Dark choice (starting on the variant shown now), one <see cref="ThemeColorRow"/> per role, and a live
/// <see cref="ThemeColorPreviewCard"/> of the chosen variant painted from the draft's own palette. Apply (primary, right-most) is enabled
/// only while the draft differs from what is saved and both variants pass the contrast gate; the reason it is not shows as a sentence in the footer.
/// "Reset all" returns both variants to the built-in colors. Escape first closes an open picker or takes back a hex entry that is no colour,
/// then closes like × and the Cancel button: with unsaved changes it asks first (Discard changes, Continue editing).
/// Nothing is saved or applied until Apply; the dialog itself does not touch the settings, it hands the draft to <c>commit</c>.
/// </summary>
internal sealed class ThemeColorsDialog : Window
{
    private readonly ThemeConfiguratorModel _model;
    private readonly Func<ThemeColorOverrides, Task> _commit;
    private readonly TwoWayToggle _variant = new() { Name = "ThemeColorsVariant" };
    private readonly TextBlock _intro = new() { Name = "ThemeColorsIntro", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock _presetsLabel = new() { TextWrapping = TextWrapping.Wrap, Classes = { "workspace-field-label" } };
    private readonly TextBlock _presetsHint = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly WrapPanel _presetRow = new() { Name = "ThemeAccentPresets", ItemSpacing = 8, LineSpacing = 8 };
    private readonly List<PresetButton> _presets = new();
    private readonly List<ThemeColorRow> _rows = new();
    private readonly ThemeColorPreviewCard _preview = new();
    private readonly TextBlock _status = new() { Name = "ThemeColorsStatus", TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "workspace-error" } };
    private readonly TextBlock _error = new() { Name = "ThemeColorsError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Name = "ThemeColorsApply", Classes = { "primary" } };
    private readonly Button _cancel = new() { Name = "ThemeColorsCancel" };
    private readonly Button _resetAll = new() { Name = "ThemeColorsResetAll" };
    private ThemeTokenTheme _theme;
    private bool _busy, _asking, _closeConfirmed, _closed;

    private sealed record PresetButton(ThemeAccentPreset Preset, ToggleButton Button, Border Light, Border Dark, TextBlock Caption);

    internal ThemeColorsDialog(ThemeConfiguratorModel model, ThemeTokenTheme initialVariant, Func<ThemeColorOverrides, Task> commit)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _theme = initialVariant;
        Name = "ThemeColorsDialog";
        DialogWindow.Configure(this, DialogSize.XL, modal: true);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        foreach (ThemeAccentPreset preset in ThemeAccentPresets.All) _presets.Add(BuildPreset(preset));
        foreach (PresetButton preset in _presets) _presetRow.Children.Add(preset.Button);
        foreach (ThemeUserColor role in ThemeUserColors.All)
        {
            ThemeColorRow row = new(role);
            row.ColorChosen += OnColorChosen;
            row.ResetRequested += OnResetRequested;
            _rows.Add(row);
        }

        StackPanel header = new()
        {
            Spacing = 12,
            Children =
            {
                _intro,
                new StackPanel { Spacing = 4, Children = { _presetsLabel, _presetsHint, _presetRow } },
                _variant
            }
        };
        StackPanel rows = new() { Name = "ThemeColorRows", Margin = new Thickness(0, 0, 8, 0) };
        foreach (ThemeColorRow row in _rows) rows.Children.Add(row);
        ScrollViewer list = new()
        {
            Name = "ThemeColorRowsScroll", Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid body = new() { Margin = new Thickness(16, 0, 16, 16), ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = ThemeColorsMetrics.ColumnGap };
        body.Children.Add(list);
        Grid.SetColumn(_preview, 1); body.Children.Add(_preview);
        // The reason Apply is off is a full-width line above the actions, so a long sentence never squeezes the buttons.
        Border footer = DialogFooter.Build(_apply, new Control[] { _resetAll, _cancel }, _status, statusAbove: true);
        DialogWindow.Frame(this, DialogFrame.Build(header, body, footer, DialogFrame.ErrorStrip(_error), fillBody: true), modal: true, captionName: "ThemeColorsChrome");

        _variant.SelectedIndex = initialVariant == ThemeTokenTheme.Light ? 0 : 1;
        _variant.SelectionChanged += (_, _) => { _theme = _variant.SelectedIndex == 0 ? ThemeTokenTheme.Light : ThemeTokenTheme.Dark; RefreshAll(); };
        _model.Changed += OnModelChanged;
        _apply.Click += async (_, _) => await ApplyAsync();
        _cancel.Click += (_, _) => Close();
        _resetAll.Click += (_, _) => _model.ResetAll();
        AutomationProperties.SetControlTypeOverride(_presetRow, Avalonia.Automation.Peers.AutomationControlType.Group);
        DialogEscape.Attach(this, TryCancelLocal, () => _busy);
        Texts.Changed += OnLanguageChanged;
        Closed += (_, _) => { _closed = true; Texts.Changed -= OnLanguageChanged; _model.Changed -= OnModelChanged; };
        Opened += (_, _) => (_variant.SelectedIndex == 0 ? _variant.First : _variant.Second).Focus();
        ApplyTexts();
        RefreshAll();
    }

    internal ThemeConfiguratorModel Model => _model;
    internal ThemeTokenTheme Variant => _theme;
    internal TwoWayToggle VariantToggle => _variant;
    internal IReadOnlyList<ThemeColorRow> Rows => _rows;
    internal ThemeColorPreviewCard Preview => _preview;
    internal Button ApplyButton => _apply;
    internal Button CancelButton => _cancel;
    internal Button ResetAllButton => _resetAll;
    internal TextBlock StatusText => _status;
    internal TextBlock ErrorText => _error;
    internal IReadOnlyList<ToggleButton> PresetButtons => _presets.Select(preset => preset.Button).ToArray();
    internal bool IsAsking => _asking;

    /// <summary>Opens the dialog over <paramref name="owner"/>; it returns when the dialog is closed.</summary>
    internal static Task ShowAsync(Window owner, ThemeConfiguratorModel model, ThemeTokenTheme initialVariant, Func<ThemeColorOverrides, Task> commit)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new ThemeColorsDialog(model, initialVariant, commit).ShowDialog(owner);
    }

    /// <summary>Shows the colours of one variant (the Hell | Dunkel choice by code; tests and smokes).</summary>
    internal void ShowVariant(ThemeTokenTheme theme) => _variant.SelectedIndex = theme == ThemeTokenTheme.Light ? 0 : 1;

    /// <summary>Applies the draft like the Apply button; completes when the commit has finished.</summary>
    internal Task ApplyNowAsync() => ApplyAsync();

    private PresetButton BuildPreset(ThemeAccentPreset preset)
    {
        Border light = new() { Name = "ThemeAccentPresetLight_" + preset.Id }, dark = new() { Name = "ThemeAccentPresetDark_" + preset.Id };
        Grid halves = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), Width = ThemeColorsMetrics.Swatch, Height = ThemeColorsMetrics.PresetSwatchHeight, ClipToBounds = true };
        halves.Children.Add(light);
        Grid.SetColumn(dark, 1); halves.Children.Add(dark);
        Border swatch = new() { Child = halves, ClipToBounds = true, BorderThickness = new Thickness(1) };
        swatch[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        swatch[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
        TextBlock caption = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Classes = { "workspace-meta" } };
        ToggleButton button = new()
        {
            Name = "ThemeAccentPreset_" + preset.Id, Padding = new Thickness(8, 4), HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new StackPanel { Spacing = 4, Children = { swatch, caption } }
        };
        button.Click += (_, _) =>
        {
            _model.ApplyPreset(preset);
            // A click on a preset that is already on changes nothing; the button shows what the model says.
            RefreshAll();
        };
        return new PresetButton(preset, button, light, dark, caption);
    }

    private void OnColorChosen(ThemeColorRow row, Color color) => _model.SetRole(_theme, row.Role.Id, color);

    private void OnResetRequested(ThemeColorRow row) => _model.ResetRole(_theme, row.Role.Id);

    private void OnModelChanged(object? sender, EventArgs e) => RefreshAll();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        ApplyTexts();
        RefreshAll();
    }

    private void ApplyTexts()
    {
        Title = Texts.T("theme.colors.title");
        _intro.Text = Texts.T("theme.colors.intro");
        _presetsLabel.Text = Texts.T("theme.colors.presets");
        _presetsHint.Text = Texts.T("theme.colors.presetsHint");
        AutomationProperties.SetName(_presetRow, _presetsLabel.Text);
        AutomationProperties.SetName(_variant, Texts.T("theme.colors.variantLabel"));
        _variant.SetItems(ThemeColorsText.VariantName(ThemeTokenTheme.Light), ThemeColorsText.VariantName(ThemeTokenTheme.Dark));
        foreach (PresetButton preset in _presets)
        {
            string name = ThemeColorsText.PresetName(preset.Preset.Id);
            preset.Caption.Text = name;
            string use = Texts.T("theme.accent.use", name);
            AutomationProperties.SetName(preset.Button, use);
            ToolTip.SetTip(preset.Button, use);
        }
        _apply.Content = Texts.T("theme.colors.apply");
        AutomationProperties.SetName(_apply, _apply.Content.ToString());
        _cancel.Content = Texts.T("action.cancel");
        AutomationProperties.SetName(_cancel, _cancel.Content.ToString());
        _resetAll.Content = Texts.T("theme.colors.resetAll");
        AutomationProperties.SetName(_resetAll, _resetAll.Content.ToString());
    }

    /// <summary>Shows the model: the rows and the preview of the chosen variant, the presets, and the footer.</summary>
    private void RefreshAll()
    {
        if (_closed) return;
        IReadOnlyList<ThemeRoleView> roles = _model.Roles(_theme);
        for (int index = 0; index < _rows.Count; index++) _rows[index].Refresh(roles[index]);
        _preview.Show(_model.Preview(_theme).Palette, ThemeColorsText.VariantName(_theme));
        ThemeAccentPreset? active = _model.ActivePreset;
        foreach (PresetButton preset in _presets)
        {
            preset.Light.Background = new SolidColorBrush(preset.Preset.For(ThemeTokenTheme.Light) ?? _model.BuiltInAccent(ThemeTokenTheme.Light));
            preset.Dark.Background = new SolidColorBrush(preset.Preset.For(ThemeTokenTheme.Dark) ?? _model.BuiltInAccent(ThemeTokenTheme.Dark));
            preset.Button.IsChecked = preset.Preset == active;
        }
        string? block = ThemeColorsText.BlockText(_model.Block);
        _status.Text = block ?? string.Empty;
        _status.IsVisible = block != null;
        _apply.IsEnabled = _model.CanApply && !_busy;
        AutomationProperties.SetHelpText(_apply, block ?? string.Empty);
        _resetAll.IsEnabled = !_model.Draft.IsEmpty && !_busy;
    }

    private async Task ApplyAsync()
    {
        if (_busy || !_model.CanApply) return;
        _busy = true;
        _error.Text = string.Empty;
        RefreshAll();
        ThemeColorOverrides draft = _model.Draft;
        try
        {
            await _commit(draft);
            _model.MarkSaved(draft);
            _closeConfirmed = true;
            Close();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "The theme colors could not be saved.");
            _error.Text = Texts.T("theme.colors.saveFailed");
        }
        finally
        {
            _busy = false;
            if (!_closed) RefreshAll();
        }
    }

    /// <summary>Escape first closes an open picker, then takes back a hex entry that is no colour; false when neither is the case.</summary>
    private bool TryCancelLocal()
    {
        ThemeColorRow? open = _rows.FirstOrDefault(row => row.IsPickerOpen);
        if (open != null) { open.SetPickerOpen(false); return true; }
        ThemeColorRow? pending = _rows.FirstOrDefault(row => row.HasPendingInvalidText);
        if (pending == null) return false;
        pending.RestoreHex();
        return true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason != WindowCloseReason.WindowClosing)
        {
            // The owner closing or the app quitting never waits for this question; the draft is simply dropped.
            return;
        }
        if (_busy) { e.Cancel = true; return; }
        if (_closeConfirmed || !_model.IsDirty) return;
        e.Cancel = true;
        if (!_asking) _ = AskBeforeDiscardingAsync();
    }

    private async Task AskBeforeDiscardingAsync()
    {
        _asking = true;
        try
        {
            bool discard = await DialogPrompt.ConfirmAsync(this, Title ?? string.Empty, Texts.T("theme.colors.discardQuestion"),
                Texts.T("dialog.discardChanges"), Texts.T("dialog.keepEditing"));
            if (discard && !_busy && !_closed) { _closeConfirmed = true; Close(); }
        }
        finally { _asking = false; }
    }
}
