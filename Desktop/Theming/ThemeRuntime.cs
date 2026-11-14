using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Serilog;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// Writes generated palettes (<see cref="ThemePalette"/>) into one resource dictionary: a single merged dictionary of the
/// target whose <c>ThemeDictionaries[Dark/Light]</c> hold the brushes and shadows. Every apply replaces that merged dictionary
/// as a whole, so the owner raises one resource change and every <c>DynamicResource</c> follows live, without a restart.
/// Separate from <see cref="ThemeRuntime"/> so the applying can be tested on a plain dictionary without an application.
/// </summary>
internal sealed class ThemeRuntimeHost
{
    private readonly IResourceDictionary _target;
    private readonly FrozenDictionary<ThemeTokenTheme, ThemePaletteBase> _bases;
    private readonly Dictionary<ThemeTokenTheme, ThemePalette> _palettes = new();
    // The stored overrides last handled per base (applied or refused), so an unchanged settings save does not rebuild the palette.
    private readonly Dictionary<ThemeTokenTheme, IReadOnlyDictionary<string, string>> _requested = new();
    private ResourceDictionary? _generated;

    internal ThemeRuntimeHost(IResourceDictionary target, ThemePaletteBase dark, ThemePaletteBase light)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentNullException.ThrowIfNull(dark);
        ArgumentNullException.ThrowIfNull(light);
        if (dark.Theme != ThemeTokenTheme.Dark || light.Theme != ThemeTokenTheme.Light)
            throw new ArgumentException("The Dark and Light bases must be the Dark and Light palettes.");
        _bases = new Dictionary<ThemeTokenTheme, ThemePaletteBase> { [ThemeTokenTheme.Dark] = dark, [ThemeTokenTheme.Light] = light }.ToFrozenDictionary();
    }

    /// <summary>The merged dictionary that currently carries the generated palettes; null before the first apply.</summary>
    internal ResourceDictionary? Generated => _generated;

    /// <summary>The palette last applied for a base, or null.</summary>
    internal ThemePalette? Current(ThemeTokenTheme theme) => _palettes.GetValueOrDefault(theme);

    /// <summary>The built-in base of a theme as read at startup.</summary>
    internal ThemePaletteBase BaseOf(ThemeTokenTheme theme) => _bases[theme];

    /// <summary>Builds the palette of <paramref name="theme"/> from its base and the overrides and applies it; the other base keeps its palette.</summary>
    internal ThemePalette Apply(ThemeTokenTheme theme, IReadOnlyDictionary<string, Color>? overrides)
    {
        ThemePalette palette = ThemePalette.Build(_bases[theme], overrides);
        Use(palette);
        // Raw seeds bypass the stored-override bookkeeping: only "no overrides" is known to match an empty stored set.
        if (overrides is null or { Count: 0 }) _requested[theme] = FrozenDictionary<string, string>.Empty;
        else _requested.Remove(theme);
        return palette;
    }

    /// <summary>Whether <see cref="ApplyOverrides"/> would look at a base again (its stored overrides differ from the last ones seen).</summary>
    internal bool NeedsApply(ThemeColorOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        return _bases.Keys.Any(theme => !_requested.TryGetValue(theme, out IReadOnlyDictionary<string, string>? last)
            || !ThemeColorOverrides.Same(last, overrides.For(theme)));
    }

    /// <summary>
    /// TH2: applies stored overrides (<c>Config.ThemeOverrides</c>) to both bases. A base whose overrides did not change since the
    /// last call is skipped. Otherwise the overrides are expanded and checked (<see cref="ThemeCustomization.Preview"/>): an
    /// accepted set is applied; a refused one (text contrast below 4.5 : 1, see <see cref="ThemeContrastReport"/>) leaves that
    /// base on its built-in palette. All changed bases are written with one dictionary replacement.
    /// </summary>
    internal ThemeOverridesApplyResult ApplyOverrides(ThemeColorOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var outcomes = new Dictionary<ThemeTokenTheme, ThemeApplyOutcome>();
        var changed = new List<ThemePalette>();
        foreach (ThemeTokenTheme theme in new[] { ThemeTokenTheme.Dark, ThemeTokenTheme.Light })
        {
            IReadOnlyDictionary<string, string> wanted = overrides.For(theme);
            if (_requested.TryGetValue(theme, out IReadOnlyDictionary<string, string>? last) && ThemeColorOverrides.Same(last, wanted))
            {
                outcomes[theme] = new(theme, ThemeApplyStatus.Unchanged, null);
                continue;
            }
            ThemePreview preview = ThemeCustomization.Preview(_bases[theme], overrides.ColorsFor(theme));
            ThemePalette palette = preview.Accepted ? preview.Palette : ThemePalette.Build(_bases[theme]);
            changed.Add(palette);
            _requested[theme] = wanted;
            outcomes[theme] = new(theme, preview.Accepted ? ThemeApplyStatus.Applied : ThemeApplyStatus.Rejected, preview);
        }
        Use(changed.ToArray());
        return new ThemeOverridesApplyResult(outcomes[ThemeTokenTheme.Dark], outcomes[ThemeTokenTheme.Light]);
    }

    /// <summary>Applies already built palettes (one per base at most) with a single dictionary replacement.</summary>
    internal void Use(params ThemePalette[] palettes)
    {
        ArgumentNullException.ThrowIfNull(palettes);
        if (palettes.Length == 0) return;
        foreach (ThemePalette palette in palettes) _palettes[palette.Theme] = palette;
        var next = new ResourceDictionary();
        foreach (var (key, value) in _palettes) next.ThemeDictionaries[ThemeRuntime.Variant(key)] = ToDictionary(value);
        // Add the new one before removing the old one: a lookup never falls back to the XAML palette in between.
        _target.MergedDictionaries.Add(next);
        if (_generated != null) _target.MergedDictionaries.Remove(_generated);
        _generated = next;
    }

    /// <summary>One theme dictionary: a new SolidColorBrush per colour key (brushes are not shared between keys) and the shadows.</summary>
    internal static ResourceDictionary ToDictionary(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var dictionary = new ResourceDictionary();
        foreach (var (key, color) in palette.Colors) dictionary[key] = new SolidColorBrush(color);
        foreach (var (key, shadows) in palette.Shadows) dictionary[key] = shadows;
        return dictionary;
    }
}

