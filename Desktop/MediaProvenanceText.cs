using System;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan.Desktop;

// Provenance belongs to one exact attachment binding, never to a globally shared asset ID.
internal static class MediaProvenanceText
{
    internal static string Summary(MediaAttachmentBinding? binding) => binding?.Acceptance.Kind switch
    {
        MediaAcceptanceKind.ExplicitProviderMismatch => Texts.T("media.provenance.providerMismatch"),
        MediaAcceptanceKind.PassiveImport => Texts.T("media.provenance.passiveImport"),
        _ => string.Empty
    };

    internal static string FactSize(long bytes) => Texts.T("media.fact.byteSize", bytes, SizeText.Format(bytes));

    internal static string LocalSize(long length) => Texts.T("media.provenance.localSize", length, SizeText.Format(length));

    internal static string Context(MediaAttachmentBinding? binding)
    {
        if (binding == null) return string.Empty;
        MediaProviderMetadata provider = binding.Provider;
        string text = Summary(binding);
        void Add(string line) => text += (text.Length == 0 ? "" : Environment.NewLine) + line;
        if (provider.ExpectedSize is long size)
            Add(Texts.T("media.provenance.sourceSize", size, SizeText.Format(size)));
        if (provider.ProviderSizeHint is MediaProviderSizeHint hint)
            Add(Texts.T("media.provenance.sizeHint", hint.RawValue, hint.UnitLabel, hint.Precision));
        return text;
    }

    internal static string Details(MediaAttachmentBinding? binding)
    {
        if (binding == null) return string.Empty;
        string text = Context(binding);
        void Add(string line) => text += (text.Length == 0 ? "" : Environment.NewLine) + line;
        Add(Texts.T("media.provenance.md5", binding.Actual.Md5));
        if (binding.Provider.ExpectedMd5 != null) Add(Texts.T("media.provenance.sourceMd5", binding.Provider.ExpectedMd5));
        if (binding.Actual.Sha1 != null) Add(Texts.T("media.provenance.sha1", binding.Actual.Sha1));
        if (binding.Provider.ExpectedSha1 != null) Add(Texts.T("media.provenance.sourceSha1", binding.Provider.ExpectedSha1));
        if (binding.Provider.ExpectedSha256 != null) Add(Texts.T("media.provenance.sourceSha256", binding.Provider.ExpectedSha256));
        if (binding.Provider.Url != null) Add(Texts.T("media.provenance.sourceUrl", binding.Provider.Url));
        return text;
    }
}
