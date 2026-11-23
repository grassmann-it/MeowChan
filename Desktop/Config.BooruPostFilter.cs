using System;

namespace MeowChan;

public partial class Config
{
    /// <summary>Starting threshold when enabling a tab's local score filter; does not enable filtering.</summary>
    public int BooruPostFilterScore { get; set; } = BooruPostFilterScoreDefault;
    internal const int BooruPostFilterScoreDefault = 0;
    internal const int BooruPostFilterScoreMinimum = -1_000_000;
    internal const int BooruPostFilterScoreMaximum = 1_000_000;

    private void ValidateBooruPostFilter()
    {
        if (BooruPostFilterScore is < BooruPostFilterScoreMinimum or > BooruPostFilterScoreMaximum)
            throw new ArgumentOutOfRangeException(nameof(BooruPostFilterScore));
    }
}
