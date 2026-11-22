using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MeowChan.DesktopTests;

/// <summary>
/// Cheap source-scan guards from the test-harness sweep S8 (16.11.2026), none of which needs a window or a host. Every scan is a ratchet over a plain-text
/// baseline in <c>SourceScanBaselines</c>: a hit that is not in the baseline fails the guard (so a NEW offender is caught), and a hit that is in the baseline
/// is a known, recorded one. The scans are deliberately line/statement based heuristics, not a parser; a baseline line is "file :: what was found", with no
/// line numbers, so it survives edits around it.
/// <list type="bullet">
/// <item>G7: every test class that swaps process-global Avalonia state is in a serialized collection (no baseline; must always hold).</item>
/// <item>G4: a bubble KeyDown handler on a list-like control that handles Enter/Space (a ListBoxItem swallows them in its own bubble handler first).
/// Baseline = the sites that exist today, recorded once; stale entries fail so the baseline only shrinks.</item>
/// <item>G5 and G6 scan the SMOKE project's source (tests/MeowChan.DesktopSmoke, owned by the smoke harness work) and can only report: their baselines are
/// the hits that exist today; fixing one never breaks the guard, a new one does. Entries that are fixed should then be removed from the baseline.</item>
/// </list>
/// </summary>
public sealed class SourceScanGuardTests
{
    private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(Here(), "../.."));
    private static string Rel(string full) => Path.GetRelativePath(RepoRoot(), full).Replace('\\', '/');

    private static string[] Sources(string relativeDir, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), relativeDir), pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

    private static HashSet<string> Baseline(string name) =>
        File.ReadAllLines(Path.Combine(Here(), "SourceScanBaselines", name))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static void AssertNoNewHits(string guard, string rule, IEnumerable<string> hits, string baselineFile, bool failOnStale)
    {
        HashSet<string> allowed = Baseline(baselineFile);
        string[] found = hits.Distinct(StringComparer.Ordinal).OrderBy(hit => hit, StringComparer.Ordinal).ToArray();
        string[] added = found.Where(hit => !allowed.Contains(hit)).ToArray();
        Assert.True(added.Length == 0, guard + ": " + rule + "\nNew (not in SourceScanBaselines/" + baselineFile + "):\n" + string.Join("\n", added));
        if (!failOnStale) return;
        string[] stale = allowed.Where(entry => !found.Contains(entry)).OrderBy(entry => entry, StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, guard + ": these baseline entries are fixed or gone; remove them from SourceScanBaselines/" + baselineFile + ":\n" + string.Join("\n", stale));
    }

    private static string Statement(string[] lines, int start)
    {
        StringBuilder statement = new();
        for (int i = start; i < lines.Length && i < start + 40; i++)
        {
            statement.Append(lines[i]).Append('\n');
            if (lines[i].TrimEnd().EndsWith(';')) break;
        }
        return statement.ToString();
    }

    // ---- G7: classes that swap process-global Avalonia state must be serialized -----------------------------------------------------------------------

    private static readonly Regex SwapperToken = new(
        @"AppBuilder|SetupWithoutStarting|AvaloniaLocator|ShortcutTestState|BooruComponentDispatcher\.Run|OwnedUiDispatcher\.(?:Run|CreateHost)|AvaloniaTestPlatform\.EnterSkia|TokenApplication\.Bind");
    private static readonly Regex TestAttribute = new(@"\[(?:Fact|Theory)\b");
    private static readonly Regex CollectionDefinitionAttribute = new(
        @"\[CollectionDefinition\((?<arg>[^\]]*)\)\]\s*public\s+sealed\s+class\s+(?<cls>\w+)");
    private static readonly Regex CollectionUse = new(@"\[Collection\((?<arg>[^)]*)\)\]");

    private static string WithoutCommentLines(string text) =>
        string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void G7_EveryTestClassThatSwapsTheLocatorOrTheDispatcherIsInASerializedCollection()
    {
        string[] files = Sources("tests/MeowChan.DesktopTests", "*.cs");
        HashSet<string> serializedNames = new(StringComparer.Ordinal);
        HashSet<string> serializedClasses = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (Match definition in CollectionDefinitionAttribute.Matches(text))
            {
                string arg = definition.Groups["arg"].Value;
                if (!arg.Contains("DisableParallelization = true", StringComparison.Ordinal)) continue;
                serializedClasses.Add(definition.Groups["cls"].Value);
                Match literal = Regex.Match(arg, "\"(?<v>[^\"]+)\"");
                if (literal.Success) serializedNames.Add(literal.Groups["v"].Value);
                Match constant = Regex.Match(text, "const string Name = \"(?<v>[^\"]+)\"");
                if (constant.Success) serializedNames.Add(constant.Groups["v"].Value);
            }
        }
        Assert.True(serializedNames.Contains("Avalonia global state"), "The collection definition of AvaloniaGlobalState.cs is missing.");

        List<string> offenders = new();
        foreach (string file in files)
        {
            if (Path.GetFileName(file) == "SourceScanGuardTests.cs") continue;
            string text = WithoutCommentLines(File.ReadAllText(file));
            if (!TestAttribute.IsMatch(text) || !SwapperToken.IsMatch(text)) continue;
            MatchCollection uses = CollectionUse.Matches(text);
            bool allSerialized = uses.Count > 0 && uses.Cast<Match>().All(use =>
            {
                string arg = use.Groups["arg"].Value.Trim();
                if (arg.StartsWith('"')) return serializedNames.Contains(arg.Trim('"'));
                return serializedClasses.Contains(arg.Split('.')[0]);
            });
            if (!allSerialized) offenders.Add(Rel(file));
        }
        Assert.True(offenders.Count == 0,
            "These test files swap the AvaloniaLocator, dispatcher or shortcut registry "
            + "but are not in a DisableParallelization collection (use [Collection(\"Avalonia global state\")]); xunit would run them in parallel with each other:\n"
            + string.Join("\n", offenders));
    }

    // ---- G4: bubble KeyDown on a list-like control that handles Enter/Space --------------------------------------------------------------------------

    private static readonly Regex MethodStart = new(@"\b(?:void|Task)\s+(?<name>\w+)\s*\(");
    private static readonly Regex EnterOrSpace = new(@"\bKey\.(?:Enter|Space|Return)\b");
    private static readonly Regex ListLikeName = new(@"^_?(?:list|posts|grid|table|cards|rows|items|actions|strip|tiles)\w*$", RegexOptions.IgnoreCase);
    private static readonly Regex KeyDownPlusEquals = new(@"^(?<recv>[A-Za-z_][\w\.]*)\??\.KeyDown\s*\+=\s*(?<h>.*)$");
    private static readonly Regex AddKeyDownHandler = new(@"(?<recv>[A-Za-z_][\w\.]*)\??\.AddHandler\(\s*(?:InputElement\.)?KeyDownEvent\s*,(?<rest>.*)$");
    private static readonly Regex NamedHandlerOnly = new(@"^\s*(?<n>\w+)\s*;");
    private static readonly Regex NamedHandlerArgument = new(@"^\s*(?<n>\w+)\s*[,)]");
    private static readonly Regex AxamlKeyDown = new("KeyDown=\"(?<h>\\w+)\"");
    private static readonly Regex AxamlTag = new(@"<(?<tag>[A-Za-z][\w:\.]*)");
    private static readonly Regex AxamlListTag = new(@"(?:ListBox|DataGrid|ItemsControl|SelectionList|TreeView|ListView)$");

    /// <summary>Method name to the text of its body(ies), over every product source file: how a named handler is checked for Enter/Space.</summary>
    private static Dictionary<string, string> MethodBodies(string[] files)
    {
        Dictionary<string, string> bodies = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (Match start in MethodStart.Matches(text))
            {
                int afterName = start.Index + start.Length;
                int brace = text.IndexOf('{', afterName), arrow = text.IndexOf("=>", afterName, StringComparison.Ordinal), semicolon = text.IndexOf(';', afterName);
                int first = new[] { brace, arrow, semicolon }.Where(index => index >= 0).DefaultIfEmpty(-1).Min();
                if (first < 0 || first == semicolon) continue;
                int end;
                if (first == arrow) end = semicolon < 0 ? text.Length : semicolon;
                else
                {
                    int depth = 0;
                    end = text.Length;
                    for (int i = brace; i < text.Length; i++)
                    {
                        if (text[i] == '{') depth++;
                        else if (text[i] == '}' && --depth == 0) { end = i + 1; break; }
                    }
                }
                string name = start.Groups["name"].Value;
                bodies[name] = (bodies.TryGetValue(name, out string? earlier) ? earlier + "\n" : "") + text[first..end];
            }
        }
        return bodies;
    }

    private static bool ListLike(string receiver, string fileText)
    {
        string last = receiver.Split('.')[^1];
        return ListLikeName.IsMatch(last)
            || Regex.IsMatch(fileText, @"\b(?:ListBox|DataGrid|ItemsControl|SelectionList|PluginSelectionList|TreeView)\s+" + Regex.Escape(last) + @"\b");
    }

    private static bool HandlesEnterOrSpace(Dictionary<string, string> bodies, string? namedHandler, string statement) =>
        namedHandler is null ? EnterOrSpace.IsMatch(statement)
            : !bodies.TryGetValue(namedHandler, out string? body) || EnterOrSpace.IsMatch(body); // a handler that cannot be found counts as a hit

    private static IEnumerable<string> BubbleKeyDownOnLists()
    {
        string[] csFiles = Sources("Desktop", "*.cs");
        Dictionary<string, string> bodies = MethodBodies(csFiles);
        foreach (string file in csFiles)
        {
            string text = File.ReadAllText(file);
            string[] lines = text.Split('\n').Select(line => line.Trim()).ToArray();
            for (int i = 0; i < lines.Length; i++)
            {
                Match plus = KeyDownPlusEquals.Match(lines[i]);
                if (plus.Success)
                {
                    string receiver = plus.Groups["recv"].Value;
                    if (!ListLike(receiver, text)) continue;
                    Match named = NamedHandlerOnly.Match(plus.Groups["h"].Value);
                    string? handler = named.Success ? named.Groups["n"].Value : null;
                    if (HandlesEnterOrSpace(bodies, handler, Statement(lines, i)))
                        yield return Rel(file) + " :: " + receiver + ".KeyDown(bubble) -> " + (handler ?? "lambda");
                    continue;
                }
                Match add = AddKeyDownHandler.Match(lines[i]);
                if (!add.Success) continue;
                string target = add.Groups["recv"].Value;
                string statement = Statement(lines, i);
                if (statement.Contains("Tunnel", StringComparison.Ordinal) || !ListLike(target, text)) continue;
                Match namedArg = NamedHandlerArgument.Match(add.Groups["rest"].Value);
                string? handlerName = namedArg.Success ? namedArg.Groups["n"].Value : null;
                if (HandlesEnterOrSpace(bodies, handlerName, statement))
                    yield return Rel(file) + " :: " + target + ".KeyDown(bubble) -> " + (handlerName ?? "lambda");
            }
        }
        foreach (string file in Sources("Desktop", "*.axaml"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                Match attribute = AxamlKeyDown.Match(lines[i]);
                if (!attribute.Success) continue;
                string? tag = null;
                for (int j = i; j >= 0 && tag is null; j--)
                {
                    IEnumerable<Match> tags = AxamlTag.Matches(lines[j]).Cast<Match>().Where(candidate => j != i || candidate.Index < attribute.Index);
                    tag = tags.LastOrDefault()?.Groups["tag"].Value;
                }
                if (tag is null || !AxamlListTag.IsMatch(tag)) continue;
                string handler = attribute.Groups["h"].Value;
                if (HandlesEnterOrSpace(bodies, handler, lines[i]))
                    yield return Rel(file) + " :: <" + tag + " KeyDown> -> " + handler;
            }
        }
    }

    [Fact]
    public void G4_NoNewBubbleKeyDownHandlerOnAListControlHandlesEnterOrSpace() =>
        AssertNoNewHits("G4",
            "a bubble KeyDown handler on a list-like control that handles Enter/Space never sees them when a ListBoxItem handled them first. "
            + "Use a Tunnel handler, or an item-level route (see SourcesPage.ItemEnter.cs, MediaPage.TileEnter.cs), or do not use Enter/Space there. "
            + "Heuristic: receiver named list/posts/grid/table/cards/rows/items/actions/strip/tiles or declared as ListBox/DataGrid/ItemsControl/SelectionList/TreeView in the same file.",
            BubbleKeyDownOnLists(), "g4-bubble-keydown-on-lists.txt", failOnStale: true);

    // ---- G5 / G6: smoke project source (tests/MeowChan.DesktopSmoke is not edited here; these report against a baseline of today's hits) -------------

    private static readonly Regex RaiseInline = new(
        @"(?<recv>(?:[\w\.\?!<>""\[\]]|\([^()]*\))+)\.RaiseEvent\(\s*new\s+(?<type>KeyEventArgs|PointerPressedEventArgs|PointerReleasedEventArgs)(?<rest>[^;]*)");
    private static readonly Regex RaiseVariable = new(
        @"(?<recv>(?:[\w\.\?!<>""\[\]]|\([^()]*\))+)\.RaiseEvent\(\s*(?<v>[A-Za-z_]\w*)\s*\)");
    private static readonly Regex SourceInitializer = new(@"\bSource\s*=\s*(?<src>[\w\.\?!]+)");
    private static readonly Regex PointerSourceArgument = new(@"^\s*\(\s*(?<src>[\w\.\?!]+)\s*,");

    private static string Normal(string expression) => Regex.Replace(expression, @"[!?\s]", "");

    private static IEnumerable<string> RaisedOnAnotherElementThanTheSource()
    {
        foreach (string file in Sources("tests/MeowChan.DesktopSmoke", "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (Match raise in RaiseInline.Matches(text))
            {
                string rest = raise.Groups["rest"].Value;
                Match source = raise.Groups["type"].Value == "KeyEventArgs" ? SourceInitializer.Match(rest) : PointerSourceArgument.Match(rest);
                if (!source.Success) continue;
                string receiver = Normal(raise.Groups["recv"].Value), declared = Normal(source.Groups["src"].Value);
                if (receiver != declared) yield return Rel(file) + " :: RaiseEvent on " + receiver + " with Source " + declared;
            }
            foreach (Match raise in RaiseVariable.Matches(text))
            {
                string variable = raise.Groups["v"].Value;
                Match definition = Regex.Matches(text[..raise.Index], @"\b" + Regex.Escape(variable) + @"\s*=\s*new\b[^;]*?\bSource\s*=\s*(?<src>[\w\.\?!]+)")
                    .Cast<Match>().LastOrDefault() ?? Match.Empty;
                if (!definition.Success) continue;
                string receiver = Normal(raise.Groups["recv"].Value), declared = Normal(definition.Groups["src"].Value);
                if (receiver != declared) yield return Rel(file) + " :: RaiseEvent on " + receiver + " with Source " + declared;
            }
        }
    }

    [Fact]
    public void G5_SmokeSourceBaseline_InputIsRaisedOnTheElementItNamesAsSource() =>
        AssertNoNewHits("G5 (scans tests/MeowChan.DesktopSmoke, baseline of today's hits)",
            "a smoke raises a key/pointer event on one element with Source = another (usually an ancestor raising for a child). The real route is tunnel then bubble "
            + "on the FOCUSED element, then the page; raising on an ancestor skips the item's own handlers and hides bugs in them (S8 F2). Raise on the element that is the Source.",
            RaisedOnAnotherElementThanTheSource(), "g5-smoke-raise-target-differs-from-source.txt", failOnStale: false);

    private static readonly Regex ShutdownVisibilityWait = new(
        @"WaitUntilAsync\(\s*\(\)\s*=>[^;]*?!\s*(?<v>[\w\.\?]*)\.IsVisible");
    private static readonly Regex ShellOrWindowName = new(@"^(?:\w*shell|\w*window)$", RegexOptions.IgnoreCase);

    private static IEnumerable<string> HiddenWindowAsCompletionWaits()
    {
        foreach (string file in Sources("tests/MeowChan.DesktopSmoke", "*.cs"))
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = Regex.Replace(raw.Trim(), @"\s+", " ");
                Match wait = ShutdownVisibilityWait.Match(line);
                if (!wait.Success) continue;
                string name = wait.Groups["v"].Value.TrimEnd('?').Split('.')[^1];
                if (ShellOrWindowName.IsMatch(name)) yield return Rel(file) + " :: " + line;
            }
    }

    [Fact]
    public void G6_SmokeSourceBaseline_ANotVisibleShellOrWindowIsNoSignThatShutdownCompleted() =>
        AssertNoNewHits("G6 (scans tests/MeowChan.DesktopSmoke, baseline of today's hits)",
            "a smoke waits for !shell.IsVisible / !window.IsVisible as 'closed'. The main window is hidden when shutdown STARTS (BoundedShutdown.HideForShutdown), so "
            + "this returns too early (S8 F3). Wait for the real completion signal (profile released and shutdown no longer running) or await CloseApplicationAsync. "
            + "Heuristic: one line, WaitUntilAsync(() => ... !<name ending in shell/window>.IsVisible).",
            HiddenWindowAsCompletionWaits(), "g6-smoke-not-visible-as-closed.txt", failOnStale: false);
}
