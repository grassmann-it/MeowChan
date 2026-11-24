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
    private readonly Button[] _labeled;
    private readonly TextBlock[] _captions = [new(), new(), new()];
    private string _file = "", _detail = "";

    internal TextBlock Meta { get; } = new()
    {
        Name = "ReaderMediaSidebarMeta", Classes = { "workspace-meta" }, MaxLines = 1, Height = 16
    };

    internal MediaCompactHeader(TextBlock title, MediaPositionPill pill, ArchiveFavoriteButton favorite, ToggleButton info, Button open, Button settings, PanelCloseButton close)
    {
        _title = title;
        _pill = pill;
        _labeled = [favorite, info, open];
        RowDefinitions = new RowDefinitions("32,Auto");
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Children.Add(title);
        Localize();
        foreach (Button button in new Button[] { favorite, info, open, settings, close })
        {
            button.Width = button.Height = WorkspaceMetrics.InputHeight;
            button.MinWidth = 0;
            button.Classes.Add(WorkspaceIcons.SquareClass);
            button.Focusable = button.IsTabStop = false;
            _actions.Children.Add(button);
        }
        SetColumn(_actions, 2);
        Children.Add(_actions);
        SetRow(Meta, 1);
        SetColumnSpan(Meta, 3);
        Children.Add(Meta);
    }

    internal void Localize()
    {
        _captions[0].Text = Texts.T("gallery.decision.favorite");
        _captions[1].Text = Texts.T("gallery.toolbar.label.info");
        _captions[2].Text = Texts.T("workview.overview.page.gallery");
        InvalidateMeasure();
    }

    // The live close button stays at this same position when both bars disappear.
    internal void ReserveCloseSpace(PanelCloseButton close)
    {
        _actions.Children.Remove(close);
        _actions.Children.Add(new Border { Width = WorkspaceMetrics.InputHeight });
    }

    internal void Describe(string file, string detail)
    {
        _file = file;
        _detail = detail;

        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        const double icons = 5 * WorkspaceMetrics.InputHeight + 4 * 4;
        string prefix = _pill.Bare ?? "";
        string Line(string file) => ReaderMediaSidebarRules.HeaderText(prefix, file, _detail);
        double titleWidth = MeasureText(_title, _title.Text);
        double fileMinimum = MeasureText(Meta, Line(_file.Length == 0 ? "" : "abcdefghij…xyz.webm"));
        double free = width - icons - titleWidth - fileMinimum - 24;
        bool single = free >= 0 && width >= 480;
        // Add labels in reverse order of removal: gallery, favorite, then info.
        bool fits = single;
        foreach (int i in new[] { 2, 0, 1 })
        {
            double extra = Math.Ceiling(MeasureText(Meta, _captions[i].Text)) + 16;
            fits = fits && free >= extra;
            if (_labeled[i] is ArchiveFavoriteButton star) star.SetCaption(fits ? _captions[i].Text : null);
            else
            {
                string command = i == 1 ? "media.sidebar.info" : "gallery.open";
                if (fits) CommandBindings.ApplyLabeled(_labeled[i], command, _captions[i].Text!);
                else CommandBindings.Apply(_labeled[i], command, iconOnly: true);
            }
            _labeled[i].Width = 32 + (fits ? extra : 0);
            _labeled[i].Classes.Set(WorkspaceIcons.SquareClass, !fits);
            _labeled[i].Padding = new Thickness(fits ? 4 : 0, 0);
            if (fits) free -= extra;
        }
        _actions.Measure(Size.Infinity);
        _title.MinWidth = 0;
        _title.MaxWidth = Math.Max(0, width - _actions.DesiredSize.Width - 4);
        SetRow(Meta, single ? 0 : 1);
        SetColumn(Meta, single ? 1 : 0);
        SetColumnSpan(Meta, single ? 1 : 3);
        Meta.VerticalAlignment = VerticalAlignment.Center;
        Meta.Margin = single ? new Thickness(16, 0, 8, 0) : default;
        double fileRoom = single ? width - titleWidth - _actions.DesiredSize.Width - 24 : width;
        string full = Line(_file);
        string file = MediaFactGrid.FitFileName(_file, candidate => MeasureText(Meta, Line(candidate)) <= fileRoom);
        Meta.Text = Line(file);
        ToolTip.SetTip(Meta, full);
        AutomationProperties.SetName(Meta, full);
        return base.MeasureOverride(availableSize);
    }

    private static double MeasureText(TextBlock block, string? text) => new FormattedText(text ?? "", Texts.Culture,
        block.FlowDirection, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
        block.FontSize, block.Foreground).Width;
}
