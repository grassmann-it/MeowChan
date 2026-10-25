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
    private ArchiveSearchRequest(ArchiveSearchSpec spec, ResolvedDateWindow? dateWindow)
    {
        Spec = spec;
        DateWindow = dateWindow;
    }
    public ArchiveSearchSpec Spec { get; }
    public ResolvedDateWindow? DateWindow { get; }
    public static ArchiveSearchRequest Create(ArchiveSearchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new ArchiveSearchRequest(spec, ArchiveDateResolver.Resolve(spec.DateRange));
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
                throw new ArgumentException("The device time zone cannot be mapped to an IANA time zone.");
            id = ianaId;
        }
        FindZone(id);
        return id;
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
