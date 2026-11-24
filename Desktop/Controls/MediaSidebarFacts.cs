using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>The six sidebar facts keep stable positions as the selected medium changes.</summary>
internal sealed class MediaSidebarFacts : Grid
{
    private sealed class Field : Grid
    {
        internal TextBlock Label { get; } = new() { Classes = { "workspace-meta" }, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly SelectableTextBlock _value = new() { Classes = { "media-fact-value" }, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly MenuItem _copy = new() { Name = "MediaFactCopy" };

        internal Field()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*");
            ColumnSpacing = 4;
            Children.Add(Label);
            SetColumn(_value, 1);
            Children.Add(_value);
            _value.ContextMenu = new ContextMenu { Items = { _copy } };
            _copy.Click += (_, _) => OpenActions.Start(async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(_value.Text ?? "");
            }, "diagnostics.copy");
        }

        internal void Show(string label, string? value, string? description = null)
        {
            Label.Text = label;
            _value.Text = string.IsNullOrWhiteSpace(value) ? "—" : value;
            _copy.Header = Texts.T("diagnostics.copy");
            _copy.IsEnabled = !string.IsNullOrWhiteSpace(value);
            string full = description ?? label + " · " + _value.Text;
            AutomationProperties.SetName(_value, full);
            ToolTip.SetTip(_value, full);
        }
    }

    private readonly Field[] _fields = [new(), new(), new(), new(), new(), new()];
    private int _columns;

    internal MediaSidebarFacts()
    {
        Name = "ReaderMediaSidebarFacts";
        ColumnSpacing = 12;
        RowSpacing = 4;
        foreach (Field field in _fields) Children.Add(field);
    }

    internal void Show(string location, string? author, string? posted, string file, string? size, string? dimensions, string? sizeDescription)
    {
        _fields[0].Show(Texts.T("media.fact.location"), location);
        _fields[1].Show(Texts.T("media.fact.author"), author);
        _fields[2].Show(Texts.T("media.fact.posted"), posted);
        _fields[3].Show(Texts.T("media.fact.file"), file);
        _fields[4].Show(Texts.T("archive.size"), size, sizeDescription);
        _fields[5].Show(Texts.T("media.fact.dimensions"), dimensions);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = availableSize.Width >= 560 ? 3 : availableSize.Width >= 384 ? 2 : 1;
        if (_columns != columns)
        {
            _columns = columns;
            ColumnDefinitions = columns == 3 ? new("*,*,*") : new("*,*");
            RowDefinitions = columns == 2 ? new("Auto,Auto,Auto") : new("Auto,Auto");
        }
        // At the smallest width only location/author and date/dimensions remain; all values retain their accessible labels.
        int layoutColumns = ColumnDefinitions.Count;
        double columnWidth = Math.Max(0, (availableSize.Width - (layoutColumns - 1) * ColumnSpacing) / layoutColumns);
        for (int column = 0; column < layoutColumns; ++column)
        {
            double labelWidth = 0;
            for (int i = column; i < _fields.Length && columns > 1; i += columns)
            {
                _fields[i].Label.IsVisible = true;
                _fields[i].Label.Measure(Size.Infinity);
                labelWidth = Math.Max(labelWidth, _fields[i].Label.DesiredSize.Width);
            }
            for (int i = column; i < _fields.Length; i += layoutColumns)
            {
                Field field = _fields[i];
                field.Label.IsVisible = columns > 1;
                field.IsVisible = columns > 1 || i is 0 or 1 or 2 or 5;
                field.ColumnSpacing = columns == 1 ? 0 : 4;
                field.ColumnDefinitions[0].Width = new GridLength(Math.Min(columnWidth * 0.4, labelWidth));
                SetColumn(field, column);
                SetRow(field, columns == 1 ? i < 2 ? 0 : 1 : i / columns);
            }
        }
        return base.MeasureOverride(availableSize);
    }
}
