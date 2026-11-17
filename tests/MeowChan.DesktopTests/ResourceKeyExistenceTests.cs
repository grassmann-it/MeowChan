using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Semi.Avalonia;
using Semi.Avalonia.DataGrid;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopTests;

/// <summary>
/// Every resource key the product asks for must exist (G-resource-keys). A dynamic reference to a key that is defined nowhere resolves to
/// nothing, silently and without an exception: 3297fe413 used <c>{DynamicResource Workspace.ControlSurface}</c> and, as no such key existed, changed nothing.
/// The keys are collected from the markup (<c>{DynamicResource X}</c>, <c>{StaticResource X}</c> in Desktop/**/*.axaml and App.axaml) and from the C# sources
/// (string literals in <c>TryFindResource</c>/<c>TryGetResource</c>/<c>FindResource</c>, <c>new DynamicResourceExtension("X")</c>, and the
/// constants of WorkspaceTokens.cs), then each one is looked up in an application that carries the same styles as App.axaml (Semi, Semi DataGrid, Ursa,
/// WorkspaceStyles), for the Light and for the Dark variant. Keys the markup defines itself (an <c>x:Key</c> in the same file) are local and need no
/// application-level definition. Computed or interpolated keys cannot be checked; their number is reported.
/// </summary>
[Collection("Avalonia global state")]
public sealed class ResourceKeyExistenceTests
{
    // Known gaps: a key used by a product file that resolves nowhere today. Every entry names the key, where it is used and who owns the fix;
    // remove the entry in the commit that defines the key or the reference. A stale entry (the key resolves now) fails the test.
    // Format: "key" -> "owner / reason".
    private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
    {
    };

    private static readonly Regex MarkupReference = new(
        @"\{\s*(?<kind>DynamicResource|StaticResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s,}]+)\s*[,}]", RegexOptions.CultureInvariant);

    private static readonly Regex MarkupDefinition = new(@"\bx:Key\s*=\s*""(?<key>[^""]+)""", RegexOptions.CultureInvariant);

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // The first argument up to the next top-level comma or the closing parenthesis; a plain string literal is a checkable key.
    private static readonly Regex CodeReference = new(
        @"\b(?<call>TryFindResource|TryGetResource|FindResource|new\s+DynamicResourceExtension|new\s+StaticResourceExtension)\s*\(\s*(?<arg>[^,)]*)",
        RegexOptions.CultureInvariant);

    private static readonly Regex PlainLiteral = new(@"^""(?<key>[^""\\{}]+)""$", RegexOptions.CultureInvariant);

    // "TryFindResource(object key, ..." and the like are declarations, not calls.
    private static readonly Regex Declaration = new(@"^[\w.<>?\[\]]+\s+\w+$", RegexOptions.CultureInvariant);

