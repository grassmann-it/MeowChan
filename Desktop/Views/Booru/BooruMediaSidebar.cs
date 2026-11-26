using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

internal sealed class BooruMediaSidebar : Grid, IDisposable
{
    private const double Inset = 8;
    internal const double HandleMargin = 2;
    private const double TagRoom = 96;
    private const double FactsHeight = 112;
    private const int PreviewEdge = 512;

    private readonly TextBlock _title = new()
    {
        Name = "BooruMediaTitle", Classes = { "card-title" }, VerticalAlignment = VerticalAlignment.Center,
        MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly MediaPositionPill _position = new("BooruMediaPosition");
    // Required by the shared header, but a local favorite has no meaning for a provider hit in 1.1.2 (Tom, 25.11. 14:48).
    private readonly ArchiveFavoriteButton _favorite = new() { Name = "BooruMediaFavorite", IsVisible = false };
    private readonly Button _settingsButton = new() { Name = "BooruMediaSettings" };
    private readonly MediaCompactHeader _header;
    private readonly TextBlock _ratingStat = StatText("BooruMediaStatRating");
    private readonly TextBlock _scoreStat = StatText("BooruMediaStatScore");
    private readonly TextBlock _tagCount = StatText("BooruMediaStatTagCount");
    private readonly StackPanel _tagStat = new() { Name = "BooruMediaStatTags", Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly WrapPanel _stats = new() { Name = "BooruMediaStats", ItemSpacing = 16, LineSpacing = 4, Margin = new Thickness(Inset, 0) };
    private readonly Image _image = new() { Name = "BooruMediaPreview", Stretch = Stretch.Uniform };
    private readonly SidebarGifPlayback _gif;
    private readonly TextBlock _placeholder = new()
    {
        Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Inset),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _kind = WorkspaceChips.CreateCornerBadge("BooruMediaKind", HorizontalAlignment.Right, VerticalAlignment.Top);
    private readonly ScrollViewer _facts = new()
    {
        Name = "BooruMediaFacts", MaxHeight = FactsHeight, Margin = new Thickness(Inset, 0),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly BooruGallerySampleLoader _sample = new();
    private readonly List<Visual> _visibilityAncestors = new();
    private BooruResultTarget? _target;
    private byte[]? _previewBytes;
    private int _previewMaximumPixels;
    private bool _previewAnimated;
    private IDisposable? _bitmap;
    private BooruGalleryPicture _picture;
    private double _aspect = 1;
    private bool _disposed;

    internal Func<TimeSpan, CancellationToken, Task>? SampleDelayAsync { get; set; }
    internal Task SampleLoad { get; private set; } = Task.CompletedTask;

    internal PanelCloseButton Close { get; } = new("BooruMediaClose") { Focusable = false, IsTabStop = false };
    internal ToggleButton Info { get; } = new() { Name = "BooruMediaInfo", IsChecked = true };
    internal Action? FactsToggled { get; set; }
    internal Action? FactsRefreshRequested { get; set; }
    internal Button OpenGallery { get; } = new()
    {
        Name = "BooruMediaOpenGallery", Focusable = false, IsTabStop = false, Classes = { "ghost" },
        Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, ClipToBounds = true,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center
    };
    internal BooruActionRow Actions { get; } = new() { Name = "BooruMediaActions", Margin = new Thickness(Inset, 0) };
    internal ScrollViewer Tags { get; } = new()
    {
        Name = "BooruMediaTags", Margin = new Thickness(Inset, 0), Focusable = false, IsTabStop = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    internal BooruMediaSidebar()
    {
        _gif = new(_image);
        Name = "BooruMediaSidebar";
        IsVisible = false;
        Margin = new Thickness(HandleMargin, 0, 0, 0); // the 6 DIP handle and this margin keep the former 8 DIP gap to the hits
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto");
        RowSpacing = Inset;
        ClipToBounds = true;
        _header = new MediaCompactHeader(_title, _position, _favorite, Info, open: null, settings: _settingsButton, close: Close)
        {
            Margin = new Thickness(Inset, 4), FileNameReserve = 40
        };
        _header.Meta.Name = "BooruMediaSummary";
        _tagStat.Children.AddRange([CommandBindings.FollowLabel(CommandBindings.IconFromKey("tag", 16), _tagCount), _tagCount]);
        _stats.Children.AddRange([_ratingStat, _scoreStat, _tagStat]);
        Info.Click += (_, _) => ToggleFacts();
        MenuItem showFacts = new() { ToggleType = MenuItemToggleType.CheckBox };
        AutomationProperties.SetAutomationId(showFacts, "BooruMediaSettingsFacts");
        showFacts.Click += (_, _) => ToggleFacts();
        MenuFlyout menu = new() { Placement = PlacementMode.BottomEdgeAlignedRight, OverlayInputPassThroughElement = _settingsButton };
        menu.Items.Add(showFacts);
        PopupToggle.BindFlyout(_settingsButton, menu);
        menu.Opening += (_, _) =>
        {
            FactsRefreshRequested?.Invoke();
            const string command = "media.sidebar.info";
            string label = CommandCatalog.Label(command);
            EffectiveShortcutBindings bindings = ShortcutService.Current;
            showFacts.Header = label;
            showFacts.Icon ??= CommandBindings.Icon(command);
            showFacts.IsChecked = _facts.IsVisible;
            showFacts.InputGesture = bindings.For(command)
                .FirstOrDefault(gesture => ShortcutHelp.ActsHere(command, gesture, "booru", bindings))?.Resolve(bindings.Platform);
            AutomationProperties.SetName(showFacts, label);
            string tip = CommandBindings.Tooltip(command, visibleLabel: label, keyContext: "booru");
            AutomationProperties.SetHelpText(showFacts, tip);
            ToolTip.SetTip(showFacts, tip);
        };
        _settingsButton.Content = CommandBindings.IconFromKey("settings", 16);
        _kind.Margin = new Thickness(0, 4, 4, 0);
        OpenGallery.Content = new Panel { Children = { _image, _placeholder, _kind } };
        AutomationProperties.SetAccessibilityView(_image, AccessibilityView.Raw);
        foreach (Control control in new Control[] { this, _title, _header.Meta, _favorite, Info, Close, OpenGallery, _image, Actions, _stats, _ratingStat, _scoreStat, _tagStat, _tagCount, Tags, _facts })
            AutomationProperties.SetAutomationId(control, control.Name);
        AutomationProperties.SetAutomationId(_header.Position, _position.Name);
        SetRow(OpenGallery, 1);
        SetRow(Actions, 2);
        SetRow(_stats, 3);
        SetRow(Tags, 4);
        SetRow(_facts, 5);
        Children.AddRange([_header, OpenGallery, Actions, _stats, Tags, _facts]);
        AutomationProperties.SetControlTypeOverride(this, Avalonia.Automation.Peers.AutomationControlType.Group);
        AttachedToVisualTree += (_, _) => ObserveVisibility();
        DetachedFromVisualTree += (_, _) => StopObservingVisibility();
    }

    internal void PresentPosition(int index, int count, bool marked)
    {
        _position.Show(new GalleryTagPanelPosition(index + 1, count, false), marked);
        string? description = AutomationProperties.GetName(_position);
        AutomationProperties.SetName(_header.Position, description);
        ToolTip.SetTip(_header.Position, description);
        _header.Position[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(marked ? "Workspace.Accent" : "Workspace.Text");
        _header.InvalidateMeasure();
    }

    internal void Present(BooruResultTarget target, BooruResultRowView row, bool saved, int maximumPixels, string provider)
    {
        if (_target != target)
        {
            Release();
            _target = target;
            _sample.Reset(row.Post.SampleUrl is not null);
            Tags.Offset = default;
        }
        if (_picture != BooruGalleryPicture.Sample
            && (!ReferenceEquals(_previewBytes, row.PreviewBytes) || _previewMaximumPixels != maximumPixels))
        {
            _previewBytes = row.PreviewBytes;
            _previewMaximumPixels = maximumPixels;
            _previewAnimated = false;
            Bitmap? preview = row.Preview == BooruPreviewState.Ready && _previewBytes is { Length: > 0 } bytes
                ? DecodePreview(bytes, maximumPixels, out _previewAnimated) : null;
            ReplacePicture(preview, preview is null ? BooruGalleryPicture.None : BooruGalleryPicture.Preview,
                _previewAnimated ? _previewBytes : null, maximumPixels);
        }

        BooruPostView post = row.Post;
        _title.Text = Texts.T("booru.ui.post.title", post.PostId);
        _header.Localize();
        string settingsLabel = Texts.T("media.sidebar.settings");
        AutomationProperties.SetName(_settingsButton, settingsLabel);
        ToolTip.SetTip(_settingsButton, settingsLabel);
        AutomationProperties.SetName(this, Texts.T("media.layout.panel"));
        AutomationProperties.SetName(Tags, Texts.T("booru.tags.sourceLabel"));
        AutomationProperties.SetName(OpenGallery, CommandCatalog.Label("gallery.open") + " · " + _title.Text);
        ToolTip.SetTip(OpenGallery, AutomationProperties.GetName(OpenGallery));
        Close.Localize(Texts.T("media.panel.close"));
        _placeholder.Text = Texts.T(row.Preview switch
        {
            BooruPreviewState.Loading => "booru.ui.post.previewLoading",
            BooruPreviewState.None => "booru.ui.post.noPreview",
            _ => "booru.ui.post.previewUnavailable"
        });

        string? extension = post.Original?.Extension?.TrimStart('.');
        string? kind = extension?.ToLowerInvariant() switch
        {
            "gif" => Texts.T("media.tile.kind.gif"),
            "mp4" or "webm" or "zip" => Texts.T("media.tile.kind.video"),
            _ => null
        };
        _kind.IsVisible = kind is not null;
        ((TextBlock)_kind.Child!).Text = kind;
        int? width = post.Original?.Width;
        int? height = post.Original?.Height;
        _aspect = width is > 0 && height is > 0 ? (double)width.Value / height.Value : 1;
        string? dimensions = width is > 0 && height is > 0 ? MediaFactGrid.FormatDimensions(width.Value, height.Value) : null;
        string? bytesText = post.Original?.ExpectedSize is { } size ? SizeText.Format(size) : null;
        string? rating = post.Rating != BooruRating.Unknown ? BooruText.Rating(post.Rating)
            : string.IsNullOrWhiteSpace(post.RawRating) ? null : BooruText.Rating(post.Rating) + " (" + post.RawRating + ")";
        string? score = post.Score?.ToString(Texts.Culture);
        string? favorites = post.FavoriteCount?.ToString(Texts.Culture);
        string file = post.Original?.Url is { IsAbsoluteUri: true } url
            ? Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath)) : "";
        _header.Describe(file.Length > 0 ? file : extension ?? "",
            string.Join(" · ", new[] { provider, bytesText }.Where(part => !string.IsNullOrWhiteSpace(part))), hasPostTitle: true);
        string ratingLabel = Texts.T("booru.rating.label");
        string? ratingName = rating is null ? null : Texts.T("media.breakdown.summary", ratingLabel, rating);
        string? scoreName = score is null ? null : Texts.T("booru.media.provider.score", score);
        PresentStats(post, rating, ratingName, score, scoreName);

        MediaFactGrid facts = new("BooruMediaFactGrid");
        AddFact(ratingLabel, rating, ratingName, "BooruMediaRating");
        AddFact(Texts.T("booru.media.provider.score", string.Empty).TrimEnd(), score, scoreName, "BooruMediaScore");
        facts.Add(Texts.T("booru.ui.post.field.favorites"), favorites);
        facts.Add(Texts.T("media.fact.dimensions"), dimensions);
        facts.Add(Texts.T("booru.ui.post.field.created"), post.CreatedAt?.ToLocalTime().ToString("g", Texts.Culture));
        facts.Add(Texts.T("media.fact.saved"), saved ? Texts.T("booru.ui.result.localYes")
            : BooruText.Load(row) ?? Texts.T("booru.ui.result.localNo"));
        facts.Add(Texts.T("booru.media.variants.heading"), _picture == BooruGalleryPicture.None
            ? null : Texts.T("booru.mediaRole." + _picture));
        _facts.Content = facts;
        InvalidateMeasure();

        void AddFact(string label, string? value, string? description, string id)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            facts.Add(label, value, id);
            Control fact = facts.Children[^1];
            AutomationProperties.SetAutomationId(fact, id);
            AutomationProperties.SetName(fact, description);
            ToolTip.SetTip(fact, description);
        }
    }

    /// <summary>
    /// The provider's own numbers for the post, in place of the local rating and tag editor (Tom, 25.11. 14:48): only
    /// what the provider delivered, no empty placeholders.
    /// </summary>
    private void PresentStats(BooruPostView post, string? rating, string? ratingName, string? score, string? scoreName)
    {
        Show(_ratingStat, rating, ratingName);
        Show(_scoreStat, score is null ? null : Texts.T("booru.ui.result.short.score", score), scoreName);
        bool tags = post.Post.Fields.HasFlag(BooruFieldPresence.Tags);
        _tagCount.Text = tags ? post.Tags.Length.ToString(Texts.Culture) : null;
        string tagName = Texts.Count("booru.media.gallery.tags", post.Tags.Length);
        _tagStat.IsVisible = tags;
        AutomationProperties.SetName(_tagStat, tags ? tagName : null);
        ToolTip.SetTip(_tagStat, tags ? tagName : null);
        UpdateCompactStats();

        static void Show(TextBlock stat, string? text, string? name)
        {
            stat.Text = text;
            AutomationProperties.SetName(stat, name);
            ToolTip.SetTip(stat, name);
        }
    }

    private static TextBlock StatText(string name) => new()
    {
        Name = name, Classes = { "workspace-meta", "tnum" }, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center
    };

    internal void ToggleFacts()
    {
        ShowFacts(!_facts.IsVisible);
        FactsToggled?.Invoke();
    }

    internal void ShowFacts(bool visible)
    {
        _facts.IsVisible = visible;
        Info.IsChecked = visible;
        UpdateCompactStats();
    }

    private void UpdateCompactStats()
    {
        _ratingStat.IsVisible = !_facts.IsVisible && _ratingStat.Text is not null;
        _scoreStat.IsVisible = !_facts.IsVisible && _scoreStat.Text is not null;
        _stats.IsVisible = _ratingStat.IsVisible || _scoreStat.IsVisible || _tagStat.IsVisible;
    }

    internal void RequestSample(IBooruSamplePort? port, BooruResultTarget target, Func<BooruGallerySampleLimits> limits,
        Func<bool> current, Action changed)
    {
        if (_disposed || _target != target || !this.IsAttachedToVisualTree() || !IsEffectivelyVisible) return;
        SampleLoad = _sample.Start(port, target.Post, limits, BooruGallerySampleLoader.DefaultDelay,
            static (bytes, width, pixels) => BooruGalleryPanel.DecodeBitmap(bytes, width, pixels),
            (image, bytes) =>
            {
                if (_disposed || _target != target || !IsEffectivelyVisible || !current()) return false;
                bool gif = GifFrameDecoder.HasGifHeader(bytes);
                if (!gif && _previewAnimated && _picture == BooruGalleryPicture.Preview)
                {
                    // Keep the available animation when the provider's larger sample is a still.
                    (image as IDisposable)?.Dispose();
                    return true;
                }
                ReplacePicture(image, BooruGalleryPicture.Sample, gif ? bytes : null, _sample.Limits.MaximumPixels);
                return true;
            }, changed, SampleDelayAsync) ?? SampleLoad;
    }

    private static Bitmap? DecodePreview(byte[] bytes, int maximumPixels, out bool animated)
    {
        animated = false;
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using SKManagedStream managed = new(stream, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managed);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > maximumPixels) return null;
            PixelSize display = ImageOrientation.DisplaySize(codec);
            double scale = Math.Min(1, (double)PreviewEdge / Math.Max(display.Width, display.Height));
            PixelSize scaled = new(Math.Max(1, (int)Math.Floor(display.Width * scale)),
                Math.Max(1, (int)Math.Floor(display.Height * scale)));
            animated = codec.EncodedFormat == SKEncodedImageFormat.Gif && (long)display.Width * display.Height <= GifFrameDecoder.MaximumPixels
                && codec.FrameCount is > 1 and <= GifFrameDecoder.MaximumFrames;
            return ImageOrientation.DecodeScaled(codec, scaled);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private void ReplacePicture(IImage? image, BooruGalleryPicture picture, byte[]? gif = null, int maximumPixels = 0)
    {
        _gif.Clear();
        _image.Source = image;
        _placeholder.IsVisible = image is null;
        _bitmap?.Dispose();
        _bitmap = image as IDisposable;
        _picture = picture;
        if (image is Bitmap poster && gif is not null)
        {
            PixelSize viewport = poster.PixelSize;
            _gif.Show(poster, async token => await GifFrameDecoder.OpenPreviewAsync(gif, maximumPixels, viewport, token));
        }
    }

    internal void ClearActions() => Actions.Clear();

    /// <summary>A labeled action shares the row's width with the others; a compact one stays a 32 DIP icon at its place.</summary>
    internal void AddAction(Button button, string icon, string label, string? shortLabel = null, bool compact = false) =>
        Actions.Add(button, icon, label, shortLabel, compact);

    protected override Size MeasureOverride(Size availableSize)
    {
        // The column gives the width now (the handle drags it), so an unset Width must not fall back to the default size.
        double width = double.IsFinite(Width) ? Math.Min(Width, availableSize.Width) : availableSize.Width;
        if (!double.IsFinite(width)) width = Config.MediaPanelWidthDefault;
        Size row = new(width, double.PositiveInfinity);
        _header.Measure(row);
        Actions.Measure(row);
        _stats.Measure(row);
        _facts.Measure(row);
        double bars = _header.DesiredSize.Height + Actions.DesiredSize.Height
            + _stats.DesiredSize.Height + _facts.DesiredSize.Height + 5 * RowSpacing;
        OpenGallery.Height = Math.Min(width / _aspect, Math.Max(0, availableSize.Height - bars - (Tags.IsVisible ? TagRoom : 0)));
        return base.MeasureOverride(availableSize);
    }

    private void ObserveVisibility()
    {
        foreach (Visual ancestor in this.GetSelfAndVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += AncestorVisibilityChanged;
        }
    }

    private void StopObservingVisibility()
    {
        foreach (Visual ancestor in _visibilityAncestors) ancestor.PropertyChanged -= AncestorVisibilityChanged;
        _visibilityAncestors.Clear();
        _sample.Pause();
    }

    private void AncestorVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        if (!e.GetNewValue<bool>()) _sample.Pause();
        else if (IsEffectivelyVisible) FactsRefreshRequested?.Invoke();
    }

    internal void Release()
    {
        if (_target is null) return;
        _sample.Reset(offered: false);
        SampleLoad = Task.CompletedTask;
        _facts.Content = null;
        _target = null;
        _previewBytes = null;
        _previewAnimated = false;
        ReplacePicture(null, BooruGalleryPicture.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopObservingVisibility();
        Release();
    }
}

/// <summary>
/// The Booru sidebar's action row (Tom, 25.11. 14:55): the labeled actions share the row's width in equal parts, a compact
/// action stays a square icon in its place, and a hidden action takes no room. A label that does not fit its share falls
/// back to the short label, then to the icon alone; it is never clipped.
/// </summary>
internal sealed class BooruActionRow : Panel
{
    internal const double Gap = 8;
    private const double LabelPadding = 8;
    private const double IconGap = 4;
    private readonly List<Entry> _actions = new();

    private sealed record Entry(Button Button, TextBlock Label, string Full, string? Short, bool Compact);

    internal void Clear()
    {
        Children.Clear();
        _actions.Clear();
    }

    internal void Add(Button button, string icon, string label, string? shortLabel, bool compact)
    {
        TextBlock text = new()
        {
            Text = label, Classes = { "panel-action-label" }, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center,
            IsVisible = !compact
        };
        AutomationProperties.SetAutomationId(text, button.Name + ":Label");
        StackPanel line = new()
        {
            Orientation = Orientation.Horizontal, Spacing = IconGap, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        line.Children.Add(text);
        button.AttachedToVisualTree += (_, _) =>
        {
            if (line.Children.Count == 1) line.Children.Insert(0, CommandBindings.NamedIcon(icon));
        };
        button.Classes.Set("panel-action", true);
        if (compact) button.Classes.Set(WorkspaceIcons.SquareClass, true);
        button.Height = WorkspaceMetrics.InputHeight;
        button.MinWidth = 0;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Content = line;
        _actions.Add(new Entry(button, text, label, shortLabel, compact));
        Children.Add(button);
    }

    private (double Labeled, int Visible) Shares(double width)
    {
        int visible = 0, compact = 0;
        foreach (Entry action in _actions)
        {
            if (!action.Button.IsVisible) continue;
            visible++;
            if (action.Compact) compact++;
        }
        int labeled = visible - compact;
        double room = width - Math.Max(0, visible - 1) * Gap - compact * WorkspaceMetrics.InputHeight;
        return (labeled == 0 ? 0 : Math.Max(WorkspaceMetrics.InputHeight, room / labeled), visible);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        (double share, int visible) = Shares(width);
        foreach (Entry action in _actions)
        {
            if (!action.Button.IsVisible) continue;
            if (action.Compact)
            {
                action.Button.Padding = default;
                action.Button.Measure(new Size(WorkspaceMetrics.InputHeight, WorkspaceMetrics.InputHeight));
                continue;
            }
            Button button = action.Button;
            double chrome = 2 * LabelPadding + 16 + IconGap + button.BorderThickness.Left + button.BorderThickness.Right;
            string? text = Fits(action.Full) ? action.Full : action.Short is { } brief && Fits(brief) ? brief : null;
            action.Label.Text = text ?? action.Full;
            action.Label.IsVisible = text is not null;
            button.Padding = text is null ? default : new Thickness(LabelPadding, 0);
            button.Measure(new Size(share, WorkspaceMetrics.InputHeight));

            bool Fits(string candidate)
            {
                TextBlock label = action.Label;
                FormattedText measured = new(candidate, Texts.Culture, label.FlowDirection,
                    new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, label.Foreground);
                return Math.Ceiling(measured.WidthIncludingTrailingWhitespace) + chrome <= share;
            }
        }
        return new Size(width, visible == 0 ? 0 : WorkspaceMetrics.InputHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        (double share, _) = Shares(finalSize.Width);
        double x = 0;
        foreach (Entry action in _actions)
        {
            if (!action.Button.IsVisible) continue;
            double width = action.Compact ? WorkspaceMetrics.InputHeight : share;
            action.Button.Arrange(new Rect(x, 0, width, WorkspaceMetrics.InputHeight));
            x += width + Gap;
        }
        return finalSize;
    }
}
