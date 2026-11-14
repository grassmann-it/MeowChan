using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// HB1: the one help button of the workspace. A 32 dip circle (<c>help-round</c> in WorkspaceStyles.axaml) with the catalog icon
/// <c>help-circle</c> and no text, never a glyph. It opens a <see cref="HelpPopover"/> with the structured text of <see cref="HelpKey"/>
/// (<c>&lt;key&gt;.title</c>, <c>.p1..</c>, <c>.li1..</c>; see <see cref="HelpContent.FromKeys"/>) or of <see cref="Source"/>.
/// The accessible name is "Help: &lt;title&gt;"; the popover text is also the help text of the button, so assistive technology reads it
/// without opening the popover. Name, tooltip and help text follow the language while the button is shown. The icon is created
/// when the button is shown, because it needs the platform's asset loader.
/// </summary>
public sealed class HelpButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public static readonly StyledProperty<string?> HelpKeyProperty = AvaloniaProperty.Register<HelpButton, string?>(nameof(HelpKey));

    private readonly HelpPopover _popover = new();
    private Control? _icon;
    private string? _helpTextOverride;

    public HelpButton()
    {
        Classes.Add("help-round");
        Classes.Add("subtle");
        _popover.Provider = Resolve;
        Flyout = _popover;
        Refresh();
    }

    /// <summary>The catalog prefix of the popover text (<c>help.stats</c> reads <c>help.stats.title</c>, <c>help.stats.p1</c> ...).</summary>
    public string? HelpKey { get => GetValue(HelpKeyProperty); set => SetValue(HelpKeyProperty, value); }

    /// <summary>Content built in code instead of from <see cref="HelpKey"/> (a text that depends on the state of the page); asked every time it is needed.</summary>
    internal Func<HelpContent>? Source { get => _source; set { _source = value; Refresh(); } }
    private Func<HelpContent>? _source;

    /// <summary>A link under the popover text ("More in Help"); the owner decides where it leads.</summary>
    internal Action? More { get; set; }

    /// <summary>
    /// The help text of the button when it should not be the concatenated popover text (the Statistics page keeps its one-paragraph
    /// note for assistive technology); null uses <see cref="HelpContent.PlainText"/>. Call <see cref="Refresh"/> after a language change.
    /// </summary>
    internal string? HelpTextOverride { get => _helpTextOverride; set { _helpTextOverride = value; Refresh(); } }

    /// <summary>The popover (test and capture seam).</summary>
    internal HelpPopover Popover => _popover;

    /// <summary>The content in the active language, or null when neither <see cref="Source"/> nor <see cref="HelpKey"/> is set.</summary>
    internal HelpContent? Resolve() =>
        Source?.Invoke() is { } built ? built with { More = built.More ?? More }
        : string.IsNullOrEmpty(HelpKey) ? null : HelpContent.FromKeys(HelpKey, More);

    /// <summary>Applies name, tooltip and help text in the active language.</summary>
    internal void Refresh()
    {
        HelpContent? content = Resolve();
        if (content == null) return;
        string name = Texts.T("help.button.name", content.Title);
        ToolTip.SetTip(this, name);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetHelpText(this, _helpTextOverride ?? content.PlainText);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HelpKeyProperty) Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Texts.Changed += OnLanguageChanged;
        Refresh();
        if (_icon != null) return;
        _icon = CommandBindings.NamedIcon("help-circle");
        AutomationProperties.SetAccessibilityView(_icon, AccessibilityView.Raw);
        Content = _icon;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Texts.Changed -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { Avalonia.Threading.Dispatcher.UIThread.Post(Refresh); return; }
        Refresh();
        // An open popover shows the new language at once.
        if (_popover.IsOpen && Resolve() is { } content) _popover.Render(content);
    }
}
