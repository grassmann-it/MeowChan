using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MeowChan.Desktop.Updates;

internal static class UpdateReleaseParser
{
    internal static UpdateInfo? Parse(byte[] json, bool includePrereleases, string runtimeIdentifier)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (!includePrereleases)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a release object.");
            return ParseRelease(document.RootElement, false, runtimeIdentifier);
        }
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 100)
            throw new InvalidDataException("Expected at most 100 releases.");
        UpdateInfo? newest = null;
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            UpdateInfo? candidate = ParseRelease(release, true, runtimeIdentifier);
            if (candidate is not null && (newest is null || candidate.Version.CompareTo(newest.Version) > 0)) newest = candidate;
        }
        return newest;
    }

    private static UpdateInfo? ParseRelease(JsonElement release, bool includePrereleases, string rid)
    {
        if (release.ValueKind != JsonValueKind.Object
            || !release.TryGetProperty("draft", out JsonElement draft)
            || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !release.TryGetProperty("prerelease", out JsonElement prerelease)
            || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryGetString(release, "tag_name", out string tag))
            throw new InvalidDataException("Release entry has an invalid structure.");
        // Well-formed drafts and non-SemVer tags are deliberately outside version selection.
        if (draft.GetBoolean() || !SemanticVersion.TryParse(tag, out SemanticVersion? version)) return null;
        if (!includePrereleases && (prerelease.GetBoolean() || version.IsPrerelease)) return null;
        if (release.TryGetProperty("assets", out JsonElement declaredAssets) && declaredAssets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Release assets must be an array.");
        // Build the official destination from the validated tag; html_url is untrusted response data.
        Uri releaseUri = ReleaseUri(tag);
        List<Uri> matchingAssets = [];
        if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (!TryGetString(asset, "name", out string name) || !MatchesAsset(name, version, rid)
                    || !TryGetString(asset, "browser_download_url", out string url)) continue;
                Uri expected = AssetUri(tag, name);
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? actual) && actual.AbsoluteUri == expected.AbsoluteUri)
                    matchingAssets.Add(expected);
            }
        }
        return new UpdateInfo(version, releaseUri, matchingAssets.Count == 1 ? matchingAssets[0] : releaseUri);
    }

    internal static bool IsSafeStoredInfo(UpdateInfo info)
    {
        string[] tags = [info.Version.ToString(), "v" + info.Version];
        foreach (string tag in tags)
        {
            if (!string.Equals(info.ReleaseUri.AbsoluteUri, ReleaseUri(tag).AbsoluteUri, StringComparison.Ordinal)) continue;
            if (string.Equals(info.DownloadUri.AbsoluteUri, info.ReleaseUri.AbsoluteUri, StringComparison.Ordinal)) return true;
            string name = Uri.UnescapeDataString(info.DownloadUri.Segments[^1]);
            return IsKnownAsset(name, info.Version)
                && string.Equals(info.DownloadUri.AbsoluteUri, AssetUri(tag, name).AbsoluteUri, StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsKnownAsset(string name, SemanticVersion version)
    {
        foreach (string rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
            if (MatchesAsset(name, version, rid)) return true;
        return false;
    }

    private static bool MatchesAsset(string name, SemanticVersion version, string rid)
    {
        string prefix = $"MeowChan-{version}-{rid}";
        return rid switch
        {
            "win-x64" or "win-arm64" => name == prefix + ".zip",
            "osx-x64" or "osx-arm64" => name == prefix + ".dmg" || name == prefix + ".zip",
            "linux-x64" or "linux-arm64" => name == prefix + ".AppImage"
                || name == prefix + "-bundled-vlc.tar.gz" || name == prefix + "-system-vlc.tar.gz",
            _ => false
        };
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement item)
            || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString()!;
        return true;
    }

    private static Uri ReleaseUri(string tag) => new($"https://github.com/tfgrass/MeowChan/releases/tag/{Uri.EscapeDataString(tag)}");
    private static Uri AssetUri(string tag, string name) => new($"https://github.com/tfgrass/MeowChan/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");
}
