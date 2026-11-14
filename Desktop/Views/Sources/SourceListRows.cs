using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan;

/// <summary>Passive UI union only. A described source is not a registered provider and gains no execution or consent authority.</summary>
internal sealed record SourceListRow(SourceRow Source, PluginCatalogEntry? Plugin, bool IsActive)
{
    internal SourceMediaIntegrityPolicyView? MediaIntegrityPolicy { get; init; } = Source.MediaIntegrityPolicy;
    internal SourceWordingClassification? Classification => Source.Classification;
    internal IReadOnlyList<SourceBadge> Badges => SourceWording.Badges(Classification, IsActive, IsActive && Plugin == null && MediaIntegrityPolicy == null);
    internal ProviderDescriptor Descriptor => Source.Descriptor;
    internal string Id => Source.Id;
    internal string Name => Source.Name;
    internal string Address => Source.Address;
    internal string Summary => Source.Summary;
    internal IReadOnlyList<SourceWatchRow> Watches => Source.Watches;
}

internal static class SourceListRows
{
    internal static IReadOnlyList<SourceListRow> Merge(IReadOnlyList<SourceRow> registered, IReadOnlyList<PluginCatalogEntry> entries)
    {
        // A plugin's files share one set of source rows. Prefer the selected build, then active, then a stable key.
        var described = entries.Where(entry => entry.Description != null)
            .GroupBy(entry => entry.PluginId ?? entry.Key, StringComparer.Ordinal)
            .Select(group => group.OrderBy(entry => entry.CandidateId != null && entry.CandidateId == entry.Activation?.SelectedCandidateId ? 0
                    : entry.CandidateId != null && entry.CandidateId == entry.Activation?.Active?.CandidateId ? 1 : 2)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal).First())
            .SelectMany(entry => entry.Description!.Sources.Select(source => (Entry: entry, Source: source))).ToArray();
        var result = new List<SourceListRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SourceRow row in registered)
        {
            if (!seen.Add(row.Id)) continue;
            var matches = described.Where(item => item.Source.SourceId == row.Id &&
                (row.MediaIntegrityPolicy == null || item.Entry.PluginId == row.MediaIntegrityPolicy.Snapshot.PluginOwnerId)).ToArray();
            // Ambiguous self-declarations never pick a plugin action target by enumeration order.
            result.Add(new(row, matches.Length == 1 ? matches[0].Entry : null, row.MediaIntegrityPolicy?.IsActive ?? true));
        }
        foreach (var group in described.GroupBy(item => item.Source.SourceId, StringComparer.Ordinal))
        {
            if (!seen.Add(group.Key)) continue;
            var matches = group.ToArray();
            var (entry, source) = matches.OrderBy(item => item.Entry.PluginId, StringComparer.Ordinal)
                .ThenBy(item => item.Entry.Key, StringComparer.Ordinal).First();
            var descriptor = new ProviderDescriptor(source.SourceId, source.Name, source.BaseUri,
                Capabilities(source.Capabilities), source.HttpHosts.Select(host => host.Host).ToArray(), entry.Description!.PluginVersion, false);
            var row = new SourceRow(descriptor, Desktop.Localization.Texts.T("sources.inactiveHint"), Array.Empty<SourceWatchRow>())
            { Classification = SourceWording.Classify(entry.Description, source) };
            // Keep the source visible even if several plugins self-declare its ID, but never attach arbitrary plugin actions.
            result.Add(new(row, matches.Length == 1 ? entry : null, false));
        }
        return result.OrderBy(row => row.IsActive ? 0 : 1).ThenBy(row => SourceWording.MaturityRank(row.Classification))
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private static ProviderCapabilities Capabilities(SourceCapabilities capabilities)
    {
        ProviderCapabilities result = ProviderCapabilities.None;
        if (capabilities.HasFlag(SourceCapabilities.ThreadRead)) result |= ProviderCapabilities.ThreadWatch;
        if (capabilities.HasFlag(SourceCapabilities.BoardCatalog)) result |= ProviderCapabilities.BoardCatalog;
        if (capabilities.HasFlag(SourceCapabilities.PostCapture)) result |= ProviderCapabilities.PostCapture;
        if (capabilities.HasFlag(SourceCapabilities.OriginalMedia)) result |= ProviderCapabilities.OriginalMedia;
        if (capabilities.HasFlag(SourceCapabilities.Previews)) result |= ProviderCapabilities.Previews;
        return result;
    }
}
