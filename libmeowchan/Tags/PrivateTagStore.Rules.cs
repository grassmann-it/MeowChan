using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Tags;

internal sealed record TagRuleChange(MediaTagSubject Subject, PrivateTagName Name, bool Assigned);

/// <summary>One selection's immutable plan. AP11 can show the original inputs, mapped names and exact private delta.</summary>
internal sealed class TagRulePreview(ProfileLease lease, long revision, string fingerprint,
    TagRuleProjection[] projections, TagRuleChange[] changes)
{
    internal ProfileLease Lease { get; } = lease;
    internal long Revision { get; } = revision;
    internal string Fingerprint { get; } = fingerprint;
    internal IReadOnlyList<TagRuleProjection> Projections { get; } = Array.AsReadOnly(projections);
    internal IReadOnlyList<TagRuleChange> Changes { get; } = Array.AsReadOnly(changes);
    internal IReadOnlyList<MediaTagSubject> AffectedSubjects { get; } = Array.AsReadOnly(
        changes.Select(change => change.Subject).Distinct().ToArray());
}

internal sealed record TagRuleCommit(TagRulePreview Preview, PrivateTagCommit Commit, TagRuleUndo? Undo);

/// <summary>In-memory undo for the exact private revision written by a changing rule apply.</summary>
internal sealed class TagRuleUndo(ProfileLease lease, long revision, string fingerprint, byte[] prior)
{
    internal ProfileLease Lease { get; } = lease;
    internal long Revision { get; } = revision;
    internal string Fingerprint { get; } = fingerprint;
    internal ReadOnlySpan<byte> Prior => prior;
}

internal sealed partial class PrivateTagStore
{
    /// <summary>
    /// Preflights a caller-selected set of media tags using the same limits and mutation as Apply. Loose media inputs are
    /// observations. The host must revalidate live media before calling Apply, as with other private tag operations.
    /// </summary>
    internal TagRulePreview PreviewTagRules(TagRuleSet rules, IReadOnlyCollection<TagRuleInput> inputs,
        long expectedRevision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(inputs);
        TagRuleInput[] selected = inputs.ToArray();
        return _lease.RunExclusive(() =>
        {
            token.ThrowIfCancellationRequested();
            (Model model, byte[] bytes) = Read();
            if (model.Revision != expectedRevision) throw new PrivateTagStoreException(PrivateTagStoreError.StaleRevision);
            RequireSubjects(selected.Select(input => input.Subject).ToArray());
            CheckRuleSuggestions(selected, token);
            TagRuleProjection[] projections = rules.Project(selected, RuleVocabulary(model), token);
            TagRuleChange[] changes = PlanRuleChanges(model, projections, token);
            if (ApplyRuleChanges(model, changes, token))
            {
                if (model.Revision == long.MaxValue) throw new PrivateTagStoreException(PrivateTagStoreError.RevisionOverflow);
                PrepareWrite(model, bytes.Length, out _);
            }
            return new TagRulePreview(_lease, expectedRevision, Fingerprint(bytes), projections, changes);
        });
    }

    /// <summary>Confirms precisely this preview. Both stores are checked under the same lease before private tags change.</summary>
    internal TagRuleCommit ApplyTagRules(TagRulePreview preview, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return ApplyTagRulesAdmitted(preview, admission, token);
    }

    internal TagRuleCommit ApplyTagRulesAdmitted(TagRulePreview preview, ProfileAdmissionToken admission,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        byte[]? prior = null;
        PrivateTagCommit commit = Mutate(admission, preview.Revision, token, (model, bytes) =>
        {
            RequireRuleBase(preview.Lease, preview.Fingerprint, bytes);
            CheckRuleSuggestions(preview.Projections.Select(row => row.Input), token);
            prior = bytes;
            return (ApplyRuleChanges(model, preview.Changes, token), default);
        });
        TagRuleUndo? undo = commit.Changed ? new(_lease, commit.Snapshot.Revision, commit.Snapshot.Fingerprint, prior!) : null;
        return new(preview, commit, undo);
    }

    internal PrivateTagCommit UndoTagRules(TagRuleUndo undo, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(undo);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return UndoTagRulesAdmitted(undo, admission, token);
    }

