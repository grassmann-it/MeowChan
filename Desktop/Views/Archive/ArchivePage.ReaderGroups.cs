using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    internal Func<Control?, Task>? OpenReaderGroupsRequested { get; set; }
    private bool _readerGroupScope;
    internal int ReaderGroupScopeGeneration => _readerGroupScope ? _readerScopeRequest : -1;
    private CancellationTokenSource? _readerGroupCancellation;
    private bool _readerGroupLoading;
    private readonly HashSet<ResourceId> _readerGroupSkipped = new();
    private ResourceId? _readerGroupFailed;
    private int _readerGroupFailedDirection;
    private void OnReaderGroupsClick(object? sender, RoutedEventArgs e) => _ = OpenReaderGroupsRequested?.Invoke(sender as Control);
    internal void BeginReaderGroupScope(IReadOnlyList<ResourceId> ids)
    {
        _readerGroupCancellation?.Cancel();
        _readerGroupScope = true; _readerGroupSkipped.Clear(); _readerGroupFailed = null; _readerGroupFailedDirection = 0;
        Control<TextBlock>("ReaderGroupAvailabilityMessage").IsVisible = false;
        Control<Button>("ReaderSkipUnavailableGroupMember").IsVisible = false;
        SetReaderScope(ids);
    }
    internal void EndReaderGroupScope()
    {
        _readerGroupCancellation?.Cancel();
        if (_readerGroupLoading) { _busy = false; _readerGroupLoading = false; }
        _readerGroupScope = false; _readerGroupSkipped.Clear(); _readerGroupFailed = null; _readerGroupFailedDirection = 0;
        Control<TextBlock>("ReaderGroupAvailabilityMessage").IsVisible = false;
        Control<Button>("ReaderSkipUnavailableGroupMember").IsVisible = false;
    }
    private void InvalidatePendingReaderGroupRead()
    {
        if (!_readerGroupScope) return;
        // Restoring a gallery origin retires in-flight reads but preserves the frozen group order.
        ++_readerScopeRequest;
        _readerGroupCancellation?.Cancel();
        _readerGroupCancellation = null;
        _readerGroupLoading = false;
    }
    private int ReaderGroupTargetIndex(int delta)
    {
        int index = ReaderThreadIndex + delta;
        while (index >= 0 && index < _readerThreads.Count && _readerGroupSkipped.Contains(_readerThreads[index])) index += delta;
        return index;
    }
    internal async Task<bool> LoadReaderGroupThreadAsync(ResourceId target)
    {
        if (_busy || _ended || !_largeReader || !_readerGroupScope || _openReader == null) return false;
        int request = _readerScopeRequest;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _readerGroupCancellation = loading; _readerGroupLoading = true;
        _busy = true; UpdateReaderNavigation();
        try
        {
            // Preflight the immutable local view before changing any visible thread, post or anchor.
            await using ArchiveReader reader = await _openReader(loading.Token);
            ArchivedThreadView? view = await reader.ReadThreadAsync(target, loading.Token);
            if (_ended || request != _readerScopeRequest || !_readerGroupScope) return false;
            if (view == null || view.PostCount == 0)
            {
                ShowUnavailableGroupMember(target); return false;
            }
            _busy = false; _readerGroupLoading = false;
            await LoadThreadAsync(target, preparedThread: view);
            if (_ended || request != _readerScopeRequest) return false;
            _readerGroupFailed = null;
            Control<TextBlock>("ReaderGroupAvailabilityMessage").IsVisible = false;
            Control<Button>("ReaderSkipUnavailableGroupMember").IsVisible = false;
            return _thread?.Summary.Id == target;
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (!_ended && request == _readerScopeRequest) ShowUnavailableGroupMember(target, exception.Message);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_readerGroupCancellation, loading)) { _readerGroupCancellation = null; _readerGroupLoading = false; }
            if (!_ended && request == _readerScopeRequest) { _busy = false; UpdateReaderNavigation(); }
        }
    }
    private void ShowUnavailableGroupMember(ResourceId target, string? error = null)
    {
        _readerGroupFailed = target;
        string id = $"{target.Source}/{target.Board}/{target.Thread}";
        TextBlock message = Control<TextBlock>("ReaderGroupAvailabilityMessage");
        message.Text = (Texts.Language == "de" ? "Gruppenmitglied lokal nicht verfügbar: " : "Group member unavailable locally: ") + id + "\n" + error;
        message.IsVisible = true;
        Button skip = Control<Button>("ReaderSkipUnavailableGroupMember");
        CommandBindings.Apply(skip, "reader.skip-unavailable-group-member");
        skip.IsVisible = _readerGroupFailedDirection != 0;
        skip.IsEnabled = true;
    }
    internal bool CanSkipUnavailableGroupMember => !_busy && !_ended && _largeReader && _readerGroupScope && _readerGroupFailed != null;
    private async void OnSkipUnavailableGroupMember(object? sender, RoutedEventArgs e) => await SkipUnavailableGroupMemberAsync();
    internal async Task SkipUnavailableGroupMemberAsync()
    {
        if (!CanSkipUnavailableGroupMember) return;
        _readerGroupSkipped.Add(_readerGroupFailed!); _readerGroupFailed = null;
        Control<Button>("ReaderSkipUnavailableGroupMember").IsVisible = false;
        await NavigateReaderAsync(0, _readerGroupFailedDirection);
        UpdateReaderNavigation();
    }
}
