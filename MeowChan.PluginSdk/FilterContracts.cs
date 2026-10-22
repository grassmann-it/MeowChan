namespace MeowChan.PluginSdk;

[Flags]
public enum FilterDataAccess { None = 0, CandidateMetadata = 1, OriginalImage = 2 }

public sealed record FilterDescription(string Key, string Name, string Description,
    FilterDataAccess RequiredAccess, SettingsSchema Settings, IReadOnlyList<string> SupportedMimeTypes);

public sealed record FilterCandidate(SourceMediaId Id, string PostId, int Position, Uri Url,
    string? OriginalFileName = null, string? MimeType = null);

/// <summary>
/// Describes the actual staged bytes, not only provider metadata. Revisions and token must
/// remain unchanged in transit. The host rechecks them before accepting a result or commit.
/// A Match is not proof that media was stored; only the host can commit the verified bytes.
/// </summary>
public sealed record ImageFilterRequest(string FilterKey, FilterCandidate Candidate,
    string InputRevision, string MediaRevision, string AssetToken, long OriginalSize,
    string OriginalSha256, string EffectiveRuleRevision, SettingsSnapshot RuleSettings,
    string PluginBuildRevision, string ConfigurationRevision, string GrantRevision);

public enum ImageFilterOutcome { Match, NoMatch, Unavailable, Timeout, Error }

/// <summary>NoMatch is a rule decision; the remaining unsuccessful outcomes are technical omissions.</summary>
public sealed record ImageFilterResult(ImageFilterOutcome Outcome, string ReasonCode, string? Message = null);
