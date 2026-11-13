using System.Runtime.CompilerServices;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;

namespace MeowChan.DesktopTests;

// Exercise the real Avalonia selector/cascade against the reader's source styles, without an
// application, native window, profile, font measurement or HTTP. Native theme/DPI evidence stays
// with the smoke. In particular, selected=True must NEVER be "fixed" to use inactive card tokens.
public sealed class ReaderReplyCardStateRegressionTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Dark", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", true)]
    public void SelectionAndDeselectionKeepTheCardContractThroughInteractionStates(string theme, bool opening)
    {
        var (root, item, presenter, tokens) = Create(theme);
        item.Classes.Set("reader-op", opening);
        foreach (string? interaction in new string?[] { null, ":pointerover", ":pressed", ":focus" })
        {
            if (interaction != null) ((IPseudoClasses)item.Classes).Set(interaction, true);
            item.IsSelected = false;
            Surface(presenter, opening ? Colors.Transparent : tokens["Workspace.Card"],
                opening ? Colors.Transparent : tokens["Workspace.CardEdge"], new Thickness(1));
            item.IsSelected = true;
            Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 1, 1, 1));
            Assert.Equal(tokens["Workspace.AccentSurface"], ColorOf(item.Background));
            item.IsSelected = false;
            Surface(presenter, opening ? Colors.Transparent : tokens["Workspace.Card"],
                opening ? Colors.Transparent : tokens["Workspace.CardEdge"], new Thickness(1));
            if (interaction != null) ((IPseudoClasses)item.Classes).Set(interaction, false);
        }
        GC.KeepAlive(root);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void KeyboardFocusAddsItsOwnEdgeWithoutReplacingTheSelectedSurface(string theme)
    {
        var (root, item, presenter, tokens) = Create(theme);
        item.IsSelected = true;
        ((IPseudoClasses)item.Classes).Set(":focus-visible", true);
        Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 2, 2, 2));
        ((IPseudoClasses)item.Classes).Set(":focus-visible", false);
        Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 1, 1, 1));
        GC.KeepAlive(root);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void RecycledOpeningPostReturnsToReplyCardButSelectionStillWins(string theme)
    {
        var (root, item, presenter, tokens) = Create(theme);
        item.Classes.Add("reader-op");
        Surface(presenter, Colors.Transparent, Colors.Transparent, new Thickness(1));
        item.Classes.Remove("reader-op");
        Surface(presenter, tokens["Workspace.Card"], tokens["Workspace.CardEdge"], new Thickness(1));
        item.IsSelected = true;
        item.Classes.Add("reader-op");
        Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 1, 1, 1));
        item.Classes.Remove("reader-op");
        Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 1, 1, 1));
        GC.KeepAlive(root);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ReaderViewStubAndHighlightDoNotReplaceSelectionOrKeyboardFocus(string theme)
    {
        var (root, item, presenter, tokens) = Create(theme);
        item.Classes.Add("reader-stub");
        Surface(presenter, Colors.Transparent, tokens["Workspace.CardEdge"], new Thickness(1));
        item.IsSelected = true;
        Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 1, 1, 1));
        item.Classes.Remove("reader-stub");
        item.Classes.Add("reader-highlight");
        Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 1, 1, 1));
        item.IsSelected = false;
        Surface(presenter, tokens["Workspace.Card"], tokens["Workspace.Warning"], new Thickness(3, 1, 1, 1));
        ((IPseudoClasses)item.Classes).Set(":focus-visible", true);
        Surface(presenter, tokens["Workspace.Card"], tokens["Workspace.Accent"], new Thickness(3, 2, 2, 2));
        item.IsSelected = true;
        Surface(presenter, tokens["Workspace.AccentSurface"], tokens["Workspace.Accent"], new Thickness(3, 2, 2, 2));
        GC.KeepAlive(root);
    }

    private static void Surface(ContentPresenter presenter, Color background, Color edge, Thickness thickness)
    {
        Assert.Equal(background, ColorOf(presenter.Background));
        Assert.Equal(edge, ColorOf(presenter.BorderBrush));
        Assert.Equal(thickness, presenter.BorderThickness);
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static (StyleRoot Root, ListBoxItem Item, ContentPresenter Presenter, Dictionary<string, Color> Tokens) Create(string theme)
    {
        XElement dictionary = Assert.Single(Source("Desktop/WorkspaceStyles.axaml").Descendants(A + "ResourceDictionary"),
            element => (string?)element.Attribute(X + "Key") == theme);
        Dictionary<string, Color> tokens = dictionary.Elements(A + "SolidColorBrush")
            .ToDictionary(element => (string)element.Attribute(X + "Key")!, element => Color.Parse(element.Value));
        XElement posts = Assert.Single(Source("Desktop/Views/Archive/ArchivePage.axaml").Descendants(A + "ListBox"),
            element => (string?)element.Attribute(X + "Name") == "ArchivePosts");
        StyleRoot root = new();
        // Only the card surface/edge setters are under test. Keep their original selectors and source
        // order (including OP, focus, stub and highlight overrides); don't duplicate the cascade here.
        foreach (XElement node in posts.Element(A + "ListBox.Styles")!.Elements(A + "Style"))
        {
            string selector = (string)node.Attribute("Selector")!;
            if (!selector.StartsWith("ListBoxItem", StringComparison.Ordinal)) continue;
            Style style = new() { Selector = CardSelector(selector) };
            foreach (XElement setter in node.Elements(A + "Setter"))
            {
                string property = (string)setter.Attribute("Property")!;
                string value = (string)setter.Attribute("Value")!;
                if (property == "BorderThickness")
                    style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, Thickness.Parse(value)));
                else if (property is "Background" or "BorderBrush")
                {
                    Color color = value.StartsWith("{DynamicResource ", StringComparison.Ordinal)
                        ? tokens[value[17..^1]] : Color.Parse(value);
                    style.Setters.Add(new Setter(property == "Background" ? TemplatedControl.BackgroundProperty
                        : TemplatedControl.BorderBrushProperty, new SolidColorBrush(color)));
                }
            }
            if (style.Setters.Count != 0) root.Styles.Add(style);
        }
        ContentPresenter presenter = new();
        ListBoxItem item = new() { Template = new FuncControlTemplate<ListBoxItem>((_, _) => presenter) };
        root.Child = item;
        item.ApplyTemplate();
        return (root, item, presenter, tokens);
    }

    // These local styles use only item classes/pseudo-classes, :not and the template
    // presenter. Fail closed if that grammar changes; Avalonia still evaluates every selector.
    private static Selector CardSelector(string text)
    {
        string[] parts = text.Split(" /template/ ", StringSplitOptions.None);
        const string itemType = "ListBoxItem";
        string conditions = parts[0][itemType.Length..];
        MatchCollection matches = Regex.Matches(conditions, @":not\(([:.][\w-]+)\)|[.:][\w-]+");
        Assert.Equal(conditions, string.Concat(matches.Select(match => match.Value)));
        Selector selector = default(Selector)!.OfType<ListBoxItem>();
        foreach (Match match in matches)
        {
            bool not = match.Groups[1].Success;
            string condition = not ? match.Groups[1].Value : match.Value;
            string name = condition.StartsWith('.') ? condition[1..] : condition;
            selector = not ? selector.Not(inner => inner.Class(name)) : selector.Class(name);
        }
        if (parts.Length == 1) return selector;
        Assert.Equal(2, parts.Length);
        Assert.Equal("ContentPresenter", parts[1]);
        return selector.Template().OfType<ContentPresenter>();
    }

    private sealed class StyleRoot : Decorator, ILogicalRoot;

    private static XElement Source(string relative, [CallerFilePath] string caller = "") =>
        XElement.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", relative)));
}
