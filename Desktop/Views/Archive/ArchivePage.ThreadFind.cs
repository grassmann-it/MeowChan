using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class ArchivePage
{
    private CancellationTokenSource? _threadFindCancellation;
    private long _threadFindRequest;
    private ReaderThreadFindIndex? _threadFindResult;
    private int _threadFindCursor = -1;
    private string _threadFindStatus = "readerFind.ready";
    internal Task ThreadFindTask { get; private set; } = Task.CompletedTask;

    private void InitializeThreadFind()
    {
        // One local tunnel registration, before native Button.Enter click handling.
        // No XAML bubble registration and no handledEventsToo replay after a click.
        ReaderFindBar.AddHandler(KeyDownEvent,
            OnThreadFindKeyDown, RoutingStrategies.Tunnel);
        LocalizeThreadFind();
    }

    private bool IsThreadFindSource(object? source) => source is Avalonia.Visual visual
        && visual.GetSelfAndVisualAncestors().Contains(ReaderFindBar);

    // Test seam: lets a harness make the one index build fail without touching real data.
    internal Func<ArchivedThreadView, string, CancellationToken, ReaderThreadFindIndex>? ThreadFindIndexFactory { get; set; }

    private void OnOpenThreadFind(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || IsReaderDialogOpen || _thread == null) return;
        ReaderFindBar.IsVisible = true;
        // Opening is explicit; asynchronous completion and hit navigation never take focus.
        ReaderFindQuery.Focus();
        ThreadFindTask = SearchThreadFindAsync();
    }

    private void OnThreadFindQueryChanged(object? sender, TextChangedEventArgs e)
    {
        if (this.FindControl<StackPanel>("ReaderFindBar")?.IsVisible == true)
            ThreadFindTask = SearchThreadFindAsync();
    }

    private void CloseThreadFind()
    {
        ++_threadFindRequest;
        _threadFindCancellation?.Cancel();
        _threadFindResult = null;
        _threadFindCursor = -1;
        _threadFindStatus = "readerFind.ready";
        ReaderFindBar.IsVisible = false;
        LocalizeThreadFind();
    }

    private bool AcceptThreadFind(long request, ReaderThreadFindIndex result) => !_ended
        && request == _threadFindRequest && ReaderFindBar.IsVisible
        && _thread != null && result.Matches(_thread.Summary.Id, _thread.Summary.Revision);

    // Validate the original input, not a prefix truncated by TextBox.MaxLength.
    // Length is UTF-16 code units; do not trim, normalize or alter query semantics.
    internal static string ThreadFindQueryStatus(string query) => query.Length > 512 || query.IndexOf('\0') >= 0
        ? "readerFind.invalid" : query.Length == 0 ? "readerFind.ready" : "readerFind.searching";

    private async Task SearchThreadFindAsync()
    {
        long request = ++_threadFindRequest;
        _threadFindCancellation?.Cancel();
        _threadFindResult = null;
        _threadFindCursor = -1;
        var thread = _thread;
        string query = ReaderFindQuery.Text ?? "";
        _threadFindStatus = ThreadFindQueryStatus(query);
        LocalizeThreadFind();
        if (_ended || thread == null || _threadFindStatus != "readerFind.searching") return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _threadFindCancellation = cancellation;
        try
        {
            var factory = ThreadFindIndexFactory;
            var result = await Task.Run(() => factory != null ? factory(thread, query, cancellation.Token)
                : ReaderThreadFindIndex.Create(thread, query, cancellationToken: cancellation.Token), cancellation.Token);
            if (!AcceptThreadFind(request, result) || cancellation.IsCancellationRequested) return;
            _threadFindResult = result;
            _threadFindStatus = "readerFind.count";
            LocalizeThreadFind();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_ended && request == _threadFindRequest)
            { _threadFindStatus = "readerFind.failed"; LocalizeThreadFind(); }
        }
        finally
        {
            if (ReferenceEquals(_threadFindCancellation, cancellation)) _threadFindCancellation = null;
        }
    }

    private void MoveThreadFind(bool forward)
    {
        var result = _threadFindResult;
        if (result == null || !AcceptThreadFind(_threadFindRequest, result)) return;
        _threadFindCursor = forward ? ReaderThreadFindIndex.Next(_threadFindCursor, result.Hits.Count)
            : ReaderThreadFindIndex.Previous(_threadFindCursor, result.Hits.Count);
        if (_threadFindCursor >= 0)
        {
            ReaderFindText hit = result.Texts[result.Hits[_threadFindCursor].TextIndex];
            if (hit.Field == ReaderFindField.Body) ExpandPostForFind(hit.PostId);
            ShowPost(hit.PostId);
            if (hit.Field == ReaderFindField.Body) ExpandPostForFind(hit.PostId); // The page may have been rebuilt around the hit.
        }
        LocalizeThreadFind();
    }
    private void OnNextThreadFind(object? sender, RoutedEventArgs e) => MoveThreadFind(true);
    private void OnPreviousThreadFind(object? sender, RoutedEventArgs e) => MoveThreadFind(false);
    private void OnCloseThreadFind(object? sender, RoutedEventArgs e) => CloseThreadFind();
    // One explicit attempt with the unchanged query against the current thread and revision. Never automatic.
    private void OnRetryThreadFind(object? sender, RoutedEventArgs e)
    {
        if (_ended || _threadFindStatus != "readerFind.failed" || _thread == null) return;
        ThreadFindTask = SearchThreadFindAsync();
    }
    internal static int ThreadFindGesture(Key key, KeyModifiers modifiers) => (key, modifiers) switch
    {
        (Key.Escape, KeyModifiers.None) => 2,
        (Key.Enter, KeyModifiers.None) => 1,
        (Key.Enter, KeyModifiers.Shift) => -1,
        _ => 0
    };

    private void OnThreadFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _ended || ShortcutService.IsCapturing || IsReaderDialogOpen
            || !ReaderFindBar.IsVisible || !IsThreadFindSource(e.Source)) return;
        if (TryMoveThreadFindByShortcut(e)) return;
        int gesture = ThreadFindGesture(e.Key, e.KeyModifiers);
        if (gesture == 0) return; // Space, Tab, modified Enter/Escape retain native ownership.
        // The first Escape empties a typed query (SearchBox); only Escape in an empty query or on a button closes the bar.
        if (gesture == 2 && SearchBox.OwnsEscape(e.Source)) return;
        e.Handled = true;
        if (gesture == 2) CloseThreadFind();
        else if (gesture == 1 && FocusedFindButton(e.Source) is { } button)
        {
            // Enter on a focused find button runs that button's labeled action, not "next hit".
            switch (button.Name)
            {
                case "ReaderFindPrevious": MoveThreadFind(false); break;
                case "ReaderFindClose": CloseThreadFind(); break;
                case "ReaderFindRetry": OnRetryThreadFind(button, e); break;
                default: MoveThreadFind(true); break;
            }
        }
        else MoveThreadFind(gesture == 1);
    }

    private static Button? FocusedFindButton(object? source) => (source as Avalonia.Visual)?
        .GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Name?.StartsWith("ReaderFind", StringComparison.Ordinal) == true);

    private void LocalizeThreadFind()
    {
        ReaderFindOpen.Content = Texts.T("readerFind.open");
        ReaderFindQuery.Watermark = Texts.T("readerFind.query");
        ReaderFindPrevious.Content = Texts.T("readerFind.previous");
        ReaderFindNext.Content = Texts.T("readerFind.next");
        ReaderFindClose.Content = Texts.T("readerFind.close");
        ReaderFindRetry.Content = Texts.T("readerFind.retry");
        ReaderFindRetry.IsVisible = _threadFindStatus == "readerFind.failed";
        var result = _threadFindResult;
        bool hits = result?.Hits.Count > 0;
        ReaderFindPrevious.IsEnabled = hits;
        ReaderFindNext.IsEnabled = hits;
        ReaderFindStatus.Text = result == null ? Texts.T(_threadFindStatus)
            : Texts.T("readerFind.count", _threadFindCursor + 1, result.Hits.Count)
                + (result.Truncation == ReaderFindTruncation.None ? "" : " · " + Texts.T("readerFind.truncated"));
        TextBlock excerpt = ReaderFindExcerpt;
        excerpt.Inlines!.Clear();
        if (result != null && _threadFindCursor >= 0)
        {
            var hit = result.Hits[_threadFindCursor];
            var text = result.Texts[hit.TextIndex];
            var snippet = ThreadFindSnippet(text.Text, hit.Offset, hit.Length);
            // Offsets address the index's full plaintext, NEVER the truncated reader body.
            // ShowPost selects only the containing post; this inert run shows the exact hit.
            string field = Texts.T(text.Field == ReaderFindField.Body ? "readerFind.body" : "readerFind.subject");
            excerpt.Inlines.Add(new Run { Text = Texts.T("readerFind.location", text.PostId,
                field, hit.Offset + 1, hit.Length) + "\n" });
            excerpt.Inlines.Add(new Run { Text = snippet.Before });
            excerpt.Inlines.Add(new Run { Text = snippet.Match, FontWeight = FontWeight.SemiBold });
            excerpt.Inlines.Add(new Run { Text = snippet.After });
            if (text.Field == ReaderFindField.Body && hit.Offset + hit.Length > 65536)
                excerpt.Inlines.Add(new Run { Text = "\n" + Texts.T("readerFind.beyondDisplay") });
        }
    }

    internal static (string Before, string Match, string After) ThreadFindSnippet(string text, int offset, int length)
    {
        // Keep the exact UTF-16 hit intact, including overlaps and supplementary characters.
        if (offset < 0 || length < 1 || offset > text.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int start = Math.Max(0, offset - 60);
        int end = Math.Min(text.Length, offset + length + 60);
        // Only expand the outer context by at most one UTF-16 unit per side.
        // Match offset/length and the index's literal query semantics stay untouched.
        if (start > 0 && start < text.Length
            && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
            start--;
        if (end > 0 && end < text.Length
            && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
            end++;
        return ((start > 0 ? "…" : "") + text.Substring(start, offset - start),
            text.Substring(offset, length),
            text.Substring(offset + length, end - offset - length) + (end < text.Length ? "…" : ""));
    }
}
