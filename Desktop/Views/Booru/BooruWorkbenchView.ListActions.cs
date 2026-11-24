using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private readonly BooruResultActionRow.LabelWidths _listActionWidths = new();

    private BooruResultActionRow CreateListActionRow(BooruResultRowView row, ResultActions result, Button gallery, Border card)
    {
        string postId = result.PostId;
        BooruResultActionRow actions = new(postId, _listActionWidths, CreateListStatus(row), () => card.Focus(NavigationMethod.Tab));
        AddResultCommands(row, result, actions);
        ResultCommand[] commands = result.Commands(row).ToArray();
        Button[] buttons = actions.Children.OfType<Button>().ToArray();
        for (int i = 0; i < commands.Length; i++)
        {
            ResultCommand command = commands[i];
            string label = command.Action switch
            {
                ResultAction.Load => BooruResultActionRow.ShortLabel(row.Load == BooruRowLoad.AlreadyLoaded ? "again" : "load"),
                ResultAction.Retry when row.Load == BooruRowLoad.RefusalPending => command.Label,
                ResultAction.Retry => BooruResultActionRow.ShortLabel("again"),
                ResultAction.Cancel => BooruResultActionRow.ShortLabel("cancel"),
                ResultAction.Open => BooruResultActionRow.ShortLabel("open"),
                ResultAction.Copy => BooruResultActionRow.ShortLabel("copy"),
                _ => BooruResultActionRow.ShortLabel("media")
            };
            string icon = command.Action == ResultAction.Copy ? "copy" : command.Icon;
            bool extra = command.Action == ResultAction.ShowInMedia || (command.Action == ResultAction.Cancel && i > 0);
            actions.Add(buttons[i], label, icon, i == 0, extra, menu => AddResultItem(menu, result, command, command.Prefix));
        }

        actions.Children.Add(gallery);
        actions.Add(gallery, Texts.T("booru.gallery.name"), CommandCatalog.Get("gallery.open").Icon, primary: false, extra: false,
            menu => menu.Command("gallery.open", () => { if (result.IsCurrent()) OpenGallery(postId); }, showShortcut: false));
        actions.Finish();
        return actions;
    }

    private static TextBlock CreateListStatus(BooruResultRowView row)
    {
        string text = row.Load switch
        {
            BooruRowLoad.AlreadyLoaded when row.Post.Original?.ExpectedSize is { } bytes =>
                Texts.T("booru.ui.tile.local") + " · " + Size(bytes),
            BooruRowLoad.Idle when row.Post.IsDeleted || row.Post.IsBanned || row.Post.OriginalAccess != BooruOriginalAccess.Available =>
                BooruText.Original(row.Post.OriginalAccess, row.Post.IsDeleted || row.Post.IsBanned),
            _ => BooruText.Load(row) ?? BooruResultActionRow.ShortLabel("idle")
        };
        TextBlock status = new()
        {
            Name = "BooruListStatus:" + row.Post.PostId, Text = text, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Classes = { "workspace-meta" }
        };
        status.Classes.Add(row.Load switch
        {
            BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.AlreadyLoaded => "workspace-status-success",
            BooruRowLoad.Failed or BooruRowLoad.DownloadFailed or BooruRowLoad.LinkFailed or BooruRowLoad.RefusalPending => "workspace-error",
            BooruRowLoad.Idle or BooruRowLoad.Handing => "workspace-meta",
            _ => "workspace-warning"
        });
        ToolTip.SetTip(status, text);
        AutomationProperties.SetName(status, text);
        return status;
    }
}

internal sealed class BooruResultActionRow : Panel
{
    private const int HeightDip = BooruListLayoutSettings.ActionHeight;
    private const int Gap = 4;
    private const int IconSize = 12;
    private const int LabelInset = 8;
    private const int StatusGap = 12;
    private const int StatusMinimumWidth = 96;

    internal static string ShortLabel(string key) => Texts.T("booru.ui.result.short." + key);

    private sealed record Entry(Button Button, string Icon, bool Primary, bool Extra, Action<ContextMenuBuilder> AddToMenu)
    {
        internal bool? LabelShown { get; set; }
    }

    // Use the standard commands and the longest primary label even for a row with fewer commands.
    internal sealed class LabelWidths
    {
        internal readonly record struct Font(Typeface Face, double Size, FlowDirection Direction, double Borders)
        {
            internal static Font From(Button button) => new(
                new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                button.FontSize, button.FlowDirection, button.BorderThickness.Left + button.BorderThickness.Right);
        }

        private readonly Dictionary<(string Label, Font Font), double> _widths = new();
        private (Font Primary, Font Secondary, string Language) _style;
        internal double Primary { get; private set; }
        private double _all;

        internal int Tier(Button primary, Button secondary, double width)
        {
            var style = (Font.From(primary), Font.From(secondary), Texts.Language);
            if (!_style.Equals(style))
            {
                _style = style;
                _widths.Clear();
                Primary = Math.Max(Width("load", _style.Primary), Math.Max(Width("again", _style.Primary), Width("cancel", _style.Primary)));
                _all = Primary + Width("open", _style.Secondary) + Width("copy", _style.Secondary)
                    + Measure(Texts.T("booru.gallery.name"), _style.Secondary) + 3 * Gap;
            }

            return width >= _all ? 1 : width >= Primary + 3 * (HeightDip + Gap) ? 2 : 3;

            double Width(string key, Font font) => Measure(ShortLabel(key), font);
        }

        internal double Measure(string label, Font font)
        {
            if (_widths.TryGetValue((label, font), out double width)) return width;
            FormattedText text = new(label, Texts.Culture, font.Direction, font.Face, font.Size, null);
            width = Math.Max(HeightDip, Math.Ceiling(text.Width + IconSize + Gap + 2 * LabelInset + font.Borders));
            _widths.Add((label, font), width);
            return width;
        }
    }

