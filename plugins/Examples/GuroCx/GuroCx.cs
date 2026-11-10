// Deliberately blocked source scaffold. The documented Kokonotsuba API uses
// query-only thread identities, which the current SDK route contract cannot
// represent without inventing a canonical browser endpoint.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.GuroCx;

public sealed class GuroCxPlugin : ISourcePlugin
{
    public const string SourceId = "gurocx";
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription("meowchan.source.gurocx", "Guro.cx", "1.0.0", SdkContract.Version,
        "Reserved for a future Guro.cx adapter after a canonical browser-route contract is verified.",
        Array.Empty<SourceDescription>(), Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("maturity", "Status", InformationKind.Text, "MeowChan · experimental · technically blocked"),
         new InformationEntry("block", "Technical block", InformationKind.Text,
            "The proven Kokonotsuba metadata API exposes threads through query-only koko.php requests. The SDK requires one canonical path-only thread route and validates snapshot URLs against it. No browser route is guessed; the source is therefore not registered yet.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://boards.guro.st/")
    ) with { DeclarationStatus = PluginDeclarationStatus.Blocked };

    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<CatalogReadResult>(new CatalogFailure(request.Board,
            new FailureDetail("technical_block", "Guro.cx is not registered: its query-only Kokonotsuba thread contract has no honest SDK canonical route.")));
    }

    public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ThreadReadResult>(new ThreadFailure(request.Thread,
            new FailureDetail("technical_block", "Guro.cx is not registered until its canonical browser thread route is verified.")));
    }
}
