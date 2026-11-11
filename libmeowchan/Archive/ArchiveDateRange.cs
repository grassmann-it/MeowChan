using System;

namespace MeowChan.Archive;

/// <summary>Inclusive local calendar days; UTC bounds are deliberately not persisted here.</summary>
public sealed record ArchiveDateRange
{
    public ArchiveDateRange(DateOnly? startDate, DateOnly? endDate, string timeZoneId, string basis = "first-seen")
    {
        if (basis != "first-seen") throw new ArgumentException("Unsupported archive date basis.", nameof(basis));
        if (startDate is { Year: < 2 or > 9998 }) throw new ArgumentOutOfRangeException(nameof(startDate));
        if (endDate is { Year: < 2 or > 9998 }) throw new ArgumentOutOfRangeException(nameof(endDate));
        if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
            throw new ArgumentException("The start date must not follow the end date.", nameof(endDate));
        ArchiveDateResolver.FindZone(timeZoneId);
        StartDate = startDate;
        EndDate = endDate;
        TimeZoneId = timeZoneId;
        Basis = basis;
    }
    public DateOnly? StartDate { get; }
    public DateOnly? EndDate { get; }
    public string TimeZoneId { get; }
    public string Basis { get; }
}

public sealed record ResolvedDateWindow(DateTimeOffset? StartInclusive, DateTimeOffset? EndExclusive, string TimeZoneId);

/// <summary>An executable immutable request. Reuse this instance for page, retry and export.</summary>
public sealed class ArchiveSearchRequest
{
    private ArchiveSearchRequest(ArchiveSearchSpec spec, ResolvedDateWindow? dateWindow,
        MeowChan.Tags.ThreadTagMembership? tagMembership = null)
    {
        Spec = spec;
        DateWindow = dateWindow;
        TagMembership = tagMembership;
    }
    public ArchiveSearchSpec Spec { get; }
    public ResolvedDateWindow? DateWindow { get; }

    /// <summary>
    /// Frozen own-tag membership; non-null exactly when the definition has a tag filter. Every
    /// reader query made with this request applies it, so no consumer can widen the result.
    /// </summary>
    internal MeowChan.Tags.ThreadTagMembership? TagMembership { get; }

    /// <summary>A definition with a tag filter cannot be executed without a tag snapshot.</summary>
    public static ArchiveSearchRequest Create(ArchiveSearchSpec spec) => Create(spec, null);

    /// <summary>
    /// Binds a tag filter to this snapshot. Throws <see cref="ArchiveTagFilterException"/> when the
    /// index is unavailable or a referenced tag no longer exists; the filter is never dropped.
    /// </summary>
    internal static ArchiveSearchRequest Create(ArchiveSearchSpec spec, MeowChan.Tags.PrivateTagStoreSnapshot? tags)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new ArchiveSearchRequest(spec, ArchiveDateResolver.Resolve(spec.DateRange), Bind(spec, tags));
    }

    /// <summary>
    /// The same request against a newer tag snapshot, keeping the frozen date window. Returns this
    /// instance when nothing tag-related changed, so refreshes keep request identity.
    /// </summary>
    internal ArchiveSearchRequest WithTagSnapshot(MeowChan.Tags.PrivateTagStoreSnapshot? tags)
    {
        if (Spec.Tags == null || tags != null && TagMembership?.Revision == tags.Revision) return this;
        return new ArchiveSearchRequest(Spec, DateWindow, Bind(Spec, tags));
    }

    private static MeowChan.Tags.ThreadTagMembership? Bind(ArchiveSearchSpec spec, MeowChan.Tags.PrivateTagStoreSnapshot? tags)
    {
        if (spec.Tags == null) return null;
        if (tags == null) throw new ArchiveTagFilterException(ArchiveTagFilterError.TagsUnavailable);
        return MeowChan.Tags.ThreadTagMembership.TryCreate(spec.Tags, tags, out MeowChan.Tags.ThreadTagMembership? membership)
            ? membership : throw new ArchiveTagFilterException(ArchiveTagFilterError.UnknownTag);
    }
}

public static class ArchiveDateResolver
{
    public static string GetLocalTimeZoneId()
    {
        TimeZoneInfo local = TimeZoneInfo.Local;
        string id = local.Id;
        if (!local.HasIanaId)
        {
            if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out string? ianaId))
                throw new ArgumentException("The device time zone cannot be mapped to an IANA time zone. Local id: " + id + ".");
            id = ianaId;
        }
        try { FindZone(id); }
        // A broken install (missing or mismatched NodaTime.dll, stale bin) surfaces here as a
        // load failure that is unrelated to the zone itself; name the facts instead of hiding them.
        catch (Exception error) when (error is not ArgumentException)
        { throw new InvalidOperationException(DescribeDeviceZoneFailure(local, id, error), error); }
        return id;
    }

    internal static string DescribeDeviceZoneFailure(TimeZoneInfo local, string mappedId, Exception error) =>
        "The device time zone could not be resolved (local id '" + local.Id + "', IANA id '" + mappedId + "'; "
        + error.GetType().Name + ": " + error.Message + "; NodaTime location: " + NodaTimeLocation() + ").";

    private static string NodaTimeLocation()
    {
        try { return typeof(NodaTime.Instant).Assembly.Location is { Length: > 0 } path ? path : "bundled"; }
        catch (Exception) { return "unavailable"; }
    }

    public static ResolvedDateWindow? Resolve(ArchiveDateRange? range)
    {
        if (range == null || (!range.StartDate.HasValue && !range.EndDate.HasValue)) return null;
        FindZone(range.TimeZoneId);
        return new ResolvedDateWindow(range.StartDate.HasValue ? Boundary(range.StartDate.Value, range.TimeZoneId) : null,
            range.EndDate.HasValue ? Boundary(range.EndDate.Value.AddDays(1), range.TimeZoneId) : null, range.TimeZoneId);
    }

    internal static TimeZoneInfo FindZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('\0')) throw new ArgumentException("An IANA time zone is required.", nameof(id));
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("The IANA time zone is unavailable.", nameof(id), error); }
        // Windows IDs are not a portable persisted definition. IANA aliases, including UTC, are.
        if (!zone.HasIanaId && !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
            throw new ArgumentException("A portable IANA time zone ID is required.", nameof(id));
        if (NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) == null)
            throw new ArgumentException("The IANA time zone is unavailable.", nameof(id));
        return zone;
    }

    private static DateTimeOffset Boundary(DateOnly day, string zoneId)
    {
        // Use one bundled IANA database on every OS. Windows registry-derived
        // historical rules can move a gap by 1 ms or miss a whole skipped day.
        NodaTime.DateTimeZone zone = NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId)
            ?? throw new ArgumentException("The IANA time zone is unavailable.", nameof(zoneId));
        NodaTime.LocalDate local = new(day.Year, day.Month, day.Day);
        for (int skipped = 0; skipped < 3; skipped++, local = local.PlusDays(1))
        {
            try { return zone.AtStartOfDay(local).ToInstant().ToDateTimeOffset(); }
            catch (NodaTime.SkippedTimeException) { }
        }
        throw new ArgumentException("Cannot resolve this local calendar boundary.", nameof(day));
    }
}