    private readonly LabelWidths _labels;
    private readonly List<Entry> _entries = [];
    private readonly TextBlock _status;
    private readonly Button _more;
    private Entry[] _overflow = [];
    private Rect[] _slots = [];

    internal BooruResultActionRow(string postId, LabelWidths labels, TextBlock status, Action returnFocus)
    {
        Name = "BooruResultActions:" + postId;
        _labels = labels;
        _status = status;
        _more = new Button { Name = Name + ".More", Classes = { "ghost" }, Content = string.Empty };
        PrepareButton(_more);
        ShowIcon(_more, "more-horizontal");
        _more.Click += (_, _) => ToggleOverflow(returnFocus);
        _more.DetachedFromVisualTree += (_, _) => _more.ContextMenu?.Close();
    }

    internal void Add(Button button, string label, string icon, bool primary, bool extra, Action<ContextMenuBuilder> addToMenu)
    {
        button.Content = label;
        PrepareButton(button);
        _entries.Add(new Entry(button, icon, primary, extra, addToMenu));
    }

    internal void Finish() => Children.AddRange([_more, _status]);

    private static void PrepareButton(Button button)
    {
        button.Width = double.NaN;
        button.Height = HeightDip;
        button.MinWidth = 0;
        button.MinHeight = 0;
        button.Focusable = false;
        button.IsTabStop = false;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button[!Button.FontSizeProperty] = new DynamicResourceExtension("Workspace.Text.Meta");
    }

    private static void ShowIcon(Button button, string icon)
    {
        button.Padding = new Thickness(0);
        IconButtons.Record(button, icon);
        button.ContentTemplate = new FuncDataTemplate<object>((_, _) => CommandBindings.NamedIcon(icon, IconSize));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        int tier = _labels.Tier(_entries[0].Button, _entries[^1].Button, width);
        var widths = new double[_entries.Count];
        double needed = -Gap;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Button.IsVisible) continue;
            double labelWidth = _labels.Measure((string)entry.Button.Content!, LabelWidths.Font.From(entry.Button));
            widths[i] = tier == 1 || (tier == 2 && entry.Primary)
                ? entry.Primary ? Math.Max(_labels.Primary, labelWidth) : labelWidth
                : HeightDip;
            needed += widths[i] + Gap;
        }
        for (int i = widths.Length - 1; i >= 0 && needed > width; i--)
        {
            if (!_entries[i].Extra || widths[i] == 0) continue;
            needed -= widths[i] - HeightDip;
            widths[i] = HeightDip;
        }

        List<Entry> overflow = [];
        if (needed > width)
        {
            needed += HeightDip + Gap;
            HideEntries(extras: true);
            HideEntries(extras: false);
        }

        Entry[] orderedOverflow = _entries.Where(overflow.Contains).ToArray();
        if (!_overflow.SequenceEqual(orderedOverflow))
        {
            _more.ContextMenu?.Close();
            _overflow = orderedOverflow;
        }

        _slots = new Rect[Children.Count];
        double x = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            double buttonWidth = widths[i];
            bool displayed = buttonWidth > 0;
            entry.Button.Opacity = displayed ? 1 : 0;
            entry.Button.IsHitTestVisible = displayed;
            AutomationProperties.SetAccessibilityView(entry.Button, displayed ? AccessibilityView.Content : AccessibilityView.Raw);
            if (displayed)
            {
                bool showLabel = buttonWidth > HeightDip;
                if (entry.LabelShown != showLabel)
                {
                    entry.LabelShown = showLabel;
                    if (showLabel)
                    {
                        entry.Button.Padding = new Thickness(LabelInset, 0);
                        CommandBindings.ShowIconBeforeText(entry.Button, entry.Icon, IconSize);
                    }
                    else ShowIcon(entry.Button, entry.Icon);
                }
            }
            Place(i, buttonWidth);
        }

        _more.IsVisible = _overflow.Length > 0;
        if (_more.IsVisible)
        {
            ToolTip.SetTip(_more, Texts.T("workview.more"));
            AutomationProperties.SetName(_more, Texts.Count("workview.moreCount", _overflow.Length));
        }
        Place(_entries.Count, _more.IsVisible ? Math.Min(HeightDip, Math.Max(0, width - x)) : 0);
        x += StatusGap - Gap;
        Place(_entries.Count + 1, width - x >= StatusMinimumWidth ? width - x : 0);
        return new Size(width, HeightDip);

        void HideEntries(bool extras)
        {
            for (int i = widths.Length - 1; i >= 0 && needed > width; i--)
            {
            if (_entries[i].Extra != extras || widths[i] == 0) continue;
                needed -= widths[i] + Gap;
                widths[i] = 0;
                overflow.Add(_entries[i]);
            }
        }

        void Place(int index, double elementWidth)
        {
            Control control = Children[index];
            if (index >= _entries.Count) control.IsVisible = elementWidth > 0;
            if (elementWidth <= 0) return;
            _slots[index] = new Rect(x, 0, elementWidth, HeightDip);
            control.Measure(_slots[index].Size);
            x += elementWidth + Gap;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < _slots.Length; i++) Children[i].Arrange(_slots[i]);
        return finalSize;
    }

    private void ToggleOverflow(Action returnFocus)
    {
        if (_more.ContextMenu is { } current)
        {
            current.Close();
            return;
        }
        ContextMenuBuilder builder = new();
        foreach (Entry entry in _overflow) entry.AddToMenu(builder);
        if (builder.Build() is not { } menu) return;
        _more.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_more.ContextMenu, menu)) _more.ContextMenu = null;
            if (_more.IsAttachedToVisualTree()) returnFocus();
        };
        menu.Open(_more);
    }
}
