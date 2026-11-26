using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Persistence;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using Serilog;

namespace MeowChan.Desktop.Booru;

internal sealed record BooruTabSnapshot(string ProviderId, int ActiveIndex, BooruSearchView?[] Tabs, BooruSearchView? Draft = null);

/// <summary>Private, profile-local search history. Calls that schedule work belong to the workspace dispatcher.</summary>
internal sealed class BooruSearchTabStore(ProfileLease lease)
{
    internal const string FileName = "booru-search-tabs.json";
    private const int Version = 1;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    // Leave room for drafts and JSON framing even with the maximum number of tabs.
    private const int MaximumRowsJsonBytes = 448 * 1024;
    private const int MaximumDraftLength = 4096;
    private static readonly JsonSerializerOptions Json = new()
    {
        // This JSON is only read back from disk. Keep base64 byte counts stable when writing through JsonElement.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true
    };

    private sealed record SavedFile(int Version, string ProviderId, int ActiveIndex, SavedTab?[] Tabs,
        bool AllTabsClosable = false, SavedTab? Draft = null);
    private sealed record SavedTab(BooruInputMode Mode, string Input, string? Query, string? PostId,
        int Page, bool IsEnd, bool Empty, JsonElement[] Rows,
        int MaximumPage = Config.BooruSearchPagesDefault, int PostsPerPage = Config.BooruSearchPostsPerPageDefault,
        int? LastResultPage = null);
    private sealed record SavedRow(BooruPost Post, byte[]? Preview);

    private readonly string _path = Path.Combine(lease.ProfileDirectory, FileName);
    private long _saveRevision;
    private bool _examinedExisting;
    private bool _preserveExisting;
    internal Task<bool> PendingSave { get; private set; } = Task.FromResult(false);

    internal BooruTabSnapshot? Load(BooruSearchView current, int resultLimit, SourceDescription? source)
    {
        try
        {
            // A replaced page may have just queued its final snapshot.
            PendingSave.GetAwaiter().GetResult();
            using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
            return lease.RunStoreWrite(admission, () => ReadOrQuarantine(current, resultLimit, source));
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _preserveExisting = true;
            Log.Warning("The local Booru search tabs could not be read.");
            return null;
        }
    }

    internal void Save(BooruTabSnapshot snapshot, int resultLimit)
    {
        PendingSave = _preserveExisting ? Task.FromResult(false) : SaveAfterAsync(PendingSave, snapshot, resultLimit);
    }

