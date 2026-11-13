using System;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

// Reader values that used to be fixed constants. Each is an internal property that clamps to the range in
// ReaderDisplayLimits; the call sites keep their names. The settings page (a later package) maps its fields onto these.
public partial class ArchivePage
{
    private int _quotePreviewMaxLines = ReaderDisplayLimits.PreviewLinesDefault;
    private int _quotePreviewDelayMilliseconds = ReaderDisplayLimits.PreviewDelayDefault;
    private int _inlineReplyPageSize = ReaderDisplayLimits.ReplyStepDefault;
    private int _replyCardMaxLines = ReaderDisplayLimits.ReplyLinesDefault;
    private int _readerFoldLines = ReaderDisplayLimits.FoldLinesDefault;
    private ReaderSpoilerMode _readerSpoilerMode = ReaderSpoilerMode.Hide;

    /// <summary>Lines of the quoted post in the hover preview.</summary>
    internal int QuotePreviewMaxLines
    {
        get => _quotePreviewMaxLines;
        set => _quotePreviewMaxLines = ReaderDisplayLimits.ClampPreviewLines(value);
    }

    /// <summary>Characters of the hover preview; grows with <see cref="QuotePreviewMaxLines"/>.</summary>
    internal int QuotePreviewMaxCharacters => ReaderDisplayLimits.PreviewCharacters(_quotePreviewMaxLines);

    /// <summary>Milliseconds the pointer rests on a reference before the preview opens.</summary>
    internal int QuotePreviewDelayMilliseconds
    {
        get => _quotePreviewDelayMilliseconds;
        set
        {
            _quotePreviewDelayMilliseconds = ReaderDisplayLimits.ClampPreviewDelay(value);
            if (_quotePreviewTimer != null) _quotePreviewTimer.Interval = QuotePreviewDelay;
        }
    }

    internal TimeSpan QuotePreviewDelay => TimeSpan.FromMilliseconds(_quotePreviewDelayMilliseconds);

    /// <summary>Reply cards shown at once under a post; "Show more replies" adds this many again.</summary>
    internal int InlineReplyPageSize
    {
        get => _inlineReplyPageSize;
        set => _inlineReplyPageSize = ReaderDisplayLimits.ClampReplyStep(value);
    }

    /// <summary>Lines of a reply card's excerpt.</summary>
    internal int ReplyCardMaxLines
    {
        get => _replyCardMaxLines;
        set => _replyCardMaxLines = ReaderDisplayLimits.ClampReplyLines(value);
    }

    /// <summary>Characters of a reply card's excerpt; grows with <see cref="ReplyCardMaxLines"/>.</summary>
    internal int ReplyCardMaxCharacters => ReaderDisplayLimits.ReplyCharacters(_replyCardMaxLines);

    /// <summary>Posts over this many wrapped lines are folded (the reader shows three quarters of it). Reaches every body through inheritance.</summary>
    internal int ReaderFoldLinesPreference
    {
        get => _readerFoldLines;
        set
        {
            _readerFoldLines = ReaderDisplayLimits.ClampFoldLines(value);
            ArchivePosts.SetValue(ReaderBodyTextBlock.FoldThresholdLinesProperty, _readerFoldLines);
        }
    }

    /// <summary>How spoilers are painted in the reader. Reaches every body through inheritance.</summary>
    internal ReaderSpoilerMode ReaderSpoilerModePreference
    {
        get => _readerSpoilerMode;
        set
        {
            _readerSpoilerMode = ReaderDisplayLimits.ClampSpoilerMode(value);
            ArchivePosts.SetValue(ReaderBodyTextBlock.SpoilerModeProperty, _readerSpoilerMode);
        }
    }
}
