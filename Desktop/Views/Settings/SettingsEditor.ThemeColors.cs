using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;
using MeowChan.Desktop.Views.Settings;

namespace MeowChan;

/// <summary>
/// The "Customize colors…" row of Appearance (TH3). The button opens <see cref="ThemeColorsDialog"/> on the variant shown now. Applying
/// commits only the colors (<see cref="ThemeColorsCommit"/>, the twin of the shortcuts commit): the rest of the Settings draft stays as
/// it is, the profile is saved through the window's settings path and the colors apply live.
/// </summary>
public partial class SettingsEditor
{
    private ThemeColorsDialog? _themeColorsDialog;

    public string ThemeColorsLabel => Texts.T("settings.themeColors.label");
    public string ThemeColorsHint => Texts.T("settings.themeColors.hint");
    public string ThemeColorsOpenLabel => Texts.T("settings.themeColors.open");

    /// <summary>The open colour dialog, or null (smokes and tests).</summary>
    internal ThemeColorsDialog? ThemeColorsWindow => _themeColorsDialog;

    private async void OnThemeColorsClick(object? sender, RoutedEventArgs e)
    {
        if (_themeColorsDialog is { } open) { open.Activate(); return; }
        if (!IsEditable || _isClosed || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (ThemeRuntime.BaseOf(ThemeVariant.Light) == null) return; // the runtime palette is not installed (no application)
        ThemeConfiguratorModel model = new(_config.ThemeOverrides, theme => ThemeRuntime.BaseOf(ThemeRuntime.Variant(theme))!);
        ThemeTokenTheme shown = ThemeRuntime.Base(owner.ActualThemeVariant);
        ThemeColorsDialog dialog = new(model, shown, CommitThemeColorsAsync);
        _themeColorsDialog = dialog;
        try { await dialog.ShowDialog(owner); }
        finally { _themeColorsDialog = null; }
    }

    private async Task CommitThemeColorsAsync(ThemeColorOverrides draft)
    {
        if (IsBusy || !IsAvailable || _isClosed) throw new InvalidOperationException("The settings are busy or unavailable.");
        IsBusy = true;
        try { await ThemeColorsCommit.CommitAsync(_config, draft, _applySettings); }
        finally { IsBusy = false; }
    }
}
