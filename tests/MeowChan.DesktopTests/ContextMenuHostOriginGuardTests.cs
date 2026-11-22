using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MeowChan.DesktopTests;

// A menu on an overflowed work-view control needs a visible anchor, such as its More button.
public sealed class ContextMenuHostOriginGuardTests
{
    private static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                           && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));

    // The text between the parentheses of the call that starts at <paramref name="open"/> (one past the opening parenthesis).
    private static string Arguments(string text, int open)
    {
        int depth = 1, i = open;
        for (; i < text.Length && depth > 0; i++)
            if (text[i] == '(') depth++; else if (text[i] == ')') depth--;
        return text[open..(i - 1)];
    }

    private static int TopLevelArguments(string args)
    {
        int depth = 0, count = 1;
        foreach (char c in args)
            if (c is '(' or '{' or '[') depth++; else if (c is ')' or '}' or ']') depth--; else if (c == ',' && depth == 0) count++;
        return count;
    }

    [Fact]
    public void EveryMenuHostWhoseOriginAWorkViewCanMoveUnderMoreResolvesAnotherOrigin()
    {
        List<(string File, string Text)> sources = Sources().ToList();
        // A row the policy marks Required stays on the screen in every view (the Tagging list), so its host needs no second origin.
        string policy = string.Concat(Directory.EnumerateFiles(Root("Desktop/WorkViews"), "WorkViewPolicy*.cs").Select(File.ReadAllText));
        bool Moves(string id) => Regex.Matches(policy, "\"" + Regex.Escape(id) + "\"[^\\n]*").Select(match => match.Value).FirstOrDefault() is { } line && !line.Contains("Required", StringComparison.Ordinal);
        HashSet<string> movable = sources.SelectMany(source => Regex.Matches(source.Text, @"WorkViewElement\.SetId\((_\w+), ""([^""]+)""").Select(match => (Field: match.Groups[1].Value, Id: match.Groups[2].Value)))
            .Where(pair => Moves(pair.Id)).Select(pair => pair.Field).ToHashSet(StringComparer.Ordinal);
        List<string> unresolved = [];
        foreach ((string file, string text) in sources)
            foreach (Match match in Regex.Matches(text, @"new ContextMenuHost\("))
            {
                string args = Arguments(text, match.Index + match.Length);
                Match origin = Regex.Match(args, @"^\s*(_\w+)\s*,");
                if (!origin.Success || !movable.Contains(origin.Groups[1].Value)) continue;
                if (!args.Contains("resolveOrigin:", StringComparison.Ordinal) && TopLevelArguments(args) < 4)
                    unresolved.Add(file + ": ContextMenuHost on " + origin.Groups[1].Value);
            }
        Assert.True(unresolved.Count == 0, "A menu host on a control a work view moves under More needs a resolveOrigin: " + string.Join("; ", unresolved));
    }

    [Fact]
    public void TheTaggingSelectionMenuStaysOnItsDedicatedButton()
    {
        string selection = File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Selection.cs"));
        Assert.Contains("new ContextMenuHost(_selMore, ComposeSelectionMenu", selection, StringComparison.Ordinal);
        Assert.Contains("Same(\"tagging.selection.more\", WorkViewPage.Tagging, WorkViewKind.Action, Required)",
            File.ReadAllText(Root("Desktop/WorkViews/WorkViewPolicy.Tagging.cs")), StringComparison.Ordinal);
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
