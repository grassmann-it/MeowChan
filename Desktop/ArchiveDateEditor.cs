using System;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Ursa.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// Keep manual raw input separately: the picker maps parse failures to null, which
// must never widen an archive search. Calendar selection explicitly replaces raw.
public sealed class ArchiveDateEditor : UserControl
{
    private readonly TextBox _raw = new() { PlaceholderText = "YYYY-MM-DD" };
    private sealed class CalendarPicker : DateOnlyPicker
    {
        private CalendarDisplayCulture? _displayCulture;
        protected override string? Format(DateOnly? value, string? format) => value?.ToString(format, CultureInfo.InvariantCulture);
        // Ursa also commits its read-only display on blur; match its ISO format.
        protected override DateOnly? Parse(string? text, string? format) =>
            DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date : null;
        protected override void OnLosingFocus(FocusChangingEventArgs e)
        {
            base.OnLosingFocus(e);
            _displayCulture?.RedirectHiddenHeaderRestoration(e);
        }
        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            if (_displayCulture?.PreserveHiddenHeaderNavigation(e) == true) return;
            base.OnLostFocus(e);
        }
        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            _displayCulture?.Dispose();
            base.OnApplyTemplate(e);
            if (e.NameScope.Find<DatePickerCalendarView>(DatePickerBase.PART_Calendar) is { } view
                && e.NameScope.Find<Popup>(DatePickerBase.PART_Popup) is { } popup)
                _displayCulture = new CalendarDisplayCulture(this, view, popup, day => FirstDayOfWeek = day, () => IsDropdownOpen = false);
            // Picker.IsReadOnly also disables opening the calendar in Ursa 2.2.
            // Restrict only its inner text input; manual raw text has its own field.
            if (e.NameScope.Find<TextBox>("PART_TextBox") is { } text) text.IsReadOnly = true;
        }
    }
    private readonly CalendarPicker _calendar = new() { DisplayFormat = "yyyy-MM-dd", NeedConfirmation = true, Width = 138 };
    private bool _syncing;
    internal event Action? Edited;
    internal string RawText
    {
        get => _raw.Text ?? "";
        set => _raw.Text = value;
    }
    internal void SetLabel(string label)
    {
        AutomationProperties.SetName(_raw, label + " (YYYY-MM-DD)");
        AutomationProperties.SetName(_calendar, label);
        ToolTip.SetTip(_calendar, label);
    }
    public ArchiveDateEditor()
    {
        Grid panel = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        panel.Children.Add(_raw); panel.Children.Add(_calendar); Grid.SetColumn(_calendar, 1); Content = panel;
        _raw.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            if (_syncing) return;
            _syncing = true;
            try { _calendar.SelectedDate = ParseRaw(validate: false); } catch (FormatException) { }
            finally { _syncing = false; }
            DataValidationErrors.ClearErrors(_raw);
            Edited?.Invoke();
        };
        _calendar.PropertyChanged += (_, e) =>
        {
            if (_syncing || e.Property.Name != nameof(DateOnlyPicker.SelectedDate)) return;
            _syncing = true;
            try { _raw.Text = _calendar.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; }
            finally { _syncing = false; }
            DataValidationErrors.ClearErrors(_raw);
            Edited?.Invoke();
        };
        // Enter commits a field only; searching is an explicit separate action.
        _raw.KeyDown += (_, e) => { if (e.Key == Key.Enter) e.Handled = true; };
    }
    internal DateOnly? ParseRaw(bool validate = true)
    {
        if (string.IsNullOrWhiteSpace(RawText)) return null;
        if (!DateOnly.TryParseExact(RawText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            || date.Year is < 2 or > 9998)
        {
            string message = Texts.T("archive.invalidDate", RawText);
            if (validate) DataValidationErrors.SetErrors(_raw, new object[] { message });
            throw new FormatException(message);
        }
        return date;
    }
}
