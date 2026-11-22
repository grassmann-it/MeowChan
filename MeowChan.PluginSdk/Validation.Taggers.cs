using System.Text;

namespace MeowChan.PluginSdk;

public static partial class SdkValidation
{
    /// <summary>
    /// Validates a batch request before the host sends it: the described tagger, 1..batch-ceiling unique items of a
    /// supported kind and MIME type inside the size ceilings, SHA-256, a confidence floor in [0, 1], a suggestion cap
    /// and the tagger settings. Issues never echo item, token or tag text.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateTaggerRequest(TaggerBatchRequest request,
        TaggerDescription tagger)
    {
        Checks checks = new Checks();
        checks.TaggerRequest(request, tagger);
        return checks.Issues;
    }

    /// <summary>
    /// Validates the request and its result: the result echoes tagger and batch, names a declared model, answers every
    /// requested item exactly once, keeps suggestions only for <see cref="TaggerItemOutcome.Tagged"/>, at most the
    /// requested count per item, with valid unique names, raw confidences between the floor and 1 and time ranges only
    /// for video or animation inside the item's duration.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateTaggerResult(TaggerBatchRequest request,
        TaggerBatchResult result, TaggerDescription tagger)
    {
        Checks checks = new Checks();
        checks.TaggerRequest(request, tagger);
        if (request is null || tagger is null) return checks.Issues;
        checks.TaggerResult(request, result, tagger);
        return checks.Issues;
    }

    private sealed partial class Checks
    {
        private const TaggerMediaKind KnownMedia = TaggerMediaKind.Image | TaggerMediaKind.Animation
            | TaggerMediaKind.Video | TaggerMediaKind.Audio;

        private void Taggers(PluginDescription description, string path)
        {
            if (description.Taggers is null) return;
            if (description.Taggers.Count > 0 && description.SdkVersion != SdkContract.Version)
                Add(path + ".sdkVersion", "incompatible_sdk", $"Taggers require SDK {SdkContract.Version}.");
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            List(description.Taggers, path + ".taggers", (tagger, itemPath) =>
            {
                Tagger(tagger, itemPath);
                if (tagger is not null && !keys.Add(tagger.Key)) Add(itemPath + ".key", "duplicate_tagger", "Tagger key is duplicated.");
            }, TaggerLimits.MaximumTaggersPerPlugin);
        }

        private void Tagger(TaggerDescription? tagger, string path)
        {
            if (tagger is null) { Add(path, "missing", "Tagger description is required."); return; }
            Identity(tagger.Key, path + ".key");
            Text(tagger.Name, path + ".name", 256, required: true);
            Text(tagger.Description, path + ".description", 16384, required: true);
            if (!TaggerContract.IsSupported(tagger.ContractVersion))
                Add(path + ".contractVersion", "incompatible_tagger_contract", $"This host supports tagger contract {TaggerContract.Version}.");
            if (tagger.SupportedMedia == TaggerMediaKind.None || (tagger.SupportedMedia & ~KnownMedia) != 0)
                Add(path + ".supportedMedia", "unknown_media", "Declare supported tagger media kinds.");
            List(tagger.SupportedMimeTypes, path + ".supportedMimeTypes", (mime, itemPath) => Text(mime, itemPath, 256, required: true), 64);
            if (tagger.SupportedMimeTypes?.Count == 0) Add(path + ".supportedMimeTypes", "missing_media_types", "Declare at least one MIME type.");
            HashSet<string> models = new HashSet<string>(StringComparer.Ordinal);
            List(tagger.Models, path + ".models", (model, itemPath) =>
            {
                if (model is null) { Add(itemPath, "missing", "Model requirement is required."); return; }
                Identity(model.ModelId, itemPath + ".modelId");
                Identity(model.Revision, itemPath + ".revision");
                Text(model.License, itemPath + ".license", 256, required: true);
                if (model.SizeBytes <= 0) Add(itemPath + ".sizeBytes", "invalid_size", "Model size must be positive.");
                Sha256(model.Sha256, itemPath + ".sha256");
                if (!models.Add(model.ModelId + "\n" + model.Revision)) Add(itemPath, "duplicate_model", "Model requirement is duplicated.");
            }, TaggerLimits.MaximumModelsPerTagger);
            Schema(tagger.Settings, path + ".settings", SettingScope.Tagger);
            HashSet<string> hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List(tagger.NetworkHosts, path + ".networkHosts", (host, itemPath) =>
            {
                if (host is null) { Add(itemPath, "missing", "Network host declaration is required."); return; }
                Host(host.Host, itemPath + ".host");
                if (host.Host is not null && !hosts.Add(host.Host)) Add(itemPath + ".host", "duplicate_host", "Network host is duplicated.");
                if (host.Purpose != HttpHostPurpose.Metadata) Add(itemPath + ".purpose", "unknown_purpose", "Tagger network hosts are metadata hosts only.");
            }, TaggerLimits.MaximumNetworkHosts);
            if (tagger.MaximumInputBytes is <= 0 or > TaggerLimits.MaximumInputBytes)
                Add(path + ".maximumInputBytes", "invalid_bounds", "Input ceiling must be between 1 byte and 256 MiB.");
            if (tagger.MaximumBatchItems is <= 0 or > TaggerLimits.MaximumBatchItems)
                Add(path + ".maximumBatchItems", "invalid_bounds", "Batch ceiling must be between 1 and 64 items.");
        }

        internal void TaggerRequest(TaggerBatchRequest? request, TaggerDescription? tagger)
        {
            if (request is null || tagger is null) { Add("request", "missing", "Tagger description and request are required."); return; }
            Identity(request.TaggerKey, "request.taggerKey");
            if (request.TaggerKey != tagger.Key) Add("request.taggerKey", "wrong_tagger", "Request identifies another tagger.");
            Identity(request.BatchId, "request.batchId");
            Identity(request.ConfigurationRevision, "request.configurationRevision");
            if (!double.IsFinite(request.MinimumConfidence) || request.MinimumConfidence is < 0 or > 1)
                Add("request.minimumConfidence", "out_of_range", "Confidence floor must be between 0 and 1.");
            if (request.MaximumSuggestionsPerItem is < 1 or > TaggerLimits.MaximumSuggestionsPerItem)
                Add("request.maximumSuggestionsPerItem", "out_of_range", "Suggestion cap must be between 1 and 512.");
            Settings(tagger.Settings, request.TaggerSettings, "request.taggerSettings");
            int batchCeiling = Math.Min(Math.Max(tagger.MaximumBatchItems, 1), TaggerLimits.MaximumBatchItems);
            long inputCeiling = Math.Min(Math.Max(tagger.MaximumInputBytes, 1), TaggerLimits.MaximumInputBytes);
            if (request.Items?.Count == 0) Add("request.items", "empty_batch", "A batch needs at least one item.");
            HashSet<string> items = new HashSet<string>(StringComparer.Ordinal);
            List(request.Items, "request.items", (item, itemPath) =>
            {
                if (item is null) { Add(itemPath, "missing", "Batch item is required."); return; }
                Identity(item.ItemId, itemPath + ".itemId");
                if (item.ItemId is not null && !items.Add(item.ItemId)) Add(itemPath + ".itemId", "duplicate_item", "Item identity is duplicated.");
                if (!IsSingleKind(item.Kind)) Add(itemPath + ".kind", "unknown_media", "An item has exactly one known media kind.");
                else if (!tagger.SupportedMedia.HasFlag(item.Kind)) Add(itemPath + ".kind", "unsupported_media", "The tagger does not accept this media kind.");
                Text(item.MimeType, itemPath + ".mimeType", 256, required: true);
                if (item.MimeType is not null && !(tagger.SupportedMimeTypes?.Contains(item.MimeType, StringComparer.OrdinalIgnoreCase) ?? false))
                    Add(itemPath + ".mimeType", "unsupported_media", "The tagger does not accept this MIME type.");
                if (item.Length <= 0 || item.Length > inputCeiling)
                    Add(itemPath + ".length", "input_size_limit", "Item bytes must fit the tagger input ceiling.");
                if (item.Kind is TaggerMediaKind.Image or TaggerMediaKind.Animation && item.Length > SdkLimits.MaximumImageBytes)
                    Add(itemPath + ".length", "image_size_limit", "Image bytes must fit the image byte budget.");
                Sha256(item.ContentSha256, itemPath + ".contentSha256", required: true);
                Identity(item.AssetToken, itemPath + ".assetToken");
                if (item.PixelWidth is <= 0 || item.PixelHeight is <= 0) Add(itemPath, "invalid_dimensions", "Pixel dimensions must be positive when given.");
                if (item.DurationMilliseconds is < 0 or > TaggerLimits.MaximumTimeRangeMilliseconds)
                    Add(itemPath + ".durationMilliseconds", "out_of_range", "Duration must be between 0 and 24 hours when given.");
            }, batchCeiling);
        }

        internal void TaggerResult(TaggerBatchRequest request, TaggerBatchResult? result, TaggerDescription tagger)
        {
            if (result is null) { Add("result", "missing", "A tagger result is required."); return; }
            if (result.TaggerKey != request.TaggerKey) Add("result.taggerKey", "wrong_tagger", "Result identifies another tagger.");
            if (result.BatchId != request.BatchId) Add("result.batchId", "wrong_batch", "Result identifies another batch.");
            Provenance(result.Provenance, tagger, "result.provenance");
            Dictionary<string, TaggerMediaInput> requested = new Dictionary<string, TaggerMediaInput>(StringComparer.Ordinal);
            foreach (TaggerMediaInput item in request.Items ?? Array.Empty<TaggerMediaInput>())
                if (item?.ItemId is not null) requested.TryAdd(item.ItemId, item);
            HashSet<string> answered = new HashSet<string>(StringComparer.Ordinal);
            List(result.Items, "result.items", (item, itemPath) =>
            {
                if (item is null) { Add(itemPath, "missing", "Item result is required."); return; }
                if (item.ItemId is null || !requested.TryGetValue(item.ItemId, out TaggerMediaInput? input))
                { Add(itemPath + ".itemId", "unknown_item", "Result names an item that was not requested."); return; }
                if (!answered.Add(item.ItemId)) Add(itemPath + ".itemId", "duplicate_item", "Item is answered more than once.");
                EnumValue(item.Outcome, itemPath + ".outcome");
                if (item.ReasonCode is not null) Identity(item.ReasonCode, itemPath + ".reasonCode");
                if (item.Suggestions is null) { Add(itemPath + ".suggestions", "missing", "A collection is required, including an empty one."); return; }
                if (item.Outcome != TaggerItemOutcome.Tagged && item.Suggestions.Count != 0)
                    Add(itemPath + ".suggestions", "unexpected_suggestions", "Only a tagged item carries suggestions.");
                if (item.Suggestions.Count > request.MaximumSuggestionsPerItem)
                { Add(itemPath + ".suggestions", "too_many_suggestions", "Item exceeds the requested suggestion cap."); return; }
                HashSet<(string, TaggerTagCategory, long, long)> seen = new HashSet<(string, TaggerTagCategory, long, long)>();
                List(item.Suggestions, itemPath + ".suggestions", (suggestion, suggestionPath) =>
                {
                    if (suggestion is null) { Add(suggestionPath, "missing", "Suggestion is required."); return; }
                    TagName(suggestion.Name, suggestionPath + ".name");
                    EnumValue(suggestion.Category, suggestionPath + ".category");
                    if (!double.IsFinite(suggestion.Confidence) || suggestion.Confidence < request.MinimumConfidence || suggestion.Confidence > 1)
                        Add(suggestionPath + ".confidence", "out_of_range", "Confidence must be between the requested floor and 1.");
                    if (suggestion.Range is { } range)
                    {
                        if (input.Kind is not (TaggerMediaKind.Video or TaggerMediaKind.Animation))
                            Add(suggestionPath + ".range", "unexpected_range", "Only video or animation suggestions have a time range.");
                        long end = input.DurationMilliseconds ?? TaggerLimits.MaximumTimeRangeMilliseconds;
                        if (range.StartMilliseconds < 0 || range.EndMilliseconds <= range.StartMilliseconds || range.EndMilliseconds > end)
                            Add(suggestionPath + ".range", "invalid_range", "Time range must be nonempty and inside the item.");
                    }
                    if (suggestion.Name is not null && !seen.Add((suggestion.Name, suggestion.Category,
                            suggestion.Range?.StartMilliseconds ?? -1, suggestion.Range?.EndMilliseconds ?? -1)))
                        Add(suggestionPath, "duplicate_suggestion", "Suggestion is duplicated for this item.");
                }, TaggerLimits.MaximumSuggestionsPerItem);
            }, TaggerLimits.MaximumBatchItems);
            if (result.Items is not null && answered.Count != requested.Count)
                Add("result.items", "missing_item", "Every requested item needs exactly one result.");
        }

        private void Provenance(TaggerModelProvenance? provenance, TaggerDescription tagger, string path)
        {
            if (provenance is null) { Add(path, "missing", "Model provenance is required."); return; }
            Identity(provenance.ModelId, path + ".modelId");
            Identity(provenance.ModelRevision, path + ".modelRevision");
            Sha256(provenance.ModelSha256, path + ".modelSha256");
            Text(provenance.Runtime, path + ".runtime", 256);
            if (tagger.Models is { Count: > 0 } models)
            {
                TaggerModelRequirement? model = models.FirstOrDefault(value => value is not null
                    && value.ModelId == provenance.ModelId && value.Revision == provenance.ModelRevision);
                if (model is null) Add(path, "undeclared_model", "Provenance names a model the tagger did not declare.");
                else if (model.Sha256 is not null && provenance.ModelSha256 is not null
                    && !string.Equals(model.Sha256, provenance.ModelSha256, StringComparison.OrdinalIgnoreCase))
                    Add(path + ".modelSha256", "model_mismatch", "Provenance hash differs from the declared model.");
            }
        }

        private void TagName(string? name, string path)
        {
            if (string.IsNullOrEmpty(name) || name != name.Trim() || name.Any(char.IsControl)
                || Encoding.UTF8.GetByteCount(name) > TaggerLimits.MaximumTagNameBytes)
                Add(path, "invalid_tag", "Tag name must be nonempty, trimmed, at most 256 UTF-8 bytes and contain no control characters.");
        }

        private static bool IsSingleKind(TaggerMediaKind kind)
            => kind is TaggerMediaKind.Image or TaggerMediaKind.Animation or TaggerMediaKind.Video or TaggerMediaKind.Audio;
    }
}
