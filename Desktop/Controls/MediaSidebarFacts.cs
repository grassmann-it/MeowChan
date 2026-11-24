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

        internal string? Value { get; private set; }

        internal void Show(string label, string? value, string? description = null)
        {
            Value = value;
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
    private readonly TextBlock[] _separators = [new(), new()];
    private int _columns;

    internal MediaSidebarFacts()
    {
        Name = "ReaderMediaSidebarFacts";
        ColumnSpacing = 12;
        RowSpacing = 4;
        foreach (Field field in _fields) Children.Add(field);
        for (int row = 0; row < _separators.Length; ++row)
        {
            var dot = _separators[row];
            dot.Text = "·";
            dot.Classes.Add("workspace-meta");
            AutomationProperties.SetAccessibilityView(dot, AccessibilityView.Raw);
            SetRow(dot, row);
            SetColumn(dot, 1);
            Children.Add(dot);
        }
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

    internal bool ShowsFileAndSize => _columns > 1;

    internal void AddHiddenFields(MediaFactGrid target, bool compactVisible, bool splitSize)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            Field field = _fields[i];
            if ((compactVisible && field.IsVisible) || (i == 4 && splitSize)) continue;
            target.Add(field.Label.Text ?? "", field.Value, fileName: i == 3);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = availableSize.Width >= 560 ? 3 : availableSize.Width >= 384 ? 2 : 1;
        if (_columns != columns)
        {
            _columns = columns;
            ColumnDefinitions = columns switch { 3 => new("*,*,*"), 2 => new("*,*"), _ => new("Auto,Auto,*") };
            RowDefinitions = columns == 2 ? new("Auto,Auto,Auto") : new("Auto,Auto");
            ColumnSpacing = columns == 1 ? 4 : 12;
            foreach (var dot in _separators) dot.IsVisible = columns == 1;
            for (int i = 0; i < _fields.Length; ++i)
            {
                var field = _fields[i];
                bool narrow = columns == 1;
                field.Label.IsVisible = !narrow;
                field.IsVisible = !narrow || i is 0 or 1 or 2 or 5;
                field.ColumnSpacing = narrow ? 0 : 4;
                if (narrow) field.ColumnDefinitions[0].Width = new GridLength(0);
                SetColumn(field, narrow ? i % 2 * 2 : i % columns);
                SetRow(field, narrow ? i < 2 ? 0 : 1 : i / columns);
            }
        }

        if (columns == 1)
        {
            // Give location and date their natural width while leaving room for the author and dimensions.
            _fields[1].Measure(Size.Infinity);
            _fields[5].Measure(Size.Infinity);
            _separators[0].Measure(Size.Infinity);
            double valuesWidth = Math.Max(0, availableSize.Width - 2 * ColumnSpacing - _separators[0].DesiredSize.Width);
            double trailing = Math.Max(_fields[1].DesiredSize.Width, _fields[5].DesiredSize.Width);
            ColumnDefinitions[0].MaxWidth = valuesWidth - Math.Min(trailing, valuesWidth / 2);
        }
        else
        {
            double columnWidth = Math.Max(0, (availableSize.Width - (columns - 1) * ColumnSpacing) / columns);
            for (int column = 0; column < columns; ++column)
            {
                double labelWidth = 0;
                for (int i = column; i < _fields.Length; i += columns)
                {
                    _fields[i].Label.Measure(Size.Infinity);
                    labelWidth = Math.Max(labelWidth, _fields[i].Label.DesiredSize.Width);
                }
                for (int i = column; i < _fields.Length; i += columns)
                    _fields[i].ColumnDefinitions[0].Width = new GridLength(Math.Min(columnWidth * 0.4, labelWidth));
            }
        }
        return base.MeasureOverride(availableSize);
    }
}
