using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// M3: the query syntax of the media search field (Desktop/MediaFilterQuery.cs). The text of the field stays a live file name filter;
// Enter turns the recognized "field:value" tokens into filter terms (chips) and leaves the rest in the field. A token that is not
// understood is never dropped: it stays in the field and a hint below the chips says why. "Copy filters as text" (the chips' menu and
// the row's menu) writes the term set back as a query. The conversion and the copy are announced through the hint (a polite live region).
public sealed partial class MediaPage
{
    private readonly TextBlock _queryNote = new() { Name = "MediaQueryNote", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Classes = { "workspace-meta" } };
    private readonly TextBlock _queryProblem = new() { Name = "MediaQueryProblem", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Classes = { "workspace-error" } };
    private readonly StackPanel _queryHint = new() { Name = "MediaQueryHint", Spacing = 2, IsVisible = false };
    private IReadOnlyDictionary<string, MediaFilterField>? _queryAliases;
    private long _queryHintRevision;

    /// <summary>The note below the chips after a conversion or a copy (null when none), for tests and smokes.</summary>
    internal string? QueryNote => _queryNote.IsVisible ? _queryNote.Text : null;
    /// <summary>What the last conversion kept as text, and why (null when nothing), for tests and smokes.</summary>
    internal string? QueryProblem => _queryProblem.IsVisible ? _queryProblem.Text : null;
    /// <summary>Replaces the clipboard in tests.</summary>
    internal Func<string, Task>? CopyTextForTests { get; set; }

