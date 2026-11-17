namespace MeowChan.DesktopTests;

/// <summary>
/// Source-pin tests name <c>BooruWorkbenchView.cs</c>, but the view is a partial class split by area (cards, preview, tags, search, watchers, ...)
/// plus the shared <c>BooruTagChips</c> helper. Reading the "view" means reading all of those files, the core file first.
/// </summary>
internal static class BooruViewSources
{
    /// <summary>The text of <paramref name="path"/>; for <c>BooruWorkbenchView.cs</c> the text of every partial file of the view and of the tag-chip helper.</summary>
    internal static string ReadAll(string path)
    {
        if (!string.Equals(Path.GetFileName(path), "BooruWorkbenchView.cs", StringComparison.Ordinal)) return File.ReadAllText(path);
        string directory = Path.GetDirectoryName(path)!;
        IEnumerable<string> parts = new[] { path }
            .Concat(Directory.GetFiles(directory, "BooruWorkbenchView.*.cs").OrderBy(file => file, StringComparer.Ordinal))
            .Append(Path.Combine(directory, "BooruTagChips.cs"));
        return string.Join("\n", parts.Select(File.ReadAllText));
    }
}
