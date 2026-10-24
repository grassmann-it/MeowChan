using System;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan.Desktop;

// Provenance belongs to one exact attachment binding, never to a globally shared asset ID.
internal static class MediaProvenanceText
{
    private static string T(string en, string de) => Texts.Culture.TwoLetterISOLanguageName == "de" ? de : en;
    internal static string Summary(MediaAttachmentBinding? binding) => binding?.Acceptance.Kind == MediaAcceptanceKind.ExplicitProviderMismatch
        ? T("Saved with source permission; source size or checksum differs.",
            "Mit Quellenfreigabe gespeichert; Anbietergröße oder Prüfsumme weicht ab.") : string.Empty;

    internal static string LocalSize(long length) => T("Saved file: ", "Gespeicherte Datei: ")
        + length.ToString("N0", Texts.Culture) + " bytes";

    internal static string Context(MediaAttachmentBinding? binding)
    {
        if (binding == null) return string.Empty;
        MediaProviderMetadata provider = binding.Provider;
        string text = Summary(binding);
        void Add(string line) => text += (text.Length == 0 ? "" : Environment.NewLine) + line;
        if (provider.ExpectedSize is long size)
            Add(T("Source reported: ", "Anbieterangabe: ") + size.ToString("N0", Texts.Culture) + " bytes");
        return text;
    }

    internal static string Details(MediaAttachmentBinding? binding)
    {
        if (binding == null) return string.Empty;
        string text = Context(binding);
        void Add(string line) => text += (text.Length == 0 ? "" : Environment.NewLine) + line;
        Add(T("Saved file MD5: ", "MD5 der gespeicherten Datei: ") + binding.Actual.Md5);
        if (binding.Provider.ExpectedMd5 != null) Add(T("Source MD5: ", "Anbieter-MD5: ") + binding.Provider.ExpectedMd5);
        if (binding.Provider.ExpectedSha256 != null) Add(T("Source SHA-256: ", "Anbieter-SHA-256: ") + binding.Provider.ExpectedSha256);
        if (binding.Provider.Url != null) Add(T("Source URL: ", "Anbieter-URL: ") + binding.Provider.Url);
        return text;
    }
}
