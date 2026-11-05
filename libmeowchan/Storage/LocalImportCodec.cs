using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeowChan.Archive;

namespace MeowChan.Storage;

/// <summary>
/// Pure bounded v1 value codecs/digests. No SQL, filesystem, source acquisition,
/// stage/owner issuance, receipt lookup, or negative-commit proof lives here.
/// </summary>
internal sealed class LocalImportCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new()
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = LocalImportBounds.MaximumJsonDepth };
    private static readonly HashSet<string> ClaimFields = new(StringComparer.Ordinal)
    {
        "version", "profileId", "profileVersion", "claimId", "publicSourceUrl", "tagsPresence", "sourceTags", "omissions",
        "privateCategories", "acquisitionUrl", "credentials"
    };
    // Conservative allowances for L3's scalar operation/ready-journal envelopes.
    // L3 must still bound the actual bytes; these are not a journal wire format.
    private const int OperationHeaderBytes = 4096;
    private const int ReadyJournalHeaderBytes = 2048;
    private readonly Func<string, int, string, bool>? _isPublicSourceUrl;

    /// <param name="isPublicSourceUrl">Trusted deterministic synchronous profile/version-specific inert classification, never network/routing.
    /// Without it, nonnull URL claims fail closed. A callback does not confer profile/write authority.</param>
    internal LocalImportCodec(Func<string, int, string, bool>? isPublicSourceUrl = null)
        => _isPublicSourceUrl = isPublicSourceUrl;

    internal byte[] EncodeClaims(LocalSourceClaims value) => Guard(() =>
    {
        ValidateClaims(value);
        return Write(LocalImportBounds.MaximumClaimsBytes, w => WriteClaims(w, value));
    });
    internal LocalSourceClaims DecodeClaims(ReadOnlyMemory<byte> json) => Guard(() =>
    {
        using JsonDocument doc = Parse(json, LocalImportBounds.MaximumClaimsBytes);
        LocalSourceClaims value = ReadClaims(doc.RootElement);
        _ = EncodeClaims(value);
        return value;
    });
    internal byte[] EncodeSelection(LocalImportSelectionDescriptor value) => Guard(() =>
    {
        ValidateDescriptor(value);
        return Write(LocalImportBounds.MaximumSelectionBytes, w => WriteSelection(w, value));
    });
    internal LocalImportSelectionDescriptor DecodeSelection(ReadOnlyMemory<byte> json) => Guard(() =>
    {
        using JsonDocument doc = Parse(json, LocalImportBounds.MaximumSelectionBytes);
        JsonElement root = doc.RootElement;
        Fields(root, "version", "catalogInstance", "operationId", "collectionId", "title", "items");
        var collection = new LocalCollectionId(Id(root, "collectionId"));
        JsonElement rows = root.GetProperty("items");
        int count = Count(rows, LocalImportBounds.MaximumItems, 1);
        var items = ImmutableArray.CreateBuilder<LocalSelectedItem>(count);
        long claimsBytes = 0;
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Fields(row, "itemId", "revisionId", "ordinal", "inputKey", "displayName", "claims");
            JsonElement claims = row.GetProperty("claims");
            LocalSourceClaims? parsedClaims = null;
            if (claims.ValueKind != JsonValueKind.Null)
            {
                int rawBytes = RawBytes(claims, LocalImportBounds.MaximumClaimsBytes);
                claimsBytes = checked(claimsBytes + rawBytes);
                Cap(claimsBytes, LocalImportBounds.MaximumCollectionClaimsBytes);
                parsedClaims = ReadClaims(claims);
            }
            items.Add(new(new(collection, new(Id(row, "itemId")), new(Id(row, "revisionId"))),
                Int(row, "ordinal"), Text(row, "inputKey"), OptionalText(row, "displayName"), parsedClaims));
        }
        var value = new LocalImportSelectionDescriptor(Int(root, "version"), Text(root, "catalogInstance"),
            Id(root, "operationId"), collection, Text(root, "title"), items.MoveToImmutable());
        _ = EncodeSelection(value);
        return value;
    });
    internal byte[] EncodeContent(LocalImportContent value) => Guard(() =>
    {
        ValidateContent(value);
        return Write(LocalImportBounds.MaximumContentBytes, w => WriteContent(w, value));
    });
    internal LocalImportContent DecodeContent(ReadOnlyMemory<byte> json) => Guard(() =>
    {
        using JsonDocument doc = Parse(json, LocalImportBounds.MaximumContentBytes);
        JsonElement root = doc.RootElement;
        Fields(root, "version", "items");
        JsonElement rows = root.GetProperty("items");
        var items = ImmutableArray.CreateBuilder<LocalImportContentItem>(Count(rows, LocalImportBounds.MaximumItems, 1));
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Fields(row, "collectionId", "itemId", "revisionId", "ordinal", "role", "length", "sha256", "payloadRelativeSlot");
            items.Add(new(new(new(Id(row, "collectionId")), new(Id(row, "itemId")), new(Id(row, "revisionId"))),
                Int(row, "ordinal"), (LocalMediaRole)Int(row, "role"), Long(row, "length"),
                Text(row, "sha256"), Text(row, "payloadRelativeSlot")));
        }
        var value = new LocalImportContent(Int(root, "version"), items.MoveToImmutable());
        _ = EncodeContent(value);
        return value;
    });
    internal byte[] EncodeReceipt(LocalCollectionImportReceipt value) => Guard(() =>
    {
        ValidateReceipt(value);
        return Write(LocalImportBounds.MaximumReceiptBytes, w => WriteReceipt(w, value));
    });
    internal LocalCollectionImportReceipt DecodeReceipt(ReadOnlyMemory<byte> json, LocalImportReceiptKey expectedKey) => Guard(() =>
    {
        ValidateReceiptKey(expectedKey);
        using JsonDocument doc = Parse(json, LocalImportBounds.MaximumReceiptBytes);
        JsonElement root = doc.RootElement;
        Fields(root, "version", "catalogInstance", "operationId", "requestSha256", "collectionId", "collectionRevision", "registeredAtUtc", "revisions");
        var collection = new LocalCollectionId(Id(root, "collectionId"));
        string time = Text(root, "registeredAtUtc");
        if (!DateTimeOffset.TryParseExact(time, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var registeredAt)
            || registeredAt.Offset != TimeSpan.Zero || time != registeredAt.ToString("O", CultureInfo.InvariantCulture)) throw Invalid();
        JsonElement rows = root.GetProperty("revisions");
        var revisions = ImmutableArray.CreateBuilder<LocalRegisteredRevision>(Count(rows, LocalImportBounds.MaximumItems, 1));
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Fields(row, "itemId", "revisionId", "ordinal", "role", "length", "sha256");
            revisions.Add(new(new(collection, new(Id(row, "itemId")), new(Id(row, "revisionId"))),
                Int(row, "ordinal"), (LocalMediaRole)Int(row, "role"), Long(row, "length"), Text(row, "sha256")));
        }
        var value = new LocalCollectionImportReceipt(Int(root, "version"), Text(root, "catalogInstance"),
            Id(root, "operationId"), Text(root, "requestSha256"), collection, Long(root, "collectionRevision"),
            registeredAt, revisions.MoveToImmutable());
        _ = EncodeReceipt(value);
        if (value.CatalogInstance != expectedKey.CatalogInstance) throw Error(LocalImportError.TargetReplaced);
        if (value.OperationId != expectedKey.OperationId || value.RequestSha256 != expectedKey.RequestSha256)
            throw Error(LocalImportError.OperationConflict);
        return value;
    });

    internal void ValidateSelection(LocalImportSelection value) => Guard(() =>
    {
        if (value is null) throw Invalid();
        String(value.Title, LocalImportBounds.MaximumTitleBytes, noControls: true);
        ArrayCount(value.Items, LocalImportBounds.MaximumItems, 1);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        long claimsBytes = 0;
        foreach (var item in value.Items)
        {
            if (item is null) throw Invalid();
            ValidateInput(item.InputKey, item.DisplayName, item.Claims, keys, ref claimsBytes);
        }
        return true;
    });

    internal string ComputeRequestSha256(LocalImportSelectionDescriptor selection, LocalImportContent content) => Guard(() =>
    {
        _ = ValidatePair(selection, content);
        using var digest = new Digest("meowchan.local-import.request.v1");
        digest.Text(selection.CatalogInstance); digest.Text(selection.OperationId.ToString("D"));
        digest.Text(selection.CollectionId.ToString()); digest.Text(selection.Title); digest.Int(selection.Items.Length);
        for (int i = 0; i < selection.Items.Length; i++)
        {
            LocalSelectedItem item = selection.Items[i];
            LocalImportContentItem bytes = content.Items[i];
            digest.Int(item.Ordinal); digest.Text(item.Reference.ItemId.ToString()); digest.Text(item.Reference.RevisionId.ToString());
            digest.Text(item.InputKey); digest.Int((int)bytes.Role); digest.Long(bytes.Length); digest.Hash(bytes.Sha256);
            digest.Text(bytes.PayloadRelativeSlot); digest.Optional(item.DisplayName);
            digest.Presence(item.Claims != null);
            if (item.Claims is { } claims)
            {
                digest.Int(claims.Version); digest.Text(claims.ProfileId); digest.Int(claims.ProfileVersion);
                digest.Optional(claims.ClaimId); digest.Optional(claims.PublicSourceUrl);
                digest.Int((int)claims.TagsPresence); digest.Int(claims.SourceTags.Length);
                foreach (string tag in claims.SourceTags) digest.Text(tag);
                digest.Int(claims.Omissions.Length);
                foreach (var omission in claims.Omissions) { digest.Text(omission.FieldCode); digest.Int((int)omission.Reason); }
            }
        }
        return digest.Finish();
    });

    internal string ComputeBindingsSha256(LocalImportSelectionDescriptor selection, LocalImportContent content,
        LocalImportBindingKind kind) => Guard(() =>
    {
        _ = ValidatePair(selection, content);
        string domain = kind switch
        {
            LocalImportBindingKind.Stage => "meowchan.local-import.stage-bindings.v1",
            LocalImportBindingKind.Candidate => "meowchan.local-import.candidate-bindings.v1",
            _ => throw Invalid()
        };
        using var digest = new Digest(domain);
        digest.Int(content.Items.Length);
        foreach (var item in content.Items)
        {
            digest.Text(item.Reference.CollectionId.ToString()); digest.Text(item.Reference.ItemId.ToString());
            digest.Text(item.Reference.RevisionId.ToString()); digest.Int((int)item.Role);
            digest.Long(item.Length); digest.Hash(item.Sha256);
            digest.Text(kind == LocalImportBindingKind.Stage
                ? Slot(selection.OperationId, item.Reference.RevisionId, "stage") : item.PayloadRelativeSlot);
        }
        return digest.Finish();
    });

    /// <summary>Conservative simultaneous-cap admission; performs no file capacity check or reservation.</summary>
    internal LocalImportMetadataBudget Preflight(LocalImportSelectionDescriptor selection, LocalImportLimits limits) => Guard(() =>
    {
        ValidateLimits(limits);
        long selectionBytes = EncodeSelection(selection).LongLength;
        // Encode worst-case scalar widths without claiming these synthetic measurements
        // are a valid payload total or a ready candidate.
        var content = new LocalImportContent(1, selection.Items.Select(item => new LocalImportContentItem(
            item.Reference, item.Ordinal, LocalMediaRole.Original, long.MaxValue, new string('f', 64),
            PayloadRelativeSlot(selection.OperationId, item.Reference.RevisionId))).ToImmutableArray());
        long contentBytes = Write(LocalImportBounds.MaximumContentBytes, w => WriteContent(w, content)).LongLength;
        var receipt = new LocalCollectionImportReceipt(1, selection.CatalogInstance, selection.OperationId, new string('f', 64),
            selection.CollectionId, 1, DateTimeOffset.MaxValue, content.Items.Select(item => new LocalRegisteredRevision(
                item.Reference, item.Ordinal, item.Role, item.Length, item.Sha256)).ToImmutableArray());
        long receiptBytes = Write(LocalImportBounds.MaximumReceiptBytes, w => WriteReceipt(w, receipt)).LongLength;
        long journal = checked(contentBytes + ReadyJournalHeaderBytes);
        Cap(journal, LocalImportBounds.MaximumJournalBytes);
        long temporary = Math.Max(selectionBytes, Math.Max(journal, receiptBytes));
        long metadata = checked(OperationHeaderBytes + selectionBytes + contentBytes + 2 * journal + receiptBytes + temporary);
        Cap(metadata, limits.ReservedMetadataBytes);
        long files = checked(2 * limits.MaxSelectedPayloadBytes + limits.ReservedMetadataBytes);
        return new LocalImportMetadataBudget(selectionBytes, contentBytes, journal, receiptBytes, temporary, metadata, files);
    });

    internal void ValidateInventory(LocalImportOperationDetail value) => Guard(() =>
    {
        try
        {
            if (value is null) throw Invalid();
            ValidateSummary(value.Summary);
            if (value.ExpectedCatalogGeneration < 0) throw Invalid();
            ArrayCount(value.Items, LocalImportBounds.MaximumItems, 1);
            if (value.Items.Length != value.Summary.ItemCount || value.Items.Any(x => x is null)) throw Invalid();
            var selection = new LocalImportSelectionDescriptor(1, value.Summary.CatalogInstance,
                value.Summary.OperationId, value.Summary.CollectionId, value.Title, value.Items.Select(x => new LocalSelectedItem(
                    x.Reference, x.Ordinal, x.InputKey, x.DisplayName, x.Claims)).ToImmutableArray());
            _ = Preflight(selection, value.Limits);
            if (value.Summary.State == LocalImportOperationState.Capturing)
            {
                if (value.StageBindingsSha256 != null || value.CandidateBindingsSha256 != null
                    || value.Items.Any(x => x.Length != null || x.Sha256 != null)) throw Invalid();
            }
            else
            {
                if (value.Items.Any(x => x.Length is null || x.Sha256 is null)) throw Invalid();
                var content = new LocalImportContent(1, value.Items.Select(x => new LocalImportContentItem(
                    x.Reference, x.Ordinal, LocalMediaRole.Original, x.Length!.Value, x.Sha256!,
                    PayloadRelativeSlot(value.Summary.OperationId, x.Reference.RevisionId))).ToImmutableArray());
                long total = ValidatePair(selection, content);
                Cap(total, value.Limits.MaxSelectedPayloadBytes);
                if (total != value.Summary.TotalLength || ComputeRequestSha256(selection, content) != value.Summary.RequestSha256
                    || ComputeBindingsSha256(selection, content, LocalImportBindingKind.Stage) != value.StageBindingsSha256) throw Invalid();
                if (value.Summary.State == LocalImportOperationState.Frozen)
                {
                    if (value.CandidateBindingsSha256 != null) throw Invalid();
                }
                else if (ComputeBindingsSha256(selection, content, LocalImportBindingKind.Candidate) != value.CandidateBindingsSha256)
                    throw Invalid();
            }
            return true;
        }
        catch (LocalImportCodecException e) when (e.Error != LocalImportError.BudgetExceeded)
        { throw Error(LocalImportError.InvalidInventory); }
    });

    internal void ValidateInventoryPage(LocalImportOperationPage value) => Guard(() =>
    {
        try
        {
            if (value is null) throw Invalid();
            ArrayCount(value.Items, LocalImportBounds.MaximumInventoryPageSize);
            string? last = null;
            foreach (var item in value.Items)
            {
                ValidateSummary(item);
                string id = item.OperationId.ToString("D");
                if (last != null && string.CompareOrdinal(last, id) >= 0) throw Invalid();
                last = id;
            }
            if (value.NextOperationId is { } next && (value.Items.Length == 0 || next == Guid.Empty
                || next != value.Items[^1].OperationId)) throw Invalid();
            return true;
        }
        catch (LocalImportCodecException) { throw Error(LocalImportError.InvalidInventory); }
    });

    /// <summary>Pure arithmetic precheck. Atomic SQL admission and persisted totals remain L3's responsibility.</summary>
    internal static void ValidateAdmission(int totalOperations, int unresolvedOperations, long reservedMetadataBytes,
        LocalImportLimits incoming) => Guard(() =>
    {
        ValidateLimits(incoming);
        if (totalOperations < 0 || unresolvedOperations < 0 || unresolvedOperations > totalOperations || reservedMetadataBytes < 0)
            throw Error(LocalImportError.InvalidInventory);
        if (totalOperations >= LocalImportBounds.MaximumOperations || unresolvedOperations >= LocalImportBounds.MaximumUnresolvedOperations
            || reservedMetadataBytes > LocalImportBounds.MaximumInventoryMetadataBytes - incoming.ReservedMetadataBytes)
            throw Error(LocalImportError.InventoryFull);
        return true;
    });

    internal static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit < 1 || limit > LocalImportBounds.MaximumPageSize) throw Invalid();
    }
    internal static string PayloadRelativeSlot(Guid operationId, LocalRevisionId revisionId) => Slot(operationId, revisionId, "payloads");

    private void ValidateDescriptor(LocalImportSelectionDescriptor value)
    {
        if (value is null || value.Version != 1) throw Invalid();
        CanonicalId(value.CatalogInstance); Nonempty(value.OperationId); Nonempty(value.CollectionId.Value);
        String(value.Title, LocalImportBounds.MaximumTitleBytes, noControls: true);
        ArrayCount(value.Items, LocalImportBounds.MaximumItems, 1);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<Guid>();
        long claimsBytes = 0;
        for (int i = 0; i < value.Items.Length; i++)
        {
            var item = value.Items[i];
            if (item is null || item.Ordinal != i) throw Invalid();
            Reference(item.Reference);
            if (item.Reference.CollectionId != value.CollectionId || !identities.Add(item.Reference.ItemId.Value)
                || !identities.Add(item.Reference.RevisionId.Value)) throw Invalid();
            ValidateInput(item.InputKey, item.DisplayName, item.Claims, keys, ref claimsBytes);
        }
    }
    private void ValidateInput(string key, string? displayName, LocalSourceClaims? claims,
        HashSet<string> keys, ref long claimsBytes)
    {
        String(key, LocalImportBounds.MaximumInputKeyBytes, nonempty: true);
        if (!keys.Add(key)) throw Invalid();
        if (displayName != null)
        {
            String(displayName, LocalImportBounds.MaximumDisplayNameBytes, noControls: true);
            // Display names are not source paths, regardless of the current OS.
            // Reject rather than extract/normalize: these exact values bind the digest.
            if (displayName.Contains('/') || displayName.Contains('\\')
                || (displayName.Length >= 2 && char.IsAsciiLetter(displayName[0]) && displayName[1] == ':')) throw Invalid();
        }
        if (claims != null)
        {
            claimsBytes = checked(claimsBytes + EncodeClaims(claims).LongLength);
            Cap(claimsBytes, LocalImportBounds.MaximumCollectionClaimsBytes);
        }
    }
    private void ValidateClaims(LocalSourceClaims value)
    {
        if (value is null || value.Version != 1 || value.ProfileVersion <= 0
            || !Enum.IsDefined(value.TagsPresence)) throw Invalid();
        String(value.ProfileId, LocalImportBounds.MaximumProfileIdBytes, nonempty: true);
        if (value.ClaimId != null) String(value.ClaimId, LocalImportBounds.MaximumClaimIdBytes);
        if (value.PublicSourceUrl != null)
        {
            String(value.PublicSourceUrl, LocalImportBounds.MaximumUrlBytes, nonempty: true, noControls: true);
            if (!Uri.TryCreate(value.PublicSourceUrl, UriKind.Absolute, out Uri? url)
                || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0
                || value.PublicSourceUrl != value.PublicSourceUrl.Trim()
                || _isPublicSourceUrl is null || !_isPublicSourceUrl(value.ProfileId, value.ProfileVersion, value.PublicSourceUrl)) throw Invalid();
        }
        ArrayCount(value.SourceTags, LocalImportBounds.MaximumTags);
        if (value.TagsPresence == LocalFieldPresence.Absent && value.SourceTags.Length != 0) throw Invalid();
        foreach (string tag in value.SourceTags) String(tag, LocalImportBounds.MaximumTagBytes);
        // The encoded 1MiB cap is authoritative; this element cap also bounds allocation before decoding strings.
        ArrayCount(value.Omissions, LocalImportBounds.MaximumClaimsBytes / 16);
        foreach (var omission in value.Omissions)
        {
            if (omission is null || !Enum.IsDefined(omission.Reason)) throw Invalid();
            String(omission.FieldCode, LocalImportBounds.MaximumFieldCodeBytes, nonempty: true);
            if (!ClaimFields.Contains(omission.FieldCode)
                || (omission.FieldCode == "privateCategories"
                    && omission.Reason is not (LocalClaimOmissionReason.Unsupported or LocalClaimOmissionReason.NotSelected))) throw Invalid();
        }
    }
    private static long ValidateContent(LocalImportContent value)
    {
        if (value is null || value.Version != 1) throw Invalid();
        ArrayCount(value.Items, LocalImportBounds.MaximumItems, 1);
        var identities = new HashSet<Guid>();
        LocalCollectionId collection = value.Items[0]?.Reference.CollectionId ?? default;
        long total = 0;
        Guid? operationId = null;
        for (int i = 0; i < value.Items.Length; i++)
        {
            var item = value.Items[i];
            if (item is null || item.Ordinal != i || item.Length < 0) throw Invalid();
            Reference(item.Reference); Role(item.Role); Hash(item.Sha256);
            if (item.Reference.CollectionId != collection || !identities.Add(item.Reference.ItemId.Value)
                || !identities.Add(item.Reference.RevisionId.Value)) throw Invalid();
            String(item.PayloadRelativeSlot, LocalImportBounds.MaximumRelativeSlotBytes, nonempty: true);
            string[] parts = item.PayloadRelativeSlot.Split('/');
            if (parts.Length != 4 || parts[0] != "local-import-operations" || parts[2] != "payloads"
                || !Guid.TryParseExact(parts[1], "N", out Guid op) || op == Guid.Empty
                || item.PayloadRelativeSlot != PayloadRelativeSlot(op, item.Reference.RevisionId))
                throw Error(LocalImportError.InvalidPayloadBinding);
            if (operationId != null && operationId != op) throw Error(LocalImportError.InvalidPayloadBinding);
            operationId = op;
            total = checked(total + item.Length);
        }
        return total;
    }
    private long ValidatePair(LocalImportSelectionDescriptor selection, LocalImportContent content)
    {
        _ = EncodeSelection(selection); _ = EncodeContent(content);
        if (selection.Items.Length != content.Items.Length) throw Invalid();
        long total = 0;
        for (int i = 0; i < selection.Items.Length; i++)
        {
            if (selection.Items[i].Reference != content.Items[i].Reference) throw Invalid();
            if (content.Items[i].PayloadRelativeSlot != PayloadRelativeSlot(selection.OperationId, content.Items[i].Reference.RevisionId))
                throw Error(LocalImportError.InvalidPayloadBinding);
            total = checked(total + content.Items[i].Length);
        }
        return total;
    }
    private static void ValidateReceipt(LocalCollectionImportReceipt value)
    {
        if (value is null || value.Version != 1 || value.CollectionRevision != 1 || value.RegisteredAtUtc.Offset != TimeSpan.Zero) throw Invalid();
        ValidateReceiptKey(new(value.CatalogInstance, value.OperationId, value.RequestSha256)); Nonempty(value.CollectionId.Value);
        ArrayCount(value.Revisions, LocalImportBounds.MaximumItems, 1);
        var identities = new HashSet<Guid>();
        long total = 0;
        for (int i = 0; i < value.Revisions.Length; i++)
        {
            var item = value.Revisions[i];
            if (item is null || item.Ordinal != i || item.Length < 0) throw Invalid();
            Reference(item.Reference); Role(item.Role); Hash(item.Sha256);
            if (item.Reference.CollectionId != value.CollectionId || !identities.Add(item.Reference.ItemId.Value)
                || !identities.Add(item.Reference.RevisionId.Value)) throw Invalid();
            total = checked(total + item.Length);
        }
    }
    internal static void ValidateReceiptKey(LocalImportReceiptKey key) => Guard(() =>
    { CanonicalId(key.CatalogInstance); Nonempty(key.OperationId); Hash(key.RequestSha256); return true; });
    internal static void ValidateRecoveryKey(LocalImportRecoveryKey key) => Guard(() =>
    { CanonicalId(key.CatalogInstance); Nonempty(key.OperationId); return true; });
    private static void ValidateSummary(LocalImportOperationSummary value)
    {
        if (value is null || !Enum.IsDefined(value.State) || value.Attempt < 1
            || value.ItemCount < 1 || value.ItemCount > LocalImportBounds.MaximumItems) throw Invalid();
        CanonicalId(value.CatalogInstance); Nonempty(value.OperationId); Nonempty(value.CollectionId.Value);
        if (value.State == LocalImportOperationState.Capturing)
        {
            if (value.RequestSha256 != null || value.TotalLength != null) throw Invalid();
        }
        else
        {
            Hash(value.RequestSha256);
            if (value.TotalLength is null or < 0) throw Invalid();
        }
    }
    private static void ValidateLimits(LocalImportLimits value)
    {
        if (value is null || value.MaxSelectedPayloadBytes <= 0 || value.ReservedMetadataBytes <= 0) throw Invalid();
        Cap(value.ReservedMetadataBytes, LocalImportBounds.MaximumOperationMetadataBytes);
        _ = checked(2 * value.MaxSelectedPayloadBytes + value.ReservedMetadataBytes);
    }

    private static void WriteSelection(Utf8JsonWriter w, LocalImportSelectionDescriptor value)
    {
        w.WriteStartObject(); w.WriteNumber("version", value.Version);
        w.WriteString("catalogInstance", value.CatalogInstance); w.WriteString("operationId", value.OperationId.ToString("D"));
        w.WriteString("collectionId", value.CollectionId.ToString()); w.WriteString("title", value.Title);
        w.WriteStartArray("items");
        foreach (var item in value.Items)
        {
            w.WriteStartObject(); WriteIds(w, item.Reference, includeCollection: false);
            w.WriteNumber("ordinal", item.Ordinal); w.WriteString("inputKey", item.InputKey);
            w.WriteString("displayName", item.DisplayName); w.WritePropertyName("claims");
            if (item.Claims is null) w.WriteNullValue(); else WriteClaims(w, item.Claims);
            w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
    private static void WriteClaims(Utf8JsonWriter w, LocalSourceClaims value)
    {
        w.WriteStartObject(); w.WriteNumber("version", value.Version); w.WriteString("profileId", value.ProfileId);
        w.WriteNumber("profileVersion", value.ProfileVersion); w.WriteString("claimId", value.ClaimId);
        w.WriteString("publicSourceUrl", value.PublicSourceUrl); w.WriteNumber("tagsPresence", (int)value.TagsPresence);
        w.WriteStartArray("sourceTags"); foreach (string tag in value.SourceTags) w.WriteStringValue(tag); w.WriteEndArray();
        w.WriteStartArray("omissions");
        foreach (var omission in value.Omissions)
        {
            w.WriteStartObject(); w.WriteString("fieldCode", omission.FieldCode);
            w.WriteNumber("reason", (int)omission.Reason); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
    private static void WriteContent(Utf8JsonWriter w, LocalImportContent value)
    {
        w.WriteStartObject(); w.WriteNumber("version", value.Version); w.WriteStartArray("items");
        foreach (var item in value.Items)
        {
            w.WriteStartObject(); WriteIds(w, item.Reference, includeCollection: true);
            WriteMeasurements(w, item.Ordinal, item.Role, item.Length, item.Sha256);
            w.WriteString("payloadRelativeSlot", item.PayloadRelativeSlot); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
    private static void WriteReceipt(Utf8JsonWriter w, LocalCollectionImportReceipt value)
    {
        w.WriteStartObject(); w.WriteNumber("version", value.Version); w.WriteString("catalogInstance", value.CatalogInstance);
        w.WriteString("operationId", value.OperationId.ToString("D")); w.WriteString("requestSha256", value.RequestSha256);
        w.WriteString("collectionId", value.CollectionId.ToString()); w.WriteNumber("collectionRevision", value.CollectionRevision);
        w.WriteString("registeredAtUtc", value.RegisteredAtUtc.ToString("O", CultureInfo.InvariantCulture)); w.WriteStartArray("revisions");
        foreach (var item in value.Revisions)
        {
            w.WriteStartObject(); WriteIds(w, item.Reference, includeCollection: false);
            WriteMeasurements(w, item.Ordinal, item.Role, item.Length, item.Sha256); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
    private static void WriteIds(Utf8JsonWriter w, LocalMediaRevisionReference reference, bool includeCollection)
    {
        if (includeCollection) w.WriteString("collectionId", reference.CollectionId.ToString());
        w.WriteString("itemId", reference.ItemId.ToString()); w.WriteString("revisionId", reference.RevisionId.ToString());
    }
    private static void WriteMeasurements(Utf8JsonWriter w, int ordinal, LocalMediaRole role, long length, string hash)
    { w.WriteNumber("ordinal", ordinal); w.WriteNumber("role", (int)role); w.WriteNumber("length", length); w.WriteString("sha256", hash); }

    private LocalSourceClaims ReadClaims(JsonElement root)
    {
        RawBytes(root, LocalImportBounds.MaximumClaimsBytes);
        Fields(root, "version", "profileId", "profileVersion", "claimId", "publicSourceUrl", "tagsPresence", "sourceTags", "omissions");
        JsonElement tags = root.GetProperty("sourceTags"), omissions = root.GetProperty("omissions");
        var tagValues = ImmutableArray.CreateBuilder<string>(Count(tags, LocalImportBounds.MaximumTags));
        foreach (JsonElement tag in tags.EnumerateArray()) tagValues.Add(Text(tag));
        var omitted = ImmutableArray.CreateBuilder<LocalClaimOmission>(Count(omissions, LocalImportBounds.MaximumClaimsBytes / 16));
        foreach (JsonElement row in omissions.EnumerateArray())
        {
            Fields(row, "fieldCode", "reason");
            omitted.Add(new(Text(row, "fieldCode"), (LocalClaimOmissionReason)Int(row, "reason")));
        }
        return new(Int(root, "version"), Text(root, "profileId"), Int(root, "profileVersion"),
            OptionalText(root, "claimId"), OptionalText(root, "publicSourceUrl"), (LocalFieldPresence)Int(root, "tagsPresence"),
            tagValues.MoveToImmutable(), omitted.MoveToImmutable());
    }
    private static JsonDocument Parse(ReadOnlyMemory<byte> json, int cap)
    {
        Cap(json.Length, cap);
        if (json.Length == 0 || json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
        _ = Utf8.GetCharCount(json.Span);
        // Reject lone escaped surrogates explicitly, before JsonElement.GetString
        // or a platform decoder can ever replace them with U+FFFD.
        var reader = new Utf8JsonReader(json.Span, new JsonReaderOptions
        { MaxDepth = LocalImportBounds.MaximumJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        while (reader.Read())
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) ValidateEscapes(reader.ValueSpan);
        return JsonDocument.Parse(json, new JsonDocumentOptions
        { MaxDepth = LocalImportBounds.MaximumJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
    }
    private static void ValidateEscapes(ReadOnlySpan<byte> raw)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\\') continue;
            if (++i >= raw.Length) throw Invalid();
            if (raw[i] != (byte)'u') continue;
            int code = Hex4(raw, i + 1); i += 4;
            if (code is >= 0xdc00 and <= 0xdfff) throw Invalid();
            if (code is >= 0xd800 and <= 0xdbff)
            {
                if (i + 6 >= raw.Length || raw[i + 1] != (byte)'\\' || raw[i + 2] != (byte)'u') throw Invalid();
                int low = Hex4(raw, i + 3);
                if (low is < 0xdc00 or > 0xdfff) throw Invalid();
                i += 6;
            }
        }
    }
    private static int Hex4(ReadOnlySpan<byte> raw, int start)
    {
        if (start > raw.Length - 4) throw Invalid();
        int code = 0;
        for (int i = start; i < start + 4; i++)
        {
            int nibble = raw[i] switch
            { >= (byte)'0' and <= (byte)'9' => raw[i] - '0', >= (byte)'a' and <= (byte)'f' => raw[i] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => raw[i] - 'A' + 10, _ => throw Invalid() };
            code = (code << 4) | nibble;
        }
        return code;
    }
    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
    }
    private static int Count(JsonElement value, int cap, int min = 0)
    {
        if (value.ValueKind != JsonValueKind.Array) throw Invalid();
        int count = value.GetArrayLength(); Cap(count, cap);
        if (count < min) throw Invalid();
        return count;
    }
    private static void ArrayCount<T>(ImmutableArray<T> array, int cap, int min = 0)
    { if (array.IsDefault || array.Length < min) throw Invalid(); Cap(array.Length, cap); }
    private static int RawBytes(JsonElement value, int cap)
    { int bytes = Utf8.GetByteCount(value.GetRawText()); Cap(bytes, cap); return bytes; }
    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
    private static string Text(JsonElement value, string field) => Text(value.GetProperty(field));
    private static string? OptionalText(JsonElement value, string field)
        => value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : Text(value, field);
    private static long Long(JsonElement value, string field)
    {
        JsonElement number = value.GetProperty(field);
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out long parsed)) throw Invalid();
        // TryGetInt64 is strict today; explicitly exclude fractional/exponent forms as wire contract.
        string text = number.GetRawText();
        if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0) throw Invalid();
        return parsed;
    }
    private static int Int(JsonElement value, string field)
    {
        long number = Long(value, field);
        if (number < int.MinValue || number > int.MaxValue) throw Invalid();
        return (int)number;
    }
    private static Guid Id(JsonElement value, string field) => CanonicalId(Text(value, field));
    private static Guid CanonicalId(string? value)
    {
        if (value is null || !Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || value != id.ToString("D")) throw Invalid();
        return id;
    }
    private static void Nonempty(Guid value) { if (value == Guid.Empty) throw Invalid(); }
    private static void Reference(LocalMediaRevisionReference value)
    { Nonempty(value.CollectionId.Value); Nonempty(value.ItemId.Value); Nonempty(value.RevisionId.Value); }
    private static void Role(LocalMediaRole role) { if (role != LocalMediaRole.Original) throw Error(LocalImportError.UnsupportedRole); }
    private static void Hash(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
    }
    private static void String(string? value, int cap, bool nonempty = false, bool noControls = false)
    {
        if (value is null || (nonempty && value.Length == 0)) throw Invalid();
        // Length is a cheap lower bound on UTF-8 bytes, before walking a huge string.
        Cap(value.Length, cap); Cap(Utf8.GetByteCount(value), cap);
        if (noControls && value.Any(char.IsControl)) throw Invalid();
    }
    private static string Slot(Guid op, LocalRevisionId revision, string directory)
    {
        Nonempty(op); Nonempty(revision.Value);
        return $"local-import-operations/{op:N}/{directory}/{revision.Value:N}.original.bin";
    }
    private static void Cap(long value, long cap) { if (value > cap) throw Error(LocalImportError.BudgetExceeded); }
    private static LocalImportCodecException Invalid() => Error(LocalImportError.InvalidSelection);
    private static LocalImportCodecException Error(LocalImportError error) => new(error);
    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (LocalImportCodecException) { throw; }
        catch (OverflowException) { throw Error(LocalImportError.BudgetExceeded); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        { throw Invalid(); }
    }
    private static byte[] Write(int cap, Action<Utf8JsonWriter> action)
    {
        using var output = new CappedBuffer(cap);
        using (var writer = new Utf8JsonWriter(output, WriterOptions)) { action(writer); writer.Flush(); }
        return output.ToArray();
    }

    private sealed class Digest : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal Digest(string domain) { _hash.AppendData(Utf8.GetBytes(domain)); _hash.AppendData(new byte[] { 0 }); }
        internal void Int(int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); _hash.AppendData(bytes); }
        internal void Long(long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); _hash.AppendData(bytes); }
        internal void Text(string value) { byte[] bytes = Utf8.GetBytes(value); Int(bytes.Length); _hash.AppendData(bytes); }
        internal void Hash(string value) => _hash.AppendData(Convert.FromHexString(value));
        internal void Presence(bool value) => _hash.AppendData(new[] { value ? (byte)1 : (byte)0 });
        internal void Optional(string? value) { Presence(value != null); if (value != null) Text(value); }
        internal string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public void Dispose() => _hash.Dispose();
    }
    private sealed class CappedBuffer(int cap) : IBufferWriter<byte>, IDisposable
    {
        private readonly MemoryStream _bytes = new();
        private byte[] _scratch = Array.Empty<byte>();
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < 0) throw Invalid();
            Cap(sizeHint, cap);
            int size = Math.Min(cap, Math.Max(4096, sizeHint));
            if (_scratch.Length < size) _scratch = new byte[size];
            return _scratch;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count)
        {
            if (count < 0 || count > _scratch.Length) throw Invalid();
            long length = checked(_bytes.Length + count); Cap(length, cap);
            if (_bytes.Capacity < length) _bytes.Capacity = (int)Math.Min(cap, Math.Max(length, Math.Max(4096L, 2L * _bytes.Capacity)));
            _bytes.Write(_scratch, 0, count);
        }
        internal byte[] ToArray() => _bytes.ToArray();
        public void Dispose() => _bytes.Dispose();
    }
}
