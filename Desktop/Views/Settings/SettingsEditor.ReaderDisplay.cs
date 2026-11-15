using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;
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
    public int ReadingHistoryMenuEntriesMinimum => Config.ReadingHistoryMenuEntriesMinimum;
    public int ReadingHistoryMenuEntriesMaximum => Config.ReadingHistoryMenuEntriesMaximum;
    public int ReaderMediaSidebarWidthMinimum => Config.ReaderMediaSidebarWidthMinimum;
    public int ReaderMediaSidebarWidthMaximum => Config.ReaderMediaSidebarWidthMaximum;
    public int ReaderMediaSidebarMinimumWidthMinimum => Config.ReaderMediaSidebarMinimumWidthMinimum;
    public int ReaderMediaSidebarMinimumWidthMaximum => Config.ReaderMediaSidebarMinimumWidthMaximum;
    public int ReaderInlineVideoMaxPlayersMinimum => Config.ReaderInlineVideoMaxPlayersMinimum;
    public int ReaderInlineVideoMaxPlayersMaximum => Config.ReaderInlineVideoMaxPlayersMaximum;
    public int ReaderInlineVideoMaxFileMiBMinimum => Config.ReaderInlineVideoMaxFileMiBMinimum;
    public int ReaderInlineVideoMaxFileMiBMaximum => Config.ReaderInlineVideoMaxFileMiBMaximum;
    public int ReaderMediaStripLimitMinimum => Config.ReaderMediaStripLimitMinimum;
    public int ReaderMediaStripLimitMaximum => Config.ReaderMediaStripLimitMaximum;
    public int ReaderMediaGridTextLimitMinimum => Config.ReaderMediaGridTextLimitMinimum;
    public int ReaderMediaGridTextLimitMaximum => Config.ReaderMediaGridTextLimitMaximum;
    public int ReaderPreviewPrefetchPostsMinimum => Config.ReaderPreviewPrefetchPostsMinimum;
    public int ReaderPreviewPrefetchPostsMaximum => Config.ReaderPreviewPrefetchPostsMaximum;
    public int ReaderPreviewSizeMemoryMinimum => Config.ReaderPreviewSizeMemoryMinimum;
    public int ReaderPreviewSizeMemoryMaximum => Config.ReaderPreviewSizeMemoryMaximum;
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
    public int GalleryMaxZoomPercentMinimum => Config.GalleryMaxZoomPercentMinimum;
    public int GalleryMaxZoomPercentMaximum => Config.GalleryMaxZoomPercentMaximum;
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
    private decimal? _galleryMaxZoomPercent;
    public decimal? GalleryMaxZoomPercent { get => _galleryMaxZoomPercent; set => SetField(ref _galleryMaxZoomPercent, value, nameof(GalleryMaxZoomPercent)); }
    public string GalleryMaxZoomPercentLabel => Texts.T("settings.gallery.maxZoom");
    public string GalleryMaxZoomPercentHint => Texts.T("settings.gallery.maxZoom.hint") + " " + Texts.T("settings.reader.range", Config.GalleryMaxZoomPercentMinimum, Config.GalleryMaxZoomPercentMaximum);
    public string GalleryMaxZoomPercentHelp => FieldHelp(nameof(GalleryMaxZoomPercentInput), GalleryMaxZoomPercentHint);
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
    private decimal? _readingHistoryMenuEntries;
    public decimal? ReadingHistoryMenuEntries { get => _readingHistoryMenuEntries; set => SetField(ref _readingHistoryMenuEntries, value, nameof(ReadingHistoryMenuEntries)); }
    public string ReadingHistoryMenuEntriesLabel => Texts.T("settings.reader.historyMenuEntries");
    public string ReadingHistoryMenuEntriesHint => Texts.T("settings.reader.historyMenuEntries.hint") + " " + Texts.T("settings.reader.range", Config.ReadingHistoryMenuEntriesMinimum, Config.ReadingHistoryMenuEntriesMaximum);
    public string ReadingHistoryMenuEntriesHelp => FieldHelp(nameof(ReadingHistoryMenuEntriesInput), ReadingHistoryMenuEntriesHint);
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
    // The media sidebar of the large reader (R8): switch (also in the reader's View menu), side, width (also dragged) and the reader width below which it hides.
    private bool _readerMediaSidebar;
    public bool ReaderMediaSidebar { get => _readerMediaSidebar; set => SetField(ref _readerMediaSidebar, value, nameof(ReaderMediaSidebar)); }
    public string ReaderMediaSidebarLabel => Texts.T("settings.reader.mediaSidebar");
    public string ReaderMediaSidebarHint => Texts.T("settings.reader.mediaSidebar.hint");
    private SettingsChoice? _selectedReaderMediaSidebarScope;
    public SettingsChoice? SelectedReaderMediaSidebarScope
    {
        get => _selectedReaderMediaSidebarScope;
        set => SetField(ref _selectedReaderMediaSidebarScope, value, nameof(SelectedReaderMediaSidebarScope));
    }
    public IReadOnlyList<SettingsChoice> ReaderMediaSidebarScopeChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string ReaderMediaSidebarScopeLabel => Texts.T("settings.reader.mediaSidebarScope");
    public string ReaderMediaSidebarScopeHint => Texts.T("settings.reader.mediaSidebarScope.hint");
    private SettingsChoice? _selectedReaderMediaSidebarSide;
    public SettingsChoice? SelectedReaderMediaSidebarSide
    {
        get => _selectedReaderMediaSidebarSide;
        set => SetField(ref _selectedReaderMediaSidebarSide, value, nameof(SelectedReaderMediaSidebarSide));
    }
    public IReadOnlyList<SettingsChoice> ReaderMediaSidebarSideChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string ReaderMediaSidebarSideLabel => Texts.T("settings.reader.mediaSidebarSide");
    public string ReaderMediaSidebarSideHint => Texts.T("settings.reader.mediaSidebarSide.hint");
    private decimal? _readerMediaSidebarWidth;
    public decimal? ReaderMediaSidebarWidth { get => _readerMediaSidebarWidth; set => SetField(ref _readerMediaSidebarWidth, value, nameof(ReaderMediaSidebarWidth)); }
    public string ReaderMediaSidebarWidthLabel => Texts.T("settings.reader.mediaSidebarWidth");
    public string ReaderMediaSidebarWidthHint => Texts.T("settings.reader.mediaSidebarWidth.hint") + " " + Texts.T("settings.reader.range", Config.ReaderMediaSidebarWidthMinimum, Config.ReaderMediaSidebarWidthMaximum);
    public string ReaderMediaSidebarWidthHelp => FieldHelp(nameof(ReaderMediaSidebarWidthInput), ReaderMediaSidebarWidthHint);
    private decimal? _readerMediaSidebarMinimumWidth;
    public decimal? ReaderMediaSidebarMinimumWidth { get => _readerMediaSidebarMinimumWidth; set => SetField(ref _readerMediaSidebarMinimumWidth, value, nameof(ReaderMediaSidebarMinimumWidth)); }
    public string ReaderMediaSidebarMinimumWidthLabel => Texts.T("settings.reader.mediaSidebarMinimumWidth");
    public string ReaderMediaSidebarMinimumWidthHint => Texts.T("settings.reader.mediaSidebarMinimumWidth.hint") + " " + Texts.T("settings.reader.range", Config.ReaderMediaSidebarMinimumWidthMinimum, Config.ReaderMediaSidebarMinimumWidthMaximum);
    public string ReaderMediaSidebarMinimumWidthHelp => FieldHelp(nameof(ReaderMediaSidebarMinimumWidthInput), ReaderMediaSidebarMinimumWidthHint);
    // "Clicking a medium" (R10, merged with the old inline video switch): the sidebar, the post (videos play, images expand) or the gallery.
    private SettingsChoice? _selectedReaderMediaClick;
    public SettingsChoice? SelectedReaderMediaClick
    {
        get => _selectedReaderMediaClick;
        set => SetField(ref _selectedReaderMediaClick, value, nameof(SelectedReaderMediaClick));
    }
    public IReadOnlyList<SettingsChoice> ReaderMediaClickChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string ReaderMediaClickLabel => Texts.T("settings.reader.mediaClick");
    public string ReaderMediaClickHint => Texts.T("settings.reader.mediaClick.hint");
    // Muted autoplay (a second opt-in) and the two limits of the players in the reader.
    private bool _readerInlineVideoAutoplayMuted;
    public bool ReaderInlineVideoAutoplayMuted { get => _readerInlineVideoAutoplayMuted; set => SetField(ref _readerInlineVideoAutoplayMuted, value, nameof(ReaderInlineVideoAutoplayMuted)); }
    public string ReaderInlineVideoAutoplayMutedLabel => Texts.T("settings.reader.inlineVideoAutoplay");
    public string ReaderInlineVideoAutoplayMutedHint => Texts.T("settings.reader.inlineVideoAutoplay.hint");
    private bool _readerSidebarVideoAutoplay;
    public bool ReaderSidebarVideoAutoplay { get => _readerSidebarVideoAutoplay; set => SetField(ref _readerSidebarVideoAutoplay, value, nameof(ReaderSidebarVideoAutoplay)); }
    public string ReaderSidebarVideoAutoplayLabel => Texts.T("settings.reader.sidebarVideoAutoplay");
    public string ReaderSidebarVideoAutoplayHint => Texts.T("settings.reader.sidebarVideoAutoplay.hint");
    private bool _readerSidebarVideoLoop;
    public bool ReaderSidebarVideoLoop { get => _readerSidebarVideoLoop; set => SetField(ref _readerSidebarVideoLoop, value, nameof(ReaderSidebarVideoLoop)); }
    public string ReaderSidebarVideoLoopLabel => Texts.T("settings.reader.sidebarVideoLoop");
    public string ReaderSidebarVideoLoopHint => Texts.T("settings.reader.sidebarVideoLoop.hint");
    private bool _readerSidebarVideoStartMuted;
    public bool ReaderSidebarVideoStartMuted { get => _readerSidebarVideoStartMuted; set => SetField(ref _readerSidebarVideoStartMuted, value, nameof(ReaderSidebarVideoStartMuted)); }
    public string ReaderSidebarVideoStartMutedLabel => Texts.T("settings.reader.sidebarVideoMuted");
    public string ReaderSidebarVideoStartMutedHint => Texts.T("settings.reader.sidebarVideoMuted.hint");
    private bool _readerKeepImagesExpanded;
    public bool ReaderKeepImagesExpanded { get => _readerKeepImagesExpanded; set => SetField(ref _readerKeepImagesExpanded, value, nameof(ReaderKeepImagesExpanded)); }
    public string ReaderKeepImagesExpandedLabel => Texts.T("settings.reader.keepExpanded");
    public string ReaderKeepImagesExpandedHint => Texts.T("settings.reader.keepExpanded.hint");
    private decimal? _readerInlineVideoMaxPlayers;
    public decimal? ReaderInlineVideoMaxPlayers { get => _readerInlineVideoMaxPlayers; set => SetField(ref _readerInlineVideoMaxPlayers, value, nameof(ReaderInlineVideoMaxPlayers)); }
    public string ReaderInlineVideoMaxPlayersLabel => Texts.T("settings.reader.inlineVideoPlayers");
    public string ReaderInlineVideoMaxPlayersHint => Texts.T("settings.reader.inlineVideoPlayers.hint") + " " + Texts.T("settings.reader.range", Config.ReaderInlineVideoMaxPlayersMinimum, Config.ReaderInlineVideoMaxPlayersMaximum);
    public string ReaderInlineVideoMaxPlayersHelp => FieldHelp(nameof(ReaderInlineVideoMaxPlayersInput), ReaderInlineVideoMaxPlayersHint);
    private decimal? _readerInlineVideoMaxFileMiB;
    public decimal? ReaderInlineVideoMaxFileMiB { get => _readerInlineVideoMaxFileMiB; set => SetField(ref _readerInlineVideoMaxFileMiB, value, nameof(ReaderInlineVideoMaxFileMiB)); }
    public string ReaderInlineVideoMaxFileMiBLabel => Texts.T("settings.reader.inlineVideoMaxFile");
    public string ReaderInlineVideoMaxFileMiBHint => Texts.T("settings.reader.inlineVideoMaxFile.hint") + " " + Texts.T("settings.reader.range", Config.ReaderInlineVideoMaxFileMiBMinimum, Config.ReaderInlineVideoMaxFileMiBMaximum);
    public string ReaderInlineVideoMaxFileMiBHelp => FieldHelp(nameof(ReaderInlineVideoMaxFileMiBInput), ReaderInlineVideoMaxFileMiBHint);
    // Posts with several files (R11): the sidebar strip's size and the two-column grid of the inline layout.
    private decimal? _readerMediaStripLimit;
    public decimal? ReaderMediaStripLimit { get => _readerMediaStripLimit; set => SetField(ref _readerMediaStripLimit, value, nameof(ReaderMediaStripLimit)); }
    public string ReaderMediaStripLimitLabel => Texts.T("settings.reader.mediaStripLimit");
    public string ReaderMediaStripLimitHint => Texts.T("settings.reader.mediaStripLimit.hint") + " " + Texts.T("settings.reader.range", Config.ReaderMediaStripLimitMinimum, Config.ReaderMediaStripLimitMaximum);
    public string ReaderMediaStripLimitHelp => FieldHelp(nameof(ReaderMediaStripLimitInput), ReaderMediaStripLimitHint);
    private bool _readerMediaGrid;
    public bool ReaderMediaGrid { get => _readerMediaGrid; set => SetField(ref _readerMediaGrid, value, nameof(ReaderMediaGrid)); }
    public string ReaderMediaGridLabel => Texts.T("settings.reader.mediaGrid");
    public string ReaderMediaGridHint => Texts.T("settings.reader.mediaGrid.hint");
    private decimal? _readerMediaGridTextLimit;
    public decimal? ReaderMediaGridTextLimit { get => _readerMediaGridTextLimit; set => SetField(ref _readerMediaGridTextLimit, value, nameof(ReaderMediaGridTextLimit)); }
    public string ReaderMediaGridTextLimitLabel => Texts.T("settings.reader.mediaGridText");
    public string ReaderMediaGridTextLimitHint => Texts.T("settings.reader.mediaGridText.hint") + " " + Texts.T("settings.reader.range", Config.ReaderMediaGridTextLimitMinimum, Config.ReaderMediaGridTextLimitMaximum);
    public string ReaderMediaGridTextLimitHelp => FieldHelp(nameof(ReaderMediaGridTextLimitInput), ReaderMediaGridTextLimitHint);
    // Stable previews: how far ahead previews are decoded, and how many preview sizes the session remembers.
    private decimal? _readerPreviewPrefetchPosts;
    public decimal? ReaderPreviewPrefetchPosts { get => _readerPreviewPrefetchPosts; set => SetField(ref _readerPreviewPrefetchPosts, value, nameof(ReaderPreviewPrefetchPosts)); }
    public string ReaderPreviewPrefetchPostsLabel => Texts.T("settings.reader.previewPrefetch");
    public string ReaderPreviewPrefetchPostsHint => Texts.T("settings.reader.previewPrefetch.hint") + " " + Texts.T("settings.reader.range", Config.ReaderPreviewPrefetchPostsMinimum, Config.ReaderPreviewPrefetchPostsMaximum);
    public string ReaderPreviewPrefetchPostsHelp => FieldHelp(nameof(ReaderPreviewPrefetchPostsInput), ReaderPreviewPrefetchPostsHint);
    private decimal? _readerPreviewSizeMemory;
    public decimal? ReaderPreviewSizeMemory { get => _readerPreviewSizeMemory; set => SetField(ref _readerPreviewSizeMemory, value, nameof(ReaderPreviewSizeMemory)); }
    public string ReaderPreviewSizeMemoryLabel => Texts.T("settings.reader.previewSizeMemory");
    public string ReaderPreviewSizeMemoryHint => Texts.T("settings.reader.previewSizeMemory.hint") + " " + Texts.T("settings.reader.range", Config.ReaderPreviewSizeMemoryMinimum, Config.ReaderPreviewSizeMemoryMaximum);
    public string ReaderPreviewSizeMemoryHelp => FieldHelp(nameof(ReaderPreviewSizeMemoryInput), ReaderPreviewSizeMemoryHint);

    private void RefreshMediaClickChoices(string click)
    {
        ReaderMediaClickChoices = new SettingsChoice[]
        {
            new(ReaderMediaClickRules.SidebarKey, Texts.T("settings.reader.mediaClick.sidebar")),
            new(ReaderMediaClickRules.PostKey, Texts.T("settings.reader.mediaClick.post")),
            new(ReaderMediaClickRules.GalleryKey, Texts.T("settings.reader.mediaClick.gallery"))
        };
        Raise(new[] { nameof(ReaderMediaClickChoices) });
        SelectedReaderMediaClick = ReaderMediaClickChoices.FirstOrDefault(item => item.Value == click) ?? ReaderMediaClickChoices[0];
    }

    private void RefreshMediaSidebarScopeChoices(string scope)
    {
        ReaderMediaSidebarScopeChoices = new SettingsChoice[]
        {
            new(Config.ReaderMediaSidebarScopeLargeReader, Texts.T("settings.reader.mediaSidebarScope.large")),
            new(Config.ReaderMediaSidebarScopeAlways, Texts.T("settings.reader.mediaSidebarScope.always"))
        };
        Raise(new[] { nameof(ReaderMediaSidebarScopeChoices) });
        SelectedReaderMediaSidebarScope = ReaderMediaSidebarScopeChoices.FirstOrDefault(item => item.Value == scope) ?? ReaderMediaSidebarScopeChoices[0];
    }

    private void RefreshMediaSidebarSideChoices(string side)
    {
        ReaderMediaSidebarSideChoices = new SettingsChoice[]
        {
            new(Config.ReaderMediaSidebarSideLeft, Texts.T("settings.reader.mediaSidebarSide.left")),
            new(Config.ReaderMediaSidebarSideRight, Texts.T("settings.reader.mediaSidebarSide.right"))
        };
        Raise(new[] { nameof(ReaderMediaSidebarSideChoices) });
        SelectedReaderMediaSidebarSide = ReaderMediaSidebarSideChoices.FirstOrDefault(item => item.Value == side) ?? ReaderMediaSidebarSideChoices[0];
    }

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
        RefreshMediaSidebarSideChoices(SelectedReaderMediaSidebarSide?.Value ?? _config.ReaderMediaSidebarSide);
        RefreshMediaSidebarScopeChoices(SelectedReaderMediaSidebarScope?.Value ?? _config.ReaderMediaSidebarScope);
        RefreshMediaClickChoices(SelectedReaderMediaClick?.Value ?? ReaderMediaClickRules.ToKey(_config.EffectiveReaderMediaClick));
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
        GalleryMaxZoomPercent = config.GalleryMaxZoomPercent;
        ResetNumber(GalleryMaxZoomPercentInput, GalleryMaxZoomPercent.Value);
        PostFilterRuleLimit = config.PostFilterRuleLimit;
        ResetNumber(PostFilterRuleLimitInput, PostFilterRuleLimit.Value);
        HiddenPostLimit = config.HiddenPostLimit;
        ResetNumber(HiddenPostLimitInput, HiddenPostLimit.Value);
        ReadingHistoryLimit = config.ReadingHistoryLimit;
        ResetNumber(ReadingHistoryLimitInput, ReadingHistoryLimit.Value);
        ReadingHistoryMenuEntries = config.ReadingHistoryMenuEntries;
        ResetNumber(ReadingHistoryMenuEntriesInput, ReadingHistoryMenuEntries.Value);
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
        ReaderMediaSidebar = config.ReaderMediaSidebar;
        ReaderMediaSidebarWidth = config.ReaderMediaSidebarWidth;
        ResetNumber(ReaderMediaSidebarWidthInput, ReaderMediaSidebarWidth.Value);
        ReaderMediaSidebarMinimumWidth = config.ReaderMediaSidebarMinimumWidth;
        ResetNumber(ReaderMediaSidebarMinimumWidthInput, ReaderMediaSidebarMinimumWidth.Value);
        RefreshMediaClickChoices(ReaderMediaClickRules.ToKey(config.EffectiveReaderMediaClick));
        ReaderInlineVideoAutoplayMuted = config.ReaderInlineVideoAutoplayMuted;
        ReaderKeepImagesExpanded = config.ReaderKeepImagesExpanded;
        ReaderSidebarVideoAutoplay = config.ReaderSidebarVideoAutoplay;
        ReaderSidebarVideoLoop = config.ReaderSidebarVideoLoop;
        ReaderSidebarVideoStartMuted = config.ReaderSidebarVideoStartMuted;
        ReaderInlineVideoMaxPlayers = config.ReaderInlineVideoMaxPlayers;
        ResetNumber(ReaderInlineVideoMaxPlayersInput, ReaderInlineVideoMaxPlayers.Value);
        ReaderInlineVideoMaxFileMiB = config.ReaderInlineVideoMaxFileMiB;
        ResetNumber(ReaderInlineVideoMaxFileMiBInput, ReaderInlineVideoMaxFileMiB.Value);
        ReaderMediaStripLimit = config.ReaderMediaStripLimit;
        ResetNumber(ReaderMediaStripLimitInput, ReaderMediaStripLimit.Value);
        ReaderMediaGrid = config.ReaderMediaGrid;
        ReaderMediaGridTextLimit = config.ReaderMediaGridTextLimit;
        ResetNumber(ReaderMediaGridTextLimitInput, ReaderMediaGridTextLimit.Value);
        ReaderPreviewPrefetchPosts = config.ReaderPreviewPrefetchPosts;
        ResetNumber(ReaderPreviewPrefetchPostsInput, ReaderPreviewPrefetchPosts.Value);
        ReaderPreviewSizeMemory = config.ReaderPreviewSizeMemory;
        ResetNumber(ReaderPreviewSizeMemoryInput, ReaderPreviewSizeMemory.Value);
        RefreshReaderChoices(config.ReaderDateStyle, config.ReaderSpoilers, config.GalleryFitUpscaleLimited ? "Limited" : "Unlimited");
        RefreshMediaSidebarSideChoices(config.ReaderMediaSidebarSide);
        RefreshMediaSidebarScopeChoices(config.ReaderMediaSidebarScope);
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
        // The media sidebar's switch (View menu) and its dragged width (splitter in the reader).
        ReaderMediaSidebar = _config.ReaderMediaSidebar;
        // The archive view's switch chooses Always or Large reader only.
        if (SelectedReaderMediaSidebarScope?.Value != _config.ReaderMediaSidebarScope) RefreshMediaSidebarScopeChoices(_config.ReaderMediaSidebarScope);
        if (ReaderMediaSidebarWidth != _config.ReaderMediaSidebarWidth)
        {
            ReaderMediaSidebarWidth = _config.ReaderMediaSidebarWidth;
            ResetNumber(ReaderMediaSidebarWidthInput, ReaderMediaSidebarWidth.Value);
        }
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
        candidate.GalleryMaxZoomPercent = GetInteger(GalleryMaxZoomPercent, GalleryMaxZoomPercentLabel, Config.GalleryMaxZoomPercentMinimum, Config.GalleryMaxZoomPercentMaximum);
        candidate.PostFilterRuleLimit = GetInteger(PostFilterRuleLimit, PostFilterRuleLimitLabel, Config.PostFilterRuleLimitMinimum, Config.PostFilterRuleLimitMaximum);
        candidate.HiddenPostLimit = GetInteger(HiddenPostLimit, HiddenPostLimitLabel, Config.HiddenPostLimitMinimum, Config.HiddenPostLimitMaximum);
        candidate.ReadingHistoryLimit = GetInteger(ReadingHistoryLimit, ReadingHistoryLimitLabel, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum);
        candidate.ReadingHistoryMenuEntries = GetInteger(ReadingHistoryMenuEntries, ReadingHistoryMenuEntriesLabel, Config.ReadingHistoryMenuEntriesMinimum, Config.ReadingHistoryMenuEntriesMaximum);
        candidate.ReaderTextlessMediaWidth = GetInteger(ReaderTextlessMediaWidth, ReaderTextlessMediaWidthLabel, Config.ReaderTextlessMediaWidthMinimum, Config.ReaderTextlessMediaWidthMaximum);
        candidate.ReaderFocusedMediaScalePercent = GetInteger(ReaderFocusedMediaScalePercent, ReaderFocusedMediaScalePercentLabel, Config.ReaderFocusedMediaScalePercentMinimum, Config.ReaderFocusedMediaScalePercentMaximum);
        candidate.ReaderHoverEnlargeSize = GetInteger(ReaderHoverEnlargeSize, ReaderHoverEnlargeSizeLabel, Config.ReaderHoverEnlargeSizeMinimum, Config.ReaderHoverEnlargeSizeMaximum);
        candidate.ReaderHoverEnlargeDelayMs = GetInteger(ReaderHoverEnlargeDelayMs, ReaderHoverEnlargeDelayMsLabel, Config.ReaderHoverEnlargeDelayMsMinimum, Config.ReaderHoverEnlargeDelayMsMaximum);
        candidate.ReaderTextlessLargeImages = ReaderTextlessLargeImages;
        candidate.ReaderFocusedMediaEnlarge = ReaderFocusedMediaEnlarge;
        candidate.ReaderHoverEnlarge = ReaderHoverEnlarge;
        candidate.ReaderMediaSidebar = ReaderMediaSidebar;
        candidate.ReaderMediaSidebarScope = SelectedReaderMediaSidebarScope?.Value ?? _config.ReaderMediaSidebarScope;
        candidate.ReaderMediaSidebarSide = SelectedReaderMediaSidebarSide?.Value ?? _config.ReaderMediaSidebarSide;
        candidate.ReaderMediaSidebarWidth = GetInteger(ReaderMediaSidebarWidth, ReaderMediaSidebarWidthLabel, Config.ReaderMediaSidebarWidthMinimum, Config.ReaderMediaSidebarWidthMaximum);
        candidate.ReaderMediaSidebarMinimumWidth = GetInteger(ReaderMediaSidebarMinimumWidth, ReaderMediaSidebarMinimumWidthLabel, Config.ReaderMediaSidebarMinimumWidthMinimum, Config.ReaderMediaSidebarMinimumWidthMaximum);
        candidate.ChooseReaderMediaClick(ReaderMediaClickRules.FromKey(SelectedReaderMediaClick?.Value ?? ReaderMediaClickRules.ToKey(_config.EffectiveReaderMediaClick)));
        candidate.ReaderInlineVideoAutoplayMuted = ReaderInlineVideoAutoplayMuted;
        candidate.ReaderKeepImagesExpanded = ReaderKeepImagesExpanded;
        candidate.ReaderSidebarVideoAutoplay = ReaderSidebarVideoAutoplay;
        candidate.ReaderSidebarVideoLoop = ReaderSidebarVideoLoop;
        candidate.ReaderSidebarVideoStartMuted = ReaderSidebarVideoStartMuted;
        candidate.ReaderInlineVideoMaxPlayers = GetInteger(ReaderInlineVideoMaxPlayers, ReaderInlineVideoMaxPlayersLabel, Config.ReaderInlineVideoMaxPlayersMinimum, Config.ReaderInlineVideoMaxPlayersMaximum);
        candidate.ReaderInlineVideoMaxFileMiB = GetInteger(ReaderInlineVideoMaxFileMiB, ReaderInlineVideoMaxFileMiBLabel, Config.ReaderInlineVideoMaxFileMiBMinimum, Config.ReaderInlineVideoMaxFileMiBMaximum);
        candidate.ReaderMediaStripLimit = GetInteger(ReaderMediaStripLimit, ReaderMediaStripLimitLabel, Config.ReaderMediaStripLimitMinimum, Config.ReaderMediaStripLimitMaximum);
        candidate.ReaderMediaGrid = ReaderMediaGrid;
        candidate.ReaderMediaGridTextLimit = GetInteger(ReaderMediaGridTextLimit, ReaderMediaGridTextLimitLabel, Config.ReaderMediaGridTextLimitMinimum, Config.ReaderMediaGridTextLimitMaximum);
        candidate.ReaderPreviewPrefetchPosts = GetInteger(ReaderPreviewPrefetchPosts, ReaderPreviewPrefetchPostsLabel, Config.ReaderPreviewPrefetchPostsMinimum, Config.ReaderPreviewPrefetchPostsMaximum);
        candidate.ReaderPreviewSizeMemory = GetInteger(ReaderPreviewSizeMemory, ReaderPreviewSizeMemoryLabel, Config.ReaderPreviewSizeMemoryMinimum, Config.ReaderPreviewSizeMemoryMaximum);
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
        yield return (GalleryMaxZoomPercentInput, GalleryMaxZoomPercent, GalleryMaxZoomPercentLabel, string.Empty, Config.GalleryMaxZoomPercentMinimum, Config.GalleryMaxZoomPercentMaximum, true);
        yield return (PostFilterRuleLimitInput, PostFilterRuleLimit, PostFilterRuleLimitLabel, string.Empty, Config.PostFilterRuleLimitMinimum, Config.PostFilterRuleLimitMaximum, true);
        yield return (HiddenPostLimitInput, HiddenPostLimit, HiddenPostLimitLabel, string.Empty, Config.HiddenPostLimitMinimum, Config.HiddenPostLimitMaximum, true);
        yield return (ReadingHistoryLimitInput, ReadingHistoryLimit, ReadingHistoryLimitLabel, string.Empty, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum, true);
        yield return (ReadingHistoryMenuEntriesInput, ReadingHistoryMenuEntries, ReadingHistoryMenuEntriesLabel, string.Empty, Config.ReadingHistoryMenuEntriesMinimum, Config.ReadingHistoryMenuEntriesMaximum, true);
        yield return (ReaderTextlessMediaWidthInput, ReaderTextlessMediaWidth, ReaderTextlessMediaWidthLabel, string.Empty, Config.ReaderTextlessMediaWidthMinimum, Config.ReaderTextlessMediaWidthMaximum, true);
        yield return (ReaderFocusedMediaScalePercentInput, ReaderFocusedMediaScalePercent, ReaderFocusedMediaScalePercentLabel, string.Empty, Config.ReaderFocusedMediaScalePercentMinimum, Config.ReaderFocusedMediaScalePercentMaximum, true);
        yield return (ReaderHoverEnlargeSizeInput, ReaderHoverEnlargeSize, ReaderHoverEnlargeSizeLabel, string.Empty, Config.ReaderHoverEnlargeSizeMinimum, Config.ReaderHoverEnlargeSizeMaximum, true);
        yield return (ReaderHoverEnlargeDelayMsInput, ReaderHoverEnlargeDelayMs, ReaderHoverEnlargeDelayMsLabel, string.Empty, Config.ReaderHoverEnlargeDelayMsMinimum, Config.ReaderHoverEnlargeDelayMsMaximum, true);
        yield return (ReaderMediaSidebarWidthInput, ReaderMediaSidebarWidth, ReaderMediaSidebarWidthLabel, string.Empty, Config.ReaderMediaSidebarWidthMinimum, Config.ReaderMediaSidebarWidthMaximum, true);
        yield return (ReaderMediaSidebarMinimumWidthInput, ReaderMediaSidebarMinimumWidth, ReaderMediaSidebarMinimumWidthLabel, string.Empty, Config.ReaderMediaSidebarMinimumWidthMinimum, Config.ReaderMediaSidebarMinimumWidthMaximum, true);
        yield return (ReaderInlineVideoMaxPlayersInput, ReaderInlineVideoMaxPlayers, ReaderInlineVideoMaxPlayersLabel, string.Empty, Config.ReaderInlineVideoMaxPlayersMinimum, Config.ReaderInlineVideoMaxPlayersMaximum, true);
        yield return (ReaderInlineVideoMaxFileMiBInput, ReaderInlineVideoMaxFileMiB, ReaderInlineVideoMaxFileMiBLabel, string.Empty, Config.ReaderInlineVideoMaxFileMiBMinimum, Config.ReaderInlineVideoMaxFileMiBMaximum, true);
        yield return (ReaderMediaStripLimitInput, ReaderMediaStripLimit, ReaderMediaStripLimitLabel, string.Empty, Config.ReaderMediaStripLimitMinimum, Config.ReaderMediaStripLimitMaximum, true);
        yield return (ReaderMediaGridTextLimitInput, ReaderMediaGridTextLimit, ReaderMediaGridTextLimitLabel, string.Empty, Config.ReaderMediaGridTextLimitMinimum, Config.ReaderMediaGridTextLimitMaximum, true);
        yield return (ReaderPreviewPrefetchPostsInput, ReaderPreviewPrefetchPosts, ReaderPreviewPrefetchPostsLabel, string.Empty, Config.ReaderPreviewPrefetchPostsMinimum, Config.ReaderPreviewPrefetchPostsMaximum, true);
        yield return (ReaderPreviewSizeMemoryInput, ReaderPreviewSizeMemory, ReaderPreviewSizeMemoryLabel, string.Empty, Config.ReaderPreviewSizeMemoryMinimum, Config.ReaderPreviewSizeMemoryMaximum, true);
    }

    /// <summary>
    /// A quick settings popover saved the settings <paramref name="ids"/> (<see cref="SettingsFieldId"/>): their editors show the
    /// confirmed value again, so the page neither shows the old one nor saves it back over the change later. Only the named
    /// editors are touched; an unrelated unsaved draft stays. An id this page does not know reloads the whole reader draft.
    /// </summary>
    internal void RefreshConfirmedSettings(IReadOnlyCollection<string> ids)
    {
        if (_isClosed) return;
        bool reloadReader = false;
        foreach (string id in ids)
        {
            switch (id)
            {
                case SettingsFieldId.ReadingFontSize:
                    _initialReadingFontSize = _config.ReadingFontSize;
                    SelectedReadingFontSize = ReadingFontSizeChoices.FirstOrDefault(item => item.Value == _config.ReadingFontSize) ?? SelectedReadingFontSize;
                    break;
                case SettingsFieldId.ReaderPostWidth:
                    SelectedReaderPostWidth = ReaderPostWidthChoices.FirstOrDefault(item => item.Value == _config.ReaderPostWidth) ?? SelectedReaderPostWidth;
                    break;
                case SettingsFieldId.ReaderThumbnailWidth:
                    ReaderThumbnailWidth = _config.ReaderThumbnailWidth;
                    ResetNumber(ReaderThumbnailWidthInput, _config.ReaderThumbnailWidth);
                    break;
                case SettingsFieldId.ReaderPageSize:
                    ReaderPageSize = _config.ReaderPageSize;
                    ResetNumber(ReaderPageSizeInput, _config.ReaderPageSize);
                    break;
                case SettingsFieldId.ReaderDateStyle:
                    SelectedReaderDateStyle = ReaderDateStyleChoices.FirstOrDefault(item => item.Value == _config.ReaderDateStyle) ?? SelectedReaderDateStyle;
                    break;
                case SettingsFieldId.ReaderSpoilers:
                    SelectedReaderSpoilers = ReaderSpoilersChoices.FirstOrDefault(item => item.Value == _config.ReaderSpoilers) ?? SelectedReaderSpoilers;
                    break;
                // The extended reader popover shares these editors too. Confirm only the named
                // field; reloading the reader draft would discard unrelated unfinished edits.
                case SettingsFieldId.ReadingHistoryLimit:
                    ReadingHistoryLimit = _config.ReadingHistoryLimit;
                    ResetNumber(ReadingHistoryLimitInput, _config.ReadingHistoryLimit);
                    break;
                case SettingsFieldId.ReaderTextlessLargeImages:
                    ReaderTextlessLargeImages = _config.ReaderTextlessLargeImages;
                    break;
                case SettingsFieldId.ReaderTextlessMediaWidth:
                    ReaderTextlessMediaWidth = _config.ReaderTextlessMediaWidth;
                    ResetNumber(ReaderTextlessMediaWidthInput, _config.ReaderTextlessMediaWidth);
                    break;
                case SettingsFieldId.ReaderFocusedMediaEnlarge:
                    ReaderFocusedMediaEnlarge = _config.ReaderFocusedMediaEnlarge;
                    break;
                case SettingsFieldId.ReaderFocusedMediaScalePercent:
                    ReaderFocusedMediaScalePercent = _config.ReaderFocusedMediaScalePercent;
                    ResetNumber(ReaderFocusedMediaScalePercentInput, _config.ReaderFocusedMediaScalePercent);
                    break;
                case SettingsFieldId.ReaderHoverEnlarge:
                    ReaderHoverEnlarge = _config.ReaderHoverEnlarge;
                    break;
                case SettingsFieldId.ReaderHoverEnlargeSize:
                    ReaderHoverEnlargeSize = _config.ReaderHoverEnlargeSize;
                    ResetNumber(ReaderHoverEnlargeSizeInput, _config.ReaderHoverEnlargeSize);
                    break;
                case SettingsFieldId.ReaderHoverEnlargeDelayMs:
                    ReaderHoverEnlargeDelayMs = _config.ReaderHoverEnlargeDelayMs;
                    ResetNumber(ReaderHoverEnlargeDelayMsInput, _config.ReaderHoverEnlargeDelayMs);
                    break;
                case SettingsFieldId.ReaderMediaSidebar:
                    ReaderMediaSidebar = _config.ReaderMediaSidebar;
                    break;
                case SettingsFieldId.ReaderMediaSidebarScope:
                    RefreshMediaSidebarScopeChoices(_config.ReaderMediaSidebarScope);
                    break;
                case SettingsFieldId.ReaderMediaSidebarSide:
                    RefreshMediaSidebarSideChoices(_config.ReaderMediaSidebarSide);
                    break;
                case SettingsFieldId.ReaderMediaSidebarWidth:
                    ReaderMediaSidebarWidth = _config.ReaderMediaSidebarWidth;
                    ResetNumber(ReaderMediaSidebarWidthInput, _config.ReaderMediaSidebarWidth);
                    break;
                case SettingsFieldId.ReaderMediaSidebarMinimumWidth:
                    ReaderMediaSidebarMinimumWidth = _config.ReaderMediaSidebarMinimumWidth;
                    ResetNumber(ReaderMediaSidebarMinimumWidthInput, _config.ReaderMediaSidebarMinimumWidth);
                    break;
                case SettingsFieldId.ReaderMediaClick:
                    RefreshMediaClickChoices(ReaderMediaClickRules.ToKey(_config.EffectiveReaderMediaClick));
                    break;
                case SettingsFieldId.ReaderInlineVideoAutoplayMuted:
                    ReaderInlineVideoAutoplayMuted = _config.ReaderInlineVideoAutoplayMuted;
                    break;
                case SettingsFieldId.ReaderKeepImagesExpanded:
                    ReaderKeepImagesExpanded = _config.ReaderKeepImagesExpanded;
                    break;
                case SettingsFieldId.ReaderSidebarVideoAutoplay:
                    ReaderSidebarVideoAutoplay = _config.ReaderSidebarVideoAutoplay;
                    break;
                case SettingsFieldId.ReaderSidebarVideoLoop:
                    ReaderSidebarVideoLoop = _config.ReaderSidebarVideoLoop;
                    break;
                case SettingsFieldId.ReaderSidebarVideoStartMuted:
                    ReaderSidebarVideoStartMuted = _config.ReaderSidebarVideoStartMuted;
                    break;
                case SettingsFieldId.VideoStartMuted:
                    VideoStartMuted = _config.VideoStartMuted;
                    break;
                // The gallery's own settings, offered by the popover too: confirm the draft and its baseline (a later save changes only what
                // the page itself edited, so it never writes the old value back over the popover's change).
                case SettingsFieldId.VideoVolume:
                    _initialVideoVolume = _config.VideoVolume;
                    VideoVolume = _config.VideoVolume;
                    ResetNumber(VideoVolumeInput, _config.VideoVolume);
                    break;
                case SettingsFieldId.GalleryVideoRepeat:
                    _initialGalleryPlayback = _initialGalleryPlayback with { VideoRepeat = _config.GalleryVideoRepeat };
                    GalleryVideoRepeat = _config.GalleryVideoRepeat;
                    break;
                case SettingsFieldId.ReaderInlineVideoMaxPlayers:
                    ReaderInlineVideoMaxPlayers = _config.ReaderInlineVideoMaxPlayers;
                    ResetNumber(ReaderInlineVideoMaxPlayersInput, _config.ReaderInlineVideoMaxPlayers);
                    break;
                case SettingsFieldId.ReaderInlineVideoMaxFileMiB:
                    ReaderInlineVideoMaxFileMiB = _config.ReaderInlineVideoMaxFileMiB;
                    ResetNumber(ReaderInlineVideoMaxFileMiBInput, _config.ReaderInlineVideoMaxFileMiB);
                    break;
                case SettingsFieldId.ReaderMediaStripLimit:
                    ReaderMediaStripLimit = _config.ReaderMediaStripLimit;
                    ResetNumber(ReaderMediaStripLimitInput, _config.ReaderMediaStripLimit);
                    break;
                case SettingsFieldId.ReaderMediaGrid:
                    ReaderMediaGrid = _config.ReaderMediaGrid;
                    break;
                case SettingsFieldId.ReaderMediaGridTextLimit:
                    ReaderMediaGridTextLimit = _config.ReaderMediaGridTextLimit;
                    ResetNumber(ReaderMediaGridTextLimitInput, _config.ReaderMediaGridTextLimit);
                    break;
                default:
                    reloadReader = true;
                    break;
            }
        }
        if (reloadReader) LoadReaderDraft(_config);
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
        nameof(GalleryMaxZoomPercentLabel), nameof(GalleryMaxZoomPercentHint), nameof(GalleryMaxZoomPercentHelp),
        nameof(PostFilterRuleLimitLabel), nameof(PostFilterRuleLimitHint), nameof(PostFilterRuleLimitHelp),
        nameof(HiddenPostLimitLabel), nameof(HiddenPostLimitHint), nameof(HiddenPostLimitHelp),
        nameof(ReadingHistoryLimitLabel), nameof(ReadingHistoryLimitHint), nameof(ReadingHistoryLimitHelp),
        nameof(ReadingHistoryMenuEntriesLabel), nameof(ReadingHistoryMenuEntriesHint), nameof(ReadingHistoryMenuEntriesHelp),
        nameof(ReaderTextlessMediaWidthLabel), nameof(ReaderTextlessMediaWidthHint), nameof(ReaderTextlessMediaWidthHelp),
        nameof(ReaderFocusedMediaScalePercentLabel), nameof(ReaderFocusedMediaScalePercentHint), nameof(ReaderFocusedMediaScalePercentHelp),
        nameof(ReaderHoverEnlargeSizeLabel), nameof(ReaderHoverEnlargeSizeHint), nameof(ReaderHoverEnlargeSizeHelp),
        nameof(ReaderHoverEnlargeDelayMsLabel), nameof(ReaderHoverEnlargeDelayMsHint), nameof(ReaderHoverEnlargeDelayMsHelp),
        nameof(ReaderTextlessLargeImagesLabel), nameof(ReaderTextlessLargeImagesHint),
        nameof(ReaderFocusedMediaEnlargeLabel), nameof(ReaderFocusedMediaEnlargeHint),
        nameof(ReaderHoverEnlargeLabel), nameof(ReaderHoverEnlargeHint),
        nameof(ReaderMediaSidebarLabel), nameof(ReaderMediaSidebarHint),
        nameof(ReaderMediaSidebarScopeLabel), nameof(ReaderMediaSidebarScopeHint),
        nameof(ReaderMediaSidebarSideLabel), nameof(ReaderMediaSidebarSideHint),
        nameof(ReaderMediaSidebarWidthLabel), nameof(ReaderMediaSidebarWidthHint), nameof(ReaderMediaSidebarWidthHelp),
        nameof(ReaderMediaSidebarMinimumWidthLabel), nameof(ReaderMediaSidebarMinimumWidthHint), nameof(ReaderMediaSidebarMinimumWidthHelp),
        nameof(ReaderMediaClickLabel), nameof(ReaderMediaClickHint),
        nameof(ReaderInlineVideoAutoplayMutedLabel), nameof(ReaderInlineVideoAutoplayMutedHint),
        nameof(ReaderKeepImagesExpandedLabel), nameof(ReaderKeepImagesExpandedHint),
        nameof(ReaderSidebarVideoAutoplayLabel), nameof(ReaderSidebarVideoAutoplayHint),
        nameof(ReaderSidebarVideoLoopLabel), nameof(ReaderSidebarVideoLoopHint),
        nameof(ReaderSidebarVideoStartMutedLabel), nameof(ReaderSidebarVideoStartMutedHint),
        nameof(ReaderInlineVideoMaxPlayersLabel), nameof(ReaderInlineVideoMaxPlayersHint), nameof(ReaderInlineVideoMaxPlayersHelp),
        nameof(ReaderInlineVideoMaxFileMiBLabel), nameof(ReaderInlineVideoMaxFileMiBHint), nameof(ReaderInlineVideoMaxFileMiBHelp),
        nameof(ReaderMediaStripLimitLabel), nameof(ReaderMediaStripLimitHint), nameof(ReaderMediaStripLimitHelp),
        nameof(ReaderMediaGridLabel), nameof(ReaderMediaGridHint),
        nameof(ReaderMediaGridTextLimitLabel), nameof(ReaderMediaGridTextLimitHint), nameof(ReaderMediaGridTextLimitHelp),
        nameof(ReaderPreviewPrefetchPostsLabel), nameof(ReaderPreviewPrefetchPostsHint), nameof(ReaderPreviewPrefetchPostsHelp),
        nameof(ReaderPreviewSizeMemoryLabel), nameof(ReaderPreviewSizeMemoryHint), nameof(ReaderPreviewSizeMemoryHelp),
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
        nameof(GalleryMaxZoomPercentHelp),
        nameof(PostFilterRuleLimitHelp),
        nameof(HiddenPostLimitHelp),
        nameof(ReadingHistoryLimitHelp),
        nameof(ReadingHistoryMenuEntriesHelp),
        nameof(ReaderTextlessMediaWidthHelp),
        nameof(ReaderFocusedMediaScalePercentHelp),
        nameof(ReaderHoverEnlargeSizeHelp),
        nameof(ReaderHoverEnlargeDelayMsHelp),
        nameof(ReaderMediaSidebarWidthHelp),
        nameof(ReaderMediaSidebarMinimumWidthHelp),
        nameof(ReaderInlineVideoMaxPlayersHelp),
        nameof(ReaderInlineVideoMaxFileMiBHelp),
        nameof(ReaderMediaStripLimitHelp),
        nameof(ReaderMediaGridTextLimitHelp),
        nameof(ReaderPreviewPrefetchPostsHelp),
        nameof(ReaderPreviewSizeMemoryHelp),
    });
}
