using System;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

internal sealed class MediaFactGrid : Grid
{
    protected override Type StyleKeyOverride => typeof(Grid);

    internal MediaFactGrid(string name)
    {
        Name = name;
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        ColumnSpacing = WorkspaceMetrics.MediaPanelSectionGap;
        RowSpacing = 4;
    }

    internal void Add(string label, string? value, string? name = null, bool fileName = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        TextBlock key = new() { Text = label, Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
        SelectableTextBlock text = fileName ? new FileNameValue(value) : new SelectableTextBlock
        {
            Text = value, Classes = { "media-fact-value" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2
        };
        text.Name = name;
        ToolTip.SetTip(text, value);
        Children.Add(key);
        Children.Add(text);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        bool stacked = availableSize.Width < WorkspaceMetrics.MediaFactStackWidth;
        ColumnDefinitions[0].MaxWidth = stacked ? WorkspaceMetrics.MediaFactLabelWidth
            : Math.Min(WorkspaceMetrics.MediaFactLabelWidth, availableSize.Width * 0.4);
        int rows = stacked ? Children.Count : Children.Count / 2;
        if (RowDefinitions.Count != rows)
        {
            RowDefinitions.Clear();
            for (int i = 0; i < rows; ++i) RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        RowSpacing = stacked ? 0 : 4;
        for (int i = 0; i < Children.Count; ++i)
        {
            Control child = Children[i];
            bool label = i % 2 == 0;
            SetColumn(child, stacked || label ? 0 : 1);
            SetColumnSpan(child, stacked ? 2 : 1);
            SetRow(child, stacked ? i : i / 2);
            child.Margin = new Thickness(0, stacked && i > 0 ? label ? 8 : 2 : 0, 0, 0);
        }
        return base.MeasureOverride(availableSize);
    }

    private sealed class FileNameValue : SelectableTextBlock
    {
        private readonly string _fullName;

        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        internal FileNameValue(string fullName)
        {
            _fullName = fullName;
            Classes.Add("media-fact-value");
            TextWrapping = TextWrapping.NoWrap;
            TextTrimming = TextTrimming.CharacterEllipsis;
            MaxLines = 1;
            AutomationProperties.SetName(this, fullName);
            MenuItem copy = new() { Header = Texts.T("diagnostics.copy"), Name = "MediaFactCopy" };
            copy.Click += (_, _) => OpenActions.Start(CopyAsync, "diagnostics.copy");
            ContextMenu = new ContextMenu { Items = { copy } };
        }

        private async System.Threading.Tasks.Task CopyAsync()
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_fullName);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            Text = FitFileName(availableSize.Width);
            return base.MeasureOverride(availableSize);
        }

        private string FitFileName(double width)
        {
            if (Fits(_fullName, width)) return _fullName;
            int[] elements = StringInfo.ParseCombiningCharacters(_fullName);
            int dot = _fullName.LastIndexOf('.');
            int extension = dot > 0 ? Array.BinarySearch(elements, dot) : -1;
            int suffixLength = extension >= 0 ? elements.Length - extension : 0;
            if (suffixLength > 0 && !Fits("…" + _fullName[dot..], width)) suffixLength = 0;
            string best = suffixLength > 0 ? "…" + _fullName[dot..] : "…";
            int low = suffixLength + 1;
            int high = elements.Length - 1;
            while (low <= high)
            {
                int kept = (low + high) / 2;
                int suffix = Math.Max(suffixLength, kept / 2);
                int prefix = kept - suffix;
                string candidate = _fullName[..elements[prefix]] + "…" + (suffix == 0 ? "" : _fullName[elements[^suffix]..]);
                if (Fits(candidate, width))
                {
                    best = candidate;
                    low = kept + 1;
                }
                else high = kept - 1;
            }
            return best;
        }

        private bool Fits(string text, double width) => double.IsPositiveInfinity(width)
            || new FormattedText(text, Texts.Culture, FlowDirection,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground).Width <= width;
    }
}
