using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MeowChan.Rules;

public enum MediaRuleScopeKind { Global, Source, Board, Thread }
public enum MediaTypeSelectionMode { Inherit, All, Selected }
public enum MediaSizeLimitMode { Inherit, Unlimited, Limited }
public enum MediaSelectionOutcome { Selected, Excluded, TechnicalOmission }
public enum MediaSelectionReason { Allowed, AllowedUnknownSize, TypeExcluded, SizeExceeded, PreviouslySaved, FilterNoMatch, FilterUnavailable }

/// <summary>Stable provider identities, independent of URLs and translated display names.</summary>
public sealed record MediaRuleScope
{
    public MediaRuleScopeKind Kind { get; }
    public string? Source { get; }
    public string? Board { get; }
    public string? Thread { get; }

    public MediaRuleScope(MediaRuleScopeKind kind, string? source = null, string? board = null, string? thread = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind >= MediaRuleScopeKind.Source) ValidateIdentity(source, nameof(source));
        else if (source != null) throw new ArgumentException("A global rule cannot name a source.", nameof(source));
        if (kind >= MediaRuleScopeKind.Board) ValidateIdentity(board, nameof(board));
        else if (board != null) throw new ArgumentException("This scope cannot name a board.", nameof(board));
        if (kind == MediaRuleScopeKind.Thread) ValidateIdentity(thread, nameof(thread));
        else if (thread != null) throw new ArgumentException("This scope cannot name a thread.", nameof(thread));
        Kind = kind;
        Source = source;
        Board = board;
        Thread = thread;
    }

    public static MediaRuleScope Global { get; } = new MediaRuleScope(MediaRuleScopeKind.Global);
    public static MediaRuleScope ForSource(string source) => new MediaRuleScope(MediaRuleScopeKind.Source, source);
    public static MediaRuleScope ForBoard(string source, string board) => new MediaRuleScope(MediaRuleScopeKind.Board, source, board);
    public static MediaRuleScope ForThread(string source, string board, string thread) => new MediaRuleScope(MediaRuleScopeKind.Thread, source, board, thread);

    internal static void ValidateIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException("An identity must be nonempty and must not have surrounding whitespace.", name);
    }
}

/// <summary>Selected with an empty list excludes all media; Inherit and All remain distinct.</summary>
public sealed class MediaTypeSelection
{
    public MediaTypeSelectionMode Mode { get; }
    public IReadOnlyList<string> Extensions { get; }

    public MediaTypeSelection(MediaTypeSelectionMode mode, IEnumerable<string>? extensions = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        string[] normalized = (extensions ?? Array.Empty<string>()).Select(NormalizeExtension)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (mode != MediaTypeSelectionMode.Selected && normalized.Length != 0)
            throw new ArgumentException("Only Selected may contain extensions.", nameof(extensions));
        Mode = mode;
        Extensions = new ReadOnlyCollection<string>(normalized);
    }

    public static MediaTypeSelection Inherit { get; } = new MediaTypeSelection(MediaTypeSelectionMode.Inherit);
    public static MediaTypeSelection All { get; } = new MediaTypeSelection(MediaTypeSelectionMode.All);

    /// <summary>Accepts an extension, not a filename, path, wildcard, or MIME type.</summary>
    public static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        string normalized = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > 32 ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("An extension must contain 1 to 32 ASCII letters or digits.", nameof(extension));
        // One optional leading dot is accepted; malformed multiple-dot extensions are rejected.
        if (extension.Trim().StartsWith("..", StringComparison.Ordinal))
            throw new ArgumentException("An extension may have only one leading dot.", nameof(extension));
        return "." + normalized;
    }
}

/// <summary>A metadata selection limit, not a transfer-byte budget. Unknown size is allowed.</summary>
public sealed record MediaSizeLimit
{
    public MediaSizeLimitMode Mode { get; }
    public long? Bytes { get; }

    public MediaSizeLimit(MediaSizeLimitMode mode, long? bytes = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == MediaSizeLimitMode.Limited)
        {
            if (!bytes.HasValue || bytes.Value < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        }
        else if (bytes != null) throw new ArgumentException("Only Limited may specify bytes.", nameof(bytes));
        Mode = mode;
        Bytes = bytes;
    }

