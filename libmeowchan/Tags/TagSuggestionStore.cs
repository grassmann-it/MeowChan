using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Tags;

// Contract: tag-suggestions-v1. Suggestions from optional taggers, kept apart from confirmed (private) tags.
// A suggestion belongs to content (SHA-256 + length), not to an archive occurrence: accepting applies the tag to the
// target the user is looking at, through the private tag store, and only then records the decision here.

/// <summary>Review state. Only the user moves a suggestion out of Pending; Superseded is set when a newer model revision ran.</summary>
internal enum TagSuggestionState { Pending, Accepted, Rejected, Superseded }

internal enum TagSuggestionStoreState { Ready, Unsupported, Failed }

/// <summary>Who proposed a suggestion. <see cref="RunKey"/> identifies a tagger run independent of the plugin build.</summary>
internal sealed record TagSuggestionProvenance(string PluginId, string TaggerKey, string PluginVersion, string ModelId,
    string ModelRevision, string? ModelSha256 = null)
{
    [JsonIgnore] internal string TaggerIdentity => PluginId + "\n" + TaggerKey;
    [JsonIgnore] internal string RunKey => TaggerIdentity + "\n" + ModelId + "\n" + ModelRevision;
}

/// <summary>One validated proposal of a run. Category is the SDK category name (General, Character, ...).</summary>
internal sealed record TagSuggestionCandidate(string Name, string Category, double Confidence,
    long? StartMilliseconds = null, long? EndMilliseconds = null);

/// <summary>The result of one tagger run for one medium. Only Outcome "Tagged" counts as done; other outcomes are retried.</summary>
internal sealed record TagSuggestionRunInput(string MediaSha256, long MediaLength, TagSuggestionProvenance Provenance,
    string Outcome, IReadOnlyList<TagSuggestionCandidate> Candidates);

internal sealed record TagSuggestion(long Id, string MediaSha256, long MediaLength, string Name, string Category,
    double Confidence, long? StartMilliseconds, long? EndMilliseconds, TagSuggestionProvenance Provenance,
    TagSuggestionState State, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt = null)
{
    internal bool SameTag(TagSuggestion other) => SameTag(other.MediaSha256, other.Name, other.Category, other.StartMilliseconds, other.EndMilliseconds);

    internal bool SameTag(string media, string name, string category, long? start, long? end)
        => string.Equals(MediaSha256, media, StringComparison.Ordinal) && string.Equals(Name, name, StringComparison.Ordinal)
            && string.Equals(Category, category, StringComparison.Ordinal) && StartMilliseconds == start && EndMilliseconds == end;
}

internal sealed record TagSuggestionRun(string MediaSha256, TagSuggestionProvenance Provenance, string Outcome,
    int SuggestionCount, DateTimeOffset At);

/// <summary>Append-only review history; an undo is a new event back to Pending, never a deletion.</summary>
internal sealed record TagSuggestionReviewEvent(long Sequence, long SuggestionId, TagSuggestionState From,
    TagSuggestionState To, DateTimeOffset At);

internal sealed record TagSuggestionSnapshot(long Revision, IReadOnlyList<TagSuggestion> Suggestions,
    IReadOnlyList<TagSuggestionRun> Runs, IReadOnlyList<TagSuggestionReviewEvent> Events,
    TagSuggestionStoreState State, string? Error = null)
{
    internal static TagSuggestionSnapshot Empty { get; } = new(0, Array.Empty<TagSuggestion>(), Array.Empty<TagSuggestionRun>(),
        Array.Empty<TagSuggestionReviewEvent>(), TagSuggestionStoreState.Ready);

    internal bool CanWrite => State == TagSuggestionStoreState.Ready;

    /// <summary>Whether this exact tagger and model revision already tagged these bytes (resume skips it).</summary>
    internal bool HasCompletedRun(string mediaSha256, TagSuggestionProvenance provenance)
        => Runs.Any(run => string.Equals(run.MediaSha256, mediaSha256, StringComparison.Ordinal)
            && run.Provenance.RunKey == provenance.RunKey && run.Outcome == TagSuggestionStore.TaggedOutcome);

    /// <summary>Pending suggestions of one medium at or above the display threshold, best first. The threshold never changes stored data.</summary>
    internal IReadOnlyList<TagSuggestion> PendingFor(string mediaSha256, double threshold)
        => PendingForContent(mediaSha256, threshold, mediaLength: null).ToArray();

    /// <summary>Binds pending raw suggestions for the observed bytes to the exact occurrence being reviewed.</summary>
    internal IReadOnlyList<TagRuleInput> PendingFor(MediaTagSubject subject, long mediaLength, double threshold)
    {
        string sha256 = subject switch
        {
            MediaTagSubject.Bound bound when mediaLength > 0 => bound.Target.Sha256,
            MediaTagSubject.Local local when mediaLength > 0 && local.Observation.Length == mediaLength => local.Observation.Sha256,
            _ => throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput)
        };
        return PendingForContent(sha256, threshold, mediaLength)
            .Select(value => new TagRuleInput(value.Name, subject, new TagRuleOrigin.Model(value, Revision))).ToArray();
    }

    private IOrderedEnumerable<TagSuggestion> PendingForContent(string mediaSha256, double threshold, long? mediaLength)
        => Suggestions.Where(value => value.State == TagSuggestionState.Pending && value.Confidence >= threshold
                && string.Equals(value.MediaSha256, mediaSha256, StringComparison.Ordinal)
                && (mediaLength is null || value.MediaLength == mediaLength))
            .OrderByDescending(value => value.Confidence).ThenBy(value => value.Name, StringComparer.Ordinal);
}

