using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Desktop.Booru;

// Keep submitted addresses and tab order. Provider responses, URLs and transfer state belong to their own lifetimes.
internal sealed record BooruSearchTabTarget(string? SourceId, string? InstanceId, string? PostId, string? Query, int Page = 1)
{
    internal string Source => SourceId ?? DanbooruPostReference.ProviderId;
    internal string Instance => InstanceId ?? BooruPostKey.DefaultInstance;
    internal BooruPostKey? PostIdentity => PostId is null ? null : new(Source, Instance, PostId);

    internal bool IsValid => BooruPostKey.IsValidScope(Source, Instance) && Page is >= 1 and <= BooruSearchModel.MaximumPage
        && (PostIdentity is null || PostIdentity is { IsValid: true } && Query is null && Page == 1)
        && (Query is null || BooruInput.Parse(BooruInputMode.Tags, Query) is { Kind: BooruInputKind.Tags, Canonical: var canonical }
            && canonical == Query);
}

internal sealed record BooruSearchTabState(int ActiveIndex, BooruSearchTabTarget?[] Tabs);

internal sealed class BooruSearchTabStore(ProfileLease lease)
{
    internal const string FileName = "booru-search-tabs.json";
    private const int MaximumDocumentKiB = 256;
    private const int MaximumBytes = MaximumDocumentKiB * 1024;
    private const int Version = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly string _path = Path.Combine(lease.ProfileDirectory, FileName);
    private sealed record Document(int Version, BooruSearchTabState State);

    internal BooruSearchTabState? Read() => lease.RunExclusive(() => ReadDocument()?.State);

    internal void Write(BooruSearchTabState state)
    {
        Validate(state);
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        lease.RunStoreWrite(admission, () =>
        {
            BooruSearchTabState? previous = ReadDocument()?.State;
            if (previous != null && previous.ActiveIndex == state.ActiveIndex && previous.Tabs.SequenceEqual(state.Tabs))
                return true;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(Version, state), Json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("The Booru tab state exceeds its file bound.");
            AtomicJsonFile.ReplaceUnderLease(_path, bytes, lease.EnsureDirectoryIdentity, CancellationToken.None);
            return true;
        });
    }

    private Document? ReadDocument()
    {
        try
        {
            using FileStream stream = RegularProfileFile.OpenRead(_path);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("The Booru tab state exceeds its file bound.");
            Document? document = JsonSerializer.Deserialize<Document>(stream, Json);
            if (document?.Version != Version || document.State is null)
                throw new InvalidDataException("The Booru tab state version is unsupported or missing.");
            Validate(document.State);
            return document;
        }
        catch (FileNotFoundException) { return null; }
    }

    private static void Validate(BooruSearchTabState state)
    {
        if (state.Tabs is not { Length: > 0 and <= MeowChan.Config.BooruSearchTabsMaximum + 1 }
            || state.ActiveIndex < 0 || state.ActiveIndex >= state.Tabs.Length
            || state.Tabs.Any(tab => tab is { IsValid: false }))
            throw new InvalidDataException("The saved Booru tab targets are invalid.");
    }
}