/// <summary>
/// TH1: the app's runtime palette. At startup <see cref="Install"/> reads both built-in theme dictionaries of
/// WorkspaceStyles.axaml from the loaded styles (they stay the source of truth and are never modified) and applies them
/// through <see cref="ThemePalette"/> with no overrides, which reproduces them exactly. Afterwards
/// <see cref="Apply(ThemeVariant, IReadOnlyDictionary{string, Color})"/> recolours the whole app live: the generated
/// dictionaries sit in <c>Application.Resources</c>, which Avalonia consults before <c>Application.Styles</c>.
/// <para>
/// No configuration is read here; the window passes <c>Config.ThemeOverrides</c> to <see cref="ApplyOverrides"/> (TH2). The requested variant and <see cref="ThemeLogo"/>
/// are untouched (the variants stay the built-in Light and Dark). If the base cannot be read, the XAML palette simply stays in
/// charge and <see cref="Apply(ThemeVariant, IReadOnlyDictionary{string, Color})"/> reports that nothing was applied.
/// </para>
/// Resources resolved once in code (a <c>TryFindResource</c> whose result is cached) or through <c>StaticResource</c> do not
/// follow a later apply; colour resources in XAML use <c>DynamicResource</c>.
/// </summary>
internal static class ThemeRuntime
{
    private static ThemeRuntimeHost? s_host;

    internal static bool IsInstalled => s_host != null;