    private async Task<bool> SaveAfterAsync(Task previous, BooruTabSnapshot snapshot, int resultLimit)
    {
        try
        {
            // Accept before returning to the caller so a final save can finish after shutdown closes ingress.
            using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
            long revision = Interlocked.Increment(ref _saveRevision);
            await previous.ConfigureAwait(false);
            if (revision != Interlocked.Read(ref _saveRevision)) return false;
            return await Task.Run(() =>
            {
                bool empty = snapshot.Tabs.Length == 0;
                // Older readers accept the V1 placeholder. It also represents an empty strip for this reader.
                SavedTab?[] tabs = empty ? [null] : Array.ConvertAll(snapshot.Tabs, view => view is null ? null : SaveTab(view, resultLimit));
                SavedTab? draft = empty && snapshot.Draft is { } view && (view.Input.Length > 0 || view.Mode != BooruInputMode.Tags)
                    ? SaveTab(view, 0) : null;
                string json = JsonSerializer.Serialize(new SavedFile(Version, snapshot.ProviderId, empty ? 0 : snapshot.ActiveIndex,
                    tabs, AllTabsClosable: true, Draft: draft), Json);
                return lease.RunStoreWrite(admission, () =>
                {
                    // A workspace opened with restore disabled has never inspected its prior snapshot.
                    if (!_examinedExisting && File.Exists(_path)) return false;
                    AtomicJsonFile.ReplaceAllText(_path, json, CancellationToken.None);
                    _examinedExisting = true;
                    return true;
                });
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            // An admission refusal must leave an already queued write in the drain.
            await previous.ConfigureAwait(false);
            // Neither errors raised by the serializer nor data from this file belong in diagnostics.
            Log.Warning("The local Booru search tabs could not be saved.");
            return false;
        }
    }

    private BooruTabSnapshot? ReadOrQuarantine(BooruSearchView current, int resultLimit, SourceDescription? source)
    {
        _examinedExisting = true;
        _preserveExisting = false;
        try
        {
            if (new FileInfo(_path).LinkTarget is not null)
            {
                _preserveExisting = true;
                return null;
            }
            byte[] bytes;
            using (FileStream stream = File.OpenRead(_path))
            {
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            if (IsScopedLegacyFile(root) || (ReadVersion(root) is int newer && newer > Version))
            {
                // The scoped source/instance store in 1.2 owns its V2 and its earlier scoped V1 format.
                _preserveExisting = true;
                return null;
            }
            if (ReadVersion(root) != Version) throw new InvalidDataException();

            SavedFile? file = root.Deserialize<SavedFile>(Json);
            if (file is null || string.IsNullOrWhiteSpace(file.ProviderId)
                || file.Tabs is not { Length: <= Config.BooruSavedTabsMaximum }
                || (file.Tabs.Length == 0 ? file.ActiveIndex != -1 : file.ActiveIndex < 0 || file.ActiveIndex >= file.Tabs.Length))
                throw new InvalidDataException();
            if (file.ProviderId != current.ProviderId)
            {
                _preserveExisting = true;
                return null;
            }

            if (file.Tabs.Length == 0 || (file.AllTabsClosable && file.Tabs is [null]))
            {
                BooruSearchView? draft = file.Draft is { } savedDraft ? RestoreTab(savedDraft, current, 0, source) : null;
                return new BooruTabSnapshot(file.ProviderId, -1, [], draft);
            }

            // Migrate before creating session IDs. A draft in the old permanent slot is a real tab.
            BooruSearchView?[] tabs = Array.ConvertAll(file.Tabs,
                tab => tab is null ? null : RestoreTab(tab, current, resultLimit, source));
            int activeIndex = file.ActiveIndex;
            if (!file.AllTabsClosable && IsLegacyPlaceholder(tabs[0]))
            {
                tabs = tabs[1..];
                activeIndex = tabs.Length == 0 ? -1 : Math.Max(0, activeIndex - 1);
            }
            return new BooruTabSnapshot(file.ProviderId, activeIndex, tabs);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or EndOfStreamException or FormatException or InvalidOperationException)
        {
            _preserveExisting = AtomicJsonFile.SetAsideDamagedBackup(_path) is null;
            return null;
        }
    }

    private static int? ReadVersion(JsonElement root, string property = "version") =>
        root.TryGetProperty(property, out JsonElement version) && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out int value) ? value : null;

    private static bool IsScopedLegacyFile(JsonElement root) =>
        ReadVersion(root, "Version") >= Version
            && root.TryGetProperty("State", out JsonElement state) && state.ValueKind == JsonValueKind.Object;

    private static bool IsLegacyPlaceholder(BooruSearchView? view) =>
        view is null || view is { Input.Length: 0, Mode: BooruInputMode.Tags, ExecutedQuery: null, ExecutedPostId: null };

    private static SavedTab SaveTab(BooruSearchView view, int resultLimit)
    {
        int count = Math.Min(view.Rows.Length, resultLimit);
        List<JsonElement> rows = new(count);
        int remaining = MaximumRowsJsonBytes;
        for (int index = 0; index < count; index++)
        {
            JsonElement row = JsonSerializer.SerializeToElement(new SavedRow(view.Rows[index].Post.Post, null), Json);
            // GetRawText uses the same escaped UTF-8 JSON that will be written by the serializer.
            remaining -= Encoding.UTF8.GetByteCount(row.GetRawText()) + 1;
            if (remaining < 0) break;
            rows.Add(row);
        }
        // Prefer result metadata when the per-tab byte budget cannot hold all previews.
        for (int index = 0; index < rows.Count; index++)
        {
            BooruResultRowView row = view.Rows[index];
            if (row.Preview != BooruPreviewState.Ready
                || row.PreviewBytes is not { Length: > 0 and <= BooruPreviewHttp.MaximumPreviewBytes } preview)
                continue;
            int encodedLength = 4 * ((preview.Length + 2) / 3) + 2;
            if (encodedLength > remaining) continue;
            rows[index] = JsonSerializer.SerializeToElement(new SavedRow(row.Post.Post, preview), Json);
            remaining -= encodedLength;
        }
        string input = view.Input.Length <= MaximumDraftLength ? view.Input : view.Input[..MaximumDraftLength];
        return new SavedTab(view.Mode, input, view.ExecutedQuery, view.ExecutedPostId,
            view.Page, view.IsEnd, view.Status == BooruSearchStatus.Empty, rows.ToArray(),
            view.PagerLimits.Pages, view.PagerLimits.PostsPerPage, view.LastResultPage);
    }

    private static BooruSearchView RestoreTab(SavedTab tab, BooruSearchView current, int resultLimit, SourceDescription? source)
    {
        if (!Enum.IsDefined(tab.Mode) || tab.Input is null || tab.Input.Length > MaximumDraftLength
            || tab.MaximumPage is < Config.BooruSearchPagesMinimum or > Config.BooruSearchPagesMaximum
            || tab.PostsPerPage is < Config.BooruSearchPostsPerPageMinimum or > Config.BooruSearchPostsPerPageMaximum
            || tab.Page < 1 || tab.Page > tab.MaximumPage
            || tab.Rows is null || tab.Rows.Length > tab.PostsPerPage
            || tab.Query is not null && BooruInput.Parse(BooruInputMode.Tags, tab.Query).Canonical != tab.Query
            || tab.PostId is not null && (tab.Query is not null || tab.Page != 1 || tab.Rows.Length > 1
                || !DanbooruPostReference.IsValidPostId(tab.PostId))
            || tab.Query is null && tab.PostId is null && (tab.Page != 1 || tab.Rows.Length != 0)
            || tab.Empty && (tab.Query is null || tab.Rows.Length != 0 || !tab.IsEnd))
            throw new InvalidDataException();

        int count = Math.Min(tab.Rows.Length, resultLimit);
        ImmutableArray<BooruResultRowView>.Builder rows = ImmutableArray.CreateBuilder<BooruResultRowView>(count);
        HashSet<string> postIds = new(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            SavedRow? row = tab.Rows[index].Deserialize<SavedRow>(Json);
            if (row?.Post?.Id is not { } id || id.SourceId != current.ProviderId || !postIds.Add(id.PostId)
                || tab.PostId is not null && id.PostId != tab.PostId
                || row.Preview is { Length: 0 or > BooruPreviewHttp.MaximumPreviewBytes })
                throw new InvalidDataException();
            if (source is null)
                _ = BooruClaimMapper.Map(row.Post with { Score = null });
            else if (SdkValidation.ValidateBooruPostResult(new BooruPostRequest(id), new BooruPostSnapshot(row.Post), source).Count != 0)
                throw new InvalidDataException();
            BooruPostView post = BooruPostView.From(row.Post);
            BooruPreviewState preview = row.Preview is not null ? BooruPreviewState.Ready
                : post.PreviewUrl is not null ? BooruPreviewState.Unavailable : BooruPreviewState.None;
            rows.Add(new BooruResultRowView(post, Preview: preview, PreviewBytes: row.Preview));
        }
        BooruSearchStatus status = count > 0 ? BooruSearchStatus.Results
            : tab.Empty ? BooruSearchStatus.Empty : BooruSearchStatus.Idle;
        int? lastResultPage = tab.Query is not null && !tab.Empty && tab.LastResultPage is int lastPage
            && lastPage >= tab.Page && lastPage <= tab.MaximumPage && (!tab.IsEnd || lastPage == tab.Page)
            ? lastPage : null;
        return new BooruSearchView(current.ProviderId, current.ProviderName, tab.Mode, tab.Input,
            BooruInput.Parse(tab.Mode, tab.Input), status, tab.Query, tab.PostId, tab.Page, tab.IsEnd,
            rows.MoveToImmutable(), null, null, null, NeedsLoad: status == BooruSearchStatus.Idle
                && (!string.IsNullOrWhiteSpace(tab.Input) || tab.Query is not null || tab.PostId is not null))
        { PagerLimits = new(tab.MaximumPage, tab.PostsPerPage), LastResultPage = lastResultPage };
    }
}
