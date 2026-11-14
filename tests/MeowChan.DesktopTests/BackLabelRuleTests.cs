using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>UX flows AP9: every way back is worded "Back to {target}" in the language's one pattern, so the same
/// action reads the same in the reader, the gallery, Media, reading position, plugin list and sources.</summary>
[Collection("Localization")]
public sealed class BackLabelRuleTests : IDisposable
{
    public BackLabelRuleTests() => Reset();
    public void Dispose() => Reset();
    private static void Reset() { Texts.Initialize(); Texts.SetLanguage("en"); }

    /// <summary>Every text that names a way back to something. The targets differ; the wording around them does not.</summary>
    internal static readonly string[] BackKeys =
    {
        "command.reader.history-back", "command.gallery.return", "command.gallery.context-close", "command.gallery.close.media",
        "command.gallery.close.tagging",
        "command.media.return", "command.sources.back", "command.archive.back-to-list", "command.reader.close.toArchive",
        "command.reader.close.toDownloads", "plugins.back", "archive.back", "gallery.return", "gallery.viewer7", "archive.cleanup.back",
    };

    /// <summary>The fixed part of the pattern per language: a prefix, or for ja/ko/zh the suffix/prefix of that language.</summary>
    private static readonly Dictionary<string, Func<string, bool>> Pattern = new(StringComparer.Ordinal)
    {
        ["en"] = text => text.StartsWith("Back to ", StringComparison.Ordinal),
        ["de"] = text => text.StartsWith("Zurück zu", StringComparison.Ordinal),
        ["es"] = text => text.StartsWith("Volver a", StringComparison.Ordinal),
        ["fr"] = text => text.StartsWith("Retour à", StringComparison.Ordinal) || text.StartsWith("Retour au", StringComparison.Ordinal),
        ["ru"] = text => text.StartsWith("Вернуться ", StringComparison.Ordinal),
        ["zh-Hans"] = text => text.StartsWith("返回", StringComparison.Ordinal),
        ["ja"] = text => text.EndsWith("に戻る", StringComparison.Ordinal),
        ["ko"] = text => text.EndsWith("돌아가기", StringComparison.Ordinal),
        ["pt-BR"] = text => text.StartsWith("Voltar ", StringComparison.Ordinal),
        ["cs"] = text => text.StartsWith("Zpět ", StringComparison.Ordinal),
        ["it"] = text => text.StartsWith("Torna ", StringComparison.Ordinal),
        ["pl"] = text => text.StartsWith("Wróć ", StringComparison.Ordinal),
    };

    public static TheoryData<string> Languages()
    {
        TheoryData<string> data = new() { "en", "de" };
        foreach (string code in Texts.BundledPackCodes()) data.Add(code);
        return data;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryBackLabelFollowsTheLanguagesOnePattern(string code)
    {
        Texts.SetLanguage(code);
        Assert.True(Pattern.TryGetValue(code, out Func<string, bool>? matches), "No back-label pattern for " + code);
        // The language review shortened the ko menu label to the destination form ("to the last read position"), without the verb.
        var off = BackKeys.Where(key => !(code == "ko" && key == "command.reader.history-back") && !matches!(Texts.T(key))).Select(key => key + " = " + Texts.T(key)).ToArray();
        Assert.True(off.Length == 0, "Back labels outside the pattern in " + code + ": " + string.Join("; ", off));
    }

    [Fact]
    public void TheEnglishAndGermanCommandLabelsNameTheirTarget()
    {
        Assert.Equal("Back to media", CommandCatalog.Get("media.return").LabelEn);
        Assert.Equal("Back to gallery", CommandCatalog.Get("gallery.return").LabelEn);
        Assert.Equal("Back to last read", CommandCatalog.Get("reader.history-back").LabelEn);
        Assert.Equal("Back to sources", CommandCatalog.Get("sources.back").LabelEn);
        Assert.Equal("Zurück zu Quellen", CommandCatalog.Get("sources.back").LabelDe);
        Assert.Equal("Back to image", CommandCatalog.Get("gallery.context-close").LabelEn);
    }

    [Fact]
    public void ReaderCloseKeepsItsCommandAndNamesTheTargetOnlyAsText()
    {
        Assert.Equal("Back to Archive", Texts.T("command.reader.close.toArchive"));
        Assert.Equal("Back to Downloads", Texts.T("command.reader.close.toDownloads"));
        Assert.True(CommandCatalog.TryGet("reader.close", out _));
        Assert.False(CommandCatalog.TryGet("reader.close.toArchive", out _));
    }
}
