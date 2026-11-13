using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one search field of the app: a text box with a leading magnifier, a placeholder that is also its
/// accessible name, a clear button and an optional result count.
/// <list type="bullet">
/// <item>Escape clears the text first and swallows that key, so a window, dialog or surface around the field
/// stays open. Escape in an empty field is offered to <see cref="ExitRequested"/>; when no handler claims it,
/// the key goes on unchanged and the owner (bar, surface, window) reacts as it did before.</item>
/// <item>Enter is not handled here; it belongs to the owner (apply, next hit, add).</item>
/// <item><see cref="Debounce"/> delays <see cref="QueryChanged"/> for live filters over in-memory data. Zero raises it
/// at once. Clearing always applies at once. Owners that need no delay may keep using TextChanged.</item>
/// </list>
/// Parts are built in code and use the workspace colour tokens, so no style file is needed.
/// </summary>
public class SearchBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public static readonly StyledProperty<string?> ResultTextProperty = AvaloniaProperty.Register<SearchBox, string?>(nameof(ResultText));

    private static readonly Lazy<Geometry> Magnifier = new(() => Geometry.Parse("M 11,6.5 A 4.5,4.5 0 1 1 2,6.5 A 4.5,4.5 0 1 1 11,6.5 Z M 10,10 L 14.5,14.5"));
    private static readonly Lazy<Geometry> Cross = new(() => Geometry.Parse("M 3,3 L 11,11 M 11,3 L 3,11"));

    private readonly Path _icon = new() { Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
    private readonly Path _cross = new() { Width = 14, Height = 14, Stretch = Stretch.None, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, IsHitTestVisible = false };
    private readonly Button _clear = new() { Name = "SearchBoxClear", Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = true, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly TextBlock _count = new TextBlock { Name = "SearchBoxCount", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
        TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false }.MetaText();
    private readonly StackPanel _tail = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private QueryDebounce? _debounce;
    private bool _silent, _geometryApplied;
    private string? _automatedName;

    public SearchBox()
    {
        _icon[!Shape.StrokeProperty] = new DynamicResourceExtension("Workspace.Muted");
        _cross[!Shape.StrokeProperty] = new DynamicResourceExtension("Workspace.Muted");
        _count[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
        _clear.Content = _cross;
        _clear.Click += (_, _) => ClearAndFocus();
        AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
        _tail.Children.Add(_count);
        _tail.Children.Add(_clear);
        InnerLeftContent = _icon;
        ApplyTexts();
    }

    /// <summary>Wait after the last edit before <see cref="QueryChanged"/>. Zero (default) raises it with every edit.</summary>
    public TimeSpan Debounce { get; set; }

    /// <summary>"12 results" beside the clear button. Hidden while the text has changed but its result has not arrived.</summary>
    public string? ResultText { get => GetValue(ResultTextProperty); set => SetValue(ResultTextProperty, value); }

    /// <summary>Whether the magnifier is shown (default).</summary>
    public bool ShowSearchIcon { get => _icon.IsVisible; set { _icon.IsVisible = value; ApplyLeadingContent(); } }

    private SearchScopeChip? _scopeChip;
    /// <summary>
    /// The scope chip shown inside the field in place of the magnifier (AP2/S5: one control, one height, one frame).
    /// A chip can sit in one field at a time: the owner moves it when it swaps fields. Alt+Down or F4 in the field
    /// opens the chip menu, so the chip itself needs no own Tab stop.
    /// </summary>
    public SearchScopeChip? ScopeChip
    {
        get => _scopeChip;
        set
        {
            if (ReferenceEquals(_scopeChip, value)) return;
            if (_scopeChip != null) _scopeChip.PropertyChanged -= OnScopeChipPropertyChanged;
            _scopeChip = value;
            if (value != null) value.PropertyChanged += OnScopeChipPropertyChanged;
            if (value != null && value.Owner is { } previous && !ReferenceEquals(previous, this)) previous.ScopeChip = null;
            if (value != null) value.Owner = this;
            ApplyLeadingContent();
        }
    }

    // A work view may hide the chip (Barebones, Text scope): the magnifier shows instead, and Alt+Down / F4 still opens the menu.
    private void OnScopeChipPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) ApplyLeadingContent();
    }

    private void ApplyLeadingContent()
    {
        object? leading = _scopeChip is { IsVisible: true } ? _scopeChip : _icon.IsVisible ? _icon : null;
        if (!ReferenceEquals(InnerLeftContent, leading)) InnerLeftContent = leading;
    }

    /// <summary>The text has changed and, after <see cref="Debounce"/>, is ready to be applied.</summary>
    public event EventHandler? QueryChanged;

    /// <summary>Escape in an empty field. Set Handled to keep the key from going on to the owner's own Escape handling.</summary>
    public event EventHandler<HandledEventArgs>? ExitRequested;

    public bool HasText => !string.IsNullOrEmpty(Text);
    public bool IsQueryPending => _debounce?.IsPending == true;

    // Test seams; set before the first edit.
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
    internal Action<Action>? Post { get; set; }
    internal Button ClearButton => _clear;

    /// <summary>True when the event came from a search field that holds text: that field takes this Escape to clear itself.</summary>
    public static bool OwnsEscape(object? source) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<SearchBox>().FirstOrDefault() is { HasText: true };

    /// <summary>Empties the field, applies that at once and keeps the focus in the field.</summary>
    public void ClearAndFocus()
    {
        if (HasText) Text = string.Empty;
        FlushQuery();
        Focus();
    }

    /// <summary>Sets the text for the owner's own reset: nothing waits and no <see cref="QueryChanged"/> follows.</summary>
    public void SetTextSilently(string? text)
    {
        CancelPendingQuery();
        _silent = true;
        try { Text = text ?? string.Empty; }
        finally { _silent = false; }
        UpdateTail();
    }

    public void CancelPendingQuery()
    {
        if (_debounce?.Cancel() == true) UpdateTail();
    }

    /// <summary>Applies a waiting query now.</summary>
    public void FlushQuery() => _debounce?.Flush();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && _scopeChip is { IsEffectivelyEnabled: true } chip && (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.Alt
            || e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None))
        {
            e.Handled = true;
            chip.ShowMenu();
            return;
        }
        if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            if (HasText) { ClearAndFocus(); e.Handled = true; return; }
            HandledEventArgs exit = new();
            ExitRequested?.Invoke(this, exit);
            if (exit.Handled) { e.Handled = true; return; }
        }
        base.OnKeyDown(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            if (!_silent) RequestQuery();
            UpdateTail();
        }
        else if (change.Property == ResultTextProperty || change.Property == IsReadOnlyProperty || change.Property == IsEnabledProperty) UpdateTail();
        else if (change.Property == PlaceholderTextProperty) NameFromPlaceholder(change.GetOldValue<string?>(), change.GetNewValue<string?>());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_geometryApplied)
        {
            // Geometry needs the rendering platform: build it when the field is shown, not when it is constructed.
            _icon.Data = Magnifier.Value;
            _cross.Data = Cross.Value;
            _geometryApplied = true;
        }
        Texts.Changed += OnTextsChanged;
        ApplyTexts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Texts.Changed -= OnTextsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ApplyTexts);

    private void ApplyTexts()
    {
        string name = Texts.T("search.clear");
        AutomationProperties.SetName(_clear, name);
        ToolTip.SetTip(_clear, name + " (Esc)");
    }

    // The placeholder names the field for assistive technology unless the owner chose another name.
    private void NameFromPlaceholder(string? previous, string? next)
    {
        string? current = AutomationProperties.GetName(this);
        if (!string.IsNullOrEmpty(current) && current != previous && current != _automatedName) return;
        _automatedName = next;
        AutomationProperties.SetName(this, next);
    }

    private void RequestQuery()
    {
        if (Debounce <= TimeSpan.Zero)
        {
            _debounce?.Cancel();
            QueryChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _debounce ??= new QueryDebounce(OnDebounced, Clock, Post);
        _debounce.Request(Debounce);
    }

    private void OnDebounced()
    {
        UpdateTail();
        QueryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTail()
    {
        _clear.IsVisible = HasText && IsEnabled && !IsReadOnly;
        string? result = ResultText;
        bool count = !string.IsNullOrEmpty(result) && !IsQueryPending;
        _count.Text = result;
        _count.IsVisible = count;
        object? tail = _clear.IsVisible || count ? _tail : null;
        if (!ReferenceEquals(InnerRightContent, tail)) InnerRightContent = tail;
    }
}
