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

internal sealed class BooruWorkspaceContent(BooruContentProfileSnapshot initial)
{
    private static readonly ConditionalWeakTable<ProfileLease, Lazy<BooruWorkspaceContent>> Profiles = new();

    internal static BooruWorkspaceContent ForProfile(ProfileLease lease) =>
        Profiles.GetValue(lease, static profile => new(() => new(new BooruContentProfileStore(profile).Load()))).Value;

    private BooruContentProfileSnapshot _snapshot = initial;
    private BooruContentProfileSnapshot? _lastReady = initial.HasChoice ? initial : null;

    internal BooruContentProfileSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal bool HasChoice => Snapshot.HasChoice;
    internal event Action? Changed;

    internal void Publish(BooruContentProfileSnapshot snapshot)
    {
        AcceptConfirmed(snapshot);
        Changed?.Invoke();
    }

    internal BooruContentProfileSnapshot Read(ProfileLease lease, bool acceptPersistedChoice = false)
    {
        bool changed = false;
        BooruContentProfileSnapshot snapshot = new BooruContentProfileStore(lease).Load(
            rules => changed = ApplyRead(rules, acceptPersistedChoice));
        if (changed) Changed?.Invoke();
        return snapshot;
    }

    internal BooruContentProfileSnapshot Save(ProfileLease lease, BooruContentSettings settings, long revision, ProfileAdmissionToken admission)
    {
        bool changed = false;
        try
        {
            return new BooruContentProfileStore(lease).SaveAdmitted(settings, revision, admission,
                onRead: rules => changed = ApplyRead(rules, acceptPersistedChoice: false),
                onSaved: saved => { AcceptConfirmed(saved); changed = true; });
        }
        finally
        {
            if (changed) Changed?.Invoke();
        }
    }

    // Store callbacks update the active filter in file access order under the profile lease.
    // Notify consumers outside the lease so they can safely read other profile stores.
    private bool ApplyRead(BooruContentProfileSnapshot observed, bool acceptPersistedChoice)
    {
        BooruContentProfileSnapshot active = Snapshot;
        if (!observed.HasChoice)
        {
            if (active.State == observed.State) return false;
            Volatile.Write(ref _snapshot, observed);
            return true;
        }

        // Preserve known restrictions through a failed read and through edits that also loosen some rules.
        BooruContentProfileSnapshot effective = _lastReady is { } previous ? Restrict(previous, observed) : observed;
        _lastReady = effective;
        if ((!active.HasChoice && !acceptPersistedChoice) || ReferenceEquals(active, effective)) return false;
        Volatile.Write(ref _snapshot, effective);
        return true;
    }

    private void AcceptConfirmed(BooruContentProfileSnapshot snapshot)
    {
        _lastReady = snapshot.HasChoice ? snapshot : null;
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

    internal static bool Allows(BooruContentFilter? filter, BooruPostView post)
    {
        if (filter is null) return true;
        try
        {
            // Signed scores cannot be represented by the backwards-compatible claim store.
            BooruPost metadata = post.Post;
            if (metadata.Score is < 0)
                metadata = metadata with { Score = null, Fields = metadata.Fields & ~BooruFieldPresence.Score };
            return filter.Evaluate(BooruClaimMapper.Map(metadata, post.SourceId, post.Identity.InstanceId)).IsAllowed;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException or OverflowException)
        {
            return false;
        }
    }
}
