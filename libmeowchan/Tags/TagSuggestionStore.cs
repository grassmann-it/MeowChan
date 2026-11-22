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

// Suggestions belong to content (SHA-256 + length). The review journal binds a decision to one private occurrence.

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

internal sealed record StoredTagSuggestion(long Id, string MediaSha256, long MediaLength, string Name, string Category,
    double Confidence, TagSuggestionProvenance Provenance, TagSuggestionState State, DateTimeOffset CreatedAt,
    long? StartMilliseconds = null, long? EndMilliseconds = null, DateTimeOffset? DecidedAt = null)
{
    internal (string MediaSha256, long MediaLength, string Name, string Category, long? Start, long? End) TagKey
        => (MediaSha256, MediaLength, Name, Category, StartMilliseconds, EndMilliseconds);

    internal bool IsDecided => State is TagSuggestionState.Accepted or TagSuggestionState.Rejected;
}

internal sealed record TagSuggestionRun(string MediaSha256, TagSuggestionProvenance Provenance, string Outcome,
    int SuggestionCount, DateTimeOffset At);

/// <summary>Append-only review history; an undo is a new event back to Pending, never a deletion.</summary>
internal sealed record TagSuggestionReviewEvent(long Sequence, long SuggestionId, TagSuggestionState From,
    TagSuggestionState To, DateTimeOffset At, Guid? OperationId = null);

internal sealed record TagSuggestionSnapshot(long Revision, IReadOnlyList<StoredTagSuggestion> Suggestions,
    IReadOnlyList<TagSuggestionRun> Runs, IReadOnlyList<TagSuggestionReviewEvent> Events,
    TagSuggestionStoreState State, string? Error = null)
{
    internal static TagSuggestionSnapshot Empty { get; } = new(0, Array.Empty<StoredTagSuggestion>(), Array.Empty<TagSuggestionRun>(),
        Array.Empty<TagSuggestionReviewEvent>(), TagSuggestionStoreState.Ready);

    internal bool CanWrite => State == TagSuggestionStoreState.Ready;
    internal long NextId { get; init; } = 1;
    internal long NextSequence { get; init; } = 1;
    internal IReadOnlyList<TagReviewOperation> Reviews { get; init; } = [];
    internal TagReviewOperation? PendingReview => Reviews.LastOrDefault() is { Status: TagReviewStatus.Prepared } review
        ? review : null;

    /// <summary>Whether this exact tagger and model revision already tagged these bytes (resume skips it).</summary>
    internal bool HasCompletedRun(string mediaSha256, TagSuggestionProvenance provenance)
        => Runs.Any(run => string.Equals(run.MediaSha256, mediaSha256, StringComparison.Ordinal)
            && run.Provenance.RunKey == provenance.RunKey && run.Outcome == TagSuggestionStore.TaggedOutcome);

    /// <summary>Pending suggestions of one medium at or above the display threshold, best first. The threshold never changes stored data.</summary>
    internal IReadOnlyList<StoredTagSuggestion> PendingFor(string mediaSha256, double threshold)
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

    private IOrderedEnumerable<StoredTagSuggestion> PendingForContent(string mediaSha256, double threshold, long? mediaLength)
    {
        IEnumerable<StoredTagSuggestion> forContent = Suggestions.Where(value =>
            string.Equals(value.MediaSha256, mediaSha256, StringComparison.Ordinal)
            && (mediaLength is null || value.MediaLength == mediaLength));
        var decided = forContent.Where(value => value.IsDecided).Select(value => value.TagKey).ToHashSet();
        return forContent.Where(value => value.State == TagSuggestionState.Pending && value.Confidence >= threshold
                && !decided.Contains(value.TagKey))
            .OrderByDescending(value => value.Confidence).ThenBy(value => value.Name, StringComparer.Ordinal);
    }
}

internal enum TagSuggestionStoreError
{
    None, StaleRevision, UnknownSuggestion, InvalidTransition, InvalidInput, LimitReached, NotWritable,
    ReviewRecoveryRequired, OperationConflict
}

/// <summary>A rejected operation; nothing was written. The message is constant and never echoes tag names.</summary>
internal sealed class TagSuggestionStoreException(TagSuggestionStoreError error)
    : Exception("The tag suggestion operation was rejected: " + error + ".")
{
    internal TagSuggestionStoreError Error { get; } = error;
}

