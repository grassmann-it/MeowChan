using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using MeowChan;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Production origin-to-presentation seam and lexical wiring checks, not native Window proof.
[Collection("Localization")]
public sealed class ReaderGroupNoticeLocalizationTests : IDisposable
{
    private const string Key = "readerGroups.firstMemberUnavailable";
    private const string English = "The first thread is no longer available locally. Review the members and read again.";
    private const string German = "Der erste Thread ist inzwischen lokal nicht verfügbar. Bitte Mitglieder prüfen und erneut lesen.";
    private readonly string _previousLanguage = Texts.Language;

    public ReaderGroupNoticeLocalizationTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previousLanguage); }

    [Fact]
    public void DedicatedCatalogPreservesExactExistingWarning()
    {
        var entry = Assert.Single(ReaderGroupsCatalog.Entries.Where(entry => entry.Key == Key));
        Assert.Equal(English, entry.English);
        Assert.Equal(German, entry.German);
        Texts.SetLanguage("de");
        Assert.Equal(German, Texts.T(Key));
        Texts.SetLanguage("en");
        Assert.Equal(English, Texts.T(Key));
    }

    [Fact]
    public void TypedNoticeRerendersTheSamePresentationAcrossAllBundledLanguagesAndReload()
    {
        var origin = Origin() with { NoticeReason = ReaderGroupsNoticeReason.FirstMemberUnavailable };
        var message = ReaderGroupsDialog.OriginNoticePresentation(origin);
        foreach ((string language, string expected) in new[]
        {
            ("de", German),
            ("en", English),
            ("es", "El primer hilo ya no está disponible localmente. Revise los miembros e intente leer de nuevo."),
            ("fr", "Le premier fil n’est plus disponible localement. Vérifiez les membres et relancez la lecture."),
            ("ru", "Первый тред больше недоступен локально. Проверьте участников и попробуйте прочитать снова."),
            ("de", German)
        })
        {
            if (language is "es" or "fr" or "ru")
            {
                using var stream = typeof(Texts).Assembly.GetManifestResourceStream(
                    "MeowChan.Desktop.Localization.Packs." + language + ".json");
                Assert.NotNull(stream);
                using var document = JsonDocument.Parse(stream!);
                Assert.True(document.RootElement.GetProperty("strings").TryGetProperty(Key, out var bundled));
                Assert.Equal(expected, bundled.GetString());
                Assert.NotEqual(English, expected);
            }
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            Assert.Equal(Texts.T(Key), message.Render());
            Assert.Equal(expected, message.Render());
            Assert.NotEqual(Key, message.Render());
            if (language is "es" or "fr" or "ru") Assert.NotEqual(English, message.Render());
            if (language == "de") Assert.Equal(German, message.Render());
            if (language == "en") Assert.Equal(English, message.Render());
            Texts.Initialize(); // same-code catalog event, no new origin/presentation required
            Assert.Equal(language, Texts.Language);
            Assert.Equal(Texts.T(Key), message.Render());
            Assert.Equal(expected, message.Render());
            if (language is "es" or "fr" or "ru") Assert.NotEqual(English, message.Render());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(Key)]
    [InlineData(English)]
    [InlineData(German)]
    [InlineData("readerGroups.saved {0}\r\n/raw/файл.json")]
    public void LegacyNoticePreservesExactRawBytesRegardlessOfItsContents(string raw)
    {
        var origin = Origin() with { Notice = raw };
        Assert.Null(origin.NoticeReason); // existing constructors need no new argument
        var message = ReaderGroupsDialog.OriginNoticePresentation(origin);
        foreach (string language in new[] { "de", "en", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(raw, message.Render());
            Assert.Equal(raw, origin.Notice);
        }
    }

    [Fact]
    public void ExplicitReasonTakesPrecedenceWithoutMutatingRawCompatibilityOrOriginState()
    {
        var origin = Origin() with { Notice = "raw compatibility {0}", NoticeReason = ReaderGroupsNoticeReason.FirstMemberUnavailable };
        var before = origin with { };
        var message = ReaderGroupsDialog.OriginNoticePresentation(origin);
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T(Key), message.Render());
            Assert.Equal(before, origin);
            Assert.Equal("raw compatibility {0}", origin.Notice);
            Assert.Equal(new Vector(13, 29), origin.Scroll);
            Assert.Equal(new Vector(2, 7), origin.GroupsScroll);
            Assert.Equal(new Vector(5, 11), origin.MembersScroll);
            Assert.Equal("GroupSearch", origin.FocusName);
        }
    }

    [Fact]
    public void NoNoticeKeepsLocalizedHintAndUnknownReasonDoesNotInventProvenance()
    {
        var hint = ReaderGroupsDialog.OriginNoticePresentation(null);
        var unknown = Origin() with { NoticeReason = (ReaderGroupsNoticeReason)999, Notice = "raw {0}" };
        var raw = ReaderGroupsDialog.OriginNoticePresentation(unknown);
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T("readerGroups.hint"), hint.Render());
            Assert.Equal("raw {0}", raw.Render());
        }
    }

    [Fact]
    public void ProducerConsumerAndLocalePathKeepTypedMessageWithoutNewSearchOrStateWrites()
    {
        string shell = ReadSource("Desktop/Shell/MainWindow.ReaderGroups.cs");
        Assert.Contains("{ Notice = null, NoticeReason = ReaderGroupsNoticeReason.FirstMemberUnavailable }", shell);
        Assert.DoesNotContain(English, shell);
        Assert.DoesNotContain(German, shell);
        Assert.Contains("generation != _readerGroupReadGeneration", shell);
        Assert.Contains("!ReferenceEquals(_readerGroupReturn, request)", shell);
        Assert.Contains("await ShowReaderGroupsAsync(request.Origin);", shell);
        string dialog = ReadSource("Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs");
        Assert.Contains("ReaderGroupsNoticeReason? NoticeReason = null", dialog);
        Assert.Contains("ShowMessage(loaded.CanWrite ? OriginNoticePresentation(_origin) : LoadError(loaded));", dialog);
        Assert.Contains("_message = text; RenderPresentation();", dialog);
        Assert.Contains("C<TextBlock>(\"GroupMessage\").Text = _message?.Render();", dialog);
        int start = dialog.IndexOf("private void LanguageChanged(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = dialog.IndexOf("internal bool CanExecute(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string refresh = dialog[start..end];
        Assert.Contains("if (_closed) return;", refresh);
        Assert.Contains("RenderPresentation();", refresh);
        foreach (string forbidden in new[] { "SearchAsync(", "ResolveMembersAsync(", "ShowMessage(", "_message =", "_draft =",
            "ItemsSource =", "SelectedItem =", "_candidateSelectionOrder.", "CaretIndex =", ".Focus()" })
            Assert.DoesNotContain(forbidden, refresh);
    }

    private static ReaderGroupsDialogOrigin Origin() => new(Guid.NewGuid(), new ResourceId("source", "b", "123"),
        new Vector(13, 29), "GroupSearch", new Vector(2, 7), new Vector(5, 11));

    private static string ReadSource(string relative, [CallerFilePath] string path = "") => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
