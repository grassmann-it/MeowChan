using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

// What the Downloads page does after an Add, beyond the report itself (final UX review of 1.1.0):
//  DL-02  the new row is selected and scrolled into view (keyboard focus stays where the user typed), and the "Show in list"
//         link of a duplicate sits right below the result line, not inside the collapsed report;
//  DL-04  the rejection banner and the results can be dismissed (close button, Esc while focus is in them). The next
//         submission clears both as before.
public partial class MainWindow
{
    private StackPanel? _admissionLinkStrip;
    private bool _admissionRepairDismissed;
    private readonly AdmissionReveal _admissionReveal = new();
    // How long a just added watch may take to appear in the rows (the projection is applied in slices); after that nothing is moved.
    private const int RevealWindowMilliseconds = 8000;

    internal StackPanel? AdmissionLinkStripForTests => _admissionLinkStrip;

    private void InitializeAdmissionFollowUp()
    {
        // Built in code beside the results panel (like the Archive hint and the problem tools): the shared window layout stays untouched.
        _admissionLinkStrip = new StackPanel { Name = "AdmissionShowInListLinks", Spacing = 4, IsVisible = false };
        int after = WorkspaceIntakeStack.Children.IndexOf(AdmissionResultsPanel);
        WorkspaceIntakeStack.Children.Insert(after < 0 ? WorkspaceIntakeStack.Children.Count : after + 1, _admissionLinkStrip);
        // Esc in the banner or anywhere in the results (report, links, problem tools) dismisses it; the key bubbles up from the focused control.
        AdmissionRepairPanel.KeyDown += OnAdmissionRepairKeyDown;
        AdmissionResultsPanel.KeyDown += OnAdmissionResultsKeyDown;
        _admissionLinkStrip.KeyDown += OnAdmissionResultsKeyDown;
        if (_admissionProblemTools != null) _admissionProblemTools.KeyDown += OnAdmissionResultsKeyDown;
        LocalizeAdmissionDismiss();
    }

    // ---- DL-04: dismiss -------------------------------------------------------------------------------------------------------

    private void LocalizeAdmissionDismiss()
    {
        string name = Texts.T("feedback.dismiss");
        string tip = name + " · Esc";
        WorkspaceIcons.MakeSquare(AdmissionRepairDismiss, WorkspaceIcons.Close, name, tip);
        WorkspaceIcons.MakeSquare(AdmissionResultsDismiss, WorkspaceIcons.Close, name, tip);
    }

    // The 32 dip square would double the header of the one-row report in the compact layout.
    private void SizeAdmissionDismiss(bool compact)
    {
        foreach (Button button in new[] { AdmissionRepairDismiss, AdmissionResultsDismiss })
        {
            if (compact) { button.Width = 24; button.Height = 24; }
            else { button.ClearValue(Layoutable.WidthProperty); button.ClearValue(Layoutable.HeightProperty); }
        }
    }

    private static bool IsDismissKey(KeyEventArgs e) => !e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None;

