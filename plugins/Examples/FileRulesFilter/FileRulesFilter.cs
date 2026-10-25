using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins;

/// <summary>Editable SDK-only filter. Uses measured stage metadata; it never opens an asset or infers a filename.</summary>
public sealed class FileRulesFilter : IImageFilterPlugin, IPluginSettingsValidator
{
    public const string PluginId = "meowchan.filter.file-rules";
    public const string FilterKey = "file-rules";
    private static readonly SettingsSchema Schema = new("1", new SettingDefinition[] {
        new("minimumBytes", SettingScope.FilterRule, SettingKind.Integer, "Minimum bytes",
            "Inclusive lower limit for the actual downloaded original. Does not avoid the initial transfer.",
            Default: new("minimumBytes", SettingKind.Integer, Integer: 0), Minimum: 0, Maximum: SdkLimits.MaximumImageBytes),
        new("maximumBytes", SettingScope.FilterRule, SettingKind.Integer, "Maximum bytes",
            "Inclusive upper limit for the actual downloaded original. Unset means no additional upper limit.",
            Minimum: 0, Maximum: SdkLimits.MaximumImageBytes),
        new("nameMode", SettingScope.FilterRule, SettingKind.Enum, "Original filename comparison",
            "Uses only the original filename supplied by the source, never the URL or local path.",
            Default: new("nameMode", SettingKind.Enum, Text: "any"), Options: new SettingOption[] {
                new("any", "Any name"), new("contains", "Contains"), new("startsWith", "Starts with"),
                new("endsWith", "Ends with"), new("exact", "Equals") }),
        new("nameText", SettingScope.FilterRule, SettingKind.Text, "Filename text",
            "Literal text, without wildcards or regular expressions. Required when a name comparison is selected.", MaximumLength: 256),
        new("ignoreCase", SettingScope.FilterRule, SettingKind.Boolean, "Ignore case",
            "Uses an ordinal comparison, independent of the current language.",
            Default: new("ignoreCase", SettingKind.Boolean, Boolean: true))
    });

    public PluginDescription Describe() => new(PluginId, "File rules", "1.0.0", SdkContract.Version,
        "Keeps downloaded originals that meet inclusive byte limits and an optional literal original-filename rule.",
        Array.Empty<SourceDescription>(), new FilterDescription[] {
            new(FilterKey, "File rules", "All enabled conditions must match. Missing original names are technical omissions when a name rule is active.",
                FilterDataAccess.CandidateMetadata, Schema, Array.Empty<string>()) },
        new SettingsSchema("1", Array.Empty<SettingDefinition>()), new InformationEntry[] {
            new("metadata", "Measured metadata only", InformationKind.Text,
                "Reads no original bytes. Host media rules and existing stage limits still apply. With no conditions this filter passes every valid candidate.") }, "MeowChan");

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FieldValidationError> errors = request.Scope == SettingScope.FilterRule
            ? Validate(request.Settings) : Array.Empty<FieldValidationError>();
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
        IReadOnlyList<FieldValidationError> errors = Validate(request.RuleSettings);
        if (errors.Count != 0) return Result(ImageFilterOutcome.Error, "invalid_rule_parameters", errors[0].Message);
        Dictionary<string, SettingValue> values = request.RuleSettings.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
        long minimum = values.GetValueOrDefault("minimumBytes")?.Integer ?? 0;
        long? maximum = values.GetValueOrDefault("maximumBytes")?.Integer;
        string mode = values.GetValueOrDefault("nameMode")?.Text ?? "any";
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OriginalSize < minimum) return Result(ImageFilterOutcome.NoMatch, "below_minimum_bytes", "The actual original is smaller than the inclusive minimum.");
        if (maximum.HasValue && request.OriginalSize > maximum.Value) return Result(ImageFilterOutcome.NoMatch, "above_maximum_bytes", "The actual original is larger than the inclusive maximum.");
        if (mode != "any")
        {
            if (request.Candidate == null) return Result(ImageFilterOutcome.Error, "invalid_candidate");
            string? name = request.Candidate.OriginalFileName;
            if (string.IsNullOrEmpty(name)) return Result(ImageFilterOutcome.Unavailable, "original_name_unavailable", "The source supplied no original filename for this name rule.");
            string text = values.GetValueOrDefault("nameText")!.Text!;
            StringComparison comparison = (values.GetValueOrDefault("ignoreCase")?.Boolean ?? true)
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool matches = mode switch {
                "contains" => name.Contains(text, comparison), "startsWith" => name.StartsWith(text, comparison),
                "endsWith" => name.EndsWith(text, comparison), "exact" => name.Equals(text, comparison), _ => false
            };
            if (!matches) return Result(ImageFilterOutcome.NoMatch, "original_name_mismatch", "The original filename does not match the literal rule.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return mode == "any" && minimum == 0 && maximum == null
            ? Result(ImageFilterOutcome.Match, "no_file_conditions", "No file conditions are enabled; this filter passes the candidate.")
            : Result(ImageFilterOutcome.Match, "file_rules_match", "All enabled file conditions match.");
    }

    private static Task<ImageFilterResult> Result(ImageFilterOutcome outcome, string reason, string? message = null) =>
        Task.FromResult(new ImageFilterResult(outcome, reason, message));

    private static IReadOnlyList<FieldValidationError> Validate(SettingsSnapshot settings)
    {
        List<FieldValidationError> errors = SdkValidation.ValidateSettings(Schema, settings, SettingScope.FilterRule)
            .Select(issue => new FieldValidationError(issue.Path, issue.Code, issue.Message)).ToList();
        if (errors.Count != 0) return errors;
        Dictionary<string, SettingValue> values = settings.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
        long minimum = values.GetValueOrDefault("minimumBytes")?.Integer ?? 0;
        long? maximum = values.GetValueOrDefault("maximumBytes")?.Integer;
        if (maximum.HasValue && minimum > maximum.Value)
            errors.Add(new("maximumBytes", "invalid_byte_range", "Maximum bytes must be at least the minimum bytes."));
        if ((values.GetValueOrDefault("nameMode")?.Text ?? "any") != "any"
            && string.IsNullOrEmpty(values.GetValueOrDefault("nameText")?.Text))
            errors.Add(new("nameText", "required_name_text", "Enter literal filename text for the selected comparison."));
        return errors;
    }
}