    /// <summary>Reads the built-in bases from the application's styles and applies them unchanged. Idempotent.</summary>
    internal static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (s_host != null) return;
        try
        {
            var host = new ThemeRuntimeHost(application.Resources,
                ReadBase(application.Styles, ThemeTokenTheme.Dark), ReadBase(application.Styles, ThemeTokenTheme.Light));
            host.Apply(ThemeTokenTheme.Dark, null);
            host.Apply(ThemeTokenTheme.Light, null);
            s_host = host;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // Fail closed to the XAML palette: the app looks exactly as without the runtime palette.
            Log.Warning(error, "The runtime theme palette could not be installed; the built-in theme dictionaries stay in use.");
        }
    }

    /// <summary>
    /// Applies <paramref name="overrides"/> (seed key → colour) on top of the built-in <paramref name="baseVariant"/>
    /// (Light, Dark or a variant inheriting one of them) and returns the palette now in use; null when the runtime palette is
    /// not installed. An empty map restores the built-in palette of that base. Call on the UI thread.
    /// </summary>
    internal static ThemePalette? Apply(ThemeVariant baseVariant, IReadOnlyDictionary<string, Color> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (s_host == null) return null;
        ThemePalette palette = s_host.Apply(Base(baseVariant), overrides);
        Applied?.Invoke();
        return palette;
    }

    /// <summary>
    /// Raised on the UI thread after every palette apply (the generated dictionary was replaced). <c>DynamicResource</c> follows
    /// by itself; code that resolved a colour once (<c>TryFindResource</c>) re-reads it here, see <see cref="FollowApplied"/>.
    /// </summary>
    internal static event Action? Applied;

    /// <summary>
    /// TH2: applies <c>Config.ThemeOverrides</c> to both bases, live (see <see cref="ThemeRuntimeHost.ApplyOverrides"/>): unchanged
    /// bases are skipped, a set the contrast gate refuses keeps that base on its built-in palette and is logged. Called at startup
    /// and after every settings apply. Null when the runtime palette is not installed. Call on the UI thread.
    /// </summary>
    internal static ThemeOverridesApplyResult? ApplyOverrides(ThemeColorOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (s_host == null) return null;
        ThemeOverridesApplyResult result = s_host.ApplyOverrides(overrides);
        foreach (ThemeApplyOutcome outcome in new[] { result.Dark, result.Light })
            if (outcome is { Status: ThemeApplyStatus.Rejected, Preview: { } preview })
                Log.Warning("The {Theme} colour overrides were not applied: {Count} text contrast pairs are below 4.5:1 (first: {Pairs}).",
                    outcome.Theme, preview.Contrast.Errors.Count, string.Join(", ", preview.Contrast.Errors.Take(3).Select(issue =>
                        issue.Foreground + " on " + issue.Background + (issue.Ratio is { } ratio ? " " + ratio.ToString("0.00", CultureInfo.InvariantCulture) : ""))));
        if (result.Changed) Applied?.Invoke();
        return result;
    }

    /// <summary>Whether <see cref="ApplyOverrides"/> would change anything; false when not installed.</summary>
    internal static bool NeedsApply(ThemeColorOverrides overrides) => s_host?.NeedsApply(overrides) ?? false;

    /// <summary>
    /// TH3: the palette and contrast report <paramref name="overrides"/> would give on the built-in <paramref name="baseVariant"/>,
    /// without applying anything; null when the runtime palette is not installed.
    /// </summary>
    internal static ThemePreview? Preview(ThemeVariant baseVariant, IReadOnlyDictionary<string, Color> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        return s_host == null ? null : ThemeCustomization.Preview(s_host.BaseOf(Base(baseVariant)), overrides);
    }

    /// <summary>The built-in base of a variant as read at startup; null when the runtime palette is not installed.</summary>
    internal static ThemePaletteBase? BaseOf(ThemeVariant baseVariant) => s_host?.BaseOf(Base(baseVariant));

    /// <summary>
    /// Runs <paramref name="reapply"/> after every palette apply while <paramref name="control"/> is in the visual tree (the
    /// subscription ends when it leaves, so a closed view is not kept alive). For colours resolved once in code; a theme variant
    /// change still needs the control's own <c>ActualThemeVariantChanged</c>.
    /// </summary>
    internal static void FollowApplied(Visual control, Action reapply)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(reapply);
        bool subscribed = false;
        void OnApplied() => reapply();
        void Subscribe() { if (!subscribed) { Applied += OnApplied; subscribed = true; } }
        void Unsubscribe() { if (subscribed) { Applied -= OnApplied; subscribed = false; } }
        control.AttachedToVisualTree += (_, _) => Subscribe();
        control.DetachedFromVisualTree += (_, _) => Unsubscribe();
        if (control.IsAttachedToVisualTree()) Subscribe();
    }

    /// <summary>The palette in use for a base, or null when the runtime palette is not installed.</summary>
    internal static ThemePalette? Current(ThemeVariant baseVariant) => s_host?.Current(Base(baseVariant));

    /// <summary>The built-in base of a variant: Light, Dark, or what a custom variant inherits from. Default has none.</summary>
    internal static ThemeTokenTheme Base(ThemeVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        for (ThemeVariant? current = variant; current != null; current = current.InheritVariant)
        {
            if (current == ThemeVariant.Dark) return ThemeTokenTheme.Dark;
            if (current == ThemeVariant.Light) return ThemeTokenTheme.Light;
        }
        throw new ArgumentException($"The theme variant {variant} is neither Light nor Dark nor inherits one of them.", nameof(variant));
    }

    internal static ThemeVariant Variant(ThemeTokenTheme theme) => theme switch
    {
        ThemeTokenTheme.Dark => ThemeVariant.Dark,
        ThemeTokenTheme.Light => ThemeVariant.Light,
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    /// <summary>
    /// The colours of one built-in theme as the loaded styles resolve them (WorkspaceStyles.axaml is the last style, so its
    /// dictionaries answer). Every inventory key must be an opaque-opacity solid brush (or the shadow); anything else throws.
    /// </summary>
    internal static ThemePaletteBase ReadBase(IResourceNode styles, ThemeTokenTheme theme)
    {
        ArgumentNullException.ThrowIfNull(styles);
        ThemeVariant variant = Variant(theme);
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (string key in ThemePalette.ColorKeys(theme))
        {
            if (!styles.TryGetResource(key, variant, out object? value) || value is not ISolidColorBrush { Opacity: 1 } brush)
                throw new InvalidOperationException($"The {theme} theme resource \"{key}\" is not a solid colour brush.");
            colors[key] = brush.Color;
        }
        var shadows = new Dictionary<string, BoxShadows>(StringComparer.Ordinal);
        foreach (string key in ThemePalette.ShadowKeys)
        {
            if (!styles.TryGetResource(key, variant, out object? value) || value is not BoxShadows shadow)
                throw new InvalidOperationException($"The {theme} theme resource \"{key}\" is not a box shadow.");
            shadows[key] = shadow;
        }
        return new ThemePaletteBase(theme, colors, shadows);
    }
}

/// <summary>What <see cref="ThemeRuntimeHost.ApplyOverrides"/> did with one base.</summary>
internal enum ThemeApplyStatus
{
    /// <summary>The stored overrides of this base did not change; nothing was rebuilt.</summary>
    Unchanged,
    /// <summary>The overrides passed the gate and are in use (an empty set restores the built-in palette).</summary>
    Applied,
    /// <summary>The gate refused the overrides (see the preview's contrast report); the base uses its built-in palette.</summary>
    Rejected,
}

/// <summary>One base's outcome; <paramref name="Preview"/> is null when unchanged.</summary>
internal sealed record ThemeApplyOutcome(ThemeTokenTheme Theme, ThemeApplyStatus Status, ThemePreview? Preview);

internal sealed record ThemeOverridesApplyResult(ThemeApplyOutcome Dark, ThemeApplyOutcome Light)
{
    internal bool Changed => Dark.Status != ThemeApplyStatus.Unchanged || Light.Status != ThemeApplyStatus.Unchanged;
    internal bool Accepted => Dark.Status != ThemeApplyStatus.Rejected && Light.Status != ThemeApplyStatus.Rejected;
}
