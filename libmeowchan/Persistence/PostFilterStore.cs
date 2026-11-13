using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Persistence;

/// <summary>
/// Post filter rules and individually hidden posts: <c>post-filters.json</c> in the profile. Built like
/// <see cref="SavedArchiveSearchStore"/>: a revision, an atomic write that rotates the previous file into <c>.bak</c>, a
/// damaged primary with a good backup is offered for an explicit recovery (never restored implicitly), a newer or unknown
/// document is read-only and kept, and only the session host owns the lease. Passive and optional: nothing is created
/// until the first write.
/// <code>
/// { "schemaVersion": 1, "revision": 3, "hiddenPostsAction": "stub" | "hide",
///   "rules": [ { "id": guid, "enabled": bool, "field": "text|subject|filename|md5", "match": "contains|exact",
///                "pattern": string, "action": "hide|stub|highlight", "replies": bool, "source"?: string, "board"?: string } ],
///   "hiddenPosts": [ { "source", "board", "thread", "post", "replies": bool, "added": ISO-8601 UTC } ] }
/// </code>
/// A rule this version cannot interpret (<c>match: "regex"</c> of stage 2, an unknown field or action, unknown members) is
/// kept per rule as unsupported and written back unchanged; unknown members of the envelope or of a hidden post make the
/// whole store read-only instead.
/// </summary>
internal sealed class PostFilterStore
{
    /// <summary>Fixed technical ceiling of the file (reads and writes). Not a setting.</summary>
    internal const int MaximumFileBytes = 32 * 1024 * 1024;
    internal const string FileName = "post-filters.json";

    private readonly ProfileLease _lease;
    private readonly string _path;
    private PostFilterSnapshot? _current;
    private string? _recoverySource;
    private string? _recoveryIdentity;

    public PostFilterStore(string directory, ProfileLease lease)
    {
        lease.EnsureMatches(directory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
    }

    // Instance-only observers: inside the synchronous consumer pin, immediately before real Atomic I/O and after its
    // finally plus snapshot publication.
    internal Action<ProfileAdmissionToken>? BeforeAdmittedMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterAdmittedMutationForTests { get; set; }

    // Passive load never enters writer admission, including recovery discovery.
    public PostFilterSnapshot Load() => _lease.RunExclusive(LoadCore);

    private PostFilterSnapshot LoadCore()
    {
        _recoverySource = null; _recoveryIdentity = null;
        try { return _current = Read(_path, missingIsEmpty: true); }
        catch (Exception error) when (IsReadError(error))
        {
            try
            {
                PostFilterSnapshot backup = Read(_path + ".bak", missingIsEmpty: false);
                if (backup.CanWrite)
                {
                    _recoverySource = ReadBounded(_path + ".bak");
                    _recoveryIdentity = FileIdentity(_path);
                    return _current = backup with { State = PostFilterStoreState.RecoveryAvailable, Error = error.Message };
                }
            }
            catch (Exception backupError) when (IsReadError(backupError)) { }
            return _current = PostFilterSnapshot.Empty(PostFilterStoreState.Failed, error.Message);
        }
    }

    // ---- Explicit recovery -------------------------------------------------------------------------------------------

    public PostFilterSnapshot Recover(long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => RecoverAdmitted(expectedRevision, admission, token));

    internal PostFilterSnapshot RecoverAdmitted(long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) => _lease.RunStoreWrite(admission, () =>
    {
        token.ThrowIfCancellationRequested();
        PostFilterSnapshot current = _current ?? LoadCore();
        if (current.Revision != expectedRevision) throw new PostFilterRevisionException(current);
        if (current.State != PostFilterStoreState.RecoveryAvailable || _recoverySource == null)
            throw new InvalidOperationException("No supported recovery snapshot is available.");
        // A changed primary or backup requires another review, never an implicit restore.
        string reviewed = _recoverySource;
        string? identity = _recoveryIdentity;
        PostFilterSnapshot disk = LoadCore();
        if (disk.State != PostFilterStoreState.RecoveryAvailable || _recoverySource != reviewed || _recoveryIdentity != identity)
            throw new PostFilterRevisionException(disk);
        long revision = checked(disk.Revision + 1);
        string json = SerializeForCommit(revision, disk.HiddenPostsAction, disk.Rules, disk.HiddenPosts);
        BeforeAdmittedMutationForTests?.Invoke(admission);
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: false, token);
        _recoverySource = null; _recoveryIdentity = null;
        _current = disk with { Revision = revision, State = PostFilterStoreState.Ready, Error = null };
        AfterAdmittedMutationForTests?.Invoke(admission);
        return _current;
    });

