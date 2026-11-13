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
}
