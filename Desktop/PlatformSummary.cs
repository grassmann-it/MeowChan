using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using MeowChan.Desktop.Motion;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// One line that says what the platform layer is doing on this machine: OS, Avalonia backend, render mode, scaling, window
/// chrome, motion policy source and the fonts that were actually resolved. A tester pastes it into a report. It is logged
/// once, after the main window opened (the scaling and fonts need a window), and kept for the Diagnostics page. It holds no
/// user name, machine name or path; any home directory is replaced by <c>~</c> as a last resort.
/// </summary>
internal static class PlatformSummary
{
    private static string? _line;

    /// <summary>The line logged at startup, or null before the main window opened.</summary>
    internal static string? Line => _line;

    /// <summary>Collects and logs the line once per process. Never throws.</summary>
    internal static void LogOnce(Window window)
    {
        if (_line != null) return;
        string line;
        try { line = Format(Collect(window)); }
        catch (Exception exception) { line = "Platform summary unavailable: " + exception.GetType().Name; }
        if (System.Threading.Interlocked.CompareExchange(ref _line, line, null) == null)
            Log.Information("Platform {PlatformSummary:l}", line);
    }

    internal sealed record Facts(string App, string Os, string Runtime, string Backend, string Render, string Scaling,
        string Chrome, string Motion, string UiFont, string PostFont, string Cjk);

    internal static string Format(Facts f) => Sanitize(string.Join(" | ", new[]
    {
        "MeowChan " + f.App, "os=" + f.Os, "runtime=" + f.Runtime, "backend=" + f.Backend, "render=" + f.Render,
        "scaling=" + f.Scaling, "chrome=" + f.Chrome, "motion=" + f.Motion, "font.ui=" + f.UiFont,
        "font.post=" + f.PostFont, "cjk=" + f.Cjk
    }));

    /// <summary>One line, and no home directory even if a part of the platform text contained one.</summary>
    internal static string Sanitize(string text) => Sanitize(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string Sanitize(string text, string? home)
    {
        string line = text.Replace("\r", " ").Replace("\n", " ");
        if (!string.IsNullOrWhiteSpace(home) && home.Length > 1)
            foreach (string variant in new[] { home, home.Replace('\\', '/') }.Distinct())
                line = line.Replace(variant, "~", StringComparison.OrdinalIgnoreCase);
        return line;
    }

    /// <summary>The OS text without the build tail (Linux "#1 SMP ...", macOS "Darwin Kernel Version ...: date; root:xnu-...").</summary>
    internal static string ShortOs(string description)
    {
        string text = description.Trim();
        foreach (string marker in new[] { " #", " Darwin Kernel Version", ": " })
        {
            int at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at > 0) text = text[..at];
        }
        return text.Length <= 80 ? text : text[..80];
    }

    internal static Facts Collect(Window window) => new(
        App: Safe(() => typeof(PlatformSummary).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?"),
        Os: Safe(() => ShortOs(RuntimeInformation.OSDescription) + " " + RuntimeInformation.OSArchitecture),
        Runtime: Safe(() => RuntimeInformation.FrameworkDescription.Replace(".NET ", ".NET") + ", Avalonia " + typeof(Application).Assembly.GetName().Version?.ToString(3)),
        Backend: Safe(() => Backend(window)),
        Render: Safe(Render),
        Scaling: Safe(() => Scaling(window)),
        Chrome: Safe(() => WindowChrome.Mode.ToString()),
        Motion: Safe(MotionPolicy.DescribeSource),
        UiFont: Safe(() => ResolvedFamily(window.FontFamily)),
        PostFont: Safe(() => Application.Current is { } app && app.TryGetResource("Workspace.Font.Reading", app.ActualThemeVariant, out object? resource)
            && resource is FontFamily reading ? ResolvedFamily(reading) : "resource missing"),
        Cjk: Safe(Cjk));

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception exception) { return "unavailable (" + exception.GetType().Name + ")"; }
    }

    private static string Backend(Window window)
    {
        string[] platform = ["Avalonia.Win32", "Avalonia.Native", "Avalonia.X11", "Avalonia.Browser", "Avalonia.LinuxFramebuffer"];
        string assemblies = string.Join("+", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName())
            .Where(name => platform.Contains(name.Name)).Select(name => name.Name + " " + name.Version?.ToString(3)));
        string handle = window.TryGetPlatformHandle()?.HandleDescriptor ?? "no handle";
        return (assemblies.Length == 0 ? "unknown" : assemblies) + " (" + handle + ")";
    }

    // No GPU interface bound means Avalonia draws with Skia on the CPU; otherwise the type names the API (Angle/EGL, WGL, GLX, Metal ...).
    private static string Render()
    {
        object? graphics = AvaloniaLocator.Current.GetService<IPlatformGraphics>();
        return graphics == null ? "software" : "gpu " + graphics.GetType().Name;
    }

    private static string Scaling(Window window)
    {
        string scales = string.Join(",", window.Screens.All.Select(screen => screen.Scaling.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        return window.RenderScaling.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            + " (screens " + window.Screens.All.Count + ": " + (scales.Length == 0 ? "none" : scales) + ")";
    }

    /// <summary>The family a typeface really resolves to, not the font-family string that was asked for.</summary>
    private static string ResolvedFamily(FontFamily requested)
    {
        if (FontManager.Current.TryGetGlyphTypeface(new Typeface(requested), out GlyphTypeface? glyphs) && glyphs != null)
            return glyphs.FamilyName;
        return "not found (asked " + requested.Name + ")";
    }

    // The CJK families the reader asks for in order, and which of them this machine really has.
    private static string Cjk()
    {
        HashSet<string> installed = FontManager.Current.SystemFonts.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(",", ReaderFonts.CjkFallbackFamilies().Select(family => family + (installed.Contains(family) ? "=yes" : "=missing")));
    }
}
