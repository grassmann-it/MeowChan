using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Booru;

internal enum BooruContentProfileState { Missing, Ready, Unsupported, Unreadable }

internal sealed record BooruContentProfileSnapshot(
    BooruContentProfileState State,
    BooruContentSettings Settings,
    BooruContentFilter Filter)
{
    internal long Revision => Filter.Revision;
    internal bool HasChoice => State == BooruContentProfileState.Ready && Settings.MaximumRating.HasValue;
}

internal sealed class BooruContentRevisionException(long expectedRevision, BooruContentProfileSnapshot current)
    : InvalidOperationException("The Booru content rules changed since they were read.")
{
    internal long ExpectedRevision { get; } = expectedRevision;
    internal long CurrentRevision => Current.Revision;
    internal BooruContentProfileSnapshot Current { get; } = current;
}

internal sealed class BooruContentRuleException(BooruContentRuleLocation rule)
    : ArgumentException("The Booru blacklist contains a rule this version cannot interpret.")
{
    internal BooruContentRuleLocation Rule { get; } = rule;
}

/// <summary>Reads the profile's Booru content rules and saves a confirmed choice under writer admission.</summary>
internal sealed class BooruContentProfileStore
{
    internal const string FileName = "booru-content.json";
    internal const int SchemaVersion = 1;
    // Bounds file parsing even if the profile has been modified outside MeowChan.
    private const int MaximumFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<BooruPostClaimRating>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly BooruContentSettings EmptySettings = new(null, "", Array.Empty<BooruContentSourceBlacklist>());

    private sealed record Document(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] long Revision,
        [property: JsonRequired] BooruContentSettings Settings);

    private readonly ProfileLease _lease;
    private readonly string _path;

    internal BooruContentProfileStore(ProfileLease lease)
    {
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
    }

    internal BooruContentProfileSnapshot Load() => _lease.RunExclusive(LoadCore);

    internal BooruContentProfileSnapshot Load(Action<BooruContentProfileSnapshot> onRead) =>
        _lease.RunExclusive(() => LoadObserved(onRead));

    internal BooruContentProfileSnapshot SaveAdmitted(BooruContentSettings settings, long expectedRevision,
        ProfileAdmissionToken admission, CancellationToken cancellationToken = default) =>
        SaveAdmitted(settings, expectedRevision, admission, null, null, cancellationToken);

    internal BooruContentProfileSnapshot SaveAdmitted(BooruContentSettings settings, long expectedRevision,
        ProfileAdmissionToken admission, Action<BooruContentProfileSnapshot>? onRead, Action<BooruContentProfileSnapshot>? onSaved,
        CancellationToken cancellationToken = default) => _lease.RunStoreWrite(admission, () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        BooruContentProfileSnapshot current = LoadObserved(onRead);
        if (current.State is not (BooruContentProfileState.Missing or BooruContentProfileState.Ready))
            throw new InvalidOperationException("The current Booru content document is read-only.");
        if (current.Revision != expectedRevision)
            throw new BooruContentRevisionException(expectedRevision, current);

        // Copy the collection before validation and compilation so the saved and published rules agree.
        BooruContentSettings confirmed = CopyAndValidate(settings);
        BooruContentProfileSnapshot next = Snapshot(BooruContentProfileState.Ready, checked(current.Revision + 1), confirmed);
        if (next.Filter.FirstInvalidRule is BooruContentRuleLocation invalidRule)
            throw new BooruContentRuleException(invalidRule);

        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Document(SchemaVersion, next.Revision, confirmed), JsonOptions);
        if (data.Length > MaximumFileBytes)
            throw new InvalidDataException("The Booru content document is too large.");

        _lease.EnsureDirectoryIdentity();
        AtomicJsonFile.ReplaceUnderLease(_path, data, _lease.EnsureDirectoryIdentity, cancellationToken);
        onSaved?.Invoke(next);
        return next;
    });

    private BooruContentProfileSnapshot LoadObserved(Action<BooruContentProfileSnapshot>? onRead)
    {
        BooruContentProfileSnapshot snapshot = LoadCore();
        onRead?.Invoke(snapshot);
        return snapshot;
    }

    private BooruContentProfileSnapshot LoadCore()
    {
        try
        {
            using FileStream stream = RegularProfileFile.OpenRead(_path);
            long length = stream.Length;
            if (length is <= 0 or > MaximumFileBytes) return Snapshot(BooruContentProfileState.Unreadable);
            byte[] data = new byte[(int)length];
            stream.ReadExactly(data);
            if (stream.ReadByte() != -1 || stream.Length != length || JsonDuplicateProperties.Any(data))
                return Snapshot(BooruContentProfileState.Unreadable);

            using JsonDocument json = JsonDocument.Parse(data);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("schemaVersion", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.Number)
                return Snapshot(BooruContentProfileState.Unreadable);
            if (!schema.TryGetInt32(out int version) || version != SchemaVersion)
                return Snapshot(BooruContentProfileState.Unsupported);

            Document? document = json.Deserialize<Document>(JsonOptions);
            if (document is null || document.Revision <= 0) return Snapshot(BooruContentProfileState.Unreadable);
            return Snapshot(BooruContentProfileState.Ready, document.Revision, CopyAndValidate(document.Settings));
        }
        catch (FileNotFoundException)
        {
            return Snapshot(BooruContentProfileState.Missing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return Snapshot(BooruContentProfileState.Unreadable);
        }
    }

    private static BooruContentProfileSnapshot Snapshot(BooruContentProfileState state, long revision = 0,
        BooruContentSettings? settings = null)
    {
        BooruContentSettings rules = settings ?? EmptySettings;
        return new(state, rules, new(revision, rules, state is BooruContentProfileState.Missing or BooruContentProfileState.Ready));
    }

    private static BooruContentSettings CopyAndValidate(BooruContentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaximumRating is not BooruPostClaimRating maximum || !BooruContentFilter.IsKnownRating(maximum)
            || settings.GlobalBlacklist is null || settings.SourceBlacklists is null)
            throw new ArgumentException("The Booru content settings need an explicit rating choice and blacklist collections.",
                nameof(settings));

        BooruContentSourceBlacklist[] sources = settings.SourceBlacklists.ToArray();
        HashSet<(string SourceId, string InstanceId)> scopes = new();
        foreach (BooruContentSourceBlacklist source in sources)
        {
            if (source is null || source.Blacklist is null || !BooruPostKey.IsValidScope(source.SourceId, source.InstanceId)
                || !scopes.Add((source.SourceId, source.InstanceId)))
                throw new ArgumentException("The Booru content settings contain an invalid or duplicate source scope.",
                    nameof(settings));
        }
        return settings with { SourceBlacklists = Array.AsReadOnly(sources) };
    }
}
