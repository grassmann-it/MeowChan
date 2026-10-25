using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// A finite, explicitly selected observation; provider declarations and prior byte observations
// deliberately occupy different fields. No catalog scanning or original probing happens here.
internal sealed record NewSourceWaveLiveManifest(int SchemaVersion, NewSourceWaveLiveSelection[] Sources);
internal sealed record NewSourceWaveLiveSelection(string SourceId, string PluginId, string BoardId, string ThreadId,
    string BoardUrl, string ThreadUrl, string CatalogUrl, string ThreadMetadataUrl, string[] OriginalHops,
    string MediaId, string PostId, int Position, string Extension, long? ProviderExpectedSize = null,
    string? ProviderExpectedMd5Base64 = null, string? ProviderExpectedSha1Base64 = null,
    string? ProviderExpectedSha256 = null, MediaProviderSizeHint? ProviderSizeHint = null,
    long? PriorObservedOriginalBytes = null, string? PriorObservedOriginalSha256 = null,
    bool AllowConsentRetry = false)
{
    internal string OriginalUrl => OriginalHops[0];
    internal ResourceId Resource => new(SourceId, BoardId, ThreadId);
    internal static readonly string[] SourceIds = ["2ch", "leftypol", "uboachan", "zzzchan", "sushichan"];

    internal void Validate()
    {
        if (!SourceIds.Contains(SourceId, StringComparer.Ordinal) || PluginId != "meowchan.source." + SourceId
            || string.IsNullOrWhiteSpace(BoardId) || string.IsNullOrWhiteSpace(ThreadId) || string.IsNullOrWhiteSpace(PostId)
            || string.IsNullOrWhiteSpace(MediaId) || Position < 0 || ProviderExpectedSize < 0
            || PriorObservedOriginalBytes is <= 0 or > NewSourceWaveLiveTransport.MaximumOriginalBytes
            || AllowConsentRetry != (SourceId == "leftypol")
            || OriginalHops.Length != (SourceId == "2ch" ? 2 : 1))
            throw new ArgumentException("Invalid finite new-source selection.");
        string[] hosts = SourceId switch {
            "2ch" => ["2ch.hk", "2ch.su"], "leftypol" => ["leftypol.org"], "uboachan" => ["uboachan.net"],
            "zzzchan" => ["zzzchan.xyz"], "sushichan" => ["sushigirl.cafe"], _ => [] };
        string[] endpoints = [BoardUrl, ThreadUrl, CatalogUrl, ThreadMetadataUrl, .. OriginalHops];
        foreach (string url in endpoints)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !hosts.Contains(uri.IdnHost, StringComparer.Ordinal))
                throw new ArgumentException("Selection URLs must be exact declared public HTTPS routes.");
        if (new[] { CatalogUrl, ThreadMetadataUrl }.Concat(OriginalHops).Distinct(StringComparer.Ordinal).Count() != 2 + OriginalHops.Length)
            throw new ArgumentException("Acquisition endpoints must be distinct.");
        if (SourceId == "2ch" && (ProviderExpectedSize != null || ProviderSizeHint == null
            || new Uri(OriginalHops[0]).Host != "2ch.hk" || new Uri(OriginalHops[1]).Host != "2ch.su"))
            throw new ArgumentException("2ch selection must keep rounded size as a hint and bind both original redirect hops.");
        if (SourceId == "uboachan" && ProviderExpectedSha1Base64 == null)
            throw new ArgumentException("Uboachan live acceptance explicitly requires a SHA-1 original.");
        if (SourceId == "sushichan" && ProviderExpectedMd5Base64 == null || SourceId == "zzzchan" && ProviderExpectedSha256 == null)
            throw new ArgumentException("Strict source digest is required.");
    }
}

public sealed class NewSourceWaveLiveFactAttribute : FactAttribute
{
    public NewSourceWaveLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEOWCHAN_NEW_WAVE_LIVE_MANIFEST")))
            Skip = "Opt-in five-new-source acceptance requires its separate bounded manifest; no network requested.";
    }
}