/// <summary>
/// Profile store for tagger suggestions (tag-suggestions.json). Run commits and review writes use the profile lease.
/// An unreadable or newer file is preserved and makes the store read-only.
/// </summary>
internal sealed partial class TagSuggestionStore
{
    internal const string FileName = "tag-suggestions.json";
    internal const string Format = "meowchan-tag-suggestions";
    internal const int FormatVersion = 1;
    private const int JournalVersion = 2;
    internal const string TaggedOutcome = "Tagged";
    /// <summary>Fixed read ceiling (the maximum of <see cref="TagSuggestionSettings.StoreLimitMiBRange"/>).</summary>
    internal const long MaximumBytes = 256L * 1024 * 1024;
    private const int MaximumTagNameBytes = 256;

    private readonly ProfileLease _lease;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly PrivateTagStore? _privateTags;
    private TagSuggestionSettings _settings = TagSuggestionSettings.Default;
    private TagSuggestionSnapshot? _current;

    internal TagSuggestionStore(ProfileLease lease, Func<DateTimeOffset>? clock = null, PrivateTagStore? privateTags = null)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _path = Path.Combine(lease.ProfileDirectory, FileName);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (privateTags is not null && !privateTags.UsesLease(lease))
            throw new ArgumentException("The private tag store belongs to a different lease.", nameof(privateTags));
        _privateTags = privateTags;
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
        => CommitRuns(runs, checkAuthority: null, token);

