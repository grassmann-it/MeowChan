using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    // Keep the XAML ranges bound to the component contracts instead of duplicating
    // the numbers beside the controls. The same values are used by Config validation
    // and by the live reader/gallery stores.
    public int ReaderThumbnailWidthMinimum => Config.ReaderThumbnailWidthMinimum;
    public int ReaderThumbnailWidthMaximum => Config.ReaderThumbnailWidthMaximum;
    public int ReaderFoldLinesMinimum => Config.ReaderFoldLinesMinimum;
    public int ReaderFoldLinesMaximum => Config.ReaderFoldLinesMaximum;
    public int ReaderPreviewLinesMinimum => Config.ReaderPreviewLinesMinimum;
    public int ReaderPreviewLinesMaximum => Config.ReaderPreviewLinesMaximum;
    public int ReaderPreviewDelayMsMinimum => Config.ReaderPreviewDelayMsMinimum;
    public int ReaderPreviewDelayMsMaximum => Config.ReaderPreviewDelayMsMaximum;
    public int ReaderReplyStepMinimum => Config.ReaderReplyStepMinimum;
    public int ReaderReplyStepMaximum => Config.ReaderReplyStepMaximum;
    public int ReaderReplyLinesMinimum => Config.ReaderReplyLinesMinimum;
    public int ReaderReplyLinesMaximum => Config.ReaderReplyLinesMaximum;
    public int ReaderPageSizeMinimum => Config.ReaderPageSizeMinimum;
    public int ReaderPageSizeMaximum => Config.ReaderPageSizeMaximum;
    public int ReaderThreadingDepthMinimum => Config.ReaderThreadingDepthMinimum;
    public int ReaderThreadingDepthMaximum => Config.ReaderThreadingDepthMaximum;
    public int ReadingHistoryLimitMinimum => Config.ReadingHistoryLimitMinimum;
    public int ReadingHistoryLimitMaximum => Config.ReadingHistoryLimitMaximum;
    public int ReaderTextlessMediaWidthMinimum => Config.ReaderTextlessMediaWidthMinimum;
    public int ReaderTextlessMediaWidthMaximum => Config.ReaderTextlessMediaWidthMaximum;
    public int ReaderFocusedMediaScalePercentMinimum => Config.ReaderFocusedMediaScalePercentMinimum;
    public int ReaderFocusedMediaScalePercentMaximum => Config.ReaderFocusedMediaScalePercentMaximum;
    public int ReaderHoverEnlargeSizeMinimum => Config.ReaderHoverEnlargeSizeMinimum;
    public int ReaderHoverEnlargeSizeMaximum => Config.ReaderHoverEnlargeSizeMaximum;
    public int ReaderHoverEnlargeDelayMsMinimum => Config.ReaderHoverEnlargeDelayMsMinimum;
    public int ReaderHoverEnlargeDelayMsMaximum => Config.ReaderHoverEnlargeDelayMsMaximum;
    public int GalleryFitMaxUpscalePercentMinimum => Config.GalleryFitMaxUpscalePercentMinimum;
    public int GalleryFitMaxUpscalePercentMaximum => Config.GalleryFitMaxUpscalePercentMaximum;
    public int PostFilterRuleLimitMinimum => Config.PostFilterRuleLimitMinimum;
    public int PostFilterRuleLimitMaximum => Config.PostFilterRuleLimitMaximum;
    public int HiddenPostLimitMinimum => Config.HiddenPostLimitMinimum;
    public int HiddenPostLimitMaximum => Config.HiddenPostLimitMaximum;
    public string ReaderDisplayTitle => Texts.T("settings.reader.display");
    public string ReaderLongPostsTitle => Texts.T("settings.reader.longPosts");
    private decimal? _readerThumbnailWidth;
    public decimal? ReaderThumbnailWidth { get => _readerThumbnailWidth; set => SetField(ref _readerThumbnailWidth, value, nameof(ReaderThumbnailWidth)); }
    public string ReaderThumbnailWidthLabel => Texts.T("settings.reader.thumbnailWidth");
    public string ReaderThumbnailWidthHint => Texts.T("settings.reader.thumbnailWidth.hint") + " " + Texts.T("settings.reader.range", Config.ReaderThumbnailWidthMinimum, Config.ReaderThumbnailWidthMaximum);
    public string ReaderThumbnailWidthHelp => FieldHelp(nameof(ReaderThumbnailWidthInput), ReaderThumbnailWidthHint);
    private decimal? _readerFoldLines;
    public decimal? ReaderFoldLines { get => _readerFoldLines; set => SetField(ref _readerFoldLines, value, nameof(ReaderFoldLines)); }
    public string ReaderFoldLinesLabel => Texts.T("settings.reader.foldLines");
    public string ReaderFoldLinesHint => Texts.T("settings.reader.foldLines.hint") + " " + Texts.T("settings.reader.range", Config.ReaderFoldLinesMinimum, Config.ReaderFoldLinesMaximum);
    public string ReaderFoldLinesHelp => FieldHelp(nameof(ReaderFoldLinesInput), ReaderFoldLinesHint);
    private decimal? _readerPreviewLines;
    public decimal? ReaderPreviewLines { get => _readerPreviewLines; set => SetField(ref _readerPreviewLines, value, nameof(ReaderPreviewLines)); }
    public string ReaderPreviewLinesLabel => Texts.T("settings.reader.previewLines");
    public string ReaderPreviewLinesHint => Texts.T("settings.reader.previewLines.hint") + " " + Texts.T("settings.reader.range", Config.ReaderPreviewLinesMinimum, Config.ReaderPreviewLinesMaximum);
    public string ReaderPreviewLinesHelp => FieldHelp(nameof(ReaderPreviewLinesInput), ReaderPreviewLinesHint);
    private decimal? _readerPreviewDelayMs;
    public decimal? ReaderPreviewDelayMs { get => _readerPreviewDelayMs; set => SetField(ref _readerPreviewDelayMs, value, nameof(ReaderPreviewDelayMs)); }
    public string ReaderPreviewDelayMsLabel => Texts.T("settings.reader.previewDelay");
    public string ReaderPreviewDelayMsHint => Texts.T("settings.reader.previewDelay.hint") + " " + Texts.T("settings.reader.range", Config.ReaderPreviewDelayMsMinimum, Config.ReaderPreviewDelayMsMaximum);
    public string ReaderPreviewDelayMsHelp => FieldHelp(nameof(ReaderPreviewDelayMsInput), ReaderPreviewDelayMsHint);
    private decimal? _readerReplyStep;
    public decimal? ReaderReplyStep { get => _readerReplyStep; set => SetField(ref _readerReplyStep, value, nameof(ReaderReplyStep)); }
    public string ReaderReplyStepLabel => Texts.T("settings.reader.replyStep");
    public string ReaderReplyStepHint => Texts.T("settings.reader.replyStep.hint") + " " + Texts.T("settings.reader.range", Config.ReaderReplyStepMinimum, Config.ReaderReplyStepMaximum);
    public string ReaderReplyStepHelp => FieldHelp(nameof(ReaderReplyStepInput), ReaderReplyStepHint);
    private decimal? _readerReplyLines;
    public decimal? ReaderReplyLines { get => _readerReplyLines; set => SetField(ref _readerReplyLines, value, nameof(ReaderReplyLines)); }
    public string ReaderReplyLinesLabel => Texts.T("settings.reader.replyLines");
    public string ReaderReplyLinesHint => Texts.T("settings.reader.replyLines.hint") + " " + Texts.T("settings.reader.range", Config.ReaderReplyLinesMinimum, Config.ReaderReplyLinesMaximum);
    public string ReaderReplyLinesHelp => FieldHelp(nameof(ReaderReplyLinesInput), ReaderReplyLinesHint);
    private decimal? _readerPageSize;
    public decimal? ReaderPageSize { get => _readerPageSize; set => SetField(ref _readerPageSize, value, nameof(ReaderPageSize)); }
    public string ReaderPageSizeLabel => Texts.T("settings.reader.pageSize");
    public string ReaderPageSizeHint => Texts.T("settings.reader.pageSize.hint") + " " + Texts.T("settings.reader.range", Config.ReaderPageSizeMinimum, Config.ReaderPageSizeMaximum);
    public string ReaderPageSizeHelp => FieldHelp(nameof(ReaderPageSizeInput), ReaderPageSizeHint);
    private decimal? _readerThreadingDepth;
    public decimal? ReaderThreadingDepth { get => _readerThreadingDepth; set => SetField(ref _readerThreadingDepth, value, nameof(ReaderThreadingDepth)); }
    public string ReaderThreadingDepthLabel => Texts.T("settings.reader.threadingDepth");
    public string ReaderThreadingDepthHint => Texts.T("settings.reader.threadingDepth.hint") + " " + Texts.T("settings.reader.range", Config.ReaderThreadingDepthMinimum, Config.ReaderThreadingDepthMaximum);
    public string ReaderThreadingDepthHelp => FieldHelp(nameof(ReaderThreadingDepthInput), ReaderThreadingDepthHint);
    private decimal? _galleryFitMaxUpscalePercent;
    public decimal? GalleryFitMaxUpscalePercent { get => _galleryFitMaxUpscalePercent; set => SetField(ref _galleryFitMaxUpscalePercent, value, nameof(GalleryFitMaxUpscalePercent)); }
    public string GalleryFitMaxUpscalePercentLabel => Texts.T("settings.gallery.fitUpscalePercent");
    public string GalleryFitMaxUpscalePercentHint => Texts.T("settings.gallery.fitUpscalePercent.hint") + " " + Texts.T("settings.reader.range", Config.GalleryFitMaxUpscalePercentMinimum, Config.GalleryFitMaxUpscalePercentMaximum);
    public string GalleryFitMaxUpscalePercentHelp => FieldHelp(nameof(GalleryFitMaxUpscalePercentInput), GalleryFitMaxUpscalePercentHint);
    private decimal? _postFilterRuleLimit;
    public decimal? PostFilterRuleLimit { get => _postFilterRuleLimit; set => SetField(ref _postFilterRuleLimit, value, nameof(PostFilterRuleLimit)); }
    public string PostFilterRuleLimitLabel => Texts.T("settings.limits.postFilterRules");
    public string PostFilterRuleLimitHint => Texts.T("settings.limits.postFilterRules.hint") + " " + Texts.T("settings.reader.range", Config.PostFilterRuleLimitMinimum, Config.PostFilterRuleLimitMaximum);
    public string PostFilterRuleLimitHelp => FieldHelp(nameof(PostFilterRuleLimitInput), PostFilterRuleLimitHint);
    private decimal? _hiddenPostLimit;
    public decimal? HiddenPostLimit { get => _hiddenPostLimit; set => SetField(ref _hiddenPostLimit, value, nameof(HiddenPostLimit)); }
    public string HiddenPostLimitLabel => Texts.T("settings.limits.hiddenPosts");
    public string HiddenPostLimitHint => Texts.T("settings.limits.hiddenPosts.hint") + " " + Texts.T("settings.reader.range", Config.HiddenPostLimitMinimum, Config.HiddenPostLimitMaximum);
    public string HiddenPostLimitHelp => FieldHelp(nameof(HiddenPostLimitInput), HiddenPostLimitHint);
    private decimal? _readingHistoryLimit;
    public decimal? ReadingHistoryLimit { get => _readingHistoryLimit; set => SetField(ref _readingHistoryLimit, value, nameof(ReadingHistoryLimit)); }
    public string ReadingHistoryLimitLabel => Texts.T("settings.reader.historyLimit");
    public string ReadingHistoryLimitHint => Texts.T("settings.reader.historyLimit.hint") + " " + Texts.T("settings.reader.range", Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum);
    public string ReadingHistoryLimitHelp => FieldHelp(nameof(ReadingHistoryLimitInput), ReadingHistoryLimitHint);
    private decimal? _readerTextlessMediaWidth;
    public decimal? ReaderTextlessMediaWidth { get => _readerTextlessMediaWidth; set => SetField(ref _readerTextlessMediaWidth, value, nameof(ReaderTextlessMediaWidth)); }
    public string ReaderTextlessMediaWidthLabel => Texts.T("settings.reader.textlessWidth");
    public string ReaderTextlessMediaWidthHint => Texts.T("settings.reader.textlessWidth.hint") + " " + Texts.T("settings.reader.range", Config.ReaderTextlessMediaWidthMinimum, Config.ReaderTextlessMediaWidthMaximum);
    public string ReaderTextlessMediaWidthHelp => FieldHelp(nameof(ReaderTextlessMediaWidthInput), ReaderTextlessMediaWidthHint);
    private decimal? _readerFocusedMediaScalePercent;
    public decimal? ReaderFocusedMediaScalePercent { get => _readerFocusedMediaScalePercent; set => SetField(ref _readerFocusedMediaScalePercent, value, nameof(ReaderFocusedMediaScalePercent)); }
    public string ReaderFocusedMediaScalePercentLabel => Texts.T("settings.reader.focusedScale");
    public string ReaderFocusedMediaScalePercentHint => Texts.T("settings.reader.focusedScale.hint") + " " + Texts.T("settings.reader.range", Config.ReaderFocusedMediaScalePercentMinimum, Config.ReaderFocusedMediaScalePercentMaximum);
    public string ReaderFocusedMediaScalePercentHelp => FieldHelp(nameof(ReaderFocusedMediaScalePercentInput), ReaderFocusedMediaScalePercentHint);
    private decimal? _readerHoverEnlargeSize;
    public decimal? ReaderHoverEnlargeSize { get => _readerHoverEnlargeSize; set => SetField(ref _readerHoverEnlargeSize, value, nameof(ReaderHoverEnlargeSize)); }
    public string ReaderHoverEnlargeSizeLabel => Texts.T("settings.reader.hoverSize");
    public string ReaderHoverEnlargeSizeHint => Texts.T("settings.reader.hoverSize.hint") + " " + Texts.T("settings.reader.range", Config.ReaderHoverEnlargeSizeMinimum, Config.ReaderHoverEnlargeSizeMaximum);
    public string ReaderHoverEnlargeSizeHelp => FieldHelp(nameof(ReaderHoverEnlargeSizeInput), ReaderHoverEnlargeSizeHint);
    private decimal? _readerHoverEnlargeDelayMs;
    public decimal? ReaderHoverEnlargeDelayMs { get => _readerHoverEnlargeDelayMs; set => SetField(ref _readerHoverEnlargeDelayMs, value, nameof(ReaderHoverEnlargeDelayMs)); }
    public string ReaderHoverEnlargeDelayMsLabel => Texts.T("settings.reader.hoverDelay");
    public string ReaderHoverEnlargeDelayMsHint => Texts.T("settings.reader.hoverDelay.hint") + " " + Texts.T("settings.reader.range", Config.ReaderHoverEnlargeDelayMsMinimum, Config.ReaderHoverEnlargeDelayMsMaximum);
    public string ReaderHoverEnlargeDelayMsHelp => FieldHelp(nameof(ReaderHoverEnlargeDelayMsInput), ReaderHoverEnlargeDelayMsHint);
    private bool _readerTextlessLargeImages;
    public bool ReaderTextlessLargeImages { get => _readerTextlessLargeImages; set => SetField(ref _readerTextlessLargeImages, value, nameof(ReaderTextlessLargeImages)); }
    public string ReaderTextlessLargeImagesLabel => Texts.T("settings.reader.textlessLarge");
    public string ReaderTextlessLargeImagesHint => Texts.T("settings.reader.textlessLarge.hint");
    private bool _readerFocusedMediaEnlarge;
    public bool ReaderFocusedMediaEnlarge { get => _readerFocusedMediaEnlarge; set => SetField(ref _readerFocusedMediaEnlarge, value, nameof(ReaderFocusedMediaEnlarge)); }
    public string ReaderFocusedMediaEnlargeLabel => Texts.T("settings.reader.focusedEnlarge");
    public string ReaderFocusedMediaEnlargeHint => Texts.T("settings.reader.focusedEnlarge.hint");
    private bool _readerHoverEnlarge;
    public bool ReaderHoverEnlarge { get => _readerHoverEnlarge; set => SetField(ref _readerHoverEnlarge, value, nameof(ReaderHoverEnlarge)); }
    public string ReaderHoverEnlargeLabel => Texts.T("settings.reader.hoverEnlarge");
    public string ReaderHoverEnlargeHint => Texts.T("settings.reader.hoverEnlarge.hint");
    private SettingsChoice? _selectedReaderDateStyle;
    public SettingsChoice? SelectedReaderDateStyle
    {
        get => _selectedReaderDateStyle;
        set
        {
            SetField(ref _selectedReaderDateStyle, value, nameof(SelectedReaderDateStyle));
        }
    }
    public IReadOnlyList<SettingsChoice> ReaderDateStyleChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string ReaderDateStyleLabel => Texts.T("settings.reader.dateStyle");
    public string ReaderDateStyleHint => Texts.T("settings.reader.dateStyle.hint");
    private SettingsChoice? _selectedReaderSpoilers;
    public SettingsChoice? SelectedReaderSpoilers
    {
        get => _selectedReaderSpoilers;
        set
        {
            SetField(ref _selectedReaderSpoilers, value, nameof(SelectedReaderSpoilers));
        }
    }
    public IReadOnlyList<SettingsChoice> ReaderSpoilersChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string ReaderSpoilersLabel => Texts.T("settings.reader.spoilers");
    public string ReaderSpoilersHint => Texts.T("settings.reader.spoilers.hint");
    private SettingsChoice? _selectedGalleryFitUpscale;
    public SettingsChoice? SelectedGalleryFitUpscale
    {
        get => _selectedGalleryFitUpscale;
        set
        {
            SetField(ref _selectedGalleryFitUpscale, value, nameof(SelectedGalleryFitUpscale));
            if (!_refreshingLanguageLabels) Raise(new[] { nameof(IsGalleryFitLimited) });
        }
    }
    public IReadOnlyList<SettingsChoice> GalleryFitUpscaleChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string GalleryFitUpscaleLabel => Texts.T("settings.gallery.fitUpscale");
    public string GalleryFitUpscaleHint => Texts.T("settings.gallery.fitUpscale.hint");
    public bool IsGalleryFitLimited => SelectedGalleryFitUpscale?.Value == "Limited";

    private void RefreshReaderChoices(string date, string spoilers, string upscale)
    {
        ReaderDateStyleChoices = new SettingsChoice[] { new("System", Texts.T("settings.reader.dateStyle.system")), new("Long", Texts.T("settings.reader.dateStyle.long")), new("SinceOp", Texts.T("settings.reader.dateStyle.sinceOp")), new("RelativeToNow", Texts.T("settings.reader.dateStyle.relative")) };
        Raise(new[] { nameof(ReaderDateStyleChoices) });
        SelectedReaderDateStyle = ReaderDateStyleChoices.FirstOrDefault(item => item.Value == date) ?? ReaderDateStyleChoices[0];
        ReaderSpoilersChoices = new SettingsChoice[] { new("Hide", Texts.T("settings.reader.spoilers.hide")), new("Hover", Texts.T("settings.reader.spoilers.hover")), new("Always", Texts.T("settings.reader.spoilers.always")) };
        Raise(new[] { nameof(ReaderSpoilersChoices) });
        SelectedReaderSpoilers = ReaderSpoilersChoices.FirstOrDefault(item => item.Value == spoilers) ?? ReaderSpoilersChoices[0];
        GalleryFitUpscaleChoices = new SettingsChoice[] { new("Unlimited", Texts.T("settings.gallery.fitUpscale.unlimited")), new("Limited", Texts.T("settings.gallery.fitUpscale.limited")) };
        Raise(new[] { nameof(GalleryFitUpscaleChoices) });
        SelectedGalleryFitUpscale = GalleryFitUpscaleChoices.FirstOrDefault(item => item.Value == upscale) ?? GalleryFitUpscaleChoices[0];
        Raise(new[] { nameof(IsGalleryFitLimited) });
    }

    private void RefreshReaderChoiceLabels()
    {
        // Snapshot all semantic values before ItemsSource replacement emits transient null selections.
        string date = SelectedReaderDateStyle?.Value ?? _config.ReaderDateStyle;
        string spoilers = SelectedReaderSpoilers?.Value ?? _config.ReaderSpoilers;
        string upscale = SelectedGalleryFitUpscale?.Value ?? (_config.GalleryFitUpscaleLimited ? "Limited" : "Unlimited");
        RefreshReaderChoices(date, spoilers, upscale);
    }

    private void LoadReaderDraft(Config config)
    {
        ReaderThumbnailWidth = config.ReaderThumbnailWidth;
        ResetNumber(ReaderThumbnailWidthInput, ReaderThumbnailWidth.Value);
        ReaderFoldLines = config.ReaderFoldLines;
        ResetNumber(ReaderFoldLinesInput, ReaderFoldLines.Value);
        ReaderPreviewLines = config.ReaderPreviewLines;
        ResetNumber(ReaderPreviewLinesInput, ReaderPreviewLines.Value);
        ReaderPreviewDelayMs = config.ReaderPreviewDelayMs;
        ResetNumber(ReaderPreviewDelayMsInput, ReaderPreviewDelayMs.Value);
        ReaderReplyStep = config.ReaderReplyStep;
        ResetNumber(ReaderReplyStepInput, ReaderReplyStep.Value);
        ReaderReplyLines = config.ReaderReplyLines;
        ResetNumber(ReaderReplyLinesInput, ReaderReplyLines.Value);
        ReaderPageSize = config.ReaderPageSize;
        ResetNumber(ReaderPageSizeInput, ReaderPageSize.Value);
        ReaderThreadingDepth = config.ReaderThreadingDepth;
        ResetNumber(ReaderThreadingDepthInput, ReaderThreadingDepth.Value);
        GalleryFitMaxUpscalePercent = config.GalleryFitMaxUpscalePercent;
        ResetNumber(GalleryFitMaxUpscalePercentInput, GalleryFitMaxUpscalePercent.Value);
        PostFilterRuleLimit = config.PostFilterRuleLimit;
        ResetNumber(PostFilterRuleLimitInput, PostFilterRuleLimit.Value);
        HiddenPostLimit = config.HiddenPostLimit;
        ResetNumber(HiddenPostLimitInput, HiddenPostLimit.Value);
        ReadingHistoryLimit = config.ReadingHistoryLimit;
        ResetNumber(ReadingHistoryLimitInput, ReadingHistoryLimit.Value);
        ReaderTextlessMediaWidth = config.ReaderTextlessMediaWidth;
        ResetNumber(ReaderTextlessMediaWidthInput, ReaderTextlessMediaWidth.Value);
        ReaderFocusedMediaScalePercent = config.ReaderFocusedMediaScalePercent;
        ResetNumber(ReaderFocusedMediaScalePercentInput, ReaderFocusedMediaScalePercent.Value);
        ReaderHoverEnlargeSize = config.ReaderHoverEnlargeSize;
        ResetNumber(ReaderHoverEnlargeSizeInput, ReaderHoverEnlargeSize.Value);
        ReaderHoverEnlargeDelayMs = config.ReaderHoverEnlargeDelayMs;
        ResetNumber(ReaderHoverEnlargeDelayMsInput, ReaderHoverEnlargeDelayMs.Value);
        ReaderTextlessLargeImages = config.ReaderTextlessLargeImages;
        ReaderFocusedMediaEnlarge = config.ReaderFocusedMediaEnlarge;
        ReaderHoverEnlarge = config.ReaderHoverEnlarge;
        RefreshReaderChoices(config.ReaderDateStyle, config.ReaderSpoilers, config.GalleryFitUpscaleLimited ? "Limited" : "Unlimited");
    }

    /// <summary>
    /// The three reader media switches are also quick toggles of the reader's View menu. After one of them changed there, the draft shows
    /// the confirmed value again (an unsaved edit of the same switch is replaced; every other field of the draft stays).
    /// </summary>
    internal void RefreshConfirmedReaderMedia()
    {
        if (_isClosed) return;
        ReaderTextlessLargeImages = _config.ReaderTextlessLargeImages;
        ReaderFocusedMediaEnlarge = _config.ReaderFocusedMediaEnlarge;
        ReaderHoverEnlarge = _config.ReaderHoverEnlarge;
    }

    private void ApplyReaderDraft(Config candidate)
    {
        candidate.ReaderThumbnailWidth = GetInteger(ReaderThumbnailWidth, ReaderThumbnailWidthLabel, Config.ReaderThumbnailWidthMinimum, Config.ReaderThumbnailWidthMaximum);
        candidate.ReaderFoldLines = GetInteger(ReaderFoldLines, ReaderFoldLinesLabel, Config.ReaderFoldLinesMinimum, Config.ReaderFoldLinesMaximum);
        candidate.ReaderPreviewLines = GetInteger(ReaderPreviewLines, ReaderPreviewLinesLabel, Config.ReaderPreviewLinesMinimum, Config.ReaderPreviewLinesMaximum);
        candidate.ReaderPreviewDelayMs = GetInteger(ReaderPreviewDelayMs, ReaderPreviewDelayMsLabel, Config.ReaderPreviewDelayMsMinimum, Config.ReaderPreviewDelayMsMaximum);
        candidate.ReaderReplyStep = GetInteger(ReaderReplyStep, ReaderReplyStepLabel, Config.ReaderReplyStepMinimum, Config.ReaderReplyStepMaximum);
        candidate.ReaderReplyLines = GetInteger(ReaderReplyLines, ReaderReplyLinesLabel, Config.ReaderReplyLinesMinimum, Config.ReaderReplyLinesMaximum);
        candidate.ReaderPageSize = GetInteger(ReaderPageSize, ReaderPageSizeLabel, Config.ReaderPageSizeMinimum, Config.ReaderPageSizeMaximum);
        candidate.ReaderThreadingDepth = GetInteger(ReaderThreadingDepth, ReaderThreadingDepthLabel, Config.ReaderThreadingDepthMinimum, Config.ReaderThreadingDepthMaximum);
        candidate.GalleryFitMaxUpscalePercent = CaptureGalleryFitCap();
        candidate.PostFilterRuleLimit = GetInteger(PostFilterRuleLimit, PostFilterRuleLimitLabel, Config.PostFilterRuleLimitMinimum, Config.PostFilterRuleLimitMaximum);
        candidate.HiddenPostLimit = GetInteger(HiddenPostLimit, HiddenPostLimitLabel, Config.HiddenPostLimitMinimum, Config.HiddenPostLimitMaximum);
        candidate.ReadingHistoryLimit = GetInteger(ReadingHistoryLimit, ReadingHistoryLimitLabel, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum);
        candidate.ReaderTextlessMediaWidth = GetInteger(ReaderTextlessMediaWidth, ReaderTextlessMediaWidthLabel, Config.ReaderTextlessMediaWidthMinimum, Config.ReaderTextlessMediaWidthMaximum);
        candidate.ReaderFocusedMediaScalePercent = GetInteger(ReaderFocusedMediaScalePercent, ReaderFocusedMediaScalePercentLabel, Config.ReaderFocusedMediaScalePercentMinimum, Config.ReaderFocusedMediaScalePercentMaximum);
        candidate.ReaderHoverEnlargeSize = GetInteger(ReaderHoverEnlargeSize, ReaderHoverEnlargeSizeLabel, Config.ReaderHoverEnlargeSizeMinimum, Config.ReaderHoverEnlargeSizeMaximum);
        candidate.ReaderHoverEnlargeDelayMs = GetInteger(ReaderHoverEnlargeDelayMs, ReaderHoverEnlargeDelayMsLabel, Config.ReaderHoverEnlargeDelayMsMinimum, Config.ReaderHoverEnlargeDelayMsMaximum);
        candidate.ReaderTextlessLargeImages = ReaderTextlessLargeImages;
        candidate.ReaderFocusedMediaEnlarge = ReaderFocusedMediaEnlarge;
        candidate.ReaderHoverEnlarge = ReaderHoverEnlarge;
        candidate.ReaderDateStyle = SelectedReaderDateStyle?.Value ?? _config.ReaderDateStyle;
        candidate.ReaderSpoilers = SelectedReaderSpoilers?.Value ?? _config.ReaderSpoilers;
        candidate.GalleryFitUpscaleLimited = IsGalleryFitLimited;
    }

    private int CaptureGalleryFitCap()
    {
        // Hidden malformed text must not block an unrelated save, but Value may
        // still contain a previous accepted edit. Preserve the saved cap in that
        // case, not that stale Value. A genuinely valid hidden draft still wins.
        if (!IsGalleryFitLimited && NumericDraftError(GalleryFitMaxUpscalePercentInput.Text,
            GalleryFitMaxUpscalePercentInput.ParsingNumberStyle, GalleryFitMaxUpscalePercentInput.NumberFormat,
            DataValidationErrors.GetHasErrors(GalleryFitMaxUpscalePercentInput), GalleryFitMaxUpscalePercent,
            Config.GalleryFitMaxUpscalePercentMinimum, Config.GalleryFitMaxUpscalePercentMaximum, true) != null)
            return _config.GalleryFitMaxUpscalePercent;
        return GetInteger(GalleryFitMaxUpscalePercent, GalleryFitMaxUpscalePercentLabel,
            Config.GalleryFitMaxUpscalePercentMinimum, Config.GalleryFitMaxUpscalePercentMaximum);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> ReaderNumericFields()
    {
        yield return (ReaderThumbnailWidthInput, ReaderThumbnailWidth, ReaderThumbnailWidthLabel, string.Empty, Config.ReaderThumbnailWidthMinimum, Config.ReaderThumbnailWidthMaximum, true);
        yield return (ReaderFoldLinesInput, ReaderFoldLines, ReaderFoldLinesLabel, string.Empty, Config.ReaderFoldLinesMinimum, Config.ReaderFoldLinesMaximum, true);
        yield return (ReaderPreviewLinesInput, ReaderPreviewLines, ReaderPreviewLinesLabel, string.Empty, Config.ReaderPreviewLinesMinimum, Config.ReaderPreviewLinesMaximum, true);
        yield return (ReaderPreviewDelayMsInput, ReaderPreviewDelayMs, ReaderPreviewDelayMsLabel, string.Empty, Config.ReaderPreviewDelayMsMinimum, Config.ReaderPreviewDelayMsMaximum, true);
        yield return (ReaderReplyStepInput, ReaderReplyStep, ReaderReplyStepLabel, string.Empty, Config.ReaderReplyStepMinimum, Config.ReaderReplyStepMaximum, true);
        yield return (ReaderReplyLinesInput, ReaderReplyLines, ReaderReplyLinesLabel, string.Empty, Config.ReaderReplyLinesMinimum, Config.ReaderReplyLinesMaximum, true);
        yield return (ReaderPageSizeInput, ReaderPageSize, ReaderPageSizeLabel, string.Empty, Config.ReaderPageSizeMinimum, Config.ReaderPageSizeMaximum, true);
        yield return (ReaderThreadingDepthInput, ReaderThreadingDepth, ReaderThreadingDepthLabel, string.Empty, Config.ReaderThreadingDepthMinimum, Config.ReaderThreadingDepthMaximum, true);
        yield return (GalleryFitMaxUpscalePercentInput, GalleryFitMaxUpscalePercent, GalleryFitMaxUpscalePercentLabel, string.Empty, Config.GalleryFitMaxUpscalePercentMinimum, Config.GalleryFitMaxUpscalePercentMaximum, true);
        yield return (PostFilterRuleLimitInput, PostFilterRuleLimit, PostFilterRuleLimitLabel, string.Empty, Config.PostFilterRuleLimitMinimum, Config.PostFilterRuleLimitMaximum, true);
        yield return (HiddenPostLimitInput, HiddenPostLimit, HiddenPostLimitLabel, string.Empty, Config.HiddenPostLimitMinimum, Config.HiddenPostLimitMaximum, true);
        yield return (ReadingHistoryLimitInput, ReadingHistoryLimit, ReadingHistoryLimitLabel, string.Empty, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum, true);
        yield return (ReaderTextlessMediaWidthInput, ReaderTextlessMediaWidth, ReaderTextlessMediaWidthLabel, string.Empty, Config.ReaderTextlessMediaWidthMinimum, Config.ReaderTextlessMediaWidthMaximum, true);
        yield return (ReaderFocusedMediaScalePercentInput, ReaderFocusedMediaScalePercent, ReaderFocusedMediaScalePercentLabel, string.Empty, Config.ReaderFocusedMediaScalePercentMinimum, Config.ReaderFocusedMediaScalePercentMaximum, true);
        yield return (ReaderHoverEnlargeSizeInput, ReaderHoverEnlargeSize, ReaderHoverEnlargeSizeLabel, string.Empty, Config.ReaderHoverEnlargeSizeMinimum, Config.ReaderHoverEnlargeSizeMaximum, true);
        yield return (ReaderHoverEnlargeDelayMsInput, ReaderHoverEnlargeDelayMs, ReaderHoverEnlargeDelayMsLabel, string.Empty, Config.ReaderHoverEnlargeDelayMsMinimum, Config.ReaderHoverEnlargeDelayMsMaximum, true);
    }

    private void RaiseReaderTextsChanged() => Raise(new[]
    {
        nameof(ReaderDisplayTitle), nameof(ReaderLongPostsTitle),
        nameof(ReaderThumbnailWidthLabel), nameof(ReaderThumbnailWidthHint), nameof(ReaderThumbnailWidthHelp),
        nameof(ReaderFoldLinesLabel), nameof(ReaderFoldLinesHint), nameof(ReaderFoldLinesHelp),
        nameof(ReaderPreviewLinesLabel), nameof(ReaderPreviewLinesHint), nameof(ReaderPreviewLinesHelp),
        nameof(ReaderPreviewDelayMsLabel), nameof(ReaderPreviewDelayMsHint), nameof(ReaderPreviewDelayMsHelp),
        nameof(ReaderReplyStepLabel), nameof(ReaderReplyStepHint), nameof(ReaderReplyStepHelp),
        nameof(ReaderReplyLinesLabel), nameof(ReaderReplyLinesHint), nameof(ReaderReplyLinesHelp),
        nameof(ReaderPageSizeLabel), nameof(ReaderPageSizeHint), nameof(ReaderPageSizeHelp),
        nameof(ReaderThreadingDepthLabel), nameof(ReaderThreadingDepthHint), nameof(ReaderThreadingDepthHelp),
        nameof(GalleryFitMaxUpscalePercentLabel), nameof(GalleryFitMaxUpscalePercentHint), nameof(GalleryFitMaxUpscalePercentHelp),
        nameof(PostFilterRuleLimitLabel), nameof(PostFilterRuleLimitHint), nameof(PostFilterRuleLimitHelp),
        nameof(HiddenPostLimitLabel), nameof(HiddenPostLimitHint), nameof(HiddenPostLimitHelp),
        nameof(ReadingHistoryLimitLabel), nameof(ReadingHistoryLimitHint), nameof(ReadingHistoryLimitHelp),
        nameof(ReaderTextlessMediaWidthLabel), nameof(ReaderTextlessMediaWidthHint), nameof(ReaderTextlessMediaWidthHelp),
        nameof(ReaderFocusedMediaScalePercentLabel), nameof(ReaderFocusedMediaScalePercentHint), nameof(ReaderFocusedMediaScalePercentHelp),
        nameof(ReaderHoverEnlargeSizeLabel), nameof(ReaderHoverEnlargeSizeHint), nameof(ReaderHoverEnlargeSizeHelp),
        nameof(ReaderHoverEnlargeDelayMsLabel), nameof(ReaderHoverEnlargeDelayMsHint), nameof(ReaderHoverEnlargeDelayMsHelp),
        nameof(ReaderTextlessLargeImagesLabel), nameof(ReaderTextlessLargeImagesHint),
        nameof(ReaderFocusedMediaEnlargeLabel), nameof(ReaderFocusedMediaEnlargeHint),
        nameof(ReaderHoverEnlargeLabel), nameof(ReaderHoverEnlargeHint),
        nameof(ReaderDateStyleLabel), nameof(ReaderDateStyleHint),
        nameof(ReaderSpoilersLabel), nameof(ReaderSpoilersHint),
        nameof(GalleryFitUpscaleLabel), nameof(GalleryFitUpscaleHint),
    });
    private void NotifyReaderHelpChanged() => Raise(new[]
    {
        nameof(ReaderThumbnailWidthHelp),
        nameof(ReaderFoldLinesHelp),
        nameof(ReaderPreviewLinesHelp),
        nameof(ReaderPreviewDelayMsHelp),
        nameof(ReaderReplyStepHelp),
        nameof(ReaderReplyLinesHelp),
        nameof(ReaderPageSizeHelp),
        nameof(ReaderThreadingDepthHelp),
        nameof(GalleryFitMaxUpscalePercentHelp),
        nameof(PostFilterRuleLimitHelp),
        nameof(HiddenPostLimitHelp),
        nameof(ReadingHistoryLimitHelp),
        nameof(ReaderTextlessMediaWidthHelp),
        nameof(ReaderFocusedMediaScalePercentHelp),
        nameof(ReaderHoverEnlargeSizeHelp),
        nameof(ReaderHoverEnlargeDelayMsHelp),
    });
}
