using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>Reader and Media header. Only the filename gives up space in the metadata line.</summary>
internal sealed class MediaCompactHeader : Grid
{
    private readonly TextBlock _title;
    private readonly MediaPositionPill _pill;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Button?[] _labeled;
    private readonly string[] _captions = ["", "", ""];
    private readonly bool?[] _visibleLabels = new bool?[3];
    private static readonly int[] LabelPriority = [2, 0, 1];
    private string _fullTitle = "", _file = "", _detail = "";
    private bool _hasPostTitle;

    private readonly Grid _metadata = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = VerticalAlignment.Center };
    internal bool UsesSingleRow => GetRow(_metadata) == 0;
    internal double FileNameReserve { get; init; } = 112;
    internal TextBlock Position { get; } = new()
    {
        FontWeight = FontWeight.SemiBold, Height = 16, MaxLines = 1, Classes = { "workspace-meta" },
        [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Text")
    };

    internal TextBlock Meta { get; } = new()
    {
        Name = "ReaderMediaSidebarMeta", Classes = { "workspace-meta" }, MaxLines = 1, Height = 16
    };

    internal MediaCompactHeader(TextBlock title, MediaPositionPill pill, ArchiveFavoriteButton favorite, ToggleButton info, Button? open, Button? settings, PanelCloseButton close)
    {
        _title = title;
        _pill = pill;
        _labeled = [favorite, info, open];
        RowDefinitions = new RowDefinitions("32,Auto");
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Children.Add(title);
        Localize();
        foreach (Button? button in new Button?[] { favorite, info, open, settings, close })
        {
            if (button == null) continue;
            button.Width = button.Height = WorkspaceMetrics.InputHeight;
            button.MinWidth = 0;
            button.Classes.Add(WorkspaceIcons.SquareClass);
            button.Focusable = button.IsTabStop = false;
            _actions.Children.Add(button);
        }
        SetColumn(_actions, 2);
        Children.Add(_actions);
        SetRow(_metadata, 1);
        SetColumnSpan(_metadata, 3);
        SetColumn(Meta, 1);
        AutomationProperties.SetAutomationId(Position, "ReaderMediaSidebarPosition");
        _metadata.Children.Add(Position);
        _metadata.Children.Add(Meta);
        Children.Add(_metadata);
    }

    internal void Localize()
    {
        Array.Clear(_visibleLabels);
        _captions[0] = Texts.T("gallery.decision.favorite");
        _captions[1] = Texts.T("gallery.toolbar.label.info");
        _captions[2] = Texts.T("workview.overview.page.gallery");
        InvalidateMeasure();
    }

    // The live close button stays at this same position when both bars disappear.
    internal void ReserveCloseSpace(PanelCloseButton close)
    {
        _actions.Children.Remove(close);
        _actions.Children.Add(new Border { Width = WorkspaceMetrics.InputHeight });
    }

    internal void Describe(string file, string detail, bool hasPostTitle)
    {
        _fullTitle = _title.Text ?? "";
        _hasPostTitle = hasPostTitle;
        AutomationProperties.SetName(_title, _fullTitle);
        _file = file;
        _detail = detail;

        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        int actionCount = 0;
        foreach (Control action in _actions.Children)
            if (action.IsVisible) actionCount++;
        double icons = actionCount * WorkspaceMetrics.InputHeight + (actionCount - 1) * _actions.Spacing;
        string prefix = _pill.Bare ?? "";
        string Line(string file) => ReaderMediaSidebarRules.HeaderText("", file, _detail);
        Position.Text = prefix;
        Position.IsVisible = prefix.Length > 0;
        double prefixWidth = MeasureText(Position, prefix);
        string separator = prefix.Length > 0 && (_file.Length > 0 || _detail.Length > 0) ? " · " : "";
        double titleWidth = MeasureText(_title, _fullTitle);
        // Reserve enough of the actual name to recognize it before spending space on action labels.
        double fileMinimum = prefixWidth + MeasureText(Meta, separator + Line("")) + Math.Min(FileNameReserve, MeasureText(Meta, _file));
        if (_file.Length > 0 && _detail.Length > 0) fileMinimum += MeasureText(Meta, " · ");
        double free = width - icons - titleWidth - fileMinimum - 24;
        bool single = free >= 0;
        // Add labels in reverse order of removal: gallery, favorite, then info.
        bool fits = single;
        foreach (int i in LabelPriority)
        {
            if (_labeled[i] is not { IsVisible: true } button) continue;
            double extra = Math.Ceiling(MeasureText(Meta, _captions[i])) + 16;
            fits = fits && free >= extra;
            if (_visibleLabels[i] != fits)
            {
                _visibleLabels[i] = fits;
                if (button is ArchiveFavoriteButton star) star.SetCaption(fits ? _captions[i] : null);
                else
                {
                    string command = i == 1 ? "media.sidebar.info" : "gallery.open";
                    if (fits && i == 1) CommandBindings.ApplyLabeled(button, command, _captions[i],
                        _ => CommandBindings.Tooltip("media.sidebar.info"));
                    else if (fits) CommandBindings.ApplyLabeled(button, command, _captions[i]);
                    else CommandBindings.Apply(button, command, iconOnly: true);
                }
            }
            button.Width = 32 + (fits ? extra : 0);
            button.Classes.Set(WorkspaceIcons.SquareClass, !fits);
            button.Padding = new Thickness(fits ? 4 : 0, 0);
            if (fits) free -= extra;
        }
        _actions.Measure(Size.Infinity);
        _title.MinWidth = 0;
        _title.MaxWidth = Math.Max(0, width - _actions.DesiredSize.Width - 4);
        // At 280 DIP keep the identifying digits when the translated post prefix no longer fits.
        int start = _fullTitle.Length;
        while (start > 0 && char.IsDigit(_fullTitle[start - 1])) start--;
        _title.Text = _hasPostTitle && titleWidth > _title.MaxWidth && start > 0 && start < _fullTitle.Length &&
            char.IsWhiteSpace(_fullTitle[start - 1]) &&
            MeasureText(_title, _fullTitle[start..]) <= _title.MaxWidth ? _fullTitle[start..] : _fullTitle;
        SetRow(_metadata, single ? 0 : 1);
        SetColumn(_metadata, single ? 1 : 0);
        SetColumnSpan(_metadata, single ? 1 : 3);
        Meta.VerticalAlignment = VerticalAlignment.Center;
        _metadata.Margin = single ? new Thickness(16, 0, 8, 0) : default;
        double fileRoom = single ? width - titleWidth - _actions.DesiredSize.Width - 24 : width;
        string full = ReaderMediaSidebarRules.HeaderText(prefix, _file, _detail);
        string file = MediaFactGrid.FitFileName(_file, candidate => prefixWidth + MeasureText(Meta, separator + Line(candidate)) <= fileRoom);
        Meta.Text = separator + Line(file);
        ToolTip.SetTip(Meta, full);
        AutomationProperties.SetName(Meta, full);
        return base.MeasureOverride(availableSize);
    }

    private static double MeasureText(TextBlock block, string? text) => new FormattedText(text ?? "", Texts.Culture,
        block.FlowDirection, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
        block.FontSize, block.Foreground).Width;
}
