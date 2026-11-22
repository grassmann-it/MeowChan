using System.Security.Cryptography;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// Reference tagger of tagger contract 1.0.0: a deterministic test double with no model. It reads every item through
/// the bound asset reader, checks the bytes against the announced SHA-256, and derives suggestions from the bytes, so a
/// test can predict them. Switches let tests produce cancellation, failures, invalid results and network attempts.
/// </summary>
internal sealed class ReferenceTagger : ITaggerPlugin
{
    internal const string PluginId = "test.reference-tagger";
    internal const string TaggerKey = "reference";
    internal const string ModelId = "reference-model";
    internal const string ModelRevision = "r1";

    internal string PluginVersion { get; init; } = "1.0.0";
    internal string ReportedRevision { get; set; } = ModelRevision;
    internal IReadOnlyList<HttpHostDeclaration> NetworkHosts { get; init; } = Array.Empty<HttpHostDeclaration>();
    internal Uri? NetworkProbe { get; init; }
    internal int CancelAfterBatches { get; set; } = -1;
    internal int FailBatch { get; set; } = -1;
    internal bool AnswerForeignItem { get; set; }
    internal bool SkipHashCheck { get; init; }
    internal Func<int, Task>? BeforeBatch { get; set; }
    internal CancellationTokenSource? CancelJob { get; set; }
    internal int Batches { get; private set; }
    internal List<HostHttpResult> NetworkResults { get; } = new();
    internal List<int> BatchSizes { get; } = new();

    internal static TaggerModelRequirement Model => new(ModelId, ModelRevision, "Apache-2.0", 1024);

    internal TaggerDescription Tagger => new(TaggerKey, "Reference tagger", "Deterministic tags derived from bytes; no model.",
        TaggerContract.Version, TaggerMediaKind.Image | TaggerMediaKind.Animation, ["image/png", "image/gif"], [Model],
        new SettingsSchema("1", Array.Empty<SettingDefinition>())) { NetworkHosts = NetworkHosts };

    public PluginDescription Describe() => new(PluginId, "Reference tagger", PluginVersion, SdkContract.Version,
        "Test double for the tagger contract.", Array.Empty<SourceDescription>(), Array.Empty<FilterDescription>(),
        new SettingsSchema("1", Array.Empty<SettingDefinition>()), Array.Empty<InformationEntry>()) { Taggers = [Tagger] };

    /// <summary>The suggestions this double proposes for these bytes (before the confidence floor).</summary>
    internal static IReadOnlyList<TaggerSuggestion> Expected(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return
        [
            new TaggerSuggestion("reference_" + (hash[0] % 8), TaggerTagCategory.General, 0.5 + hash[1] / 512.0),
            new TaggerSuggestion("weak_" + (hash[2] % 4), TaggerTagCategory.Meta, 0.05 + hash[3] / 2560.0),
            new TaggerSuggestion("general", TaggerTagCategory.Rating, 0.99)
        ];
    }

    public async Task<TaggerBatchResult> TagAsync(TaggerBatchRequest request, PluginCallContext context,
        IProgress<TaggerBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        int batch = Batches++;
        BatchSizes.Add(request.Items.Count);
        if (BeforeBatch is not null) await BeforeBatch(batch).ConfigureAwait(false);
        if (batch == CancelAfterBatches) await (CancelJob?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch == FailBatch) throw new InvalidOperationException("Synthetic tagger failure.");
        if (NetworkProbe is not null)
            NetworkResults.Add(await context.Http.SendAsync(new HostHttpRequest(PluginId, HostHttpMethod.Get, NetworkProbe), cancellationToken).ConfigureAwait(false));
        List<TaggerItemResult> items = new();
        for (int index = 0; index < request.Items.Count; index++)
        {
            TaggerMediaInput item = request.Items[index];
            byte[] bytes = await ReadAllAsync(context.Images, item, cancellationToken).ConfigureAwait(false);
            if (!SkipHashCheck && Convert.ToHexStringLower(SHA256.HashData(bytes)) != item.ContentSha256)
                items.Add(new TaggerItemResult(item.ItemId, TaggerItemOutcome.Error, [], "hash_mismatch"));
            else
                items.Add(new TaggerItemResult(AnswerForeignItem && index == 0 ? "foreign" : item.ItemId, TaggerItemOutcome.Tagged,
                    Expected(bytes).Where(value => value.Confidence >= request.MinimumConfidence).Take(request.MaximumSuggestionsPerItem).ToArray()));
            progress?.Report(new TaggerBatchProgress(index + 1, request.Items.Count));
        }
        return new TaggerBatchResult(request.TaggerKey, request.BatchId,
            new TaggerModelProvenance(ModelId, ReportedRevision, Runtime: "managed-test-double"), items);
    }

    private static async Task<byte[]> ReadAllAsync(IImageAssetReader reader, TaggerMediaInput item, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        long offset = 0;
        while (true)
        {
            ImageAssetChunk chunk = await reader.ReadImageAsync(item.AssetToken, offset,
                (int)Math.Min(SdkLimits.MaximumImageChunkBytes, Math.Max(1, item.Length - offset)), cancellationToken).ConfigureAwait(false);
            buffer.Write(chunk.Bytes);
            offset += chunk.Bytes.Length;
            if (chunk.IsEnd) return buffer.ToArray();
        }
    }
}
