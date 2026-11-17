using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop;

// Shared test oracles: compiled into the Debug apphost or the external test executables, never Release MeowChan.
internal static class ApphostAcceptanceAssertions
{
    // Exact current delivery contract, not an enabled-plugin count: 12 chan plugins serving 16 chan sources
    // (two engine families with several sites each), one post-search booru provider, five filters and
    // two route-less blocked scaffolds.
    internal static readonly string[] ApphostBundledPluginIds =
    {
        "meowchan.official.4chan", "meowchan.official.8kun", "meowchan.official.danbooru",
        "meowchan.source.vichan", "meowchan.source.kohlchan",
        "meowchan.source.endchan", "meowchan.example.image-dimensions", "meowchan.filter.file-rules",
        "meowchan.filter.hash-blocklist", "meowchan.filter.animation-format", "meowchan.filter.png-metadata",
        "meowchan.source.2ch", "meowchan.source.jschan", "meowchan.source.sevenchan",
        "meowchan.source.bbwchan", "meowchan.source.anonimageboards", "meowchan.source.capybarachan",
        "meowchan.source.eightchanmoe", "meowchan.source.fchan", "meowchan.source.gurocx",
    };
    // Family plugins are on by default; their experimental sites start off behind their own site switch.
    internal static readonly string[] ApphostDefaultOffPluginIds =
    {
        "meowchan.source.kohlchan", "meowchan.source.endchan", "meowchan.source.2ch", "meowchan.source.sevenchan",
        "meowchan.source.bbwchan", "meowchan.source.anonimageboards", "meowchan.source.eightchanmoe",
        "meowchan.source.gurocx", "meowchan.source.fchan", "meowchan.source.capybarachan",
    };
    internal static readonly string[] ApphostChanSourceIds = { "2ch", "4chan", "8kun", "bbwchan", "capybarachan", "eightchanmoe", "endchan", "fchan", "kohlchan", "lainchan", "leftypol", "sevenchan", "sushichan", "uboachan", "wizchan", "zzzchan" };

    internal static bool ApphostFixturePluginEnabled(string id) =>
        !ApphostDefaultOffPluginIds.Contains(id, StringComparer.Ordinal)
        || id is "meowchan.source.kohlchan" or "meowchan.source.endchan";

    /// <summary>The plugin that serves a delivered chan source: its family for a family site, else its own plugin.</summary>
    internal static string ApphostSourceOwner(string sourceId) => ChanSourceFamilies.Replaced
        .FirstOrDefault(pair => pair.Value.SourceId == sourceId).Value.Family ?? "meowchan.source." + sourceId;

    internal static void RequireApphostBundledInventory(PluginSession plugins, PluginProfileSnapshot installed)
    {
        RequireWorkflow(installed.Plugins.Select(plugin => plugin.PluginId).Order().SequenceEqual(ApphostBundledPluginIds.Order()),
            "Delivered plugin identity inventory differs.");
        RequireWorkflow(plugins.Sources.Select(source => source.Name).Order().SequenceEqual(ApphostChanSourceIds),
            "Delivered chan source inventory differs.");
        RequireWorkflow(plugins.Booru != null, "Delivered Danbooru binding missing.");
        foreach (var plugin in installed.Plugins)
        {
            var description = plugin.Active?.Description;
            RequireWorkflow(description != null, "Delivered declaration unavailable: " + plugin.PluginId);
            bool blocked = plugin.PluginId is "meowchan.source.anonimageboards" or "meowchan.source.gurocx";
            RequireWorkflow((description!.DeclarationStatus == PluginDeclarationStatus.Blocked) == blocked,
                "Delivered declaration status differs: " + plugin.PluginId);
            if (blocked)
                RequireWorkflow(description.Sources.Count == 0 && description.Filters.Count == 0,
                    "Blocked scaffold registered capabilities: " + plugin.PluginId);
            else if (plugin.PluginId == "meowchan.official.danbooru")
                RequireWorkflow(description.Sources.Count == 1 && description.Sources[0].SourceId == "danbooru"
                    && description.Sources[0].Capabilities.HasFlag(SourceCapabilities.PostSearch) && description.Filters.Count == 0,
                    "Delivered booru declaration differs.");
            else if (plugin.PluginId.StartsWith("meowchan.filter.", StringComparison.Ordinal) || plugin.PluginId == "meowchan.example.image-dimensions")
                RequireWorkflow(description.Sources.Count == 0 && description.Filters.Count == 1, "Delivered filter declaration differs.");
            else
                RequireWorkflow((ChanSourceFamilies.IsFamily(plugin.PluginId) ? description.Sources.Count > 1 || plugin.PluginId == ChanSourceFamilies.Jschan
                        : description.Sources.Count == 1) && description.Sources.Count > 0
                    && description.Sources.All(source => !source.Capabilities.HasFlag(SourceCapabilities.PostSearch))
                    && description.Filters.Count == 0, "Delivered chan declaration differs.");
        }
    }

    internal static void RequireAuthorStartupState(PluginProfileSnapshot installed, IReadOnlyList<string> warnings, bool isPaused, string phase)
    {
        RequireWorkflow(isPaused, "Source activation startup is not paused.");
        RequireWorkflow(phase is "install" or "acquire" or "offline", "Unknown author startup phase.");
        const string selectedAdvisory = "Multiple candidates exist; the selected candidate is retained.";
        string[] authorIds = { ChanSourceFamilies.Vichan, "meowchan.example.image-dimensions" };
        List<string> expectedWarnings = new();
        foreach (var plugin in installed.Plugins)
        {
            RequireWorkflow(plugin.Enabled == ApphostFixturePluginEnabled(plugin.PluginId) && plugin.Active != null, "Source activation inventory is unavailable: " + plugin.PluginId);
            if (phase != "install" && authorIds.Contains(plugin.PluginId, StringComparer.Ordinal))
            {
                RequireWorkflow(plugin.Problem == selectedAdvisory && plugin.SelectedCandidateId == plugin.Active!.CandidateId
                    && plugin.Pending == null && plugin.Candidates.Count == 2
                    && plugin.Candidates.All(candidate => candidate.IsPresent && candidate.Problem == null),
                    "Author selection advisory/state differs: " + plugin.PluginId);
                expectedWarnings.Add(plugin.Active!.Description.Name + ": " + selectedAdvisory);
            }
            else RequireWorkflow(plugin.Problem == null, "Unexpected author startup problem: " + plugin.PluginId + ": " + plugin.Problem);
        }
        RequireWorkflow(phase == "install" || expectedWarnings.Count == 2
            && authorIds.All(id => installed.Plugins.Count(plugin => plugin.PluginId == id) == 1), "Both explicitly selected author candidates must be present.");
        RequireWorkflow(warnings.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(expectedWarnings.OrderBy(value => value, StringComparer.Ordinal)),
            "Author startup warning inventory differs: " + string.Join(" | ", warnings));
    }
    private static void RequireWorkflow(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
