using System;
using System.Collections.Generic;
using System.Linq;

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
        TimeZoneInfo zone = FindZone(range.TimeZoneId);
        return new ResolvedDateWindow(range.StartDate.HasValue ? Boundary(range.StartDate.Value, zone) : null,
            range.EndDate.HasValue ? Boundary(range.EndDate.Value.AddDays(1), zone) : null, range.TimeZoneId);
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
        return zone;
    }

    private static DateTimeOffset Boundary(DateOnly day, TimeZoneInfo zone)
    {
        DateTime midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Enumerating actual offsets and checking the round trip also detects whole skipped days
        // (some TimeZoneInfo implementations do not classify these with IsInvalidTime).
        HashSet<TimeSpan> offsets = [zone.BaseUtcOffset];
        foreach (TimeZoneInfo.AdjustmentRule rule in zone.GetAdjustmentRules())
        {
            offsets.Add(zone.BaseUtcOffset + rule.BaseUtcOffsetDelta);
            offsets.Add(zone.BaseUtcOffset + rule.BaseUtcOffsetDelta + rule.DaylightDelta);
        }
        DateTimeOffset? Map(DateTime local)
        {
            DateTimeOffset? earliest = null;
            foreach (TimeSpan offset in offsets)
            {
                long ticks = local.Ticks - offset.Ticks;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
                DateTimeOffset utc = new(ticks, TimeSpan.Zero);
                if (TimeZoneInfo.ConvertTime(utc, zone).DateTime != local) continue;
                if (!earliest.HasValue || utc < earliest.Value) earliest = utc;
            }
            return earliest;
        }
        DateTimeOffset? exact = Map(midnight);
        if (exact.HasValue) return exact.Value; // Minimum UTC mapping includes the whole repeated midnight.
        DateTime invalid = midnight;
        DateTime valid = midnight;
        do
        {
            invalid = valid;
            valid = valid.AddHours(1);
            if (valid - midnight > TimeSpan.FromDays(3)) throw new ArgumentException("Cannot resolve this local calendar boundary.", nameof(day));
        } while (!Map(valid).HasValue);
        // Find the first valid tick, including historical transitions with second precision.
        long low = invalid.Ticks, high = valid.Ticks;
        while (high - low > 1)
        {
            long middle = low + (high - low) / 2;
            if (Map(new DateTime(middle, DateTimeKind.Unspecified)).HasValue) high = middle;
            else low = middle;
        }
        return Map(new DateTime(high, DateTimeKind.Unspecified))!.Value;
    }
}
