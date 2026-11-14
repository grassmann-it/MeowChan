using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A search carried over from another page (or pane) never replaces a different query the target field already holds.
/// Instead this line offers it: "From Archive: “lamp”. This field already has “desk”." with Use it / Keep current.
/// </summary>
public sealed class SearchCarryOffer : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _use = new() { Name = "SearchCarryUse" };
    private readonly Button _keep = new() { Name = "SearchCarryKeep" };
    private Action? _apply;
    private string _source = string.Empty, _query = string.Empty, _current = string.Empty;

    public SearchCarryOffer()
    {
        IsVisible = false;
        Classes.Add("search-carry-offer");
        Padding = new Thickness(8, 6);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        this[!BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Border");
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Polite);
        WrapPanel actions = new() { ItemSpacing = 6, LineSpacing = 4 };
        actions.Children.Add(_use); actions.Children.Add(_keep);
        Child = new StackPanel { Spacing = 6, Children = { _text, actions } };
        _use.Click += (_, _) => { Action? apply = _apply; Hide(); apply?.Invoke(); };
        _keep.Click += (_, _) => Hide();
        Localize();
    }

    /// <summary>The query waiting for a decision, or null.</summary>
    public string? PendingQuery => IsVisible ? _query : null;

    /// <summary>Shows the offer; <paramref name="apply"/> runs only when the user chooses Use it.</summary>
    public void Offer(string source, string query, string current, Action apply)
    {
        _source = source; _query = query; _current = current; _apply = apply;
        Localize();
        IsVisible = true;
    }

    public void Hide()
    {
        _apply = null;
        IsVisible = false;
    }

    /// <summary>Re-reads the texts (language change).</summary>
    public void Localize()
    {
        _text.Text = Texts.T("search.carry.offerText", _source, _query, _current);
        _use.Content = Texts.T("search.carry.offerUse");
        _keep.Content = Texts.T("search.carry.offerKeep");
        AutomationProperties.SetName(_use, Texts.T("search.carry.offerUse"));
        AutomationProperties.SetName(_keep, Texts.T("search.carry.offerKeep"));
    }

    /// <summary>
    /// The one carry-over rule for every field: an empty target or the same text takes the query at once;
    /// a different query already in the target is offered instead. Returns true when applied at once.
    /// </summary>
    public bool Carry(string source, string query, string current, Action apply)
    {
        if (current.Length == 0 || string.Equals(current, query, StringComparison.Ordinal))
        {
            Hide();
            apply();
            return true;
        }
        Offer(source, query, current, apply);
        return false;
    }
}
