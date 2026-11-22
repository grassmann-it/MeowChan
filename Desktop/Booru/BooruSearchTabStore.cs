using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Persistence;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;
using Serilog;

namespace MeowChan.Desktop.Booru;

internal sealed record BooruSearchTabRow(BooruPost Post, byte[]? Preview);

internal sealed record BooruSearchTabTarget(string? SourceId, string? InstanceId, string? PostId, string? Query,
    int Page = 1, BooruInputMode? Mode = null, string? Input = null, bool IsEnd = true, bool Empty = false,
    BooruSearchTabRow[]? Rows = null)
{
    internal const int MaximumDraftLength = 4096;
    internal string Source => SourceId ?? DanbooruPostReference.ProviderId;
    internal string Instance => InstanceId ?? BooruPostKey.DefaultInstance;
    internal BooruPostKey? PostIdentity => PostId is null ? null : new(Source, Instance, PostId);

    internal bool IsValid => BooruPostKey.IsValidScope(Source, Instance) && Page is >= 1 and <= BooruSearchModel.MaximumPage
        && (Mode is null || Enum.IsDefined(Mode.Value)) && Input is not { Length: > MaximumDraftLength }
        && (PostIdentity is null || PostIdentity is { IsValid: true } && Query is null && Page == 1 && Rows is not { Length: > 1 })
        && (Query is not null || PostId is not null || Page == 1 && Rows is not { Length: > 0 })
        && (!Empty || Query is not null && IsEnd && Rows is not { Length: > 0 })
        && (Query is null || BooruInput.Parse(BooruInputMode.Tags, Query) is { Kind: BooruInputKind.Tags, Canonical: var canonical }
            && canonical == Query)
        && RowsAreValid();

    private bool RowsAreValid()
    {
        if (Rows is null) return true;
        if (Rows.Length > BooruSearchModel.PageSize) return false;
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (BooruSearchTabRow? row in Rows)
        {
            if (row?.Post?.Id is not { } id || id.SourceId != Source || !new BooruPostKey(Source, Instance, id.PostId).IsValid
                || !ids.Add(id.PostId) || PostId is not null && id.PostId != PostId
                || row.Preview is { Length: 0 or > BooruPreviewHttp.MaximumPreviewBytes })
                return false;
        }
        return true;
    }
}

internal sealed record BooruSearchTabState(int ActiveIndex, BooruSearchTabTarget?[] Tabs)
{
    internal bool HasSameContent(BooruSearchTabState other) => ActiveIndex == other.ActiveIndex
        && Tabs.Length == other.Tabs.Length && Tabs.Zip(other.Tabs).All(pair => SameTarget(pair.First, pair.Second));

    private static bool SameTarget(BooruSearchTabTarget? left, BooruSearchTabTarget? right) =>
        left is null || right is null ? left == right
        : (left with { Rows = null }) == (right with { Rows = null })
            && (left.Rows ?? []).SequenceEqual(right.Rows ?? []);
}

/// <summary>Stores profile-local tab snapshots, without transfer state or exception data.</summary>
internal sealed class BooruSearchTabStore(ProfileLease lease, ILogger? logger = null)
{
    internal const string FileName = "booru-search-tabs.json";
    private const int Version = 2;
    private const int LegacyVersion = 1;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumRowsJsonBytes = 448 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private static readonly JsonSerializerOptions ScopedV1Json = new(Json)
    {
        PropertyNamingPolicy = null,
        RespectRequiredConstructorParameters = false
    };

    private sealed record Document(int Version, BooruSearchTabState State);
    private sealed record ScopedV1File(int Version, ScopedV1State State);
    private sealed record ScopedV1State(int ActiveIndex, ScopedV1Target?[] Tabs);
    private sealed record ScopedV1Target(string? SourceId = null, string? InstanceId = null, string? PostId = null,
        string? Query = null, int Page = 1);
    private sealed record LegacyFile(int Version, string ProviderId, int ActiveIndex, LegacyTab?[] Tabs);
    private sealed record LegacyTab(BooruInputMode Mode, string Input, string? Query, string? PostId,
        int Page, bool IsEnd, bool Empty, BooruSearchTabRow[] Rows);

    private readonly string _path = Path.Combine(lease.ProfileDirectory, FileName);
    private readonly ILogger _logger = logger ?? Log.Logger;
    private long _saveRevision;
    internal Task<bool> PendingSave { get; private set; } = Task.FromResult(true);

    internal BooruSearchTabState? Read()
    {
        // A replaced page may have queued its final snapshot before this page was created.
        PendingSave.GetAwaiter().GetResult();
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        return lease.RunStoreWrite(admission, ReadAndMigrate);
    }

    internal void Write(BooruSearchTabState state)
    {
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        WriteUnderAdmission(admission, state);
    }

    internal Task<bool> Save(BooruSearchTabState state) => PendingSave = SaveAfterAsync(PendingSave, state);