    // ---- Rules -------------------------------------------------------------------------------------------------------

    /// <summary>Adds a rule. A rule with the same field, match, scope and pattern throws <see cref="PostFilterDuplicateException"/>.</summary>
    public PostFilterSnapshot AddRule(Guid id, PostFilterRuleDraft draft, long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => AddRuleAdmitted(id, draft, expectedRevision, admission, token));

    internal PostFilterSnapshot AddRuleAdmitted(Guid id, PostFilterRuleDraft draft, long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        _lease.RunStoreWrite(admission, () =>
        {
            if (id == Guid.Empty) throw new ArgumentException("A post filter rule needs a nonempty ID.");
            PostFilterRule rule = PostFilterRules.Create(id, PostFilterRules.Normalize(draft));
            return Mutate(expectedRevision, work =>
            {
                if (work.Rules.Any(x => x.Id == id)) throw new ArgumentException("Duplicate post filter rule ID.");
                RejectDuplicate(work.Rules, rule);
                work.Rules.Add(rule);
            }, admission, token);
        });

    /// <summary>Replaces the content of a rule, keeping its ID and place. An unsupported rule cannot be edited, only removed.</summary>
    public PostFilterSnapshot ReplaceRule(Guid id, PostFilterRuleDraft draft, long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => ReplaceRuleAdmitted(id, draft, expectedRevision, admission, token));

    internal PostFilterSnapshot ReplaceRuleAdmitted(Guid id, PostFilterRuleDraft draft, long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        _lease.RunStoreWrite(admission, () =>
        {
            PostFilterRule rule = PostFilterRules.Create(id, PostFilterRules.Normalize(draft));
            return Mutate(expectedRevision, work =>
            {
                int index = FindRule(work.Rules, id);
                RequireSupported(work.Rules[index]);
                RejectDuplicate(work.Rules.Where(x => x.Id != id), rule);
                work.Rules[index] = rule;
            }, admission, token);
        });

    public PostFilterSnapshot SetRuleEnabled(Guid id, bool enabled, long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => SetRuleEnabledAdmitted(id, enabled, expectedRevision, admission, token));

    internal PostFilterSnapshot SetRuleEnabledAdmitted(Guid id, bool enabled, long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        _lease.RunStoreWrite(admission, () => Mutate(expectedRevision, work =>
        {
            int index = FindRule(work.Rules, id);
            RequireSupported(work.Rules[index]);
            work.Rules[index] = work.Rules[index] with { Enabled = enabled };
        }, admission, token));

    public PostFilterSnapshot RemoveRule(Guid id, long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => RemoveRuleAdmitted(id, expectedRevision, admission, token));

    internal PostFilterSnapshot RemoveRuleAdmitted(Guid id, long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        _lease.RunStoreWrite(admission, () => Mutate(expectedRevision, work => work.Rules.RemoveAt(FindRule(work.Rules, id)), admission, token));

    // ---- Individually hidden posts -----------------------------------------------------------------------------------

    public PostFilterSnapshot SetHiddenPostsAction(HiddenPostsAction action, long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => SetHiddenPostsActionAdmitted(action, expectedRevision, admission, token));

    internal PostFilterSnapshot SetHiddenPostsActionAdmitted(HiddenPostsAction action, long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        _lease.RunStoreWrite(admission, () =>
        {
            if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown hidden posts action.");
            return Mutate(expectedRevision, work => work.Action = action, admission, token);
        });

    /// <summary>Hides one post. Hiding a post that is already hidden throws <see cref="PostFilterDuplicateException"/>.</summary>
    public PostFilterSnapshot HidePost(ResourceId thread, string post, bool replies, DateTimeOffset added, long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => HidePostAdmitted(thread, post, replies, added, expectedRevision, admission, token));

    internal PostFilterSnapshot HidePostAdmitted(ResourceId thread, string post, bool replies, DateTimeOffset added, long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        _lease.RunStoreWrite(admission, () =>
        {
            PostFilterRules.ValidateHiddenPost(thread, post);
            HiddenPost entry = new(thread, post, replies, added.ToUniversalTime());
            return Mutate(expectedRevision, work =>
            {
                if (work.Hidden.Any(x => x.Key == entry.Key)) throw new PostFilterDuplicateException(rule: false);
                work.Hidden.Add(entry);
            }, admission, token);
        });

    /// <summary>Shows a hidden post again. A post that is not hidden is an <see cref="InvalidOperationException"/>.</summary>
    public PostFilterSnapshot UnhidePost(ResourceId thread, string post, long expectedRevision, CancellationToken token = default) =>
        Standalone(token, admission => UnhidePostAdmitted(thread, post, expectedRevision, admission, token));

    internal PostFilterSnapshot UnhidePostAdmitted(ResourceId thread, string post, long expectedRevision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        _lease.RunStoreWrite(admission, () =>
        {
            PostFilterRules.ValidateHiddenPost(thread, post);
            return Mutate(expectedRevision, work =>
            {
                int index = work.Hidden.FindIndex(x => x.Key == (thread, post));
                if (index < 0) throw new InvalidOperationException("The selected post is not hidden.");
                work.Hidden.RemoveAt(index);
            }, admission, token);
        });

    /// <summary>
    /// Replaces the complete post-filter image for an explicit profile transfer or backup restore.
    /// The caller supplies only a snapshot that has already passed the transfer codec; this method
    /// still validates it again at the store boundary and derives the target revision locally.
    /// </summary>
    internal PostFilterSnapshot RestoreSnapshot(PostFilterSnapshot incoming, long expectedRevision,
        CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RestoreSnapshotAdmitted(incoming, expectedRevision, admission, token);
    }

    internal PostFilterSnapshot RestoreSnapshotAdmitted(PostFilterSnapshot incoming, long expectedRevision,
        ProfileAdmissionToken admission, CancellationToken token = default) => _lease.RunStoreWrite(admission, () =>
    {
        ArgumentNullException.ThrowIfNull(incoming);
        token.ThrowIfCancellationRequested();
        ValidateRestorableSnapshot(incoming);
        PostFilterSnapshot current = _current ?? LoadCore();
        PostFilterSnapshot disk = LoadCore();
        if (current.Revision != expectedRevision || disk.Revision != expectedRevision
            || !current.CanWrite || !disk.CanWrite
            || Serialize(current.Revision, current.HiddenPostsAction, current.Rules, current.HiddenPosts)
                != Serialize(disk.Revision, disk.HiddenPostsAction, disk.Rules, disk.HiddenPosts))
            throw new PostFilterRevisionException(disk);

        // A lowered configured limit may leave older data above the limit readable. A
        // replacement may not use a transfer to grow such a store past its current limit.
        if (incoming.Rules.Count > _lease.StoreLimits.PostFilterRules
            && incoming.Rules.Count > disk.Rules.Count)
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.PostFilterRules, _lease.StoreLimits.PostFilterRules,
                "The post filter rule limit is reached.");
        if (incoming.HiddenPosts.Count > _lease.StoreLimits.HiddenPosts
            && incoming.HiddenPosts.Count > disk.HiddenPosts.Count)
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.HiddenPosts, _lease.StoreLimits.HiddenPosts,
                "The hidden post limit is reached.");

        long next = checked(expectedRevision + 1);
        string json = SerializeForCommit(next, incoming.HiddenPostsAction, incoming.Rules, incoming.HiddenPosts);
        if (File.Exists(_path) && !BackupMayBeRotated())
            throw new InvalidOperationException("The post filters backup is damaged or unsupported. No files were changed.");
        BeforeAdmittedMutationForTests?.Invoke(admission);
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token);
        _current = new(next, incoming.HiddenPostsAction,
            Array.AsReadOnly(incoming.Rules.ToArray()), Array.AsReadOnly(incoming.HiddenPosts.ToArray()),
            PostFilterStoreState.Ready);
        AfterAdmittedMutationForTests?.Invoke(admission);
        return _current;
    });

    private static void ValidateRestorableSnapshot(PostFilterSnapshot snapshot)
    {
        if (snapshot.State != PostFilterStoreState.Ready || snapshot.Error != null || snapshot.Revision < 0
            || !Enum.IsDefined(snapshot.HiddenPostsAction) || snapshot.Rules == null || snapshot.HiddenPosts == null
            || snapshot.Rules.Count > ProfileStoreLimits.PostFilterRuleCeiling
            || snapshot.HiddenPosts.Count > ProfileStoreLimits.HiddenPostCeiling)
            throw new InvalidDataException("The post filter transfer snapshot is not supported.");

        HashSet<Guid> ruleIds = new();
        HashSet<string> duplicateRules = new(StringComparer.Ordinal);
        foreach (PostFilterRule rule in snapshot.Rules)
        {
            if (rule == null || !rule.CanApply || rule.RawDefinition != null || rule.Id == Guid.Empty
                || !ruleIds.Add(rule.Id))
                throw new InvalidDataException("The post filter transfer snapshot contains an invalid rule.");
            PostFilterRuleDraft normalized;
            try
            {
                normalized = PostFilterRules.Normalize(new(rule.Field, rule.Match, rule.Pattern,
                    rule.Action, rule.Replies, rule.Source, rule.Board, rule.Enabled));
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("The post filter transfer snapshot contains an invalid rule.", error);
            }
            if (normalized.Field != rule.Field || normalized.Match != rule.Match || normalized.Pattern != rule.Pattern
                || normalized.Action != rule.Action || normalized.Source != rule.Source || normalized.Board != rule.Board)
                throw new InvalidDataException("The post filter transfer snapshot contains an unnormalized rule.");
            if (!duplicateRules.Add(rule.DuplicateKey))
                throw new InvalidDataException("The post filter transfer snapshot contains duplicate rules.");
        }

        HashSet<(ResourceId Thread, string Post)> hidden = new();
        foreach (HiddenPost entry in snapshot.HiddenPosts)
        {
            if (entry == null) throw new InvalidDataException("The post filter transfer snapshot contains an invalid hidden post.");
            try { PostFilterRules.ValidateHiddenPost(entry.Thread, entry.Post); }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("The post filter transfer snapshot contains an invalid hidden post.", error);
            }
            if (!hidden.Add(entry.Key))
                throw new InvalidDataException("The post filter transfer snapshot contains duplicate hidden posts.");
        }
    }

    // ---- Mutation core -----------------------------------------------------------------------------------------------

    private sealed class Work
    {
        internal HiddenPostsAction Action;
        internal List<PostFilterRule> Rules = [];
        internal List<HiddenPost> Hidden = [];
    }

    private T Standalone<T>(CancellationToken token, Func<ProfileAdmissionToken, T> run)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return run(admission);
    }

    // Called only by the admitted wrappers while RunStoreWrite is pinned.
    private PostFilterSnapshot Mutate(long revision, Action<Work> change, ProfileAdmissionToken admission, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PostFilterSnapshot current = _current ?? LoadCore();
        PostFilterSnapshot disk = LoadCore();
        // Compare content too: an external writer must not reuse an existing revision.
        if (current.Revision != revision || disk.Revision != revision || !disk.CanWrite || !current.CanWrite ||
            Serialize(current.Revision, current.HiddenPostsAction, current.Rules, current.HiddenPosts) !=
            Serialize(disk.Revision, disk.HiddenPostsAction, disk.Rules, disk.HiddenPosts))
            throw new PostFilterRevisionException(disk);
        Work work = new() { Action = disk.HiddenPostsAction, Rules = disk.Rules.ToList(), Hidden = disk.HiddenPosts.ToList() };
        change(work);
        // Only growth is refused: with a limit lowered below the saved count, edits and removals still work.
        if (work.Rules.Count > _lease.StoreLimits.PostFilterRules && work.Rules.Count > disk.Rules.Count)
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.PostFilterRules, _lease.StoreLimits.PostFilterRules,
                "The post filter rule limit is reached.");
        if (work.Hidden.Count > _lease.StoreLimits.HiddenPosts && work.Hidden.Count > disk.HiddenPosts.Count)
            throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.HiddenPosts, _lease.StoreLimits.HiddenPosts,
                "The hidden post limit is reached.");
        long next = checked(revision + 1);
        string json = SerializeForCommit(next, work.Action, work.Rules, work.Hidden);
        if (File.Exists(_path) && !BackupMayBeRotated())
            throw new InvalidOperationException("The post filters backup is damaged or unsupported. No files were changed.");
        BeforeAdmittedMutationForTests?.Invoke(admission);
        AtomicJsonFile.WriteAllText(_path, json, backupCurrentFile: true, token);
        _current = new(next, work.Action, Array.AsReadOnly(work.Rules.ToArray()), Array.AsReadOnly(work.Hidden.ToArray()), PostFilterStoreState.Ready);
        AfterAdmittedMutationForTests?.Invoke(admission);
        return _current;
    }