internal enum TagSuggestionStoreError { None, StaleRevision, UnknownSuggestion, InvalidTransition, InvalidInput, LimitReached, NotWritable }

/// <summary>A rejected operation; nothing was written. The message is constant and never echoes tag names.</summary>
internal sealed class TagSuggestionStoreException(TagSuggestionStoreError error)
    : Exception("The tag suggestion operation was rejected: " + error + ".")
{
    internal TagSuggestionStoreError Error { get; } = error;
}

/// <summary>
/// Passive profile store for tagger suggestions (tag-suggestions.json). Writes are atomic (temporary file + replace),
/// run under the profile lease and never touch confirmed tags. An unreadable or newer file is preserved and makes the
/// store read-only; nothing is reset implicitly.
/// </summary>
internal sealed class TagSuggestionStore
{
    internal const string FileName = "tag-suggestions.json";
    internal const string Format = "meowchan-tag-suggestions";
    internal const int FormatVersion = 1;
    internal const string TaggedOutcome = "Tagged";
    /// <summary>Fixed read ceiling (the maximum of <see cref="TagSuggestionSettings.StoreLimitMiBRange"/>).</summary>
    internal const long MaximumBytes = 256L * 1024 * 1024;
    private const int MaximumTagNameBytes = 256;

    private readonly ProfileLease _lease;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private TagSuggestionSettings _settings = TagSuggestionSettings.Default;
    private TagSuggestionSnapshot? _current;

    internal TagSuggestionStore(ProfileLease lease, Func<DateTimeOffset>? clock = null)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _path = Path.Combine(lease.ProfileDirectory, FileName);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal string FilePath => _path;