    public static MediaSizeLimit Inherit { get; } = new MediaSizeLimit(MediaSizeLimitMode.Inherit);
    public static MediaSizeLimit Unlimited { get; } = new MediaSizeLimit(MediaSizeLimitMode.Unlimited);
}

public sealed record MediaRule
{
    public MediaRuleScope Scope { get; }
    public bool Enabled { get; }
    public MediaImageFilterRule ImageFilter { get; init; } = MediaImageFilterRule.Inherit;
    public MediaTypeSelection Types { get; }
    public MediaSizeLimit SizeLimit { get; }

    public MediaRule(MediaRuleScope scope, bool enabled = true, MediaTypeSelection? types = null, MediaSizeLimit? sizeLimit = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Enabled = enabled;
        Types = types ?? MediaTypeSelection.Inherit;
        SizeLimit = sizeLimit ?? MediaSizeLimit.Inherit;
    }
}

/// <summary>An immutable revision. Saving changes produces a unique revision, including after recovery.</summary>
public sealed class MediaRuleSet
{
    public string Revision { get; }
    public IReadOnlyList<MediaRule> Rules { get; }
    public static MediaRuleSet Default { get; } = new MediaRuleSet("default", Array.Empty<MediaRule>());

    public MediaRuleSet(string revision, IEnumerable<MediaRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(rules);
        MediaRule[] copied = rules.ToArray();
        if (copied.Any(rule => rule == null)) throw new ArgumentException("Rules must not contain null.", nameof(rules));
        if (copied.Select(rule => rule.Scope).Distinct().Count() != copied.Length)
            throw new ArgumentException("Only one rule may exist at each scope.", nameof(rules));
        Revision = revision;
        Rules = new ReadOnlyCollection<MediaRule>(copied);
    }
}

/// <param name="Scope">Null denotes the built-in default, rather than a persisted global override.</param>
/// <param name="Revision">The complete rule catalog revision used for this resolution.</param>
public sealed record MediaRuleOrigin(MediaRuleScope? Scope, string Revision);

public sealed record EffectiveMediaRule(string Revision, MediaTypeSelection Types, MediaSizeLimit SizeLimit,
    MediaRuleOrigin TypesOrigin, MediaRuleOrigin SizeLimitOrigin)
{
    public MediaImageFilterRule ImageFilter { get; init; } = MediaImageFilterRule.Disabled;
    public MediaRuleOrigin? ImageFilterOrigin { get; init; }
}

public sealed record MediaSelectionResult(MediaSelectionOutcome Outcome, MediaSelectionReason Reason,
    EffectiveMediaRule EffectiveRule, string? Extension, long? SizeBytes)
{
    public string? FilterReason { get; init; }
}

public enum MediaImageFilterMode { Inherit, Disabled, Plugin }

/// <summary>Per-rule values, independent of a worker process or plugin runtime configuration.</summary>
public sealed class MediaImageFilterRule
{
    public MediaImageFilterMode Mode { get; }
    public string? PluginId { get; }
    public string? FilterKey { get; }
    public string SchemaVersion { get; }
    public IReadOnlyDictionary<string, string> Parameters { get; }
    public static MediaImageFilterRule Inherit { get; } = new MediaImageFilterRule(MediaImageFilterMode.Inherit);
    public static MediaImageFilterRule Disabled { get; } = new MediaImageFilterRule(MediaImageFilterMode.Disabled);

    public MediaImageFilterRule(MediaImageFilterMode mode, string? pluginId = null, string? filterKey = null,
        string schemaVersion = "1", IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == MediaImageFilterMode.Plugin) { MediaRuleScope.ValidateIdentity(pluginId, nameof(pluginId)); MediaRuleScope.ValidateIdentity(filterKey, nameof(filterKey)); }
        else if (pluginId != null || filterKey != null || parameters?.Count > 0) throw new ArgumentException("Only a plugin rule may contain plugin values.");
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        Mode = mode; PluginId = pluginId; FilterKey = filterKey; SchemaVersion = schemaVersion;
        Parameters = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal));
    }
}
