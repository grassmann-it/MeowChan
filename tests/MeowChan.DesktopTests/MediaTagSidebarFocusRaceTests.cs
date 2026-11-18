using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

public sealed class MediaTagSidebarFocusRaceTests
{
    [Fact]
    public void DeferredFocusRepairUsesStableRowIdentityAndPreservesNewerFocus()
    {
        string rebuild = Method("Desktop/Views/Media/MediaTagSidebar.cs", "Rebuild");
        string callback = rebuild[rebuild.IndexOf("Dispatcher.UIThread.Post", StringComparison.Ordinal)..];

        Assert.Contains("Control? focusedElement = focused ? TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control : null;", rebuild);
        Assert.Contains("(_list.SelectedItem as MediaTagListItem)?.Identity != selected", callback);
        Assert.Contains("object? current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();", callback);
        Assert.Contains("RowAt(currentControl)?.Identity != selected", callback);
        Before(callback, "object? current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();", "_list.ScrollIntoView(index);");
    }

    private static void Before(string body, string first, string second)
    {
        int firstIndex = body.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = body.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex, $"Expected '{first}' before '{second}'.");
    }

    private static string Read(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)));

    private static string Method(string relative, string name)
    {
        string text = Read(relative);
        System.Text.RegularExpressions.Match declaration = System.Text.RegularExpressions.Regex.Match(text,
            @"private\s+void\s+" + name + @"\([^\n]*\)\s*\{");
        Assert.True(declaration.Success, $"Missing method {name} in {relative}.");
        int start = text.IndexOf('{', declaration.Index), depth = 1, end = start + 1;
        for (; end < text.Length && depth > 0; end++)
        {
            if (text[end] == '{') depth++;
            else if (text[end] == '}') depth--;
        }
        return text[start..end];
    }
}
