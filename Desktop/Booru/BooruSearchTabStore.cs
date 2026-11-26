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
    BooruSearchTabRow[]? Rows = null, int MaximumPage = Config.BooruSearchPagesDefault, int PostsPerPage = Config.BooruSearchPostsPerPageDefault)
{
    internal const int MaximumDraftLength = 4096;
    internal string Source => SourceId ?? DanbooruPostReference.ProviderId;
    internal string Instance => InstanceId ?? BooruPostKey.DefaultInstance;
    internal BooruPostKey? PostIdentity => PostId is null ? null : new(Source, Instance, PostId);

    internal bool IsValid => BooruPostKey.IsValidScope(Source, Instance)
        && MaximumPage is >= Config.BooruSearchPagesMinimum and <= Config.BooruSearchPagesMaximum
        && PostsPerPage is >= Config.BooruSearchPostsPerPageMinimum and <= Config.BooruSearchPostsPerPageMaximum
        && Page >= 1 && Page <= MaximumPage
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
        if (Rows.Length > PostsPerPage) return false;
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

// V2 keeps the null start slot on disk. An optional startDraft identifies an empty strip and
// stores the input (possibly blank); without it, older V2 slots are restored as ordinary tabs.
internal sealed record BooruSearchTabState(int ActiveIndex, BooruSearchTabTarget?[] Tabs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BooruSearchTabTarget? StartDraft = null)
{
    internal bool HasSameContent(BooruSearchTabState other) => ActiveIndex == other.ActiveIndex
        && SameTarget(StartDraft, other.StartDraft)
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
    private sealed record LegacyFile(int Version, string ProviderId, int ActiveIndex, LegacyTab?[] Tabs,
        bool AllTabsClosable = false, LegacyTab? Draft = null);
    // The 1.1.3 writer includes LastResultPage on saved tabs and start drafts.
    private sealed record LegacyTab(BooruInputMode Mode, string Input, string? Query, string? PostId,
        int Page, bool IsEnd, bool Empty, BooruSearchTabRow[] Rows,
        int MaximumPage = Config.BooruSearchPagesDefault, int PostsPerPage = Config.BooruSearchPostsPerPageDefault,
        int? LastResultPage = null);

    private readonly string _path = Path.Combine(lease.ProfileDirectory, FileName);
    private readonly ILogger _logger = logger ?? Log.Logger;
    private readonly object _saveGate = new();
    private QueuedSave? _queuedSave;
    private Task<bool> _pendingSave = Task.FromResult(true);
    // Both flags are read and updated under the profile lease.
    private bool _examinedExisting;
    private bool _preserveExisting;

    private sealed record QueuedSave(BooruSearchTabState State, ProfileAdmissionToken Admission);

    internal Task<bool> PendingSave
    {
        get { lock (_saveGate) return _pendingSave; }
    }

    internal BooruSearchTabState? Read(bool migrateLegacy = true)
    {
        // A replaced page may have queued its final snapshot before this page was created.
        PendingSave.GetAwaiter().GetResult();
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        return lease.RunStoreWrite(admission, () => ReadExisting(migrateLegacy));
    }

    internal void Write(BooruSearchTabState state)
    {
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        WriteUnderAdmission(admission, state);
    }

    internal Task<bool> Save(BooruSearchTabState state)
    {
        ProfileAdmissionToken admission;
        try
        {
            // Reserve admission before returning to the dispatcher so the final save joins shutdown's drain.
            admission = lease.Admission.EnterStoreWriter();
        }
        catch (Exception error) when (IsStoreError(error))
        {
            _logger.Warning("Could not save local Booru search tabs");
            lock (_saveGate)
            {
                if (_pendingSave.IsCompleted) _pendingSave = Task.FromResult(false);
            }
            return Task.FromResult(false);
        }

        lock (_saveGate)
        {
            _queuedSave?.Admission.Dispose();
            _queuedSave = new QueuedSave(state, admission);
            if (_pendingSave.IsCompleted)
            {
                TaskCompletionSource<bool> drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingSave = drained.Task;
                _ = Task.Run(() => DrainSaves(drained));
            }
            return _pendingSave;
        }
    }

    private void DrainSaves(TaskCompletionSource<bool> drained)
    {
        bool saved = true;
        try
        {
            while (true)
            {
                QueuedSave request;
                lock (_saveGate)
                {
                    if (_queuedSave is not { } queued)
                    {
                        drained.SetResult(saved);
                        return;
                    }
                    request = queued;
                    _queuedSave = null;
                }

                using (request.Admission)
                {
                    try
                    {
                        saved = WriteUnderAdmission(request.Admission, request.State, coalesce: true);
                    }
                    catch (Exception error) when (IsStoreError(error))
                    {
                        _logger.Warning("Could not save local Booru search tabs");
                        saved = false;
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (_saveGate)
            {
                _queuedSave?.Admission.Dispose();
                _queuedSave = null;
                drained.SetException(error);
            }
        }
    }

    private bool HasQueuedSave
    {
        get { lock (_saveGate) return _queuedSave is not null; }
    }

    private bool WriteUnderAdmission(ProfileAdmissionToken admission, BooruSearchTabState state, bool coalesce = false)
    {
        if (coalesce && HasQueuedSave) return false;
        Validate(state);
        BooruSearchTabState trimmed = state with
        {
            Tabs = Array.ConvertAll(state.Tabs, tab => tab is null ? null : tab with { Rows = FitRows(tab.Rows) })
        };
        return lease.RunStoreWrite(admission, () =>
        {
            // A newer snapshot can arrive during preparation or while waiting for the profile.
            if (coalesce && (HasQueuedSave || _preserveExisting || (!_examinedExisting && HasExistingEntry()))) return false;
            Replace(trimmed);
            _examinedExisting = true;
            _preserveExisting = false;
            return true;
        });
    }

    private bool HasExistingEntry()
    {
        lease.EnsureDirectoryIdentity();
        try { _ = File.GetAttributes(_path); return true; }
        catch (FileNotFoundException) { return false; }
    }

    private BooruSearchTabState? ReadExisting(bool migrateLegacy)
    {
        _examinedExisting = true;
        _preserveExisting = true;

        BooruSearchTabState state;
        bool migrate;
        try
        {
            lease.EnsureDirectoryIdentity();
            string? text = RegularProfileFile.ReadBoundedText(_path, MaximumFileBytes);
            if (text is null)
            {
                _preserveExisting = HasExistingEntry();
                return null;
            }
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                MaxDepth = Json.MaxDepth
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && (HasNewerVersion(root, "version", Version) || HasNewerVersion(root, "Version", LegacyVersion)))
                return null;
            (state, migrate) = Decode(root);
            Validate(state);
        }
        catch (Exception error) when ((error is JsonException or InvalidDataException or EndOfStreamException
            or ProfileDocumentTooLargeException or FormatException) && RegularProfileFile.TryGetIdentity(_path) is not null)
        {
            lease.EnsureDirectoryIdentity();
            if (AtomicJsonFile.SetAsideDamagedBackup(_path) is not null)
            {
                _preserveExisting = false;
                _logger.Warning("Quarantined damaged or unsupported local Booru search tabs");
            }
            return null;
        }

        _preserveExisting = false;
        if (migrate && migrateLegacy)
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
                return (DecodeLegacy(legacy), true);
        }
        throw new InvalidDataException("The Booru tab state version is unsupported or missing.");
    }

    private static BooruSearchTabState DecodeLegacy(LegacyFile legacy)
    {
        int active = legacy.ActiveIndex;
        if (legacy.Tabs.Length > Config.BooruSavedTabsMaximum
            || (legacy.Tabs.Length == 0 ? active != -1 : active < 0 || active >= legacy.Tabs.Length))
            throw new InvalidDataException("The saved Booru active tab is invalid.");

        BooruSearchTabTarget ConvertTab(LegacyTab tab) => new(legacy.ProviderId, BooruPostKey.DefaultInstance,
            tab.PostId, tab.Query, tab.Page, tab.Mode, tab.Input, tab.IsEnd, tab.Empty, tab.Rows, tab.MaximumPage, tab.PostsPerPage);

        if (legacy.Tabs.Length == 0 || legacy.AllTabsClosable && legacy.Tabs is [null])
            return new(0, [null], legacy.Draft is { } draft ? ConvertTab(draft) : BlankStart(legacy.ProviderId));
        if (legacy.Draft is not null)
            throw new InvalidDataException("The saved Booru start draft has no empty start slot.");

        BooruSearchTabTarget?[] tabs = Array.ConvertAll(legacy.Tabs, tab => tab is null ? null : ConvertTab(tab));
        if (!legacy.AllTabsClosable && (tabs[0] is null or
            { Input.Length: 0, Mode: BooruInputMode.Tags, Query: null, PostId: null, IsValid: true }))
        {
            tabs = tabs[1..];
            active = Math.Max(0, active - 1);
        }
        return tabs.Length == 0 ? new(0, [null], BlankStart(legacy.ProviderId)) : new(active, tabs);
    }

    private static BooruSearchTabTarget BlankStart(string source) =>
        new(source, BooruPostKey.DefaultInstance, null, null, Mode: BooruInputMode.Tags, Input: string.Empty);

    private static bool HasVersion(JsonElement root, string name, int version) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number) && number == version;

    private static bool HasNewerVersion(JsonElement root, string name, int version) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number) && number > version;

    private void Replace(BooruSearchTabState state)
    {
        BooruSearchTabState scoped = state with
        {
            Tabs = Array.ConvertAll(state.Tabs, ExplicitScope),
            StartDraft = ExplicitScope(state.StartDraft)
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(Version, scoped), Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("The Booru tab state exceeds its file bound.");
        lease.EnsureDirectoryIdentity();
        AtomicJsonFile.ReplaceUnderLease(_path, bytes, lease.EnsureDirectoryIdentity, CancellationToken.None);
    }

    private static BooruSearchTabTarget? ExplicitScope(BooruSearchTabTarget? target) =>
        target is null ? null : target with { SourceId = target.Source, InstanceId = target.Instance };

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
            || state.Tabs.Any(tab => tab is { IsValid: false })
            || state.StartDraft is { } draft && (state.Tabs is not [null] || !draft.IsValid
                || draft.Query is not null || draft.PostId is not null))
            throw new InvalidDataException("The saved Booru tab targets are invalid.");
    }

    internal static bool IsStoreError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or InvalidOperationException or JsonException;
}
