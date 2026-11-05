using System.Reflection;
using System.Text.Json;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ShortcutIssueBundledPackTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public ShortcutIssueBundledPackTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void BundledPreviewHasExactIssueKeysAndUsesThemForEveryReason(string language)
    {
        var assembly = typeof(Texts).Assembly;
        string resource = Assert.Single(assembly.GetManifestResourceNames().Where(n => n.EndsWith("." + language + ".json", StringComparison.Ordinal)));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var pack = JsonDocument.Parse(stream);
        var strings = pack.RootElement.GetProperty("strings");
        Assert.Equal(ShortcutIssueCatalog.Entries.Select(e => e.Key).Order(),
            strings.EnumerateObject().Where(p => p.Name.StartsWith(ShortcutIssueCatalog.Prefix, StringComparison.Ordinal)).Select(p => p.Name).Order());
        Texts.SetLanguage(language);
        foreach (var entry in ShortcutIssueCatalog.Entries)
        {
            string translated = strings.GetProperty(entry.Key).GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(translated));
            string[] args = entry.English.Contains("{0}", StringComparison.Ordinal) ? ["raw_{0}_Ä/原"] : [];
            var reason = new ShortcutIssueReason(entry.Key[ShortcutIssueCatalog.Prefix.Length..], entry.English, args);
            string rendered = ShortcutIssueCatalog.Render(reason);
            Assert.Equal(Texts.T(entry.Key, args.Cast<object?>().ToArray()), rendered);
            Assert.DoesNotContain(ShortcutIssueCatalog.Prefix, rendered);
            Assert.Equal(args.Length == 0 ? translated : translated.Replace("{0}", args[0], StringComparison.Ordinal), rendered);
            Assert.Equal(entry.English, reason.LegacyMessage);
        }
    }
}