    private Control BuildQueryHint()
    {
        AutomationProperties.SetLiveSetting(_queryNote, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_queryProblem, AutomationLiveSetting.Polite);
        _queryHint.Children.Add(_queryNote);
        _queryHint.Children.Add(_queryProblem);
        _nameFilter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && !e.Handled && SubmitQuery()) e.Handled = true;
        };
        return _queryHint;
    }

    private void ShowQueryHint(string? note, string? problem)
    {
        _queryNote.Text = note; _queryNote.IsVisible = !string.IsNullOrEmpty(note);
        _queryProblem.Text = problem; _queryProblem.IsVisible = !string.IsNullOrEmpty(problem);
        _queryHint.IsVisible = _queryNote.IsVisible || _queryProblem.IsVisible;
    }

    private void ClearQueryHint()
    {
        ++_queryHintRevision; // A pending clipboard result belongs to the old query/context, even when no hint is visible.
        if (_queryHint.IsVisible) ShowQueryHint(null, null);
    }

    /// <summary>
    /// Enter in the search field: reads its text as a query, adds the understood terms to the filter and keeps the rest (and every
    /// token that was not understood) in the field. False, and nothing changed, for plain text, so Enter then does what it always did.
    /// </summary>
    internal bool SubmitQuery()
    {
        if (_lifetime.IsCancellationRequested) return false;
        string text = _nameFilter.Text ?? string.Empty;
        MediaFilterQueryResult parsed = MediaFilterQuery.Parse(text, QueryVocabulary(out Func<ResourceId, string?> threadTitle));
        if (parsed.Tokens.All(token => token.Kind == MediaFilterQueryTokenKind.Text && token.Text == token.Raw))
        {
            ClearQueryHint();
            return false;
        }
        // Do not validate recognized terms against the complete raw query as file-name text. A query such as
        // "queue:yes" is still in the search box while its queue term is being admitted; validating that raw
        // spelling as the name can refuse the page-side term before the queue-read error is surfaced.
        _nameFilter.SetTextSilently(string.Empty);
        MediaFilterQueryApplication applied = MediaFilterQuery.Apply(_terms.ToArray(), parsed, AdmitQueryTerm);
        bool termsChanged = !applied.Terms.SequenceEqual(_terms);
        if (termsChanged)
            foreach (MediaFilterTerm thread in applied.Added.Where(term => term.Field == MediaFilterField.Thread && term.Resource != null))
                _threadTitle = threadTitle(thread.Resource!) is { Length: > 0 } title ? title : "#" + thread.Value;
        _nameFilter.SetTextSilently(applied.Remainder);
        _nameFilter.CaretIndex = applied.Remainder.Length;
        if (termsChanged) EditTerms(_ => applied.Terms);
        else FilterEdited();
        string? note = applied.Added.Count == 0 ? null
            : Texts.T("media.query.converted", string.Join(", ", applied.Added.Select(term => TermLabel(term)?.Label ?? MediaFilterTermSet.IdentityKey(term))));
        string? problem = applied.Kept.Count == 0 ? null
            : Texts.T("media.query.kept", string.Join(", ", applied.Kept.Select(item => Texts.T("media.query.kept.item", item.Raw, ReasonText(item.Problem)))));
        ShowQueryHint(note, problem);
        return true;
    }

    private static string ReasonText(MediaFilterQueryProblem problem) => Texts.T("media.query.reason." + problem switch
    {
        MediaFilterQueryProblem.UnknownField => "unknownField",
        MediaFilterQueryProblem.UnknownValue => "unknownValue",
        MediaFilterQueryProblem.NeedsValue => "needsValue",
        MediaFilterQueryProblem.CannotExclude => "cannotExclude",
        MediaFilterQueryProblem.Ambiguous => "ambiguous",
        MediaFilterQueryProblem.TooManyTags => "tooManyTags",
        _ => "refused"
    });

    // A term joins the filter when the tag lists still hold it and the terms still make one filter (the same checks as the picker's).
    private MediaFilterQueryProblem? AdmitQueryTerm(IReadOnlyList<MediaFilterTerm> terms, MediaFilterTerm term)
    {
        IReadOnlyList<MediaFilterTerm> next = MediaFilterTermSet.With(terms, term);
        // SP5: the configured tags-per-list limit is checked only for the list the term grows; the ceiling guards the filter itself.
        if (term.Field == MediaFilterField.Tag && TagTermCount(next, term.Exclude) > Math.Max(TagTermCount(terms, term.Exclude), ArchiveTagFilterTagsPerListPreference))
            return MediaFilterQueryProblem.TooManyTags;
        try
        {
            _ = new ArchiveTagFilter(next.Where(item => item.Field == MediaFilterField.Tag && !item.Exclude).Select(item => Guid.Parse(item.Value)),
                next.Where(item => item.Field == MediaFilterField.Tag && item.Exclude).Select(item => Guid.Parse(item.Value)),
                ArchiveTagMatch.All, ArchiveTagFilter.MaximumConfigurableTagsPerList);
        }
        catch (ArgumentException) { return MediaFilterQueryProblem.TooManyTags; }
        if (SourceTagRefusal(term, terms) != null) return MediaFilterQueryProblem.TooManyTags; // MP4: the same tags-per-list limit
        // Queue/source-tag terms are applied by MediaPage after the core filter. Their local-read
        // availability is deliberately handled by Present, not by query admission.
        if (MediaFilterTerms.IsPageTerm(term)) return null;
        return IsValidFilter(next) ? null : MediaFilterQueryProblem.Refused;
    }

    private static int TagTermCount(IEnumerable<MediaFilterTerm> terms, bool exclude) =>
        terms.Count(item => item.Field == MediaFilterField.Tag && item.Exclude == exclude);

    /// <summary>The names the query reads and writes, from the language pack, the own tags and the index (read once per call).</summary>
    private MediaFilterQueryVocabulary QueryVocabulary() => QueryVocabulary(out _);

    private MediaFilterQueryVocabulary QueryVocabulary(out Func<ResourceId, string?> threadTitle)
    {
        PrivateTagStoreSnapshot? tags = _userData.TagSnapshot;
        IReadOnlyList<MediaLibraryEntry> entries = _snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>();
        // Sources, boards and threads of the index, distinct, computed once for this call (the index may be large).
        Lazy<string[]> sources = new(() => entries.Select(entry => entry.Thread.Source).Distinct(StringComparer.Ordinal).ToArray());
        Lazy<(string Source, string Board)[]> boards = new(() => entries.Select(entry => (entry.Thread.Source, entry.Thread.Board)).Distinct().ToArray());
        Lazy<Dictionary<string, List<ResourceId>>> threads = new(() =>
        {
            Dictionary<string, List<ResourceId>> byNumber = new(StringComparer.Ordinal);
            foreach (ResourceId thread in entries.Select(entry => entry.Thread).Distinct())
                if (thread.Thread is { Length: > 0 } number)
                {
                    if (!byNumber.TryGetValue(number, out List<ResourceId>? list)) byNumber[number] = list = new List<ResourceId>();
                    list.Add(thread);
                }
            return byNumber;
        });
        threadTitle = thread => entries.FirstOrDefault(entry => entry.Thread == thread)?.ThreadTitle;
        return new MediaFilterQueryVocabulary
        {
            Aliases = _queryAliases ??= MediaFilterQueryVocabulary.AliasesFrom(key => Texts.T(key)),
            FindTag = name =>
            {
                if (tags == null) return null;
                if (Guid.TryParseExact(name, "D", out Guid id)) return tags.Find(new TagId(id)) != null ? id : null;
                return PrivateTagName.TryCreate(name, out PrivateTagName? tagName, out _) && tagName != null
                    ? tags.Tags.FirstOrDefault(tag => tag.Name.Key == tagName.Key)?.Id.Value : null;
            },
            TagName = id => tags?.Find(new TagId(id))?.Name.Display,
            FindSource = text => sources.Value.FirstOrDefault(source => string.Equals(source, text, StringComparison.OrdinalIgnoreCase))
                ?? sources.Value.FirstOrDefault(source => string.Equals(SourceLabel(source), text, StringComparison.OrdinalIgnoreCase)),
            FindBoards = board => boards.Value.Where(pair => string.Equals(pair.Board, board, StringComparison.OrdinalIgnoreCase)).ToArray(),
            FindThreads = number => threads.Value.TryGetValue(number, out List<ResourceId>? list) ? list : Array.Empty<ResourceId>()
        };
    }

    /// <summary>The whole filter as a query (the file name last): what "Copy filters as text" puts on the clipboard.</summary>
    internal string FiltersAsText() => MediaFilterQuery.Serialize(MediaFilterTermSet.Ordered(AllTerms()), QueryVocabulary());

    /// <summary>Copies <see cref="FiltersAsText"/> and says so (or why not) in the hint below the chips.</summary>
    internal async Task CopyFiltersAsTextAsync()
    {
        string text = FiltersAsText();
        if (text.Length == 0 || _lifetime.IsCancellationRequested) return;
        long revision = _queryHintRevision;
        Func<bool> interaction = CaptureBooruHandoffInteraction();
        bool Current() => interaction() && revision == _queryHintRevision;
        try
        {
            if (CopyTextForTests is { } seam) await seam(text);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            else throw new InvalidOperationException("The clipboard is unavailable.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current()) ShowQueryHint(null, Texts.T("media.query.copyFailed"));
            return;
        }
        if (!Current()) return;
        ShowQueryHint(text.Length > MediaLibraryFilter.MaximumNameLength
            ? Texts.T("media.query.copiedLong", MediaLibraryFilter.MaximumNameLength) : Texts.T("media.query.copied"), null);
    }
}
