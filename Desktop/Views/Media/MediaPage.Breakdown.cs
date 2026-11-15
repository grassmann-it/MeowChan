using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// Where was it found: the row under the search field that says in which fields the words of the text hit
// ("All 57", "12 in file names", "5 in thread titles", "3 as tag") and lets the user narrow the search to some of them.
// The counts come from the filter pass that already runs (MediaLibraryFilterEngine's text tally) and are tiles, like the grid's count;
// the rules of the row (parts, toggles, when it is there) are MediaTextBreakdown. The scope (_textScope, Filters.cs) is the same one the
// in: token of the query and the scope chip in the field show. Media only: post text is not part of the index.
public sealed partial class MediaPage
{
    /// <summary>How long the summary of the row waits for a quiet moment before a screen reader hears it, so typing announces once.</summary>
    internal const int BreakdownAnnounceDelayMs = 700;

    private readonly MediaTextBreakdownRow _breakdown = new() { Name = "MediaTextBreakdown", IsVisible = false };
    // The live region: in the tree at no size, only its text is for a screen reader.
    private readonly TextBlock _breakdownLive = new()
    {
        Name = "MediaTextBreakdownAnnouncement", Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false, Focusable = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private MediaTextCounts? _breakdownCounts;
    private string _breakdownText = string.Empty;
    private string? _breakdownPending, _breakdownAnnounced;
    private DispatcherTimer? _breakdownTimer;

    /// <summary>The row where the text was found (tests and smokes).</summary>
    internal MediaTextBreakdownRow Breakdown => _breakdown;
    /// <summary>The fields the text is searched in (<see cref="MediaTextFields.All"/> unless narrowed).</summary>
    internal MediaTextFields TextScope => _textScope;
    /// <summary>What the screen reader summary of the row says now (null when none), for tests and smokes.</summary>
    internal string? BreakdownAnnouncement => _breakdownLive.Text;

    private Control BuildBreakdown()
    {
        AutomationProperties.SetLiveSetting(_breakdownLive, AutomationLiveSetting.Polite);
        _breakdown.PartInvoked += field => SetTextScope(MediaTextBreakdown.Toggle(_textScope, field));
        _breakdown.EverywhereInvoked += (_, _) =>
        {
            SetTextScope(MediaTextFields.All);
            // The button went away with the note: "All" is the nearest place for the keyboard.
            Dispatcher.UIThread.Post(() => { if (!_lifetime.IsCancellationRequested) _breakdown.PartButtons.FirstOrDefault()?.Focus(NavigationMethod.Directional); }, DispatcherPriority.Loaded);
        };
        _breakdown.EscapeRequested += (_, _) => _nameFilter.Focus(NavigationMethod.Tab);
        // The clear mark of the field ends the search: the scope goes with the text it chose the fields for. (Emptying the box by hand keeps it.)
        _nameFilter.ClearButton.Click += (_, _) => ResetTextScope();
        // Escape in the field ends the search the same way: the text goes (the field's own Escape) and so does the scope, while the other filter chips stay.
        _nameFilter.AddHandler(InputElement.KeyDownEvent, OnNameFilterEscape, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return _breakdown;
    }

    /// <summary>Back to searching every field, shown at once. The scope chip, the name chip and the row follow; nothing is filtered again here (the text, or its going, does that).</summary>
    private bool ResetTextScope()
    {
        if (_textScope == MediaTextFields.All) return false;
        _textScope = MediaTextFields.All;
        RenderFilterChips();
        RefreshBreakdown();
        return true;
    }

    // Tunnel: before the field takes Escape to clear its text. With text, the field clears it next; without text the narrowed scope is what this Escape ends,
    // so it stops here and the following Escape leaves the field as before. A field that searches everywhere is left to its own Escape.
    private void OnNameFilterEscape(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || _lifetime.IsCancellationRequested || !ResetTextScope()) return;
        if (!_nameFilter.HasText) e.Handled = true;
    }

    /// <summary>Narrows (or widens) where the text is searched, shows it at once and filters again. The counts of the row do not depend on it.</summary>
    internal void SetTextScope(MediaTextFields scope)
    {
        if (_lifetime.IsCancellationRequested) return;
        scope = (scope & MediaTextFields.All) == 0 ? MediaTextFields.All : scope & MediaTextFields.All;
        if (scope == _textScope) { RefreshBreakdown(); return; }
        _textScope = scope;
        RefreshBreakdown(); // the choice shows now, while the new result is being made
        FilterEdited();
    }

    /// <summary>The names of the fields in a scope, in row order: "file names, thread titles".</summary>
    private static string TextScopeNames(MediaTextFields scope) =>
        string.Join(", ", MediaTextBreakdown.Selected(scope).Select(field => Texts.T("media.scope." + MediaTextBreakdown.Key(field))));

    // The chip in the search field: "All", the one field the text is searched in (capitalized: it stands alone), or "3 fields".
    private void UpdateScopeChip()
    {
        IReadOnlyList<MediaTextFields> selected = MediaTextBreakdown.Selected(_textScope);
        string? label = selected.Count == 0 ? null
            : selected.Count == 1 ? Capitalize(Texts.T("media.scope." + MediaTextBreakdown.Key(selected[0])))
            : Texts.Count("search.scope.media.some", selected.Count);
        _searchScope.LabelOverride = label;
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Texts.Culture) + text[1..];

    private string PartText(MediaBreakdownPart part) => part.Field == MediaTextFields.All
        ? Texts.Count("media.breakdown.all", part.Count)
        : Texts.Count("media.breakdown.field." + MediaTextBreakdown.Key(part.Field), part.Count);

    // Shows the row for the last settled counts and the scope as it is now. Cheap and idempotent: the status update calls it too.
    private void RefreshBreakdown()
    {
        if (_lifetime.IsCancellationRequested) return;
        MediaTextCounts? counts = _breakdownCounts;
        // The row stays while the text has words, even while a new result is being made (the grid moves once, not per keystroke);
        // it goes the moment the words go, and with a refused filter.
        if (_filterFailed || !MediaTextBreakdown.IsShown(counts, MediaLibraryFilter.HasText(_nameFilter.Text)))
        {
            if (_breakdown.IsVisible) { _breakdown.IsVisible = false; UpdateQueryHintVisibility(); }
            _breakdownPending = _breakdownAnnounced = null;
            _breakdownTimer?.Stop();
            _breakdownLive.Text = null;
            return;
        }
        List<MediaBreakdownPart> parts = MediaTextBreakdown.Parts(counts!, _textScope).ToList();
        string name = Texts.T("media.breakdown.name", MediaTextBreakdown.TextLabel(_breakdownText));
        // Only the settled result may say that a narrowed scope found nothing: the counts belong to the scope they were made with.
        string? elsewhere = counts!.NothingInScope && counts.Scope == _textScope
            ? Texts.T("media.breakdown.elsewhere", TextScopeNames(_textScope), counts.All) : null;
        string? note = counts.OnlyAcrossFields ? Texts.T("media.breakdown.across") : null;
        _breakdown.Show(new MediaTextBreakdownView(name,
            parts.Select(part => new MediaTextBreakdownView.Part(part.Field, PartText(part), part.Selected)).ToArray(),
            note, elsewhere, elsewhere == null ? null : Texts.T("media.breakdown.everywhere")));
        if (!_breakdown.IsVisible) { _breakdown.IsVisible = true; UpdateQueryHintVisibility(); }
        Announce(Texts.T("media.breakdown.summary", name, string.Join(", ", parts.Select(PartText)))
            + (elsewhere == null ? string.Empty : ". " + elsewhere) + (note == null ? string.Empty : ". " + note));
    }

    // One polite announcement per settled change: the text waits for a quiet moment and an unchanged summary says nothing again.
    private void Announce(string summary)
    {
        if (summary == (_breakdownPending ?? _breakdownAnnounced)) return;
        _breakdownPending = summary;
        DispatcherTimer timer = _breakdownTimer ??= CreateBreakdownTimer();
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(BreakdownAnnounceDelayMs);
        timer.Start();
    }

    private DispatcherTimer CreateBreakdownTimer()
    {
        DispatcherTimer timer = new();
        timer.Tick += (_, _) => { timer.Stop(); FlushBreakdownAnnouncement(); };
        return timer;
    }

    /// <summary>Says the waiting summary now (the timer's tick; tests and smokes call it instead of waiting).</summary>
    internal void FlushBreakdownAnnouncement()
    {
        _breakdownTimer?.Stop();
        if (_lifetime.IsCancellationRequested || _breakdownPending is not { } pending) return;
        _breakdownPending = null;
        _breakdownAnnounced = pending;
        if (_breakdown.IsVisible) _breakdownLive.Text = pending;
    }
}
