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
    internal Func<string?>? GetBoardSuggestionSource { get; set; }

    internal string? ReadIdentityEditor(string name) => Control<IdentitySuggestionBox>(name).Text;
    internal IdentitySuggestion? TryReadSelectedIdentity(string name) => Control<IdentitySuggestionBox>(name).AcceptedCandidate;
    internal void WriteIdentityEditor(string name, string? value) => Control<IdentitySuggestionBox>(name).SetCurrentValue(IdentitySuggestionBox.TextProperty, value);

    // URSA-14 adapters can consume qualified candidates without parsing display labels.
    internal Task<IdentitySuggestion[]> GetIdentityCandidatesAsync(bool boards, string text, CancellationToken token) => ReadSuggestionsAsync(boards, text, token);

    internal void InvalidateBoardSuggestions() => Control<IdentitySuggestionBox>("ArchiveBoardFilter").Dismiss();

    private void InitializeSuggestions()
    {
        var source = Control<IdentitySuggestionBox>("ArchiveSourceFilter");
        var board = Control<IdentitySuggestionBox>("ArchiveBoardFilter");
        source.Candidates = (text, token) => ReadSuggestionsAsync(false, text, token);
        board.Candidates = (text, token) => ReadSuggestionsAsync(true, text, token);
        source.TextChanged += (_, _) => { board.Dismiss(); board.RefreshSuggestions(); };
        source.LoadCompleted += SuggestionLoadCompleted;
        board.LoadCompleted += SuggestionLoadCompleted;
        Control<Button>("ArchiveRetrySuggestions").Click += (_, _) =>
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
        string? source = GetBoardSuggestionSource != null ? GetBoardSuggestionSource()
            : Control<IdentitySuggestionBox>("ArchiveSourceFilter").Text?.Trim();
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
        AutomationProperties.SetName(Control<IdentitySuggestionBox>("ArchiveSourceFilter"), Texts.T("archive.sourceFilter"));
        AutomationProperties.SetName(Control<IdentitySuggestionBox>("ArchiveBoardFilter"), Texts.T("archive.boardFilter"));
        var hint = Control<TextBlock>("ArchiveSuggestionsHint");
        hint.Text = Texts.T(_suggestionsFailed ? "suggestions.unavailable" : "suggestions.limited");
        Control<Button>("ArchiveRetrySuggestions").Content = Texts.T("suggestions.retry");
        Control<Button>("ArchiveRetrySuggestions").IsVisible = _suggestionsFailed;
    }
    private void InvalidateSuggestions()
    {
        _suggestionCache?.Dispose(); _suggestionCache = null; _suggestionsFailed = false;
        Control<IdentitySuggestionBox>("ArchiveSourceFilter").Dismiss();
        Control<IdentitySuggestionBox>("ArchiveBoardFilter").Dismiss();
        UpdateSuggestionHint();
    }
}
