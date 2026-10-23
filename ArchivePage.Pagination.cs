using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using Ursa.Controls;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Threading;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private bool _updatingPagination;
    internal static int ResultsPageCount(int total) => total <= 0 ? 0 : (total - 1) / PageSize + 1;
    internal static bool TryResultsOffset(int page, int total, out int offset)
    {
        offset = 0;
        if (page <= 0 || page > ResultsPageCount(total)) return false;
        long candidate = ((long)page - 1) * PageSize;
        if (candidate > int.MaxValue) return false;
        offset = (int)candidate; return true;
    }
    internal async Task<bool> NavigateResultsPage(int page)
    {
        if (_busy || _ended || !_available || _appliedSearch == null
            || !TryResultsOffset(page, _listTotal, out int offset) || offset == _listOffset) return false;
        bool retainFocus = Control<Pagination>("ArchiveResultsPagination").IsKeyboardFocusWithin;
        bool success = await LoadListAsync(new ListLoad(_appliedSearch, offset, _appliedWork));
        if (success && retainFocus) Dispatcher.UIThread.Post(() =>
        {
            if (_ended) return;
            Pagination navigation = Control<Pagination>("ArchiveResultsPagination");
            if (navigation.IsVisible)
                navigation.GetVisualDescendants().OfType<PaginationButton>().FirstOrDefault(button =>
                    button.Page == _listOffset / PageSize + 1 && !button.Classes.Contains(PaginationButton.PC_Left) && !button.Classes.Contains(PaginationButton.PC_Right))?.Focus();
            else Control<TextBox>("ArchiveSearchText").Focus();
        }, DispatcherPriority.Loaded);
        return success;
    }
    private void UpdateSearchActions(bool ready)
    {
        Control<WrapPanel>("ArchiveAppliedFilters").IsEnabled = ready;
        Button retry = Control<Button>("ArchiveRetrySearch");
        retry.IsVisible = _retryList != null; retry.IsEnabled = ready && _retryList != null;
        Pagination pagination = Control<Pagination>("ArchiveResultsPagination");
        _updatingPagination = true;
        try
        {
            pagination.TotalCount = _listTotal;
            pagination.PageSize = PageSize;
            pagination.CurrentPage = _listTotal > 0 ? _listOffset / PageSize + 1 : null;
            pagination.IsVisible = _listLoaded && ResultsPageCount(_listTotal) > 1;
            pagination.IsEnabled = ready;
            foreach (PaginationButton button in pagination.GetVisualDescendants().OfType<PaginationButton>())
            {
                string label = button.Name == Pagination.PART_PreviousButton ? Texts.T("archive.previous")
                    : button.Name == Pagination.PART_NextButton ? Texts.T("archive.next")
                    : button.Classes.Contains(PaginationButton.PC_Left) ? Texts.T("archive.jumpBackward")
                    : button.Classes.Contains(PaginationButton.PC_Right) ? Texts.T("archive.jumpForward")
                    : Texts.T("archive.pageAction", button.Page, ResultsPageCount(_listTotal))
                        + (button.Page == _listOffset / PageSize + 1 ? " · " + Texts.T("archive.currentPage") : "");
                AutomationProperties.SetName(button, label);
            }
            AutomationProperties.SetName(pagination, Texts.T("archive.pageAction", _listTotal > 0 ? _listOffset / PageSize + 1 : 0, ResultsPageCount(_listTotal)));
        }
        finally { _updatingPagination = false; }
        // Ursa owns visible previous/next actions; keep old named buttons only as
        // hidden command adapters for existing command/smoke consumers.
        Control<Button>("PreviousThreadsButton").IsVisible = false;
        Control<Button>("NextThreadsButton").IsVisible = false;
        SearchStateChanged?.Invoke();
    }
    private async void OnResultsPageChanged(object? sender, Ursa.Controls.ValueChangedEventArgs<int> e)
    {
        if (_updatingPagination) return;
        int? target = e.NewValue;
        UpdateSearchActions(_available && !_busy && !_ended); // revert speculative CurrentPage before await
        if (target.HasValue) await NavigateResultsPage(target.Value);
    }
    internal Task<bool> RetrySearchAsync() => _busy || _retryList == null
        ? Task.FromResult(false) : LoadListAsync(_retryList);
    private async void OnRetrySearch(object? sender, RoutedEventArgs e) => await RetrySearchAsync();
}