    internal TagSuggestionSnapshot CommitRuns(IReadOnlyList<TagSuggestionRunInput> runs, Action? checkAuthority,
        CancellationToken token)
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
            return Write(disk, document, forReview: false, checkAuthority, token);
        });
    }

    /// <summary>Changes legacy suggestion-only decisions. Occurrence reviews use the journal.</summary>
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
                StoredTagSuggestion current = document.Suggestions[index];
                bool allowed = target == TagSuggestionState.Pending
                    ? current.IsDecided && document.Events.FindLast(review => review.SuggestionId == id)?.OperationId is null
                    : current.State == TagSuggestionState.Pending
                        && !document.Suggestions.Any(value => value.IsDecided && value.TagKey == current.TagKey);
                if (!allowed)
                    throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidTransition);
                document.Suggestions[index] = current with { State = target, DecidedAt = target == TagSuggestionState.Pending ? null : now };
                document.AddEvent(id, current.State, target, now);
            }
            return Write(disk, document, forReview: true, checkAuthority: null, token);
        });
    }

    private TagSuggestionSnapshot Writable(bool allowPending = false)
    {
        TagSuggestionSnapshot disk = LoadCore();
        if (!disk.CanWrite) throw new TagSuggestionStoreException(TagSuggestionStoreError.NotWritable);
        if (!allowPending && disk.PendingReview is not null)
            throw new TagSuggestionStoreException(TagSuggestionStoreError.ReviewRecoveryRequired);
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
            StoredTagSuggestion value = document.Suggestions[index];
            if (value.State != TagSuggestionState.Pending || value.Provenance.TaggerIdentity != provenance.TaggerIdentity
                || !string.Equals(value.MediaSha256, run.MediaSha256, StringComparison.Ordinal) || value.MediaLength != run.MediaLength) continue;
            document.Suggestions[index] = value with { State = TagSuggestionState.Superseded, DecidedAt = now };
            document.AddEvent(value.Id, TagSuggestionState.Pending, TagSuggestionState.Superseded, now);
        }
        // Decisions apply across taggers. Pending duplicates are coalesced only within the incoming run.
        var seen = document.Suggestions.Where(value => value.IsDecided
                && string.Equals(value.MediaSha256, run.MediaSha256, StringComparison.Ordinal) && value.MediaLength == run.MediaLength)
            .Select(value => value.TagKey).ToHashSet();
        foreach (TagSuggestionCandidate candidate in candidates)
        {
            if (!seen.Add((run.MediaSha256, run.MediaLength, candidate.Name, candidate.Category,
                candidate.StartMilliseconds, candidate.EndMilliseconds))) continue;
            long id = document.NextId;
            document.NextId = checked(id + 1);
            document.Suggestions.Add(new StoredTagSuggestion(id, run.MediaSha256, run.MediaLength, candidate.Name,
                candidate.Category, candidate.Confidence, provenance, TagSuggestionState.Pending, now,
                candidate.StartMilliseconds, candidate.EndMilliseconds));
        }
    }

    private TagSuggestionSnapshot Write(TagSuggestionSnapshot disk, Document document, bool forReview,
        Action? checkAuthority, CancellationToken token)
    {
        document.Revision = checked(disk.Revision + 1);
        token.ThrowIfCancellationRequested();
        byte[] json = PrepareWrite(document, forReview);
        return Publish(document, json, checkAuthority, token);
    }

    private byte[] PrepareWrite(Document document, bool forReview)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        TagSuggestionSettings settings = Settings;
        long limit = settings.StoreLimitBytes;
        if (!forReview) limit -= limit * settings.ReviewReservePercent / 100;
        long before = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        // Preserve the ability to reduce an existing file after its configured limit has been lowered.
        if (json.LongLength > MaximumBytes || (json.LongLength > limit && json.LongLength > before))
            throw new TagSuggestionStoreException(TagSuggestionStoreError.LimitReached);
        return json;
    }

    private TagSuggestionSnapshot Publish(Document document, byte[] json, Action? checkAuthority, CancellationToken token)
    {
        AtomicJsonFile.ReplaceUnderLease(_path, json, checkAuthority, token);
        return _current = document.ToSnapshot();
    }

    private TagSuggestionSnapshot LoadCore()
    {
        try
        {
            using FileStream file = RegularProfileFile.OpenRead(_path);
            long length = file.Length;
            if (length > MaximumBytes) return _current = Failed("The tag suggestion file exceeds its read ceiling.");
            byte[] bytes = new byte[(int)length];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) return _current = Failed("The tag suggestion file changed during reading.");
            Document? document = JsonSerializer.Deserialize<Document>(bytes, JsonOptions);
            if (document is null || document.Format != Format || document.Version is null)
                return _current = Failed("The tag suggestion file has an unknown format.");
            if (document.Version is not (FormatVersion or JournalVersion))
                return _current = Failed("The tag suggestion file was written by a newer version.", TagSuggestionStoreState.Unsupported);
            document.Check();
            return _current = document.ToSnapshot();
        }
        catch (FileNotFoundException)
        {
            return _current = TagSuggestionSnapshot.Empty;
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
            || string.IsNullOrWhiteSpace(run.Outcome) || !IsValidProvenance(run.Provenance))
            throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);
        foreach (TagSuggestionCandidate candidate in run.Candidates)
            if (candidate is null || !IsValidTag(candidate.Name, candidate.Category, candidate.Confidence,
                candidate.StartMilliseconds, candidate.EndMilliseconds))
                throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);
    }

    private static bool IsValidProvenance(TagSuggestionProvenance? value) => value is not null
        && IsIdentity(value.PluginId) && IsIdentity(value.TaggerKey) && IsIdentity(value.PluginVersion)
        && IsIdentity(value.ModelId) && IsIdentity(value.ModelRevision)
        && (value.ModelSha256 is null || IsSha256(value.ModelSha256));

    private static bool IsValidTag(string? name, string? category, double confidence, long? start, long? end) =>
        IsTagName(name) && IsIdentity(category) && double.IsFinite(confidence) && confidence is >= 0 and <= 1
        && (start is null && end is null || start is >= 0 && end > start);

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
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    /// <summary>The persisted document. Public members only for System.Text.Json; the type itself stays internal.</summary>
    private sealed class Document
    {
        public string? Format { get; init; }
        public int? Version { get; set; }
        public long Revision { get; set; } = -1;
        public long NextId { get; set; }
        public long NextSequence { get; set; }
        // Check rejects omitted or null lists on read. From always supplies all three on write.
        public List<StoredTagSuggestion> Suggestions { get; init; } = null!;
        public List<TagSuggestionRun> Runs { get; init; } = null!;
        public List<TagSuggestionReviewEvent> Events { get; init; } = null!;
        public List<TagReviewOperation>? Reviews { get; set; }

        internal static Document From(TagSuggestionSnapshot snapshot) => new()
        {
            Format = TagSuggestionStore.Format,
            Version = snapshot.Reviews.Count == 0 ? FormatVersion : JournalVersion,
            Revision = snapshot.Revision,
            NextId = snapshot.NextId,
            NextSequence = snapshot.NextSequence,
            Suggestions = snapshot.Suggestions.ToList(),
            Runs = snapshot.Runs.ToList(),
            Events = snapshot.Events.ToList(),
            Reviews = snapshot.Reviews.Count == 0 ? null : snapshot.Reviews.ToList()
        };

        internal void AddEvent(long id, TagSuggestionState from, TagSuggestionState to, DateTimeOffset at, Guid? operationId = null)
        {
            long sequence = NextSequence;
            NextSequence = checked(sequence + 1);
            Events.Add(new TagSuggestionReviewEvent(sequence, id, from, to, at, operationId));
        }

        internal void Check()
        {
            if (Revision < 0 || NextId <= 0 || NextSequence <= 0 || Suggestions is null || Runs is null || Events is null
                || Suggestions.Any(value => value is null || value.Id <= 0 || value.MediaLength <= 0
                    || !IsSha256(value.MediaSha256) || !IsValidProvenance(value.Provenance)
                    || !IsValidTag(value.Name, value.Category, value.Confidence, value.StartMilliseconds, value.EndMilliseconds)
                    || (value.State == TagSuggestionState.Pending) != (value.DecidedAt is null))
                || Suggestions.Select(value => value.Id).Distinct().Count() != Suggestions.Count
                || Runs.Any(value => value is null || !IsValidProvenance(value.Provenance) || !IsSha256(value.MediaSha256)
                    || string.IsNullOrWhiteSpace(value.Outcome) || value.SuggestionCount < 0
                    || value.SuggestionCount > TagSuggestionSettings.MaximumSuggestionsPerItemRange.Maximum
                    || (value.Outcome != TaggedOutcome && value.SuggestionCount != 0)))
                throw new InvalidDataException("The tag suggestion file is inconsistent.");
            long lastSequence = CheckEvents();
            CheckReviewJournal(this);

            // Old writers could reset NextId after deleting a superseded pending suggestion.
            // The greatest identifier in surviving records or review events is still occupied.
            long lastId = Math.Max(Suggestions.Select(value => value.Id).DefaultIfEmpty().Max(),
                Events.Select(value => value.SuggestionId).DefaultIfEmpty().Max());
            NextId = Math.Max(NextId, checked(lastId + 1));
            NextSequence = Math.Max(NextSequence, checked(lastSequence + 1));
        }

        private long CheckEvents()
        {
            const string invalid = "The tag suggestion history is inconsistent.";
            Dictionary<long, TagSuggestionReviewEvent> latest = new();
            long sequence = 0;
            foreach (TagSuggestionReviewEvent item in Events)
            {
                if (item is null || item.Sequence <= sequence || item.SuggestionId <= 0)
                    throw new InvalidDataException(invalid);
                TagSuggestionState previous = latest.GetValueOrDefault(item.SuggestionId)?.To ?? TagSuggestionState.Pending;
                bool allowed = previous switch
                {
                    TagSuggestionState.Pending => item.To is TagSuggestionState.Accepted or TagSuggestionState.Rejected or TagSuggestionState.Superseded,
                    TagSuggestionState.Accepted or TagSuggestionState.Rejected => item.To == TagSuggestionState.Pending,
                    _ => false
                };
                if (!allowed || item.From != previous) throw new InvalidDataException(invalid);
                latest[item.SuggestionId] = item;
                sequence = item.Sequence;
            }
            foreach (StoredTagSuggestion value in Suggestions)
            {
                TagSuggestionReviewEvent? last = latest.GetValueOrDefault(value.Id);
                if ((last?.To ?? TagSuggestionState.Pending) != value.State
                    || value.State != TagSuggestionState.Pending && last?.At != value.DecidedAt)
                    throw new InvalidDataException(invalid);
            }
            return sequence;
        }

        internal TagSuggestionSnapshot ToSnapshot() => new(Revision, Suggestions.ToArray(), Runs.ToArray(), Events.ToArray(),
            TagSuggestionStoreState.Ready)
            { NextId = NextId, NextSequence = NextSequence, Reviews = Reviews?.ToArray() ?? [] };
    }
}
