using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace MeowChan;

public partial class Config
{
    [JsonIgnore]
    internal BooruListLayoutSettings BooruListLayout { get; set; } = new();

    [JsonInclude]
    internal int BooruListPreviewWidth
    {
        get => BooruListLayout.PreviewWidth;
        set => BooruListLayout = BooruListLayout with { PreviewWidth = value };
    }

    [JsonInclude]
    internal int BooruListMinimumWidth
    {
        get => BooruListLayout.MinimumWidth;
        set => BooruListLayout = BooruListLayout with { MinimumWidth = value };
    }

    [JsonInclude]
    internal int BooruListMaximumWidth
    {
        get => BooruListLayout.MaximumWidth;
        set => BooruListLayout = BooruListLayout with { MaximumWidth = value };
    }

    [JsonInclude]
    internal int BooruListMediumFrom
    {
        get => BooruListLayout.MediumFrom;
        set => BooruListLayout = BooruListLayout with { MediumFrom = value };
    }

    [JsonInclude]
    internal int BooruListWideAbove
    {
        get => BooruListLayout.WideAbove;
        set => BooruListLayout = BooruListLayout with { WideAbove = value };
    }

    [JsonInclude]
    internal int BooruListNarrowPreviewHeight
    {
        get => BooruListLayout.NarrowPreviewHeight;
        set => BooruListLayout = BooruListLayout with { NarrowPreviewHeight = value };
    }

    [JsonInclude]
    internal int BooruListMediumPreviewHeight
    {
        get => BooruListLayout.MediumPreviewHeight;
        set => BooruListLayout = BooruListLayout with { MediumPreviewHeight = value };
    }

    [JsonInclude]
    internal int BooruListWidePreviewHeight
    {
        get => BooruListLayout.WidePreviewHeight;
        set => BooruListLayout = BooruListLayout with { WidePreviewHeight = value };
    }

    [JsonInclude]
    internal int BooruListNarrowRowHeight
    {
        get => BooruListLayout.NarrowRowHeight;
        set => BooruListLayout = BooruListLayout with { NarrowRowHeight = value };
    }

    [JsonInclude]
    internal int BooruListMediumRowHeight
    {
        get => BooruListLayout.MediumRowHeight;
        set => BooruListLayout = BooruListLayout with { MediumRowHeight = value };
    }

    [JsonInclude]
    internal int BooruListWideRowHeight
    {
        get => BooruListLayout.WideRowHeight;
        set => BooruListLayout = BooruListLayout with { WideRowHeight = value };
    }

    private void ValidateBooruListLayout()
    {
        if (BooruListLayout is null || BooruListLayout.InvalidField() is { })
            throw new ArgumentOutOfRangeException(nameof(BooruListLayout), "Booru list layout values are outside their supported ranges or do not fit together.");
    }
}

internal sealed record BooruListLayoutSettings
{
    internal const int WidthStep = 32;
    internal const int ActionHeight = 20;
    internal const int MinimumFrameAndActions = ActionHeight + 4;
    internal const int MinimumNarrowFrameAndTags = MinimumFrameAndActions + 48;

    public int PreviewWidth { get; init; } = 192;
    public int MinimumWidth { get; init; } = 128;
    public int MaximumWidth { get; init; } = 320;
    public int MediumFrom { get; init; } = 900;
    public int WideAbove { get; init; } = 1400;
    public int NarrowPreviewHeight { get; init; } = 104;
    public int MediumPreviewHeight { get; init; } = 128;
    public int WidePreviewHeight { get; init; } = 144;
    public int NarrowRowHeight { get; init; } = 248;
    public int MediumRowHeight { get; init; } = 200;
    public int WideRowHeight { get; init; } = 168;

    internal static BooruListLayoutSettings Default { get; } = new();

    internal (int Tier, int Width, int Height, int RowHeight) At(double viewport)
    {
        int tier = viewport < MediumFrom ? 0 : viewport <= WideAbove ? 1 : 2;
        int width = Math.Clamp(PreviewWidth + (tier - 1) * WidthStep, MinimumWidth, MaximumWidth);
        return tier switch
        {
            0 => (tier, width, NarrowPreviewHeight, NarrowRowHeight),
            1 => (tier, width, MediumPreviewHeight, MediumRowHeight),
            _ => (tier, width, WidePreviewHeight, WideRowHeight)
        };
    }

    // The settings form and the profile validator use the same bounds.
    internal sealed record Field(string Name, int Min, int Max, Func<BooruListLayoutSettings, int> Read,
        Func<BooruListLayoutSettings, int, BooruListLayoutSettings> Write);

    internal static Field[] Fields { get; } =
    [
        new(nameof(PreviewWidth), 128, 480, p => p.PreviewWidth, (p, x) => p with { PreviewWidth = x }),
        new(nameof(MinimumWidth), 128, 480, p => p.MinimumWidth, (p, x) => p with { MinimumWidth = x }),
        new(nameof(MaximumWidth), 128, 480, p => p.MaximumWidth, (p, x) => p with { MaximumWidth = x }),
        new(nameof(MediumFrom), 720, 1600, p => p.MediumFrom, (p, x) => p with { MediumFrom = x }),
        new(nameof(WideAbove), 960, 2560, p => p.WideAbove, (p, x) => p with { WideAbove = x }),
        new(nameof(NarrowPreviewHeight), 64, 320, p => p.NarrowPreviewHeight, (p, x) => p with { NarrowPreviewHeight = x }),
        new(nameof(MediumPreviewHeight), 64, 320, p => p.MediumPreviewHeight, (p, x) => p with { MediumPreviewHeight = x }),
        new(nameof(WidePreviewHeight), 64, 320, p => p.WidePreviewHeight, (p, x) => p with { WidePreviewHeight = x }),
        new(nameof(NarrowRowHeight), 160, 512, p => p.NarrowRowHeight, (p, x) => p with { NarrowRowHeight = x }),
        new(nameof(MediumRowHeight), 128, 512, p => p.MediumRowHeight, (p, x) => p with { MediumRowHeight = x }),
        new(nameof(WideRowHeight), 128, 512, p => p.WideRowHeight, (p, x) => p with { WideRowHeight = x })
    ];

    internal string? InvalidField()
    {
        Field? invalid = Fields.FirstOrDefault(field => field.Read(this) < field.Min || field.Read(this) > field.Max);
        if (invalid != null) return invalid.Name;
        if (MinimumWidth > MaximumWidth) return nameof(MinimumWidth);
        if (PreviewWidth < MinimumWidth || PreviewWidth > MaximumWidth) return nameof(PreviewWidth);
        if (MediumFrom >= WideAbove || MediumFrom - PreviewWidth < 320) return nameof(MediumFrom);
        if (NarrowPreviewHeight > MediumPreviewHeight) return nameof(MediumPreviewHeight);
        if (MediumPreviewHeight > WidePreviewHeight) return nameof(WidePreviewHeight);
        if (NarrowRowHeight < NarrowPreviewHeight + MinimumNarrowFrameAndTags) return nameof(NarrowRowHeight);
        if (MediumRowHeight < MediumPreviewHeight + MinimumFrameAndActions) return nameof(MediumRowHeight);
        if (WideRowHeight < WidePreviewHeight + MinimumFrameAndActions) return nameof(WideRowHeight);
        return null;
    }
}
