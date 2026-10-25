using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins;

/// <summary>Editable SDK-only filter comparing the measured stage hash, without reading or hashing the original again.</summary>
public sealed class HashBlocklistFilter : IImageFilterPlugin, IPluginSettingsValidator
{
    public const string PluginId = "meowchan.filter.hash-blocklist";
    public const string FilterKey = "sha256-blocklist";
    public const int MaximumHashes = 256;
    public const int MaximumListBytes = 20 * 1024;
    private static readonly SettingsSchema Schema = new("1", new SettingDefinition[] {
        new("blockedSha256", SettingScope.FilterRule, SettingKind.Text, "Blocked SHA-256 hashes",
            "Up to 256 distinct 64-character hexadecimal hashes (20 KiB of text). Separate with whitespace or commas. An empty list blocks nothing.",
            Default: new("blockedSha256", SettingKind.Text, Text: ""), MaximumLength: MaximumListBytes)
    });

    public PluginDescription Describe() => new(PluginId, "SHA-256 blocklist", "1.0.0", SdkContract.Version,
        "Skips originals whose measured SHA-256 matches a small list saved in this filter rule. Existing archived files are unchanged.",
        Array.Empty<SourceDescription>(), new FilterDescription[] {
            new(FilterKey, "SHA-256 blocklist", "Exact file identity only. Uses the actual staged SHA-256, without another read, hash calculation or archive scan.",
                FilterDataAccess.CandidateMetadata, Schema, Array.Empty<string>()) },
        new SettingsSchema("1", Array.Empty<SettingDefinition>()), new InformationEntry[] {
            new("metadata", "Measured metadata only", InformationKind.Text,
                "No original bytes, file paths, network lists or subscriptions are read. The initial transfer and normal host stage limits still apply.") }, "MeowChan");

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FieldValidationError> errors = request.Scope == SettingScope.FilterRule
            ? Validate(request.Settings, cancellationToken, out _) : Array.Empty<FieldValidationError>();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(errors);
    }

    public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.FilterKey != FilterKey) return Result(ImageFilterOutcome.Unavailable, "unknown_filter");
        if (request.OriginalSize <= 0) return Result(ImageFilterOutcome.Error, "invalid_original_size", "The staged original size must be positive.");
        if (request.OriginalSize > SdkLimits.MaximumImageBytes) return Result(ImageFilterOutcome.Error, "image_byte_limit", "The staged original exceeds the SDK byte limit.");
        if (!IsSha256(request.OriginalSha256)) return Result(ImageFilterOutcome.Error, "invalid_original_sha256", "The staged original has no valid SHA-256 identity.");
        IReadOnlyList<FieldValidationError> errors = Validate(request.RuleSettings, cancellationToken, out HashSet<string> hashes);
        if (errors.Count != 0) return Result(ImageFilterOutcome.Error, "invalid_rule_parameters", errors[0].Message);
        cancellationToken.ThrowIfCancellationRequested();
        if (hashes.Count == 0) return Result(ImageFilterOutcome.Match, "empty_blocklist", "The list is empty; no SHA-256 hashes are blocked.");
        return hashes.Contains(request.OriginalSha256)
            ? Result(ImageFilterOutcome.NoMatch, "sha256_blocked", "The actual original matches a blocked SHA-256 hash.")
            : Result(ImageFilterOutcome.Match, "sha256_allowed", "The actual original does not match the blocked SHA-256 hashes.");
    }

    private static Task<ImageFilterResult> Result(ImageFilterOutcome outcome, string reason, string? message = null) =>
        Task.FromResult(new ImageFilterResult(outcome, reason, message));

    private static IReadOnlyList<FieldValidationError> Validate(SettingsSnapshot settings, CancellationToken cancellationToken,
        out HashSet<string> hashes)
    {
        hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<FieldValidationError> errors = SdkValidation.ValidateSettings(Schema, settings, SettingScope.FilterRule)
            .Select(issue => new FieldValidationError(issue.Path, issue.Code, issue.Message)).ToList();
        if (errors.Count != 0) return errors;
        string text = settings.Values.FirstOrDefault(value => value.Key == "blockedSha256")?.Text ?? "";
        if (Encoding.UTF8.GetByteCount(text) > MaximumListBytes)
        {
            errors.Add(new("blockedSha256", "hash_list_text_limit", "The SHA-256 list must fit within 20 KiB of UTF-8 text."));
            return errors;
        }
        int offset = 0;
        while (offset < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSeparator(text[offset])) { offset++; continue; }
            int start = offset;
            while (offset < text.Length && !IsSeparator(text[offset])) offset++;
            string token = text.Substring(start, offset - start);
            if (!IsSha256(token))
            {
                errors.Add(new("blockedSha256", "invalid_sha256_entry", "Every entry must contain exactly 64 hexadecimal characters, without prefixes or filenames."));
                return errors;
            }
            hashes.Add(token);
            if (hashes.Count > MaximumHashes)
            {
                errors.Add(new("blockedSha256", "hash_list_count_limit", "Use at most 256 distinct SHA-256 hashes."));
                return errors;
            }
        }
        return errors;
    }

    private static bool IsSeparator(char value) => value == ',' || char.IsWhiteSpace(value);
    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