    /// <summary>Applies to the next write. Values outside their ranges are refused.</summary>
    internal TagSuggestionSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.IsValid) throw new ArgumentOutOfRangeException(nameof(value), "A tag suggestion setting is outside its supported range.");
            Volatile.Write(ref _settings, value);
        }
    }

    internal TagSuggestionSnapshot Load() => _lease.RunExclusive(LoadCore);

    /// <summary>
    /// Records runs and their suggestions. Per medium and tagger: pending suggestions of an older model revision become
    /// Superseded, pending ones of the same revision are replaced, and a tag the user already accepted or rejected for
    /// these bytes is not proposed again. A run is a merge, so it needs no expected revision.
    /// </summary>
    internal TagSuggestionSnapshot CommitRuns(IReadOnlyList<TagSuggestionRunInput> runs, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(runs);
        foreach (TagSuggestionRunInput run in runs) ValidateRun(run);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return _lease.RunStoreWrite(admission, () =>
        {
            token.ThrowIfCancellationRequested();
            TagSuggestionSnapshot disk = Writable();
            DateTimeOffset now = _clock();
            Document document = Document.From(disk);
            foreach (TagSuggestionRunInput run in runs) Merge(document, run, now);
            return Write(disk, document, token);
        });
    }

    /// <summary>Accept or reject pending suggestions, or reopen decided ones (undo). Checks the expected revision.</summary>
    internal TagSuggestionSnapshot Decide(IReadOnlyCollection<long> ids, TagSuggestionState target, long expectedRevision,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || target == TagSuggestionState.Superseded || !Enum.IsDefined(target))
            throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return _lease.RunStoreWrite(admission, () =>
        {
            token.ThrowIfCancellationRequested();
            TagSuggestionSnapshot disk = Writable();
            if (disk.Revision != expectedRevision) throw new TagSuggestionStoreException(TagSuggestionStoreError.StaleRevision);
            DateTimeOffset now = _clock();
            Document document = Document.From(disk);
            foreach (long id in ids.Distinct())
            {
                int index = document.Suggestions.FindIndex(value => value.Id == id);
                if (index < 0) throw new TagSuggestionStoreException(TagSuggestionStoreError.UnknownSuggestion);
                TagSuggestion current = document.Suggestions[index];
                bool allowed = target == TagSuggestionState.Pending
                    ? current.State is TagSuggestionState.Accepted or TagSuggestionState.Rejected
                    : current.State == TagSuggestionState.Pending;
                if (!allowed) throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidTransition);
                document.Suggestions[index] = current with { State = target, DecidedAt = target == TagSuggestionState.Pending ? null : now };
                document.AddEvent(id, current.State, target, now);
            }
            return Write(disk, document, token);
        });
    }

    private TagSuggestionSnapshot Writable()
    {
        TagSuggestionSnapshot disk = LoadCore();
        if (!disk.CanWrite) throw new TagSuggestionStoreException(TagSuggestionStoreError.NotWritable);
        return disk;
    }

    private void Merge(Document document, TagSuggestionRunInput run, DateTimeOffset now)
    {
        TagSuggestionProvenance provenance = run.Provenance;
        document.Runs.RemoveAll(value => string.Equals(value.MediaSha256, run.MediaSha256, StringComparison.Ordinal)
            && value.Provenance.RunKey == provenance.RunKey);
        int limit = Settings.MaximumSuggestionsPerItem;
        TagSuggestionCandidate[] candidates = run.Outcome == TaggedOutcome
            ? run.Candidates.OrderByDescending(value => value.Confidence).ThenBy(value => value.Name, StringComparer.Ordinal).Take(limit).ToArray()
            : Array.Empty<TagSuggestionCandidate>();
        document.Runs.Add(new TagSuggestionRun(run.MediaSha256, provenance, run.Outcome, candidates.Length, now));
        if (run.Outcome != TaggedOutcome) return;
        for (int index = 0; index < document.Suggestions.Count; index++)
        {
            TagSuggestion value = document.Suggestions[index];
            if (value.State != TagSuggestionState.Pending || value.Provenance.TaggerIdentity != provenance.TaggerIdentity
                || !string.Equals(value.MediaSha256, run.MediaSha256, StringComparison.Ordinal)) continue;
            if (value.Provenance.RunKey == provenance.RunKey) { document.Suggestions.RemoveAt(index--); continue; }
            document.Suggestions[index] = value with { State = TagSuggestionState.Superseded, DecidedAt = now };
            document.AddEvent(value.Id, TagSuggestionState.Pending, TagSuggestionState.Superseded, now);
        }
        foreach (TagSuggestionCandidate candidate in candidates)
        {
            bool decided = document.Suggestions.Any(value => value.State is TagSuggestionState.Accepted or TagSuggestionState.Rejected
                && value.SameTag(run.MediaSha256, candidate.Name, candidate.Category, candidate.StartMilliseconds, candidate.EndMilliseconds));
            bool pending = document.Suggestions.Any(value => value.State == TagSuggestionState.Pending
                && value.SameTag(run.MediaSha256, candidate.Name, candidate.Category, candidate.StartMilliseconds, candidate.EndMilliseconds));
            if (decided || pending) continue;
            document.Suggestions.Add(new TagSuggestion(document.NextId++, run.MediaSha256, run.MediaLength, candidate.Name,
                candidate.Category, candidate.Confidence, candidate.StartMilliseconds, candidate.EndMilliseconds, provenance,
                TagSuggestionState.Pending, now));
        }
    }

    private TagSuggestionSnapshot Write(TagSuggestionSnapshot disk, Document document, CancellationToken token)
    {
        document.Revision = checked(disk.Revision + 1);
        string json = JsonSerializer.Serialize(document, JsonOptions);
        long bytes = Encoding.UTF8.GetByteCount(json);
        long before = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        // Only growth is refused: with the limit lowered below the current size, decisions still work.
        if (bytes > Settings.StoreLimitBytes && bytes > before) throw new TagSuggestionStoreException(TagSuggestionStoreError.LimitReached);
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: false, token);
        return _current = document.ToSnapshot();
    }

    private TagSuggestionSnapshot LoadCore()
    {
        try
        {
            if (!File.Exists(_path)) return _current = TagSuggestionSnapshot.Empty;
            FileInfo info = new FileInfo(_path);
            if (info.Length > MaximumBytes) return _current = Failed("The tag suggestion file exceeds its read ceiling.");
            Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(_path), JsonOptions);
            if (document is null || document.Format != Format) return _current = Failed("The tag suggestion file has an unknown format.");
            if (document.Version != FormatVersion)
                return _current = Failed("The tag suggestion file was written by a newer version.", TagSuggestionStoreState.Unsupported);
            document.Check();
            return _current = document.ToSnapshot();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or NotSupportedException or ArgumentException or OverflowException)
        {
            return _current = Failed("The tag suggestion file could not be read.");
        }
    }

    private static TagSuggestionSnapshot Failed(string error, TagSuggestionStoreState state = TagSuggestionStoreState.Failed)
        => TagSuggestionSnapshot.Empty with { State = state, Error = error };

    private static void ValidateRun(TagSuggestionRunInput run)
    {
        if (run is null || run.Provenance is null || run.Candidates is null || !IsSha256(run.MediaSha256) || run.MediaLength <= 0
            || string.IsNullOrWhiteSpace(run.Outcome) || !IsIdentity(run.Provenance.PluginId) || !IsIdentity(run.Provenance.TaggerKey)
            || !IsIdentity(run.Provenance.PluginVersion) || !IsIdentity(run.Provenance.ModelId) || !IsIdentity(run.Provenance.ModelRevision)
            || (run.Provenance.ModelSha256 is not null && !IsSha256(run.Provenance.ModelSha256)))
            throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);
        foreach (TagSuggestionCandidate candidate in run.Candidates)
            if (candidate is null || !IsTagName(candidate.Name) || !IsIdentity(candidate.Category) || !double.IsFinite(candidate.Confidence)
                || candidate.Confidence is < 0 or > 1 || (candidate.StartMilliseconds is null) != (candidate.EndMilliseconds is null)
                || candidate.StartMilliseconds < 0 || candidate.EndMilliseconds <= candidate.StartMilliseconds)
                throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim()
        && !value.Any(char.IsControl);

    private static bool IsTagName(string? value) => !string.IsNullOrEmpty(value) && value == value.Trim() && !value.Any(char.IsControl)
        && Encoding.UTF8.GetByteCount(value) <= MaximumTagNameBytes;

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = false,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    /// <summary>The persisted document. Public members only for System.Text.Json; the type itself stays internal.</summary>
    private sealed class Document
    {
        public string Format { get; set; } = TagSuggestionStore.Format;
        public int Version { get; set; } = FormatVersion;
        public long Revision { get; set; }
        public long NextId { get; set; } = 1;
        public long NextSequence { get; set; } = 1;
        public List<TagSuggestion> Suggestions { get; set; } = new();
        public List<TagSuggestionRun> Runs { get; set; } = new();
        public List<TagSuggestionReviewEvent> Events { get; set; } = new();

        internal static Document From(TagSuggestionSnapshot snapshot) => new()
        {
            Revision = snapshot.Revision,
            NextId = snapshot.Suggestions.Count == 0 ? 1 : snapshot.Suggestions.Max(value => value.Id) + 1,
            NextSequence = snapshot.Events.Count == 0 ? 1 : snapshot.Events.Max(value => value.Sequence) + 1,
            Suggestions = snapshot.Suggestions.ToList(),
            Runs = snapshot.Runs.ToList(),
            Events = snapshot.Events.ToList()
        };

        internal void AddEvent(long id, TagSuggestionState from, TagSuggestionState to, DateTimeOffset at)
            => Events.Add(new TagSuggestionReviewEvent(NextSequence++, id, from, to, at));

        internal void Check()
        {
            if (Revision < 0 || Suggestions is null || Runs is null || Events is null
                || Suggestions.Any(value => value is null || value.Provenance is null || value.Id <= 0 || !IsSha256(value.MediaSha256) || !IsTagName(value.Name))
                || Suggestions.Select(value => value.Id).Distinct().Count() != Suggestions.Count
                || Runs.Any(value => value is null || value.Provenance is null || !IsSha256(value.MediaSha256))
                || Events.Any(value => value is null))
                throw new InvalidDataException("The tag suggestion file is inconsistent.");
        }

        internal TagSuggestionSnapshot ToSnapshot() => new(Revision, Suggestions.ToArray(), Runs.ToArray(), Events.ToArray(),
            TagSuggestionStoreState.Ready);
    }
}