    private void OnAdmissionRepairKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsDismissKey(e)) return;
        e.Handled = true;
        DismissAdmissionRepair();
    }

    private void OnAdmissionResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsDismissKey(e)) return;
        e.Handled = true;
        DismissAdmissionResults();
    }

    private void OnAdmissionRepairDismiss(object? sender, RoutedEventArgs e) => DismissAdmissionRepair();
    private void OnAdmissionResultsDismiss(object? sender, RoutedEventArgs e) => DismissAdmissionResults();

    /// <summary>Hides the red banner only; the report and its counters stay until the next submission or their own dismissal.</summary>
    internal void DismissAdmissionRepair()
    {
        if (_isClosing) return;
        bool hadFocus = AdmissionRepairPanel.IsKeyboardFocusWithin;
        _admissionRepairDismissed = true;
        AdmissionRepairPanel.IsVisible = false;
        // The field is the place to fix the address; the focused close button is gone with the banner.
        if (hadFocus) UrlInput.Focus();
    }

    /// <summary>Clears the results with their banner, links and problem tools. The rejected addresses can still be put back only while they are shown.</summary>
    internal void DismissAdmissionResults()
    {
        if (_isClosing) return;
        bool hadFocus = AdmissionResultsPanel.IsKeyboardFocusWithin || AdmissionRepairPanel.IsKeyboardFocusWithin
            || _admissionLinkStrip?.IsKeyboardFocusWithin == true || _admissionProblemTools?.IsKeyboardFocusWithin == true;
        _admissionResults.Clear();
        _admissionProblemsOnly = false;
        RefreshAdmissionResults();
        if (hadFocus) UrlInput.Focus();
    }

    // ---- DL-02: the way to the duplicate --------------------------------------------------------------------------------------

    // One link per duplicate (the first few), below the result line so it works without opening the report.
    private void RefreshAdmissionDuplicateLinks(bool problemsOnly)
    {
        if (_admissionLinkStrip == null) return;
        _admissionLinkStrip.Children.Clear();
        foreach (AdmissionFeedback known in _admissionResults
                     .Where(item => !problemsOnly && item.Status == AdmissionStatus.AlreadyPresent && item.WatchId != null).Take(MaxDuplicateLinks))
            _admissionLinkStrip.Children.Add(CreateShowInListLink(known));
        _admissionLinkStrip.IsVisible = _admissionLinkStrip.Children.Count > 0;
    }

    // ---- DL-02: the new row ---------------------------------------------------------------------------------------------------

    /// <summary>Remembers the first watch the finished Add created, so its row is selected and shown as soon as the rows have it.</summary>
    private void RequestAdmissionReveal()
    {
        long? first = _admissionResults.FirstOrDefault(item => item.Status == AdmissionStatus.Added && item.WatchId != null)?.WatchId;
        _admissionReveal.Request(first, Environment.TickCount64, RevealWindowMilliseconds);
        TryRevealAdmittedWatch();
    }

    /// <summary>
    /// Selects the pending watch's row and scrolls it into view; called when an Add finishes and after every completed row frame (the
    /// projection reaches the rows in slices, so the row often is not there yet). The keyboard focus is never moved: the user
    /// adds the next address from the same field.
    /// </summary>
    private void TryRevealAdmittedWatch()
    {
        // Called from the end of a row frame: a failing reveal must never abandon that frame.
        try { RevealAdmittedWatch(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _admissionReveal.Cancel();
            Log.Warning(error, "The row of the new watch could not be shown.");
        }
    }

    private void RevealAdmittedWatch()
    {
        if (_admissionReveal.WatchId is not long id) return;
        bool pageReady = !_isClosing && _selectedPage == WorkspacePage.Downloads && !_largeReaderOpen && (_gallery == null || _gallerySuspended);
        bool isThread = _threadRows.TryGetValue(id, out ThreadRow? thread);
        BoardRow? board = null;
        bool isBoard = !isThread && _boardRows.TryGetValue(id, out board);
        // Only a row that exists is looked up in the (linear) visible lists.
        bool visible = isThread ? VisibleThreads.Contains(thread!) : isBoard && VisibleBoards.Contains(board!);
        AdmissionRevealStep step = _admissionReveal.Next(Environment.TickCount64, pageReady, _sessionRowsApplying, isThread, isBoard, visible);
        if (step is not (AdmissionRevealStep.ShowThread or AdmissionRevealStep.ShowBoard)) return;
        bool showThread = step == AdmissionRevealStep.ShowThread;
        bool fieldHadFocus = UrlInput.IsKeyboardFocusWithin;
        object row = showThread ? thread! : board!;
        DownloadsTabs.SelectedIndex = showThread ? 0 : 1;
        DataGrid grid = DownloadList(!showThread);
        grid.SelectedItem = row;
        grid.ScrollIntoView(row, null);
        // The grid of a tab that was just switched to may not be measured yet: once more after layout.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isClosing && ReferenceEquals(grid.SelectedItem, row)) grid.ScrollIntoView(row, null);
        }, DispatcherPriority.Background);
        if (fieldHadFocus && !UrlInput.IsKeyboardFocusWithin) UrlInput.Focus();
    }
}