    internal PrivateTagCommit UndoTagRulesAdmitted(TagRuleUndo undo, ProfileAdmissionToken admission,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(undo);
        return Mutate(admission, undo.Revision, token, (model, bytes) =>
        {
            RequireRuleBase(undo.Lease, undo.Fingerprint, bytes);
            model.ReplaceContent(undo.Prior.IsEmpty ? new Model() : Parse(undo.Prior));
            return (true, default);
        });
    }

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private void RequireRuleBase(ProfileLease lease, string fingerprint, byte[] bytes)
    {
        if (!ReferenceEquals(_lease, lease) || Fingerprint(bytes) != fingerprint)
            throw new PrivateTagStoreException(PrivateTagStoreError.StaleRevision);
    }

    private void CheckRuleSuggestions(IEnumerable<TagRuleInput> inputs, CancellationToken token)
    {
        // Loading through the same lease also serializes the check with P10 run commits and review decisions.
        TagSuggestionSnapshot? snapshot = null;
        Dictionary<long, StoredTagSuggestion>? byId = null;
        foreach (TagRuleInput input in inputs)
        {
            token.ThrowIfCancellationRequested();
            if (input.Origin is not TagRuleOrigin.Model origin) continue;
            snapshot ??= new TagSuggestionStore(_lease).Load();
            if (!snapshot.CanWrite) throw new TagSuggestionStoreException(TagSuggestionStoreError.NotWritable);
            if (snapshot.Revision != origin.Revision) throw new TagSuggestionStoreException(TagSuggestionStoreError.StaleRevision);
            byId ??= snapshot.Suggestions.ToDictionary(value => value.Id);
            StoredTagSuggestion suggestion = origin.Suggestion;
            if (!byId.TryGetValue(suggestion.Id, out StoredTagSuggestion? saved))
                throw new TagSuggestionStoreException(TagSuggestionStoreError.UnknownSuggestion);
            if (saved.State != TagSuggestionState.Pending)
                throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidTransition);
            bool matches = input.Subject switch
            {
                MediaTagSubject.Bound bound => bound.Target.Sha256 == saved.MediaSha256,
                MediaTagSubject.Local local => local.Observation.Sha256 == saved.MediaSha256 && local.Observation.Length == saved.MediaLength,
                _ => false
            };
            if (!matches || saved != suggestion || input.OriginalName != saved.Name)
                throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);
        }
    }

    private static TagRuleChange[] PlanRuleChanges(Model model, TagRuleProjection[] projections, CancellationToken token)
    {
        HashSet<(MediaTagSubject, PrivateTagName)> wanted = new();
        List<TagRuleChange> add = new();
        List<TagRuleChange> remove = new();
        Dictionary<PrivateTagName, PrivateTagName> privateNames = new();
        Dictionary<LocalMediaObservation, LocalMediaResolution> locals = new();
        for (int index = 0; index < projections.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            TagRuleProjection projection = projections[index];
            TagRuleInput input = projection.Input;
            MediaTagSubject subject = ResolveRuleSubject(model, input.Subject, locals);
            if (projection.Canonical is { } canonical)
            {
                PrivateTagName planned = PlanName(canonical);
                IReadOnlyList<PrivateTagName> parents = projection.Parents.Count == 0 ? [] : Array.AsReadOnly(
                    projection.Parents.Select(PlanName).Where(parent => parent != planned).Distinct().ToArray());
                projections[index] = projection with { Canonical = planned, Parents = parents };
            }
            if (input.Origin is TagRuleOrigin.Private)
            {
                PrivateTagDefinition? original = model.Catalog.Find(RequireName(input.OriginalName));
                if (original is null || !CarriesRuleTag(model, subject, original.Id))
                    throw new TagRuleException(TagRuleError.UnassignedPrivateTag);
                remove.Add(new(subject, original.Name, Assigned: false));
            }

            PrivateTagName PlanName(PrivateTagName name)
            {
                token.ThrowIfCancellationRequested();
                PrivateTagDefinition? existing = model.Catalog.Find(name);
                if (!privateNames.TryAdd(name, name)) name = privateNames[name];
                if (wanted.Add((subject, name)) && (existing is null || !CarriesRuleTag(model, subject, existing.Id)))
                    add.Add(new(subject, name, Assigned: true));
                return name;
            }
        }
        return remove.Where(change => !wanted.Contains((change.Subject, change.Name))).Distinct().Concat(add).ToArray();
    }

    private static Func<PrivateTagName, PrivateTagName?> RuleVocabulary(Model model)
    {
        var byId = model.Catalog.Definitions.ToDictionary(definition => definition.Id);
        Dictionary<PrivateTagName, PrivateTagName?> resolved = new();
        return name =>
        {
            if (model.Catalog.Find(name) is { } exact) return exact.Name;
            if (resolved.TryGetValue(name, out PrivateTagName? mapped)) return mapped;
            PrivateTagAlias? alias = PrivateTagAliasIndex.Find(model.Aliases, name, out bool ambiguous);
            if (ambiguous) throw new TagRuleException(TagRuleError.AmbiguousMapping);
            mapped = alias is null ? null : byId[alias.Tag].Name;
            resolved.Add(name, mapped);
            return mapped;
        };
    }

    private static MediaTagSubject ResolveRuleSubject(Model model, MediaTagSubject subject,
        Dictionary<LocalMediaObservation, LocalMediaResolution> locals)
    {
        if (subject is not MediaTagSubject.Local local) return subject;
        if (!locals.TryGetValue(local.Observation, out LocalMediaResolution resolution))
            locals.Add(local.Observation, resolution = model.Resolve(local.Observation));
        if (resolution.State == LocalMediaResolutionState.Conflict)
            throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        if (local.Expected is { } expected && (!resolution.IsRegistered || resolution.Target != expected))
            throw new PrivateTagStoreException(PrivateTagStoreError.StaleTarget);
        return local with { Expected = resolution.IsRegistered ? resolution.Target : null };
    }

    private static bool CarriesRuleTag(Model model, MediaTagSubject subject, TagId tag) => subject switch
    {
        MediaTagSubject.Bound bound => model.MediaSet.Contains(new(tag, bound.Target)),
        MediaTagSubject.Local { Expected: { } target } => model.LocalSet.Contains(new(tag, target)),
        _ => false
    };

    private bool ApplyRuleChanges(Model model, IReadOnlyList<TagRuleChange> changes, CancellationToken token)
    {
        if (changes.Count == 0) return false;
        HashSet<PrivateMediaTagAssignment> boundRemovals = new();
        HashSet<PrivateLocalMediaTagAssignment> localRemovals = new();
        foreach (TagRuleChange change in changes.Where(change => !change.Assigned))
        {
            token.ThrowIfCancellationRequested();
            TagId id = model.Catalog.Find(change.Name)!.Id;
            if (change.Subject is MediaTagSubject.Bound bound) boundRemovals.Add(new(id, bound.Target));
            else if (change.Subject is MediaTagSubject.Local { Expected: { } target }) localRemovals.Add(new(id, target));
        }
        // Remove exact revision assignments. The ordinary remove-by-file path also removes handed-over keys.
        if (boundRemovals.Count > 0)
        {
            model.Media.RemoveAll(boundRemovals.Contains);
            model.MediaSet.ExceptWith(boundRemovals);
        }
        if (localRemovals.Count > 0)
        {
            model.Local.RemoveAll(localRemovals.Contains);
            model.LocalSet.ExceptWith(localRemovals);
        }
        foreach (IGrouping<PrivateTagName, TagRuleChange> additions in changes.Where(change => change.Assigned).GroupBy(change => change.Name))
        {
            token.ThrowIfCancellationRequested();
            TagId id;
            if (model.Catalog.Find(additions.Key) is { } existing) id = existing.Id;
            else
            {
                RequireTagRoom(model);
                id = new(Guid.NewGuid());
                if (!model.Catalog.TryDefine(id, additions.Key, model.Catalog.Revision, out PrivateTagCatalog next,
                    out PrivateTagCatalogError error))
                    throw new PrivateTagStoreException(Map(error));
                model.Catalog = next;
            }
            ApplySubjects(model, id, additions.Select(change => change.Subject).ToArray(), assigned: true);
        }
        return true;
    }
}