    private static readonly Regex TokenConstant = new(@"\bconst\s+string\s+\w+\s*=\s*""(?<key>[^""]+)""\s*;", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void EveryResourceKeyTheProductAsksForResolvesInTheApplication(string variantName)
    {
        ThemeVariant variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        Collected collected = Collect();
        Assert.True(collected.Uses.Count > 100, $"The scan found only {collected.Uses.Count} resource keys; the collectors are broken.");

        string[] missing = [];
        string[] stale = [];
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            Application app = new() { RequestedThemeVariant = variant };
            AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
            app.Styles.Add(new SemiTheme());
            app.Styles.Add(new DataGridSemiTheme());
            app.Styles.Add(new UrsaSemiTheme());
            app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MeowChan/"))
            {
                Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml"),
            });

            bool Resolves(string key) => app.TryGetResource(key, variant, out _);
            missing = collected.Uses
                .Where(use => !Resolves(use.Key) && !use.DefinedLocally && !KnownGaps.ContainsKey(use.Key))
                .Select(use => $"  {use.Key}  <- {use.File}:{use.Line}")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            stale = KnownGaps.Keys.Where(Resolves).Select(key => $"  {key} ({KnownGaps[key]})").Order(StringComparer.Ordinal).ToArray();
        });

        Assert.True(missing.Length == 0,
            $"{missing.Length} resource key(s) are asked for but not defined in the {variantName} theme of the application "
            + $"(typo, or a token that was never added to WorkspaceStyles.axaml / the Semi / Ursa themes). {collected.Skipped} computed keys were not checked.\n"
            + string.Join("\n", missing));
        Assert.True(stale.Length == 0,
            "These KnownGaps entries resolve now; remove them from ResourceKeyExistenceTests.KnownGaps:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void TheCollectorsFindMarkupAndCodeKeysAndCountWhatTheySkip()
    {
        Collected collected = Collect(
            [("A.axaml", "<Border Background=\"{DynamicResource Workspace.ControlSurface}\" Tag=\"{StaticResource Other}\"/><!-- {DynamicResource Commented} -->"
                + "<Border Background=\"{DynamicResource Local}\"><Border.Resources><SolidColorBrush x:Key=\"Local\"/></Border.Resources></Border>")],
            [("B.cs", "x.TryFindResource(\"Code.One\", out _); y[!P] = new DynamicResourceExtension(\"Code.Two\"); z.TryFindResource(name, out _);"
                + " w.TryFindResource($\"Pre.{n}\", out _); bool TryFindResource(object key, out object? value);")],
            "const string TextCaption = \"Workspace.Text.Caption\";");

        Assert.Equal(new[] { "Code.One", "Code.Two", "Local", "Other", "Workspace.ControlSurface", "Workspace.Text.Caption" },
            collected.Uses.Select(use => use.Key).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Local" }, collected.Uses.Where(use => use.DefinedLocally).Select(use => use.Key));
        Assert.Equal(2, collected.Skipped);
        Assert.Contains(collected.Uses, use => use is { Key: "Code.Two", File: "B.cs" });
    }

    private sealed record Use(string Key, string File, int Line, bool DefinedLocally);

    private sealed record Collected(List<Use> Uses, int Skipped);

    private static Collected Collect()
    {
        string root = Root("");
        List<(string File, string Text)> markup = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Desktop"), "*.axaml", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "App.axaml")).Order(StringComparer.Ordinal))
            markup.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
        List<(string File, string Text)> code = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Desktop"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal))
            code.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
        return Collect(markup, code, File.ReadAllText(Path.Combine(root, "Desktop/WorkspaceTokens.cs")));
    }

    private static Collected Collect(IEnumerable<(string File, string Text)> markup, IEnumerable<(string File, string Text)> code, string tokensSource)
    {
        List<Use> uses = [];
        int skipped = 0;
        foreach ((string file, string text) in markup)
        {
            // A commented-out reference is not a use; keep the line count by replacing the comment with the same number of newlines.
            string live = XmlComment.Replace(text, comment => new string('\n', comment.Value.Count(c => c == '\n')));
            HashSet<string> local = MarkupDefinition.Matches(live).Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match match in MarkupReference.Matches(live))
            {
                string key = match.Groups["key"].Value;
                if (key.Contains('{')) { skipped++; continue; }
                uses.Add(new Use(key, file, LineOf(live, match.Index), local.Contains(key)));
            }
        }
        foreach ((string file, string text) in code)
        {
            foreach (Match match in CodeReference.Matches(text))
            {
                string argument = match.Groups["arg"].Value.Trim();
                Match literal = PlainLiteral.Match(argument);
                if (literal.Success) uses.Add(new Use(literal.Groups["key"].Value, file, LineOf(text, match.Index), false));
                else if (argument.Length > 0 && !Declaration.IsMatch(argument)) skipped++;
            }
        }
        foreach (Match match in TokenConstant.Matches(tokensSource))
            uses.Add(new Use(match.Groups["key"].Value, "Desktop/WorkspaceTokens.cs", LineOf(tokensSource, match.Index), false));
        return new Collected(uses, skipped);
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
