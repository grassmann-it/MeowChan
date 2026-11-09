// Deliberately blocked source scaffold. The current public entrance redirects to
// a members-only page, so registering a board route would invent a contract or
// imply credentials that MeowChan is not allowed to acquire.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.AnonImageboards;

public sealed class AnonImageboardsPlugin : ISourcePlugin
{
    public const string SourceId = "anonimageboards";
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription("meowchan.source.anonimageboards",
        "Anon Imageboards", "1.0.0", SdkContract.Version,
        "Reserved for a future public Anon Imageboards adapter after a public board contract is verified.",
        Array.Empty<SourceDescription>(), Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("maturity", "Status", InformationKind.Text,
            "MeowChan · experimental · public entrance unavailable"),
         new InformationEntry("block", "Technical block", InformationKind.Text,
            "The current public entrances redirect to an Anon Premium members-only page. No public board, catalog, thread or media contract is proven; no login, credentials, cookies, replacement domain or synthetic Tinyboard/Vichan route is used."),
         new InformationEntry("recheck", "Recheck requirement", InformationKind.Text,
            "A future adapter requires a fresh public probe proving board, catalog, thread, media, redirect and 404 behavior before any source route is registered.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://anonimageboards.com/"));

    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<CatalogReadResult>(new CatalogFailure(request.Board,
            new FailureDetail("public_entrance_unavailable",
                "Anon Imageboards is not registered: its current public entrance redirects to a members-only page.")));
    }

    public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ThreadReadResult>(new ThreadFailure(request.Thread,
            new FailureDetail("public_entrance_unavailable",
                "Anon Imageboards is not registered until a public board and thread contract is verified.")));
    }
}
