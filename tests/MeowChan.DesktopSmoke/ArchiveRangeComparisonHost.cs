using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Same raw ISO/year/inversion guard and explicit ArchiveSearchRequest commit as
// ArchiveDateEditor + ArchivePage. Only calendar endpoint presentation differs.
internal sealed class ArchiveRangeComparisonHost : StackPanel
{
    internal sealed class GuardedRangePicker : DateOnlyRangePicker
    {
        private readonly List<CalendarDisplayCulture> _displayCultures = [];
        protected override void OnLosingFocus(FocusChangingEventArgs e)
        {
            base.OnLosingFocus(e);
            foreach (CalendarDisplayCulture adapter in _displayCultures)
                if (adapter.RedirectHiddenHeaderRestoration(e)) break;
        }
        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            if (_displayCultures.Any(adapter => adapter.PreserveHiddenHeaderNavigation(e))) return;
            base.OnLostFocus(e);
        }
        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            foreach (CalendarDisplayCulture adapter in _displayCultures) adapter.Dispose();
            _displayCultures.Clear();
            base.OnApplyTemplate(e);
            foreach (string name in new[] { "PART_StartCalendar", "PART_EndCalendar" })
                if (e.NameScope.Find<DatePickerCalendarView>(name) is { } view && e.NameScope.Find<Popup>("PART_Popup") is { } popup)
                    _displayCultures.Add(new CalendarDisplayCulture(this, view, popup, day => FirstDayOfWeek = day, () => IsDropdownOpen = false));
            foreach (string name in new[] { "PART_StartTextBox", "PART_EndTextBox" })
                if (e.NameScope.Find<TextBox>(name) is { } box) box.IsReadOnly = true;
        }
    }
    internal readonly TextBox Start = new() { Name = "RangeRawStart", PlaceholderText = "YYYY-MM-DD" };
    internal readonly TextBox End = new() { Name = "RangeRawEnd", PlaceholderText = "YYYY-MM-DD" };
    internal readonly GuardedRangePicker Picker = new() { Name = "RealUrsaRangePicker", DisplayFormat = "yyyy-MM-dd" };
    internal readonly Button Apply = new() { Name = "RangeCommit", Content = "Apply first-seen range" };
    internal readonly TextBlock Error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    internal ArchiveSearchRequest? Applied { get; private set; }
    internal int Commits { get; private set; }
    private bool _syncing;
    internal ArchiveRangeComparisonHost()
    {
        Spacing = 12;
        Children.Add(new TextBlock { Text = "Isolated DateOnlyRangePicker — first registered locally — Europe/Berlin", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Children.Add(Start); Children.Add(End); Children.Add(Picker); Children.Add(Apply); Children.Add(Error);
        AutomationProperties.SetName(Start, "From (YYYY-MM-DD)"); AutomationProperties.SetName(End, "Through (YYYY-MM-DD)");
        AutomationProperties.SetName(Picker, "From and through, Europe/Berlin");
        foreach (TextBox raw in new[] { Start, End })
        {
            raw.KeyDown += (_, e) => { if (e.Key == Key.Enter) e.Handled = true; };
            raw.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBox.TextProperty || _syncing) return;
                _syncing = true;
                try
                {
                    // Preserve invalid raw text and do not let picker parse failure become null.
                    DateOnly? start = ArchiveComparisonChecks.Parse(Start.Text), end = ArchiveComparisonChecks.Parse(End.Text);
                    if (start.HasValue && end.HasValue && start > end) return;
                    Picker.SelectedStartDate = start; Picker.SelectedEndDate = end;
                }
                catch (FormatException) { }
                finally { _syncing = false; }
                DataValidationErrors.ClearErrors(raw);
            };
        }
        Picker.PropertyChanged += (_, e) =>
        {
            if (_syncing || (e.Property.Name != nameof(DateOnlyRangePicker.SelectedStartDate) && e.Property.Name != nameof(DateOnlyRangePicker.SelectedEndDate))) return;
            _syncing = true;
            try
            {
                Start.Text = Picker.SelectedStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
                End.Text = Picker.SelectedEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            }
            finally { _syncing = false; }
        };
        Apply.Click += (_, _) =>
        {
            try
            {
                DateOnly? start = ArchiveComparisonChecks.Parse(Start.Text), end = ArchiveComparisonChecks.Parse(End.Text);
                if (start.HasValue && end.HasValue && start > end) throw new FormatException(Texts.T("archive.invertedDates"));
                ArchiveSearchSpec spec = new(dateRange: start == null && end == null ? null : new ArchiveDateRange(start, end, "Europe/Berlin"));
                ArchiveSearchRequest next = ArchiveSearchRequest.Create(spec);
                Applied = next; ++Commits; Error.Text = "";
                DataValidationErrors.ClearErrors(Start); DataValidationErrors.ClearErrors(End);
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                Error.Text = error.Message;
                DataValidationErrors.SetErrors(End, new object[] { error.Message });
            }
        };
    }
}
