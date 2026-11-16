using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

/// <summary>
/// Bug-class audit 2026-11-16, class 8 (disabled looks like enabled): a class style that sets Foreground or Background on a button (or on its
/// presenter in a state rule) beats the theme's disabled look, so the class has to say what disabled is. The rule of the product: the label in
/// Muted or the whole control dimmed to 0.75 (the disabled chip and tag pill), no hover, pressed or checked fill. Parsed sources only; contrast is
/// computed from the token values, no Avalonia application.
/// </summary>
public sealed class DisabledStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex ClassOfButton = new(@"(?<![\w.])(?<kind>Button|ToggleButton)\.(?<cls>[A-Za-z][\w-]*)", RegexOptions.Compiled);

    // Classes whose rules set a colour and that are never disabled, or whose disabled look is the theme's on purpose, as "kind.class: why".
    // A class is added here only with a reason; every other class has to name :disabled.
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["Button.window-caption"] = "the title-bar buttons are never disabled",
        ["Button.window-close"] = "the title-bar buttons are never disabled"
    };

    private static IEnumerable<XElement> StyleElements() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style");

    private static string[] Parts(XElement style) => ((string?)style.Attribute("Selector") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool SetsColour(XElement style) => style.Elements(A + "Setter").Any(setter =>
        (string?)setter.Attribute("Property") is "Foreground" or "Background" && (string?)setter.Attribute("Value") != null);

    /// <summary>
    /// The button classes the stylesheet gives a colour in a rule that beats the theme's disabled look: a rule on the template presenter (a hover,
    /// pressed or checked fill) or on a child of the button (its label or icon), as "Kind.class". A colour on the control itself is the rest look and
    /// the theme's own disabled rule on the presenter still wins over it.
    /// </summary>
    private static SortedSet<string> ColouredButtonClasses()
    {
        SortedSet<string> found = [];
        foreach (XElement style in StyleElements().Where(SetsColour))
            foreach (string part in Parts(style))
            {
                string head = part.Split(" /template/ ")[0];
                bool onPresenter = part.Contains(" /template/ ");
                bool onChild = head.Contains(' ');
                if (!onPresenter && !onChild) continue;
                // The button is the first compound of the selector, not a button inside another control.
                Match first = ClassOfButton.Match(head);
                if (first.Success && first.Index == 0) found.Add(first.Groups["kind"].Value + "." + first.Groups["cls"].Value);
            }
        return found;
    }

    private static bool NamesDisabled(string kindClass)
    {
        string[] kindAndClass = kindClass.Split('.');
        Regex disabled = new(@"(?<![\w.])" + kindAndClass[0] + @"\." + Regex.Escape(kindAndClass[1]) + @"(?![\w-])(\.[\w-]+)*(:[\w-]+)*:disabled\b");
        return StyleElements().SelectMany(Parts).Any(part => disabled.IsMatch(part.Split(" /template/ ")[0]));
    }

    [Fact]
    public void EveryButtonClassWithAColourNamesItsDisabledLook()
    {
        string[] missing = ColouredButtonClasses().Where(cls => !Exempt.ContainsKey(cls) && !NamesDisabled(cls)).ToArray();
        Assert.True(missing.Count() == 0,
            "These button classes set Foreground or Background and have no ':disabled' rule, so disabled looks like enabled (add one: Muted label or 0.75 opacity, no hover fill):\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void TheGuardSeesAClassWithoutADisabledRule()
    {
        // The scan finds the classes the audit named once they had none: the rule that names them is what the test above looks for.
        Assert.Contains("Button.nav", ColouredButtonClasses());
        Assert.Contains("ToggleButton.segment", ColouredButtonClasses());
        Assert.True(NamesDisabled("Button.ghost"));
        Assert.False(NamesDisabled("Button.no-such-class"));
        // A class that only shares a prefix is not the class.
        Assert.False(NamesDisabled("ToggleButton.segment-first"));
    }

    public static IEnumerable<object[]> FixedClasses() => new[]
    {
        "Button.nav", "ToggleButton.filter-toggle", "ToggleButton.segment", "ToggleButton.breakdown-chip", "ToggleButton.status-toggle",
        "ToggleButton.disclosure", "ToggleButton.gallery-favorite-badge", "ToggleButton.tile-favorite"
    }.Select(cls => new object[] { cls });

    // The disabled look of the product: dimmed to 0.75 like the disabled chip (and the label in Muted where the class has a theme label colour).
    [Theory, MemberData(nameof(FixedClasses))]
    public void ADisabledControlIsDimmedAndNamesTheStatesThatWouldOtherwiseWin(string kindClass)
    {
        string[] kindAndClass = kindClass.Split('.');
        string kind = kindAndClass[0], cls = kindAndClass[1];
        XElement[] disabled = StyleElements().Where(style => Parts(style).Any(part => part.StartsWith(kind + "." + cls + ":", StringComparison.Ordinal) && part.Contains(":disabled"))).ToArray();
        Assert.NotEmpty(disabled);
        Assert.Contains(disabled, style => style.Elements(A + "Setter").Any(setter =>
            (string?)setter.Attribute("Property") == "Opacity" && (string?)setter.Attribute("Value") == "0.75"));
        // No hover, pressed or checked state beats the disabled rule: it names each of them (the ones this class has).
        string[] parts = StyleElements().SelectMany(Parts).ToArray();
        foreach (string state in new[] { "pointerover", "pressed" })
            if (parts.Any(part => Regex.IsMatch(part.Split(" /template/ ")[0], Regex.Escape(kind + "." + cls) + @"(:[\w-]+)*:" + state + @"\b") && !part.Contains(":disabled")))
                Assert.Contains(parts, part => part.StartsWith(kind + "." + cls + ":", StringComparison.Ordinal) && part.Contains(":disabled") && part.Contains(":" + state));
    }

    [Theory, MemberData(nameof(FixedClasses))]
    public void ADisabledStateIsNeverTheHoverOrPressedFill(string kindClass)
    {
        string[] kindAndClass = kindClass.Split('.');
        string cls = kindAndClass[1];
        foreach (XElement style in StyleElements().Where(style => Parts(style).Any(part =>
                     part.Contains(":disabled") && Regex.IsMatch(part, @"\." + Regex.Escape(cls) + @"(?![\w-])"))))
            foreach (XElement setter in style.Elements(A + "Setter"))
            {
                string value = (string?)setter.Attribute("Value") ?? "";
                Assert.DoesNotContain("Workspace.Hover", value);
                // A pressed or set (checked) toggle keeps its accent fill and edge when disabled, never a hover one; accent text would not be Muted.
                if ((string?)setter.Attribute("Property") == "Foreground") Assert.DoesNotContain("Workspace.Accent", value);
            }
    }

    // The label is Muted on every surface a disabled control sits on: once in the dictionary (4.5:1, the body-text rule) and dimmed to 0.75
    // (still 3:1, the mark rule: a disabled label is exempt from WCAG but must not vanish).
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void TheDimmedMutedLabelStaysReadableOnTheSurfacesOfTheDisabledControls(string theme)
    {
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.AccentSurface", "Workspace.ChipSurface" })
        {
            string muted = Color(theme, "Workspace.Muted"), under = Color(theme, surface);
            Assert.True(ContrastRules.Contrast(muted, under) >= 4.5, theme + ": Muted on " + surface);
            double dimmed = ContrastRules.Contrast(ContrastRules.Blend(muted, under, 0.75), under);
            Assert.True(dimmed >= 3.0, theme + ": Muted at 0.75 on " + surface + " is " + dimmed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ":1");
        }
    }

    // A disabled nav button is distinguishable from an idle one: its label is Muted either way, so the dimming has to be a visible step (at least 1.4:1
    // between the idle and the disabled label on the bar, 4.7:1 / 3.7:1 against 7.4:1 / 6.7:1 on the panel).
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ADisabledNavButtonDiffersFromAnIdleOneByAVisibleStep(string theme)
    {
        string muted = Color(theme, "Workspace.Muted"), panel = Color(theme, "Workspace.Panel");
        double idle = ContrastRules.Contrast(muted, panel);
        double disabled = ContrastRules.Contrast(ContrastRules.Blend(muted, panel, 0.75), panel);
        Assert.True(idle / disabled >= 1.4, theme + ": idle " + idle.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " vs disabled " + disabled.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Color(string theme, string key) =>
        Assert.Single(Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Descendants(A + "ResourceDictionary"),
            element => (string?)element.Attribute(X + "Key") == theme).Elements(), element => (string?)element.Attribute(X + "Key") == key).Value.Trim();

    private static string Root(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
