using System.Text;
using System.Text.RegularExpressions;

namespace MeowChan.DesktopTests.Hygiene;

/// <summary>
/// Text scans over Desktop/**/*.cs for the fire-and-forget class of bug (bug-class audit 16.11.2026, class 9): a discarded task
/// (<c>_ = SomethingAsync(...)</c>) loses its failure, an <c>async void</c> without a catch leaves its busy flags and its context behind.
/// The scans work on sanitized text (comments, string and character literals blanked, same length and line breaks).
/// </summary>
internal static class HygieneScanner
{
    internal const string DiscardedTask = "discarded-task";
    internal const string UnguardedAsyncVoid = "async-void-unguarded";

    // `_ = Foo.BarAsync(`, `_ = x?.BarAsync<T>(`: the audit's pattern, widened over every callee that ends in Async.
    private static readonly Regex Discard = new(@"(?<![\w.])_\s*=\s*[\w.?!]*(?:<[^<>()]*>[\w.?!]*)*Async(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex AsyncVoid = new(@"\basync\s+void\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex TryKeyword = new(@"\btry\b", RegexOptions.Compiled);

    /// <summary>The one sanctioned observer of a task nobody awaits (OpenActions.Start): it is the guard itself.</summary>
    private static readonly HashSet<string> SanctionedAsyncVoid = new(StringComparer.Ordinal) { "Desktop/OpenActions.cs::Observe" };

    internal sealed record Finding(string Rule, string Key, int Count);

    internal static IReadOnlyList<Finding> ScanRepository(string root)
    {
        Dictionary<(string Rule, string Key), int> counts = new();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Desktop"), "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            foreach (Finding finding in ScanFile(relative, File.ReadAllText(file)))
                counts[(finding.Rule, finding.Key)] = counts.GetValueOrDefault((finding.Rule, finding.Key)) + finding.Count;
        }
        return counts.Select(entry => new Finding(entry.Key.Rule, entry.Key.Key, entry.Value))
            .OrderBy(f => f.Rule, StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    internal static IEnumerable<Finding> ScanFile(string relative, string content)
    {
        string code = Sanitize(content);
        int discards = Discard.Matches(code).Count;
        if (discards > 0) yield return new Finding(DiscardedTask, relative, discards);
        foreach (Match method in AsyncVoid.Matches(code))
        {
            string key = relative + "::" + method.Groups[1].Value;
            if (SanctionedAsyncVoid.Contains(key) || !TryBody(code, method.Index + method.Length - 1, out string? body)) continue;
            // A body that handles its own failure: a try block, UnhandledErrors.TryRecover or the shared safe path (OpenActions.RunAsync).
            if (TryKeyword.IsMatch(body!) || body!.Contains("TryRecover", StringComparison.Ordinal) || body.Contains("OpenActions.", StringComparison.Ordinal)) continue;
            yield return new Finding(UnguardedAsyncVoid, key, 1);
        }
    }

    /// <summary>The text of a method body (block or expression) that follows the parameter list opening at <paramref name="openParen"/>.</summary>
    internal static bool TryBody(string code, int openParen, out string? body)
    {
        body = null;
        int index = SkipBalanced(code, openParen, '(', ')');
        if (index < 0) return false;
        while (index < code.Length && char.IsWhiteSpace(code[index])) index++;
        if (index >= code.Length) return false;
        if (code[index] == '{')
        {
            int end = SkipBalanced(code, index, '{', '}');
            if (end < 0) return false;
            body = code[index..end];
            return true;
        }
        if (index + 1 < code.Length && code[index] == '=' && code[index + 1] == '>')
        {
            int depth = 0, end = index + 2;
            for (; end < code.Length; end++)
            {
                char c = code[end];
                if (c is '(' or '{' or '[') depth++;
                else if (c is ')' or '}' or ']') depth--;
                else if (c == ';' && depth <= 0) break;
            }
            body = code[(index + 2)..Math.Min(end, code.Length)];
            return true;
        }
        return false;
    }

    // Index just after the bracket that closes the one at `open`, or -1.
    private static int SkipBalanced(string code, int open, char opening, char closing)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == opening) depth++;
            else if (code[i] == closing && --depth == 0) return i + 1;
        }
        return -1;
    }

    /// <summary>Blanks comments, string literals (regular, verbatim, interpolated, raw) and character literals; keeps length and line breaks.</summary>
    internal static string Sanitize(string text)
    {
        StringBuilder result = new(text.Length);
        int i = 0;
        void Blank(int from, int to)
        {
            for (int k = from; k < to && k < text.Length; k++) result.Append(text[k] is '\n' or '\r' ? text[k] : ' ');
        }
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                Blank(i, end);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Blank(i, end);
                i = end;
            }
            else if (c == '"')
            {
                int quotes = 1;
                while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                bool verbatim = false;
                for (int back = i - 1; back >= 0 && text[back] is '$' or '@'; back--) verbatim |= text[back] == '@';
                int end;
                if (quotes >= 3)
                {
                    int close = text.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                    end = close < 0 ? text.Length : close + quotes;
                }
                else if (quotes == 2) end = i + 2; // the empty string
                else
                {
                    end = i + 1;
                    while (end < text.Length)
                    {
                        if (verbatim && text[end] == '"' && end + 1 < text.Length && text[end + 1] == '"') { end += 2; continue; }
                        if (!verbatim && text[end] == '\\') { end += 2; continue; }
                        if (text[end] == '"') { end++; break; }
                        if (!verbatim && text[end] == '\n') break; // unterminated: do not eat the file
                        end++;
                    }
                }
                Blank(i, end);
                i = end;
            }
            else if (c == '\'')
            {
                int end = i + 1;
                while (end < text.Length && text[end] != '\'' && text[end] != '\n') end += text[end] == '\\' ? 2 : 1;
                end = Math.Min(text.Length, end + 1);
                Blank(i, end);
                i = end;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }
        return result.ToString();
    }
}
