using System.Text.RegularExpressions;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Style rules for the shipped catalogs that a translator or reviewer would otherwise have to remember.</summary>
public sealed class CatalogTextStyleTests
{
    [Fact]
    public void EllipsisFollowsTheLastCharacterWithoutASpaceInEveryBuiltInText()
    {
        // One style per language: "Bewertung bearbeiten…", never "Bewertung bearbeiten …".
        List<string> offenders = new();
        foreach ((string language, IReadOnlyDictionary<string, string> strings) in new[] { ("en", BuiltInCatalog.English), ("de", BuiltInCatalog.German) })
            foreach (KeyValuePair<string, string> entry in strings)
                if (entry.Value.Contains(" …", StringComparison.Ordinal) || entry.Value.Contains("...", StringComparison.Ordinal))
                    offenders.Add(language + ": " + entry.Key);
        foreach (CommandDefinition command in CommandCatalog.All)
            if (command.LabelEn.Contains(" …", StringComparison.Ordinal) || command.LabelDe.Contains(" …", StringComparison.Ordinal)
                || command.LabelEn.Contains("...", StringComparison.Ordinal) || command.LabelDe.Contains("...", StringComparison.Ordinal))
                offenders.Add("command: " + command.Id);

        Assert.True(offenders.Count == 0, "Use the single character '…' attached to the preceding word: " + string.Join(", ", offenders));
    }

    // "Ihre"/"Sie" that refer to a thing, not to the reader ("Sie bleiben erhalten", "Ihre Bytes"), are not formal address.
    private static readonly HashSet<string> ThirdPersonIhr = new(StringComparer.Ordinal)
    {
        "sources.providerMismatchInactive", "transfer.conflicts.error.promoteUnavailable"
    };

    private static readonly Regex FormalSie = new(
        @"(?<=[\p{Ll}\d,;)] )Sie\b|(?<![\p{L}])Sie (?:können|müssen|sollten|möchten|wollen|dürfen)\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void GermanTextsAddressTheReaderWithDuNeverWithSie()
    {
        List<string> offenders = new();
        foreach (KeyValuePair<string, string> entry in BuiltInCatalog.German)
        {
            if (FormalSie.IsMatch(entry.Value)
                || (!ThirdPersonIhr.Contains(entry.Key) && Regex.IsMatch(entry.Value, @"\bIhr(?:e|en|em|er|es)?\b", RegexOptions.CultureInvariant)))
                offenders.Add(entry.Key);
        }
        Assert.True(offenders.Count == 0, "German texts use \"du\", not \"Sie\": " + string.Join(", ", offenders));
    }
}
