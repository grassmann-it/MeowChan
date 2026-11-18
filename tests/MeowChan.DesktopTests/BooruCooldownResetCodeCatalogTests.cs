using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Contract coverage for the fail-closed provider-cooldown reset outcomes.</summary>
public sealed class BooruCooldownResetCodeCatalogTests
{
    private static readonly (string Code, string English, string German)[] Expected =
    {
        (BooruCodes.ProviderCooldownUnavailable,
            "MeowChan could not verify the provider cooldown. Watcher scheduling is paused for safety; no new request is sent while the cooldown is unknown.",
            "MeowChan konnte die Anbieterpause nicht prüfen. Die Planung der Suchbeobachtungen ist vorsichtshalber angehalten; solange die Pause unbekannt ist, wird keine neue Anfrage gesendet."),
        (BooruCodes.TransportResetFailed,
            "The provider cooldown reset failed. The existing pause remains active; MeowChan did not confirm recovery.",
            "Das Zurücksetzen der Anbieterpause ist fehlgeschlagen. Die bestehende Pause bleibt aktiv; MeowChan hat keine Wiederherstellung bestätigt."),
        (BooruCodes.TransportResetUnavailable,
            "This session cannot reset the provider cooldown. The existing pause remains active; MeowChan did not confirm recovery.",
            "In dieser Sitzung kann die Anbieterpause nicht zurückgesetzt werden. Die bestehende Pause bleibt aktiv; MeowChan hat keine Wiederherstellung bestätigt.")
    };

    [Fact]
    public void ResetBoundaryCodesAreRegisteredWithEnglishAndGermanCatalogText()
    {
        Assert.Equal(Expected.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count(), Expected.Length);

        foreach ((string code, string english, string german) in Expected)
        {
            Assert.Contains(code, BooruCodes.All);
            Assert.Equal(1, BooruCodeCatalog.Entries.Count(entry => entry.Key == BooruCodes.Key(code)));
            (string Key, string English, string German) entry = Assert.Single(
                BooruCodeCatalog.Entries, entry => entry.Key == BooruCodes.Key(code));
            Assert.Equal(english, entry.English);
            Assert.Equal(german, entry.German);
            Assert.Equal(english, BuiltInCatalog.English[entry.Key]);
            Assert.Equal(german, BuiltInCatalog.German[entry.Key]);
        }
    }

    [Fact]
    public void ResetBoundaryTextDoesNotPromiseRecoveryOrDescribeAnHttpReset()
    {
        foreach ((_, string english, string german) in Expected)
        {
            Assert.DoesNotContain("http", english, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http", german, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("success", english, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("erfolgreich", german, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("did not confirm recovery", Expected[1].English, StringComparison.Ordinal);
        Assert.Contains("did not confirm recovery", Expected[2].English, StringComparison.Ordinal);
        Assert.Contains("keine Wiederherstellung bestätigt", Expected[1].German, StringComparison.Ordinal);
        Assert.Contains("keine Wiederherstellung bestätigt", Expected[2].German, StringComparison.Ordinal);
    }
}
