using System;
using System.IO;
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
    private static readonly ConditionalWeakTable<ProfileLease, BooruWorkspaceContent> Profiles = new();

    internal static BooruWorkspaceContent ForProfile(ProfileLease lease) =>
        Profiles.GetValue(lease, static profile => new BooruWorkspaceContent(new BooruContentProfileStore(profile).Load()));

    private BooruContentProfileSnapshot _snapshot = initial;

    internal BooruContentProfileSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal bool HasChoice => Snapshot.State == BooruContentProfileState.Ready && Snapshot.Settings.MaximumRating.HasValue;
    internal event Action? Changed;

    internal void Publish(BooruContentProfileSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke();
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
