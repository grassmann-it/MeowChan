using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class PassiveMediaProvenanceTests
{
    [Theory]
    [InlineData("en", "Imported archive file; no local source permission is implied.")]
    [InlineData("de", "Importierte Archivdatei; daraus folgt keine lokale Quellenfreigabe.")]
    public void PassiveSummaryNeverTurnsImportedHistoryIntoLocalPermission(string language, string expected)
    {
        string previousLanguage = Texts.Language;
        try
        {
            Texts.InitializeWithoutBundlesForTests(); Texts.SetLanguage(language);
            MediaAttachmentBinding binding = new(new(new("4chan", "g", "1"), "1", 0, "synthetic"),
                "synthetic-acq", new string('a', 64), new("1", "original", "https://i.4cdn.org/g/1.jpg", null, 4, null, null),
                new(4, "AAAAAAAAAAAAAAAAAAAAAA==", new string('b', 64)), new(MediaAcceptanceKind.PassiveImport, false, false, DateTimeOffset.UnixEpoch))
            {
                PassiveImport = new("b8cce56f-562e-4c44-8a65-a68879108f11", new string('c', 64), DateTimeOffset.UnixEpoch,
                    "synthetic", new(MediaAcceptanceKind.ExplicitProviderMismatch, DateTimeOffset.UnixEpoch, true, false))
            };
            Assert.Equal(expected, MediaProvenanceText.Summary(binding));
            Assert.Contains(expected, MediaProvenanceText.Details(binding));
            Assert.DoesNotContain(binding.PassiveImport.OperationId, MediaProvenanceText.Details(binding));
            Assert.DoesNotContain(binding.PassiveImport.PackageFingerprint, MediaProvenanceText.Details(binding));
            Assert.Empty(MediaProvenanceText.Summary(binding with { Acceptance = new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch), PassiveImport = null }));
        }
        finally { Texts.Initialize(); Texts.SetLanguage(previousLanguage); }
    }
}
