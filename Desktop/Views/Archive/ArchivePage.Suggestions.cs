using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Automation;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private ArchiveSuggestionCache? _suggestionCache;
    private bool _suggestionsFailed;
    private bool _hasBoardSuggestionContext;
    private string? _boardSuggestionContext;
    internal Func<string?>? GetBoardSuggestionSource { get; set; }

    internal string? ReadIdentityEditor(string name) => Control<IdentitySuggestionBox>(name).Text;
    internal IdentitySuggestion? TryReadSelectedIdentity(string name)
    {
        var editor = Control<IdentitySuggestionBox>(name);
        return IdentitySuggestions.CurrentSelection(editor.Text, editor.AcceptedCandidate);
    }
    // URSA-14 adapters can consume qualified candidates without parsing display labels.
    internal void InvalidateBoardSuggestions()
    {
        string? source = ReadBoardSuggestionSource();
        var board = ArchiveBoardFilter;
        if (_hasBoardSuggestionContext && !StringComparer.Ordinal.Equals(source, _boardSuggestionContext))
            board.InvalidateIdentityContext();
        else board.Dismiss(); // A label/layout rebind does not revoke an explicit choice.
        _boardSuggestionContext = source;
        _hasBoardSuggestionContext = true;
    }

    private string? ReadBoardSuggestionSource() => GetBoardSuggestionSource != null ? GetBoardSuggestionSource()
        : IdentitySuggestions.InputIdentity(ReadIdentityEditor("ArchiveSourceFilter"), TryReadSelectedIdentity("ArchiveSourceFilter"));

    private void InitializeSuggestions()
    {
        var source = ArchiveSourceFilter;
        var board = ArchiveBoardFilter;
        source.Candidates = (text, token) => ReadSuggestionsAsync(false, text, token);
        board.Candidates = (text, token) => ReadSuggestionsAsync(true, text, token);
        source.TextChanged += (_, _) => { InvalidateBoardSuggestions(); board.RefreshSuggestions(); };
        source.LoadCompleted += SuggestionLoadCompleted;
        // Accepting a suggestion (click, or Enter on the highlighted one) adds it as a chip once the box has
        // finished its selection update, and only while that exact accepted value is still in the field.
        foreach (var (editor, isBoard) in new[] { (source, false), (board, true) })
        {
            editor.ReleaseUnselectedEnter = true;
            editor.CandidateAccepted += item => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { if (!_ended && ReferenceEquals(IdentitySuggestions.CurrentSelection(editor.Text, editor.AcceptedCandidate), item)) _ = AddIdentityAsync(isBoard, item); },
                Avalonia.Threading.DispatcherPriority.Send);
        }
        board.LoadCompleted += SuggestionLoadCompleted;
        ArchiveRetrySuggestions.Click += (_, _) =>
        {
            InvalidateSuggestions();
            source.Focus(); source.RestartSuggestions();
        };
    }
    private ArchiveSuggestionCache CreateSuggestionCache() => new(async token =>
    {
        if (_openReader == null) throw new InvalidOperationException("No archive reader.");
        await using var reader = await _openReader(token);
        var sources = await reader.ReadSourceIdentitiesAsync(cancellationToken: token);
        var boards = await reader.ReadBoardIdentitiesAsync(cancellationToken: token);
        return new IdentitySuggestions(sources, boards);
    }, async (source, token) =>
    {
        await using var reader = await _openReader!(token);
        return await reader.ReadBoardIdentitiesAsync(source, 256, token);
    }, _lifetime.Token);

    private Task<IdentitySuggestion[]> ReadSuggestionsAsync(bool boards, string text, CancellationToken token)
    {
        if (_ended) return Task.FromResult(Array.Empty<IdentitySuggestion>());
        if (_suggestionsFailed) throw new InvalidOperationException("Suggestions unavailable; explicit retry required.");
        string? source = ReadBoardSuggestionSource();
        if (boards) { _boardSuggestionContext = source; _hasBoardSuggestionContext = true; }
        return (_suggestionCache ??= CreateSuggestionCache()).MatchAsync(boards, text, source, token);
    }
    private void SuggestionLoadCompleted(Exception? error)
    {
        if (_ended) return;
        _suggestionsFailed = error != null;
        UpdateSuggestionHint();
    }
    private void UpdateSuggestionHint()
    {
        AutomationProperties.SetName(ArchiveSourceFilter, Texts.T("archive.sourceFilter"));
        AutomationProperties.SetName(ArchiveBoardFilter, Texts.T("archive.boardFilter"));
        var hint = ArchiveSuggestionsHint;
        hint.Text = Texts.T(_suggestionsFailed ? "suggestions.unavailable" : "archive.suggestionsHint");
        ArchiveRetrySuggestions.Content = Texts.T("suggestions.retry");
        ArchiveRetrySuggestions.IsVisible = _suggestionsFailed;
    }
    private void InvalidateSuggestions()
    {
        _suggestionCache?.Dispose(); _suggestionCache = null; _suggestionsFailed = false;
        ArchiveSourceFilter.Dismiss();
        ArchiveBoardFilter.Dismiss();
        UpdateSuggestionHint();
    }
}
