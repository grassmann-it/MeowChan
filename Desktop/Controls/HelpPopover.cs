using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// HB1: the structured text of a help popover. A title, up to <see cref="MaxEntries"/> paragraphs and up to as many bullet points, and
/// optionally one link ("More in Help") that runs <see cref="More"/>. Built from catalog keys by <see cref="FromKeys"/>:
/// <c>&lt;prefix&gt;.title</c>, <c>&lt;prefix&gt;.p1</c>, <c>.p2</c> ... (a paragraph each), <c>&lt;prefix&gt;.li1</c>, <c>.li2</c> ... (a bullet each).
/// Reading stops at the first key that is missing, so a popover has exactly the paragraphs its catalog defines.
/// </summary>
internal sealed record HelpContent(string Title, IReadOnlyList<string> Paragraphs, IReadOnlyList<string> Bullets, Action? More = null)
{
    /// <summary>Documented ceiling per list (paragraphs, bullets): a help popover is a short note, not a manual (the plan says three).</summary>
    internal const int MaxEntries = 8;

    /// <summary>Reads the content for <paramref name="prefix"/> from the catalog in the active language.</summary>
    internal static HelpContent FromKeys(string prefix, Action? more = null) =>
        new(Texts.T(prefix + ".title"), Read(prefix + ".p"), Read(prefix + ".li"), more);

    private static string[] Read(string stem)
    {
        List<string> found = [];
        for (int index = 1; index <= MaxEntries; index++)
        {
            string key = stem + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string text = Texts.T(key);
            // Texts.T answers with the key itself when no language (not even English) has it.
            if (string.Equals(text, key, StringComparison.Ordinal)) break;
            found.Add(text);
        }
        return [.. found];
    }

    /// <summary>The whole text in reading order (title, paragraphs, bullets), one line each: the help text of the target element.</summary>
    internal string PlainText => string.Join("\n", new[] { Title }.Concat(Paragraphs).Concat(Bullets));
}

/// <summary>
/// HB1: the help flyout. A <see cref="HelpContent"/> in a column of at most <see cref="MaxContentWidth"/> dip: the title (Subtitle, one
/// heading for assistive technology), the paragraphs, the bullet points with a small dot, and the optional "More" link as a button.
/// The flyout takes focus when it opens (the column is focusable, named by the title), Esc closes it, and focus returns to the
/// control that opened it. Inset, outline and radius come from the <c>help-popover</c> class in WorkspaceStyles.axaml.
/// </summary>
internal sealed class HelpPopover : Flyout
{
    /// <summary>The widest the content column gets (the same limit as a tooltip).</summary>
    internal const double MaxContentWidth = 360;

    private StackPanel? _body;
    private Func<HelpContent?>? _provider;

    /// <summary>Where the content comes from; asked each time the popover opens, so it follows the language and the state of the page.</summary>
    internal Func<HelpContent?>? Provider { get => _provider; set => _provider = value; }

    internal HelpPopover()
    {
        Placement = PlacementMode.BottomEdgeAlignedRight;
        FlyoutPresenterClasses.Add("help-popover");
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _body?.Focus(), DispatcherPriority.Input);
        Closed += (_, _) => ReturnFocus();
        Opening += (_, _) => { if (_provider?.Invoke() is { } content) Render(content); };
    }

    /// <summary>Builds the column for <paramref name="content"/> and makes it the flyout content (also while it is open: language change).</summary>
    internal void Render(HelpContent content)
    {
        _body = Build(content, Hide);
        _body.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
            Hide();
            e.Handled = true;
        };
        Content = _body;
    }

    /// <summary>The column of a popover (a seam for tests: no flyout, no window).</summary>
    internal static StackPanel Build(HelpContent content, Action close)
    {
        StackPanel body = new() { Classes = { "help-popover-body" }, Spacing = 8, MaxWidth = MaxContentWidth, Focusable = true };
        AutomationProperties.SetName(body, content.Title);
        TextBlock title = new() { Text = content.Title, Classes = { "card-title" }, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(title, 2);
        body.Children.Add(title);
        foreach (string paragraph in content.Paragraphs)
            body.Children.Add(new TextBlock { Text = paragraph, TextWrapping = TextWrapping.Wrap });
        if (content.Bullets.Count > 0)
        {
            StackPanel list = new() { Spacing = 4 };
            foreach (string bullet in content.Bullets)
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
                Border dot = new() { Classes = { "help-popover-dot" } };
                AutomationProperties.SetAccessibilityView(dot, AccessibilityView.Raw);
                TextBlock text = new() { Text = bullet, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(dot);
                row.Children.Add(text);
                list.Children.Add(row);
            }
            body.Children.Add(list);
        }
        if (content.More is { } more)
        {
            Button link = new() { Content = Texts.T("help.popover.more"), Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(link, Texts.T("help.popover.more"));
            link.Click += (_, _) => { close(); more(); };
            body.Children.Add(link);
        }
        return body;
    }

    private void ReturnFocus()
    {
        Control? opener = Target;
        if (opener is { IsEffectivelyVisible: true })
            Dispatcher.UIThread.Post(() => opener.Focus(), DispatcherPriority.Input);
    }
}
