using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Guard: every catalog icon a product file names (NamedIcon, the icon-before-text / icon-only helpers, the command catalog and its state
// icons) is a file of Assets/icons/feather/masks. A missing mask used to throw when the control was shown (the Tagging page could not be
// built because of "chevron-down"); CommandBindings.IconMask now degrades to an empty icon and logs, and this test keeps the names strict.
// Source scan and plain objects; no application, no GUI.
public sealed class IconMaskCatalogGuardTests
{
    private static readonly Regex Literal = new("\"([a-z][a-z0-9]*(?:-[a-z0-9]+)*)\"", RegexOptions.CultureInvariant);

    private static IEnumerable<(string File, string Text)> ProductSources() =>
        Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                           && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));

    private static bool MaskExists(string icon) => File.Exists(Root("Assets/icons/feather/masks/" + icon + ".png"));

    // The argument list of a call, one level of nested parentheses deep, so a ternary or switch expression is covered whole.
    private static IEnumerable<string> ArgumentLists(string text, string callName)
    {
        Regex call = new(Regex.Escape(callName) + "\\(((?:[^()]|\\((?:[^()]|\\([^()]*\\))*\\))*)\\)", RegexOptions.CultureInvariant);
        foreach (Match match in call.Matches(text)) yield return match.Groups[1].Value;
    }

    [Fact]
    public void EveryLiteralNamedIconExistsInTheMaskCatalog()
    {
        List<string> missing = [];
        int seen = 0;
        foreach ((string file, string text) in ProductSources())
            foreach (string args in ArgumentLists(text, "NamedIcon"))
                foreach (Match literal in Literal.Matches(args))
                {
                    seen++;
                    if (!MaskExists(literal.Groups[1].Value)) missing.Add(file + ": NamedIcon(" + literal.Groups[1].Value + ")");
                }
        Assert.True(seen >= 15, "The scan found only " + seen + " NamedIcon names.");
        Assert.True(missing.Count == 0, "Icon names without a mask: " + string.Join("; ", missing));
    }

    // The other ways a source hands a catalog icon to the mask loader: by file name directly (IconFromKey, IconMask), the helper that
    // puts an icon before the text, and the icon-only button helper. (Their literal second arguments are also covered by
    // IconButtonConversionTests; this adds the direct loader calls and the ternary/switch forms.)
    [Fact]
    public void EveryIconNamedToTheHelpersAndTheMaskLoaderExists()
    {
        List<string> missing = [];
        foreach ((string file, string text) in ProductSources())
            foreach (string name in new[] { "IconFromKey", "IconMask", "IconUri", "ShowIconBeforeText", "EnsureIconBeforeText", "IconButtons.Apply", "IconButtons.WithText", "IconButtons.Record" })
                foreach (string args in ArgumentLists(text, name))
                    foreach (Match literal in Literal.Matches(args))
                        if (!MaskExists(literal.Groups[1].Value)) missing.Add(file + ": " + name + "(" + literal.Groups[1].Value + ")");
        Assert.True(missing.Count == 0, "Icon names without a mask: " + string.Join("; ", missing));
    }

    // The command catalog's icons (and the icons its states switch to) are mask names; CommandBindings.Icon(id) loads them by name.
    [Fact]
    public void EveryCommandCatalogIconAndStateIconExists()
    {
        Assert.NotEmpty(CommandCatalog.All);
        string[] missing = CommandCatalog.All.Select(command => command.Icon).Distinct(StringComparer.Ordinal).Where(icon => !MaskExists(icon)).ToArray();
        Assert.True(missing.Length == 0, "Command catalog icons without a mask: " + string.Join(", ", missing));

        string catalog = File.ReadAllText(Root("Desktop/Commands/CommandCatalog.cs"));
        int start = catalog.IndexOf("StateIcons =", StringComparison.Ordinal), end = catalog.IndexOf("public static string IconForState", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "The state icon table moved.");
        // { "state", "icon" } pairs; the ids and the states are matched too, so only the second literal of each pair is checked.
        List<string> stateMissing = [];
        foreach (Match pair in Regex.Matches(catalog[start..end], "\\{\\s*\"[a-z_]+\"\\s*,\\s*\"([a-z][a-z0-9-]*)\"\\s*\\}", RegexOptions.CultureInvariant))
            if (!MaskExists(pair.Groups[1].Value)) stateMissing.Add(pair.Groups[1].Value);
        Assert.True(stateMissing.Count == 0, "State icons without a mask: " + string.Join(", ", stateMissing));
    }

    // The chevrons of the Tagging page's selection state and the sibling that points the other way.
    [Theory]
    [InlineData("chevron-down")]
    [InlineData("chevron-up")]
    [InlineData("chevron-left")]
    [InlineData("chevron-right")]
    public void TheChevronMasksExistAndAreTransparent48PixelPngs(string icon)
    {
        string path = Root("Assets/icons/feather/masks/" + icon + ".png");
        Assert.True(File.Exists(path), icon + " is missing");
        byte[] png = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        // IHDR: width and height are big-endian at bytes 16 and 20; colour type 6 (RGBA) at byte 25.
        Assert.Equal(48, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(48, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Assert.Equal(6, png[25]);
        Assert.True(File.Exists(Root("Assets/icons/feather/" + icon + ".svg")), icon + ".svg original is missing");
    }

    [Fact]
    public void ThePageThatCrashedUsesTheMaskThatNowExists() =>
        Assert.Contains("NamedIcon(\"chevron-down\", 12)", File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Selection.cs")), StringComparison.Ordinal);

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
