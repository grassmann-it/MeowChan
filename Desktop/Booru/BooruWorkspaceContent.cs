using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using MeowChan.Persistence;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The profile's effective Booru content rules: the strictest combination of the last confirmed choice and the current
/// document. An unreadable document or an unknown schema blocks everything. Every consumer decides through
/// <see cref="Snapshot"/> or <see cref="ReadEffective"/>; only the content editor sees the document itself
/// (<see cref="ReadForEdit"/>), and only a confirmed choice (<see cref="Publish"/>, <see cref="Save"/>) loosens the rules.
/// </summary>
internal sealed class BooruWorkspaceContent
{
    /// <summary>Reads the profile document and reports it to the callback under the profile lease.</summary>
    internal delegate BooruContentProfileSnapshot DocumentReader(ProfileLease lease, Action<BooruContentProfileSnapshot> onRead);

    private static readonly ConditionalWeakTable<ProfileLease, Lazy<BooruWorkspaceContent>> Profiles = new();
    private static readonly DocumentReader StoreReader = static (lease, onRead) => new BooruContentProfileStore(lease).Load(onRead);

    internal static BooruWorkspaceContent ForProfile(ProfileLease lease) =>
        Profiles.GetValue(lease, static profile => new(() => new(new BooruContentProfileStore(profile).Load(), profile))).Value;

    private readonly ProfileLease? _lease;
    private readonly DocumentReader _read;
    private readonly bool _startedWithReadFailure;
    private BooruContentProfileSnapshot _snapshot;
    // The strictest rules observed so far, including a ceiling for saved media before the first choice.
    private BooruContentProfileSnapshot? _knownRules;

    internal BooruWorkspaceContent(BooruContentProfileSnapshot initial, ProfileLease? lease = null, DocumentReader? read = null)
    {
        _lease = lease;
        _read = read ?? StoreReader;
        _startedWithReadFailure = initial.State is BooruContentProfileState.Unreadable or BooruContentProfileState.Unsupported;
        _snapshot = initial;
        _knownRules = initial.HasChoice ? initial : null;
    }

    /// <summary>The effective rules, without reading the document.</summary>
    internal BooruContentProfileSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal bool HasChoice => Snapshot.HasChoice;
    internal event Action? Changed;

    /// <summary>Makes a confirmed choice effective, including any loosening it contains.</summary>
    internal void Publish(BooruContentProfileSnapshot snapshot)
    {
        AcceptConfirmed(snapshot);
        Changed?.Invoke();
    }

    /// <summary>The single access for consumers: reads the document and returns the effective rules.</summary>
    internal BooruContentProfileSnapshot ReadEffective()
    {
        if (_lease is { } lease) Read(lease, acceptPersistedChoice: false);
        return Snapshot;
    }

    /// <summary>
    /// The document as the editor shows it. Applies it to the effective rules like any read; never use the result to
    /// decide what is allowed.
    /// </summary>
    internal BooruContentProfileSnapshot ReadForEdit(ProfileLease lease, bool acceptPersistedChoice) =>
        Read(lease, acceptPersistedChoice);

    private BooruContentProfileSnapshot Read(ProfileLease lease, bool acceptPersistedChoice)
    {
        bool changed = false;
        BooruContentProfileSnapshot document = _read(lease, rules => changed = ApplyRead(rules, acceptPersistedChoice));
        if (changed) Changed?.Invoke();
        return document;
    }

    /// <summary>Saves the editor's changes as a field-wise merge onto the current document; the result is the confirmed choice.</summary>
    internal BooruContentProfileSnapshot Save(ProfileLease lease, BooruContentEdit edit, ProfileAdmissionToken admission)
    {
        bool changed = false;
        try
        {
            return new BooruContentProfileStore(lease).SaveEditAdmitted(edit, admission,
                onRead: rules => changed = ApplyRead(rules, acceptPersistedChoice: false),
                onSaved: saved => { AcceptConfirmed(saved); changed = true; });
        }
        finally
        {
            if (changed) Changed?.Invoke();
        }
    }

