using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
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
    private sealed record PaginationFocusOrigin(TopLevel Owner, Control Source);
    private PaginationFocusOrigin? _paginationInputOrigin;
    private PaginationButton? _paginationInputButton;
    private int _paginationNavigationRevision;

    private PaginationFocusOrigin? CapturePaginationFocusOrigin(PaginationButton button)
    {
        Pagination navigation = Control<Pagination>("ArchiveResultsPagination");
        TopLevel? owner = TopLevel.GetTopLevel(navigation);
        return owner?.FocusManager?.GetFocusedElement() is Control focused
            && focused.GetSelfAndVisualAncestors().Contains(button) ? new(owner, focused) : null;
    }

    private void InitializePaginationFocus()
    {
        Pagination navigation = Control<Pagination>("ArchiveResultsPagination");
        // Ursa updates/disables its next/previous button before raising
        // CurrentPageChanged. Capture the real focus before native activation.
        navigation.AddHandler(PointerPressedEvent, CapturePointer, RoutingStrategies.Tunnel);
        navigation.AddHandler(PointerReleasedEvent, CaptureInput, RoutingStrategies.Tunnel);
        navigation.AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
        navigation.AddHandler(KeyUpEvent, CaptureKey, RoutingStrategies.Tunnel);
        void CapturePointer(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual source
                && source.GetSelfAndVisualAncestors().OfType<PaginationButton>().FirstOrDefault() is { } button
                && button.IsEffectivelyEnabled && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed
                && TopLevel.GetTopLevel(button) is { } owner)
                RememberPaginationInput(button, new PaginationFocusOrigin(owner, button));
        }
        void CaptureKey(object? sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Space) CaptureInput(sender, e);
        }
        void CaptureInput(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Visual source
                || source.GetSelfAndVisualAncestors().OfType<PaginationButton>().FirstOrDefault() is not { } button) return;
            if (CapturePaginationFocusOrigin(button) is { } origin) RememberPaginationInput(button, origin);
        }
    }

    private void RememberPaginationInput(PaginationButton button, PaginationFocusOrigin origin)
    {
        ClearPaginationInput();
        _paginationInputOrigin = origin;
        _paginationInputButton = button;
        button.PropertyChanged += OnPaginationInputStateChanged;
        QueueClearInactivePaginationInput(button, origin);
    }
    private void OnPaginationInputStateChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Button.IsPressedProperty && sender is PaginationButton button
            && !button.IsPressed && _paginationInputOrigin is { } origin)
            QueueClearInactivePaginationInput(button, origin);
    }
    private void QueueClearInactivePaginationInput(PaginationButton button, PaginationFocusOrigin origin)
    {
        // Native RepeatButton can activate after its hold timer, without a new
        // input event. Keep the origin while pressed; clear only after the input
        // stack has delivered any release/disable-triggered CurrentPageChanged.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_paginationInputOrigin, origin) && ReferenceEquals(_paginationInputButton, button)
                && !button.IsPressed) ClearPaginationInput();
        }, DispatcherPriority.Background);
    }
    private void ClearPaginationInput()
    {
        if (_paginationInputButton != null) _paginationInputButton.PropertyChanged -= OnPaginationInputStateChanged;
        _paginationInputButton = null;
        _paginationInputOrigin = null;
    }
    internal static int ResultsPageCount(int total) => total <= 0 ? 0 : (total - 1) / PageSize + 1;
    internal static bool TryResultsOffset(int page, int total, out int offset)
    {
        offset = 0;
        if (page <= 0 || page > ResultsPageCount(total)) return false;
        long candidate = ((long)page - 1) * PageSize;
        if (candidate > int.MaxValue) return false;
        offset = (int)candidate; return true;
    }
    internal Task<bool> NavigateResultsPage(int page) => NavigateResultsPage(page, null);
    private async Task<bool> NavigateResultsPage(int page, PaginationFocusOrigin? origin)
    {
        if (_busy || _ended || !_available || _appliedSearch == null
            || !TryResultsOffset(page, _listTotal, out int offset) || offset == _listOffset) return false;
        int revision = ++_paginationNavigationRevision;
        bool focusChanged = false;
        void OnNewFocus(object? sender, FocusChangedEventArgs e) => focusChanged = true;
        origin?.Owner.AddHandler(GotFocusEvent, OnNewFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            Task<bool> loading = LoadListAsync(new ListLoad(_appliedSearch, offset, _appliedWork));
            int request = _contentRequest;
            bool success = await loading;
            if (success && origin != null) await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Pagination navigation = Control<Pagination>("ArchiveResultsPagination");
                if (_ended || _busy || revision != _paginationNavigationRevision || request != _contentRequest
                    || focusChanged || !IsEffectivelyVisible || !IsEffectivelyEnabled
                    || !ReferenceEquals(TopLevel.GetTopLevel(navigation), origin.Owner)
                    || origin.Owner is Window { IsActive: false }) return;
                Control? focused = origin.Owner.FocusManager?.GetFocusedElement() as Control;
                // A newer focus owner always wins, including a field outside
                // this page. Null is expected when native paging disabled origin.
                if (focused != null && !ReferenceEquals(focused, origin.Source)) return;
                if (navigation.IsEffectivelyVisible)
                    navigation.GetVisualDescendants().OfType<PaginationButton>().FirstOrDefault(button =>
                        button.Page == _listOffset / PageSize + 1 && !button.Classes.Contains(PaginationButton.PC_Left)
                        && !button.Classes.Contains(PaginationButton.PC_Right))?.Focus();
                else Control<TextBox>("ArchiveSearchText").Focus();
            }, DispatcherPriority.Loaded);
            return success;
        }
        finally { origin?.Owner.RemoveHandler(GotFocusEvent, OnNewFocus); }
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
        PaginationFocusOrigin? origin = _paginationInputOrigin;
        ClearPaginationInput();
        UpdateSearchActions(_available && !_busy && !_ended); // revert speculative CurrentPage before await
        if (target.HasValue) await NavigateResultsPage(target.Value, origin);
    }
    internal Task<bool> RetrySearchAsync() => _busy || _retryList == null
        ? Task.FromResult(false) : LoadListAsync(_retryList);
    private async void OnRetrySearch(object? sender, RoutedEventArgs e) => await RetrySearchAsync();
}
