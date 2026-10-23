using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.Desktop;

// Ursa 2.2 reads process culture for calendar names and exposes no local Culture.
// Adapt only its public template parts and public button data; never change a
// thread culture or the selected date. Recheck these parts on a vendor upgrade.
internal sealed class CalendarDisplayCulture : IDisposable
{
    private readonly Control _owner;
    private readonly DatePickerCalendarView _calendar;
    private readonly Action<DayOfWeek> _setFirstDay;
    private readonly Popup _popup;
    private readonly Action _closePopup;
    private long _focusGeneration;
    private readonly List<AvaloniaObject> _observed = [];
    private Grid? _monthGrid, _yearGrid;
    private Button? _month, _year, _header, _previous, _next;
    private bool _active, _refreshing;
    private readonly HashSet<string> _monthNames = new(StringComparer.Ordinal);

    internal CalendarDisplayCulture(Control owner, DatePickerCalendarView calendar, Popup popup, Action<DayOfWeek> setFirstDay, Action closePopup)
    {
        _owner = owner; _calendar = calendar; _popup = popup; _setFirstDay = setFirstDay; _closePopup = closePopup;
        owner.PropertyChanged += OnOwnerChanged;
        owner.AttachedToVisualTree += OnAttached;
        owner.DetachedFromVisualTree += OnDetached;
        calendar.TemplateApplied += OnTemplate;
        if (owner.IsAttachedToVisualTree()) Activate();
        calendar.ApplyTemplate();
        if (_monthGrid is null) BindParts(name => calendar.GetVisualDescendants().OfType<Control>().FirstOrDefault(part => part.Name == name));
    }
    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Activate();
    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Deactivate();
    private void Activate()
    {
        if (_active) return;
        _active = true; Texts.Changed += OnLanguageChanged; Refresh();
    }
    private void Deactivate()
    {
        if (!_active) return;
        _active = false; ++_focusGeneration; Texts.Changed -= OnLanguageChanged;
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(() => { if (_active) Refresh(); });
    }
    private void OnTemplate(object? sender, TemplateAppliedEventArgs e) => BindParts(name => e.NameScope.Find<Control>(name));
    private void BindParts(Func<string, Control?> find)
    {
        ++_focusGeneration;
        foreach (AvaloniaObject item in _observed) item.PropertyChanged -= OnPartChanged;
        _observed.Clear();
        _monthGrid = find(DatePickerCalendarView.PART_MonthGrid) as Grid;
        _yearGrid = find(DatePickerCalendarView.PART_YearGrid) as Grid;
        _month = find(DatePickerCalendarView.PART_MonthButton) as Button;
        _year = find(DatePickerCalendarView.PART_YearButton) as Button;
        _previous = find(DatePickerCalendarView.PART_PreviousButton) as Button;
        _next = find(DatePickerCalendarView.PART_NextButton) as Button;
        _header = find(DatePickerCalendarView.PART_HeaderButton) as Button;
        IEnumerable<AvaloniaObject?> parts = new AvaloniaObject?[] { _month, _header, _yearGrid }
            .Concat(_monthGrid?.Children.OfType<TextBlock>() ?? Enumerable.Empty<TextBlock>())
            .Concat(_yearGrid?.Children.OfType<DatePickerCalendarYearButton>() ?? Enumerable.Empty<DatePickerCalendarYearButton>());
        foreach (AvaloniaObject item in parts.OfType<AvaloniaObject>())
        { _observed.Add(item); item.PropertyChanged += OnPartChanged; }
        Refresh();
    }
    private void OnPartChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ContentControl.ContentProperty || e.Property == TextBlock.TextProperty || e.Property == Visual.IsVisibleProperty) Refresh();
    }
    private void OnOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is "IsDropdownOpen" or "SelectedDate" or "SelectedStartDate" or "SelectedEndDate") ++_focusGeneration;
    }
    internal bool RedirectHiddenHeaderRestoration(FocusChangingEventArgs e)
    {
        // FocusManager may restore a previous outside focus scope when a header
        // hides itself. That path is cancellable: redirect before focus commits,
        // rather than treating the automatic restoration as an intentional blur.
        if (!_active || e.Canceled || e.NavigationMethod != NavigationMethod.Unspecified || e.KeyModifiers != KeyModifiers.None
            || e.NewFocusedElement is not Control proposed || e.OldFocusedElement is not Button old
            || (!ReferenceEquals(old, _month) && !ReferenceEquals(old, _year)) || old.IsVisible
            || !old.IsAttachedToVisualTree() || !_owner.IsAttachedToVisualTree() || !_owner.IsEffectivelyVisible || !_owner.IsEffectivelyEnabled
            || !_calendar.IsAttachedToVisualTree() || !_calendar.IsEffectivelyVisible || !_calendar.IsEffectivelyEnabled || _yearGrid is not { IsVisible: true }
            || !_popup.IsOpen || _popup.Child is not { } child || !child.IsAttachedToVisualTree()
            || TopLevel.GetTopLevel(child) is not { } popupRoot || !ReferenceEquals(TopLevel.GetTopLevel(_calendar), popupRoot)
            || (!ReferenceEquals(_calendar, child) && !_calendar.GetVisualAncestors().Contains(child))
            || TopLevel.GetTopLevel(_owner) is not Window { IsActive: true }
            || !ReferenceEquals(popupRoot.FocusManager?.GetFocusedElement(), old)
            || ReferenceEquals(proposed, child) || proposed.GetVisualAncestors().Contains(child)) return false;
        Button? target = new[] { _previous, _next }.FirstOrDefault(button => button is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true }
            && button.IsAttachedToVisualTree() && ReferenceEquals(TopLevel.GetTopLevel(button), popupRoot));
        return target is not null && e.TrySetNewFocusedElement(target);
    }
    internal bool PreserveHiddenHeaderNavigation(FocusChangedEventArgs e)
    {
        long generation = ++_focusGeneration;
        if (!_active || e.NewFocusedElement is not null || e.OldFocusedElement is not Button old
            || (!ReferenceEquals(old, _month) && !ReferenceEquals(old, _year)) || old.IsVisible
            || !old.IsAttachedToVisualTree() || !_owner.IsAttachedToVisualTree() || !_owner.IsEffectivelyVisible || !_owner.IsEffectivelyEnabled
            || !_calendar.IsAttachedToVisualTree() || !_calendar.IsEffectivelyVisible || !_calendar.IsEffectivelyEnabled || _yearGrid is not { IsVisible: true }
            || !_popup.IsOpen || _popup.Child is not { } child || !child.IsAttachedToVisualTree()
            || TopLevel.GetTopLevel(child) is not { } popupRoot || !ReferenceEquals(TopLevel.GetTopLevel(_calendar), popupRoot)
            || (!ReferenceEquals(_calendar, child) && !_calendar.GetVisualAncestors().Contains(child))
            || TopLevel.GetTopLevel(_owner) is not Window { IsActive: true } ownerRoot
            || popupRoot.FocusManager?.GetFocusedElement() is not null) return false;
        Button? target = new[] { _previous, _next }.FirstOrDefault(button => button is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true });
        if (target is null) return false;
        // Avalonia clears focus when Ursa hides its own month/year header. Its
        // non-cancellable LostFocus(null) must finish before selecting a live
        // successor; synchronous reentry would confuse KeyboardDevice's update.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_active || generation != _focusGeneration || !_popup.IsOpen || !ReferenceEquals(_popup.Child, child)
                || !_owner.IsAttachedToVisualTree() || !ReferenceEquals(TopLevel.GetTopLevel(_owner), ownerRoot)
                || !child.IsAttachedToVisualTree() || !ReferenceEquals(TopLevel.GetTopLevel(child), popupRoot)
                || !_calendar.IsAttachedToVisualTree() || !ReferenceEquals(TopLevel.GetTopLevel(_calendar), popupRoot)
                || (!ReferenceEquals(_calendar, child) && !_calendar.GetVisualAncestors().Contains(child))) return;
            if (popupRoot.FocusManager?.GetFocusedElement() is { } current)
            {
                // A real outside focus may occur after null focus without routing
                // another LostFocus through this picker. Close, never steal it.
                if (current is not Control control || (!ReferenceEquals(control, child) && !control.GetVisualAncestors().Contains(child))) _closePopup();
                return;
            }
            if (!ownerRoot.IsActive) { _closePopup(); return; }
            if (old.IsVisible || !_owner.IsEffectivelyVisible || !_owner.IsEffectivelyEnabled
                || !_calendar.IsEffectivelyVisible || !_calendar.IsEffectivelyEnabled || !target.IsAttachedToVisualTree()
                || !ReferenceEquals(TopLevel.GetTopLevel(target), popupRoot) || !target.IsEffectivelyVisible || !target.IsEffectivelyEnabled) return;
            target.Focus();
        }, DispatcherPriority.Input);
        return true;
    }
    private static DateTimeFormatInfo GregorianFormat(CultureInfo culture)
    {
        // Match Ursa2.2 DateTimeHelper without touching process culture: its
        // displayed calendar is Gregorian even under Thai/Arabic calendars.
        if (culture.Calendar is GregorianCalendar) return culture.DateTimeFormat;
        GregorianCalendar? calendar = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
        DateTimeFormatInfo info = new CultureInfo(calendar is null ? CultureInfo.InvariantCulture.Name : culture.Name).DateTimeFormat;
        info.Calendar = calendar ?? new GregorianCalendar();
        return info;
    }
    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            DateTimeFormatInfo info = GregorianFormat(Texts.Culture);
            _monthNames.UnionWith(GregorianFormat(CultureInfo.CurrentCulture).AbbreviatedMonthNames.Take(12));
            _monthNames.UnionWith(info.AbbreviatedMonthNames.Take(12));
            _setFirstDay(info.FirstDayOfWeek);
            _calendar.SetCurrentValue(DatePickerCalendarView.FirstDayOfWeekProperty, info.FirstDayOfWeek);
            if (_monthGrid is not null)
            {
                foreach (TextBlock day in _monthGrid.Children.OfType<TextBlock>().Where(text => Grid.GetRow(text) == 0))
                    day.Text = info.ShortestDayNames[((int)info.FirstDayOfWeek + Grid.GetColumn(day)) % 7];
                // The middle day cell always belongs to the displayed Gregorian
                // month, regardless of the selected date or the week's start.
                if (_month is not null && _monthGrid.Children.OfType<DatePickerCalendarDayButton>().Skip(14).FirstOrDefault()?.DataContext is DateOnly date)
                    _month.Content = info.AbbreviatedMonthNames[date.Month - 1];
            }
            // Ursa's public year-view header contains an integer year; decade /
            // century headers contain ranges. Only year view has month buttons.
            if (_yearGrid is { IsVisible: true } && _header?.Content is int)
                foreach (DatePickerCalendarYearButton month in _yearGrid.Children.OfType<DatePickerCalendarYearButton>())
                    if (month.Content is string name && _monthNames.Contains(name))
                        month.Content = info.AbbreviatedMonthNames[Grid.GetRow(month) * 3 + Grid.GetColumn(month)];
        }
        finally { _refreshing = false; }
    }
    public void Dispose()
    {
        Deactivate();
        ++_focusGeneration;
        _owner.PropertyChanged -= OnOwnerChanged;
        _owner.AttachedToVisualTree -= OnAttached;
        _owner.DetachedFromVisualTree -= OnDetached;
        _calendar.TemplateApplied -= OnTemplate;
        foreach (AvaloniaObject item in _observed) item.PropertyChanged -= OnPartChanged;
        _observed.Clear();
    }
}
