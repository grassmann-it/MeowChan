using System;

namespace MeowChan.Desktop;

/// <summary>How the reader paints spoilers: kept hidden until a click or Enter, shown while the pointer is over the post, or always shown.</summary>
internal enum ReaderSpoilerMode
{
    Hide = 0,
    Hover = 1,
    Always = 2
}

/// <summary>
/// Reader display values that used to be fixed constants. Every value has a default (today's behaviour), a minimum and a
/// maximum; the reader reads the clamped value through an internal property, so a value from a profile, an old file or a
/// future setting can never leave the range. The settings page maps its fields onto these constants.
/// </summary>
internal static class ReaderDisplayLimits
{
    /// <summary>A body over this many wrapped lines is folded until the post is expanded.</summary>
    internal const int FoldLinesDefault = 24;
    internal const int FoldLinesMinimum = 8;
    internal const int FoldLinesMaximum = 400;

    /// <summary>Lines of the quoted post shown by the hover preview.</summary>
    internal const int PreviewLinesDefault = 8;
    internal const int PreviewLinesMinimum = 3;
    internal const int PreviewLinesMaximum = 40;
    /// <summary>Characters the preview keeps per line (8 lines keep 420, as before).</summary>
    internal const double PreviewCharactersPerLine = 52.5;

    /// <summary>Milliseconds the pointer rests on a reference before the preview opens.</summary>
    internal const int PreviewDelayDefault = 300;
    internal const int PreviewDelayMinimum = 0;
    internal const int PreviewDelayMaximum = 2000;

    /// <summary>Reply cards shown at once under a post; "Show more replies" adds this many again.</summary>
    internal const int ReplyStepDefault = 5;
    internal const int ReplyStepMinimum = 1;
    internal const int ReplyStepMaximum = 50;

    /// <summary>Lines of a reply card's excerpt.</summary>
    internal const int ReplyLinesDefault = 4;
    internal const int ReplyLinesMinimum = 1;
    internal const int ReplyLinesMaximum = 20;
    /// <summary>Characters a reply card keeps per line (4 lines keep 260, as before).</summary>
    internal const double ReplyCharactersPerLine = 65;

    internal static int ClampFoldLines(int value) => Math.Clamp(value, FoldLinesMinimum, FoldLinesMaximum);
    internal static int ClampPreviewLines(int value) => Math.Clamp(value, PreviewLinesMinimum, PreviewLinesMaximum);
    internal static int ClampPreviewDelay(int value) => Math.Clamp(value, PreviewDelayMinimum, PreviewDelayMaximum);
    internal static int ClampReplyStep(int value) => Math.Clamp(value, ReplyStepMinimum, ReplyStepMaximum);
    internal static int ClampReplyLines(int value) => Math.Clamp(value, ReplyLinesMinimum, ReplyLinesMaximum);

    /// <summary>An undefined enum value (an old or damaged profile) falls back to hidden spoilers.</summary>
    internal static ReaderSpoilerMode ClampSpoilerMode(ReaderSpoilerMode value) =>
        Enum.IsDefined(value) ? value : ReaderSpoilerMode.Hide;

    /// <summary>The lines a folded body shows: three quarters of the threshold, rounded (24 shows 18).</summary>
    internal static int FoldVisibleLines(int foldLines) =>
        (int)Math.Round(ClampFoldLines(foldLines) * 0.75, MidpointRounding.AwayFromZero);

    /// <summary>The preview keeps this many characters for the given number of lines (the line limit grows the character limit with it).</summary>
    internal static int PreviewCharacters(int previewLines) =>
        (int)Math.Round(ClampPreviewLines(previewLines) * PreviewCharactersPerLine, MidpointRounding.AwayFromZero);

    internal static int ReplyCharacters(int replyLines) =>
        (int)Math.Round(ClampReplyLines(replyLines) * ReplyCharactersPerLine, MidpointRounding.AwayFromZero);
}

/// <summary>The pure rules of spoiler painting; <see cref="ReaderBodyTextBlock"/> only feeds them its state.</summary>
internal static class ReaderSpoilerPolicy
{
    /// <summary>The spoilers are painted readable: revealed for good, always shown by the mode, or shown by the pointer in hover mode.</summary>
    internal static bool Shown(ReaderSpoilerMode mode, bool revealed, bool hovered) =>
        revealed || mode == ReaderSpoilerMode.Always || (mode == ReaderSpoilerMode.Hover && hovered);

    /// <summary>A click on a spoiler keeps it shown for good, unless it already is or the mode shows everything anyway.</summary>
    internal static bool ClickReveals(ReaderSpoilerMode mode, bool revealed) =>
        !revealed && mode != ReaderSpoilerMode.Always;

    /// <summary>
    /// What a screen reader gets: the text with every spoiler run replaced by one neutral placeholder while the spoilers are
    /// hidden, the real text otherwise (the painted text only hides the pixels; the accessibility tree must hide the words too).
    /// </summary>
    internal static string AccessibleText(ReaderBodyDocument document, bool shown, string hiddenPlaceholder)
    {
        if (shown || !document.HasSpoilers) return document.Text;
        System.Text.StringBuilder text = new(document.Text.Length);
        bool inSpoiler = false;
        foreach (ReaderBodySegment segment in document.Segments)
        {
            if (segment.IsSpoiler)
            {
                if (!inSpoiler) text.Append(hiddenPlaceholder);
                inSpoiler = true;
                continue;
            }
            inSpoiler = false;
            text.Append(document.Text, segment.Start, segment.Length);
        }
        return text.ToString();
    }
}