    private async Task<bool> SaveAfterAsync(Task previous, BooruSearchTabState state)
    {
        try
        {
            // Reserve admission synchronously, so a final save joins the shutdown drain.
            using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
            long revision = Interlocked.Increment(ref _saveRevision);
            await previous.ConfigureAwait(false);
            if (revision != Interlocked.Read(ref _saveRevision)) return true;
            await Task.Run(() => WriteUnderAdmission(admission, state)).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (IsStoreError(error))
        {
            await previous.ConfigureAwait(false);
            _logger.Warning("Could not save local Booru search tabs");
            return false;
        }
    }

    private void WriteUnderAdmission(ProfileAdmissionToken admission, BooruSearchTabState state)
    {
        Validate(state);
        BooruSearchTabState trimmed = state with
        {
            Tabs = Array.ConvertAll(state.Tabs, tab => tab is null ? null : tab with { Rows = FitRows(tab.Rows) })
        };
        lease.RunStoreWrite(admission, () =>
        {
            Replace(trimmed);
            return true;
        });
    }

    private BooruSearchTabState? ReadAndMigrate()
    {
        BooruSearchTabState state;
        bool migrate;
        try
        {
            lease.EnsureDirectoryIdentity();
            string? text = RegularProfileFile.ReadBoundedText(_path, MaximumFileBytes);
            if (text is null) return null;
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                MaxDepth = Json.MaxDepth
            });
            (state, migrate) = Decode(document.RootElement);
            Validate(state);
        }
        catch (Exception error) when ((error is JsonException or InvalidDataException or EndOfStreamException
            or ProfileDocumentTooLargeException or FormatException) && RegularProfileFile.TryGetIdentity(_path) is not null)
        {
            lease.EnsureDirectoryIdentity();
            if (AtomicJsonFile.SetAsideDamagedBackup(_path) is not null)
                _logger.Warning("Quarantined damaged or unsupported local Booru search tabs");
            return null;
        }

        if (migrate)
        {
            // Convert the complete file before applying the current view limits. An untouched
            // workspace must keep any valid tabs and rows beyond those limits on disk.
            try { Replace(state); }
            catch (Exception error) when (IsStoreError(error))
            {
                _logger.Warning("Could not migrate local Booru search tabs");
            }
        }
        return state;
    }

    private static (BooruSearchTabState State, bool Migrate) Decode(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (HasVersion(root, "version", Version) && root.Deserialize<Document>(Json)?.State is { } current)
                return (current, false);
            if (HasVersion(root, "Version", LegacyVersion)
                && root.Deserialize<ScopedV1File>(ScopedV1Json)?.State is { Tabs: not null } scoped)
                return (new(scoped.ActiveIndex, Array.ConvertAll(scoped.Tabs, tab => tab is null ? null
                    : new BooruSearchTabTarget(tab.SourceId, tab.InstanceId, tab.PostId, tab.Query, tab.Page))), true);
            if (HasVersion(root, "version", LegacyVersion) && root.Deserialize<LegacyFile>(Json) is { } legacy
                && BooruPostKey.IsToken(legacy.ProviderId) && legacy.Tabs is not null)
            {
                return (new(legacy.ActiveIndex, Array.ConvertAll(legacy.Tabs, tab => tab is null ? null
                    : new BooruSearchTabTarget(legacy.ProviderId, BooruPostKey.DefaultInstance, tab.PostId, tab.Query,
                        tab.Page, tab.Mode, tab.Input, tab.IsEnd, tab.Empty, tab.Rows))), true);
            }
        }
        throw new InvalidDataException("The Booru tab state version is unsupported or missing.");
    }

    private static bool HasVersion(JsonElement root, string name, int version) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number) && number == version;

    private void Replace(BooruSearchTabState state)
    {
        BooruSearchTabState scoped = state with
        {
            Tabs = Array.ConvertAll(state.Tabs, tab => tab is null ? null : tab with { SourceId = tab.Source, InstanceId = tab.Instance })
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(Version, scoped), Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("The Booru tab state exceeds its file bound.");
        lease.EnsureDirectoryIdentity();
        AtomicJsonFile.ReplaceUnderLease(_path, bytes, lease.EnsureDirectoryIdentity, CancellationToken.None);
    }

    private static BooruSearchTabRow[]? FitRows(BooruSearchTabRow[]? rows)
    {
        if (rows is not { Length: > 0 }) return rows;
        List<BooruSearchTabRow> retained = new(rows.Length);
        int remaining = MaximumRowsJsonBytes;
        foreach (BooruSearchTabRow row in rows)
        {
            BooruSearchTabRow metadata = row with { Preview = null };
            remaining -= JsonSerializer.SerializeToUtf8Bytes(metadata, Json).Length + 1;
            if (remaining < 0) break;
            retained.Add(metadata);
        }
        // Spend the rest of the budget on previews once the result metadata is reserved.
        for (int index = 0; index < retained.Count; index++)
        {
            if (rows[index].Preview is not { } preview) continue;
            int encodedLength = 4 * ((preview.Length + 2) / 3) + 2;
            if (encodedLength > remaining) continue;
            retained[index] = rows[index];
            remaining -= encodedLength;
        }
        return retained.ToArray();
    }

    private static void Validate(BooruSearchTabState state)
    {
        if (state.Tabs is not { Length: > 0 and <= Config.BooruSavedTabsMaximum }
            || state.ActiveIndex < 0 || state.ActiveIndex >= state.Tabs.Length
            || state.Tabs.Any(tab => tab is { IsValid: false }))
            throw new InvalidDataException("The saved Booru tab targets are invalid.");
    }

    internal static bool IsStoreError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or InvalidOperationException or JsonException;
}