    private static void RejectDuplicate(IEnumerable<PostFilterRule> existing, PostFilterRule rule)
    {
        string key = rule.DuplicateKey;
        PostFilterRule? same = existing.FirstOrDefault(x => x.CanApply && x.DuplicateKey == key);
        if (same != null) throw new PostFilterDuplicateException(rule: true, same.Id);
    }

    private static int FindRule(List<PostFilterRule> list, Guid id)
    {
        int index = list.FindIndex(x => x.Id == id);
        if (index < 0) throw new InvalidOperationException("The selected post filter rule no longer exists.");
        return index;
    }

    private static void RequireSupported(PostFilterRule rule)
    {
        if (!rule.CanApply) throw new InvalidOperationException("This post filter rule needs a newer version and is kept unchanged.");
    }

    // ---- Reading -----------------------------------------------------------------------------------------------------

    // InvalidDataException derives from SystemException, not IOException.
    private static bool IsReadError(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException or KeyNotFoundException or DecoderFallbackException or NotSupportedException;

    // The commit replaces the backup with the current file. A damaged, unsupported or unreadable backup is kept instead:
    // the commit is refused with the store's own error, never a parser exception.
    private bool BackupMayBeRotated()
    {
        try { return Read(_path + ".bak", missingIsEmpty: true).CanWrite; }
        catch (Exception error) when (IsReadError(error)) { return false; }
    }

    private static string FileIdentity(string path)
    {
        using FileStream stream = Open(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // The system messages of open failures name the full profile path, and load errors reach the dialog's text.
    private static FileStream Open(string path)
    {
        try { return new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { throw new FileNotFoundException("The post filters file is missing."); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("The post filters file could not be read."); }
        catch (IOException) { throw new IOException("The post filters file could not be read."); }
    }

    private static string ReadBounded(string path)
    {
        using FileStream stream = Open(path);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Post filters exceed 32 MiB.");
        using MemoryStream contents = new();
        byte[] chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk)) != 0)
        {
            if (contents.Length + read > MaximumFileBytes) throw new InvalidDataException("Post filters exceed 32 MiB.");
            contents.Write(chunk, 0, read);
        }
        string text = new UTF8Encoding(false, true).GetString(contents.GetBuffer(), 0, checked((int)contents.Length));
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static readonly string[] RuleMembers = ["id", "enabled", "field", "match", "pattern", "action", "replies", "source", "board"];
    private static readonly string[] HiddenMembers = ["source", "board", "thread", "post", "replies", "added"];

    private static PostFilterSnapshot Read(string path, bool missingIsEmpty)
    {
        string json;
        try { json = ReadBounded(path); }
        catch (FileNotFoundException) when (missingIsEmpty) { return PostFilterSnapshot.Empty(PostFilterStoreState.Ready); }
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid post filters envelope.");
        CheckDuplicates(root);
        int schema = root.GetProperty("schemaVersion").GetInt32();
        if (schema != 1) return PostFilterSnapshot.Empty(PostFilterStoreState.Unsupported, "Unsupported post filters schema: " + schema);
        long revision = root.GetProperty("revision").GetInt64();
        if (revision < 0) throw new InvalidDataException("Invalid post filters revision.");
        bool unknownEnvelopeFields = root.EnumerateObject().Any(x => x.Name is not ("schemaVersion" or "revision" or "hiddenPostsAction" or "rules" or "hiddenPosts"));
        bool unsupported = unknownEnvelopeFields;
        HiddenPostsAction action = HiddenPostsAction.Stub;
        JsonElement actionElement = root.GetProperty("hiddenPostsAction");
        if (actionElement.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid hidden posts action.");
        switch (actionElement.GetString())
        {
            case "stub": action = HiddenPostsAction.Stub; break;
            case "hide": action = HiddenPostsAction.Hide; break;
            default: unsupported = true; break;
        }

        JsonElement rulesArray = root.GetProperty("rules");
        if (rulesArray.ValueKind != JsonValueKind.Array || rulesArray.GetArrayLength() > ProfileStoreLimits.PostFilterRuleCeiling)
            throw new InvalidDataException("Invalid post filter rules list.");
        List<PostFilterRule> rules = new();
        HashSet<Guid> ids = new();
        foreach (JsonElement item in rulesArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A post filter rule is not an object.");
            CheckDuplicates(item);
            JsonElement idElement = item.GetProperty("id");
            if (idElement.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid post filter rule ID.");
            Guid id = idElement.GetGuid();
            if (id == Guid.Empty || !ids.Add(id)) throw new InvalidDataException("Duplicate or empty post filter rule ID.");
            PostFilterRule rule = ReadRule(id, item);
            // An unknown envelope may change what any rule means: none of them is applied, none is rewritten.
            if (unknownEnvelopeFields && rule.CanApply)
                rule = rule with { RawDefinition = item.GetRawText(), UnsupportedReason = "Unsupported post filters envelope fields." };
            rules.Add(rule);
        }

        JsonElement hiddenArray = root.GetProperty("hiddenPosts");
        if (hiddenArray.ValueKind != JsonValueKind.Array || hiddenArray.GetArrayLength() > ProfileStoreLimits.HiddenPostCeiling)
            throw new InvalidDataException("Invalid hidden posts list.");
        List<HiddenPost> hidden = new();
        HashSet<(ResourceId, string)> keys = new();
        foreach (JsonElement item in hiddenArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A hidden post is not an object.");
            CheckDuplicates(item);
            if (item.EnumerateObject().Any(x => !HiddenMembers.Contains(x.Name))) unsupported = true;
            HiddenPost entry = new(
                new ResourceId(RequiredString(item, "source"), RequiredString(item, "board"), RequiredString(item, "thread")),
                RequiredString(item, "post"), RequiredBool(item, "replies"), item.GetProperty("added").GetDateTimeOffset().ToUniversalTime());
            PostFilterRules.ValidateHiddenPost(entry.Thread, entry.Post);
            if (!keys.Add(entry.Key)) throw new InvalidDataException("Duplicate hidden post.");
            hidden.Add(entry);
        }
        return new(revision, action, Array.AsReadOnly(rules.ToArray()), Array.AsReadOnly(hidden.ToArray()),
            unsupported ? PostFilterStoreState.Unsupported : PostFilterStoreState.Ready,
            unsupported ? "Unsupported post filter data. The file is read-only and has been preserved." : null);
    }

    private static PostFilterRule ReadRule(Guid id, JsonElement item)
    {
        string raw = item.GetRawText();
        PostFilterRule Unsupported(string reason) =>
            new(id, false, PostFilterField.Text, PostFilterMatch.Contains, "", PostFilterAction.Hide, false, null, null, raw, reason);

        if (item.EnumerateObject().Any(x => !RuleMembers.Contains(x.Name))) return Unsupported("Unsupported post filter rule members.");
        if (!TryEnum(item, "field", out PostFilterField field, ("text", PostFilterField.Text), ("subject", PostFilterField.Subject),
                ("filename", PostFilterField.Filename), ("md5", PostFilterField.Md5)))
            return Unsupported("Unsupported post filter field.");
        if (!TryEnum(item, "match", out PostFilterMatch match, ("contains", PostFilterMatch.Contains), ("exact", PostFilterMatch.Exact)))
            return Unsupported("Unsupported post filter match.");
        if (!TryEnum(item, "action", out PostFilterAction action, ("hide", PostFilterAction.Hide), ("stub", PostFilterAction.Stub),
                ("highlight", PostFilterAction.Highlight)))
            return Unsupported("Unsupported post filter action.");
        string pattern = RequiredString(item, "pattern");
        PostFilterRules.ValidatePattern(pattern);
        string? source = OptionalScope(item, "source");
        string? board = OptionalScope(item, "board");
        if (board != null && source == null) throw new InvalidDataException("A board scope needs its source.");
        return new(id, RequiredBool(item, "enabled"), field, match, pattern, action, RequiredBool(item, "replies"), source, board);
    }

    private static bool TryEnum<T>(JsonElement item, string name, out T value, params (string Name, T Value)[] map) where T : struct
    {
        value = default;
        JsonElement element = item.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid post filter member: " + name);
        string text = element.GetString()!;
        foreach ((string key, T mapped) in map)
            if (string.Equals(key, text, StringComparison.Ordinal)) { value = mapped; return true; }
        return false;
    }

    private static string RequiredString(JsonElement item, string name)
    {
        JsonElement element = item.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid post filter member: " + name);
        return element.GetString()!;
    }

    private static bool RequiredBool(JsonElement item, string name)
    {
        JsonElement element = item.GetProperty(name);
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid post filter member: " + name);
        return element.GetBoolean();
    }

    private static string? OptionalScope(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid post filter member: " + name);
        string text = element.GetString()!;
        if (text.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > PostFilterRules.MaximumIdentityLength || text.Any(char.IsControl))
            throw new InvalidDataException("Invalid post filter scope: " + name);
        return text;
    }

    private static void CheckDuplicates(JsonElement value)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name);
    }

    // ---- Writing -----------------------------------------------------------------------------------------------------

    private static string SerializeForCommit(long revision, HiddenPostsAction action, IReadOnlyList<PostFilterRule> rules, IReadOnlyList<HiddenPost> hidden)
    {
        string json = Serialize(revision, action, rules, hidden);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            throw new InvalidDataException("Post filters exceed 32 MiB.");
        return json;
    }

    private static string Serialize(long revision, HiddenPostsAction action, IReadOnlyList<PostFilterRule> rules, IReadOnlyList<HiddenPost> hidden)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("revision", revision);
            writer.WriteString("hiddenPostsAction", action == HiddenPostsAction.Hide ? "hide" : "stub");
            writer.WriteStartArray("rules");
            foreach (PostFilterRule rule in rules)
            {
                if (rule.RawDefinition != null) { writer.WriteRawValue(rule.RawDefinition); continue; }
                writer.WriteStartObject();
                writer.WriteString("id", rule.Id);
                writer.WriteBoolean("enabled", rule.Enabled);
                writer.WriteString("field", rule.Field switch
                {
                    PostFilterField.Subject => "subject", PostFilterField.Filename => "filename", PostFilterField.Md5 => "md5", _ => "text"
                });
                writer.WriteString("match", rule.Match == PostFilterMatch.Exact ? "exact" : "contains");
                writer.WriteString("pattern", rule.Pattern);
                writer.WriteString("action", rule.Action switch
                {
                    PostFilterAction.Stub => "stub", PostFilterAction.Highlight => "highlight", _ => "hide"
                });
                writer.WriteBoolean("replies", rule.Replies);
                if (rule.Source != null) writer.WriteString("source", rule.Source);
                if (rule.Board != null) writer.WriteString("board", rule.Board);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("hiddenPosts");
            foreach (HiddenPost entry in hidden)
            {
                writer.WriteStartObject();
                writer.WriteString("source", entry.Thread.Source);
                writer.WriteString("board", entry.Thread.Board);
                writer.WriteString("thread", entry.Thread.Thread);
                writer.WriteString("post", entry.Post);
                writer.WriteBoolean("replies", entry.Replies);
                writer.WriteString("added", PostFilterRules.FormatAdded(entry.Added));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