    // Store callbacks update the effective rules in file access order under the profile lease.
    // Consumers are notified outside the lease so they can safely read other profile stores.
    private bool ApplyRead(BooruContentProfileSnapshot observed, bool acceptPersistedChoice)
    {
        BooruContentProfileSnapshot active = Snapshot;
        BooruContentProfileSnapshot effective;
        if (observed.State is BooruContentProfileState.Unreadable or BooruContentProfileState.Unsupported)
        {
            // Block everything, but keep the known rules as the bound for a later repair.
            effective = observed;
        }
        else if (!observed.HasChoice)
        {
            // Keep previously observed restrictions when the document disappears.
            effective = _knownRules ?? observed;
        }
        else
        {
            bool acceptRepair = _startedWithReadFailure && acceptPersistedChoice;
            if (_knownRules is null && _startedWithReadFailure && !acceptRepair) return false;
            effective = _knownRules is { } known ? Restrict(known, observed) : observed;
            if (_knownRules?.HasChoice != true && !acceptRepair && effective.HasChoice)
            {
                // A new document can restrict saved media while provider actions still require a first choice.
                effective = effective with
                {
                    State = BooruContentProfileState.PendingChoice,
                    Filter = new(effective.Revision, effective.Settings with { MaximumRating = null })
                };
            }
            _knownRules = effective;
        }

        if (ReferenceEquals(active, effective) || active.State == effective.State
            && effective.State is BooruContentProfileState.Missing or BooruContentProfileState.Unreadable or BooruContentProfileState.Unsupported)
            return false;
        Volatile.Write(ref _snapshot, effective);
        return true;
    }

    private void AcceptConfirmed(BooruContentProfileSnapshot snapshot)
    {
        _knownRules = snapshot.HasChoice ? snapshot : null;
        Volatile.Write(ref _snapshot, snapshot);
    }

    private static BooruContentProfileSnapshot Restrict(BooruContentProfileSnapshot previous, BooruContentProfileSnapshot observed)
    {
        BooruContentSettings current = previous.Settings;
        BooruContentSettings incoming = observed.Settings;
        BooruContentSettings effective = new(
            incoming.MaximumRating < current.MaximumRating ? incoming.MaximumRating : current.MaximumRating,
            CombineBlacklists(current.GlobalBlacklist, incoming.GlobalBlacklist),
            CombineSourceBlacklists(current.SourceBlacklists, incoming.SourceBlacklists));
        if (previous.Revision == observed.Revision && SameRules(current, effective)) return previous;
        if (SameRules(incoming, effective)) return observed;
        return observed with { Settings = effective, Filter = new(observed.Revision, effective) };
    }

    private static bool SameRules(BooruContentSettings left, BooruContentSettings right) =>
        left.MaximumRating == right.MaximumRating && left.GlobalBlacklist == right.GlobalBlacklist
        && left.SourceBlacklists.SequenceEqual(right.SourceBlacklists);

    private static IReadOnlyList<BooruContentSourceBlacklist> CombineSourceBlacklists(
        IReadOnlyList<BooruContentSourceBlacklist> current, IReadOnlyList<BooruContentSourceBlacklist> incoming)
    {
        if (current.Count == 0) return incoming;
        if (incoming.Count == 0 || current.SequenceEqual(incoming)) return current;
        Dictionary<(string Source, string Instance), BooruContentSourceBlacklist> combined =
            current.ToDictionary(rule => (rule.SourceId, rule.InstanceId));
        foreach (BooruContentSourceBlacklist rule in incoming)
        {
            var scope = (rule.SourceId, rule.InstanceId);
            combined[scope] = combined.TryGetValue(scope, out BooruContentSourceBlacklist? prior)
                ? rule with { Blacklist = CombineBlacklists(prior.Blacklist, rule.Blacklist) }
                : rule;
        }
        return Array.AsReadOnly(combined.Values.ToArray());
    }

    private static string CombineBlacklists(string current, string incoming)
    {
        if (current.Length == 0) return incoming;
        if (incoming.Length == 0 || current == incoming) return current;
        char[] breaks = ['\r', '\n'];
        return string.Join('\n', current.Split(breaks, StringSplitOptions.RemoveEmptyEntries)
            .Concat(incoming.Split(breaks, StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal bool Allows(BooruPostClaim post) => Snapshot.Filter.Evaluate(post).IsAllowed;
    internal bool Allows(BooruPostView post) => Allows(Snapshot.Filter, post);

    internal static bool Allows(BooruContentFilter? filter, BooruPostView post) =>
        filter is null || Allows(filter, post.Post, post.SourceId, post.Identity.InstanceId);

    internal static bool Allows(BooruContentFilter filter, BooruPost metadata, string source, string instance)
    {
        try
        {
            // Signed scores cannot be represented by the backwards-compatible claim store.
            if (metadata.Score is < 0)
                metadata = metadata with { Score = null, Fields = metadata.Fields & ~BooruFieldPresence.Score };
            return filter.Evaluate(BooruClaimMapper.Map(metadata, source, instance)).IsAllowed;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException or OverflowException)
        {
            return false;
        }
    }
}
