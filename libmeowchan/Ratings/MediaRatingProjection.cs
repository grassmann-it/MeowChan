namespace MeowChan.Ratings;
/// <summary>Session filter: 0 all, 1 unrated, 2..6 minimum 1..5. Unrated sorts last.</summary>
public static class MediaRatingProjection
{
    public static bool Matches(int? value, int filter) => filter switch
    {
        0 => true,
        1 => value == null,
        >= 2 and <= 6 => value >= filter - 1,
        _ => throw new System.ArgumentOutOfRangeException(nameof(filter))
    };
    public static int SortRank(int? value) => value ?? 0;
}
