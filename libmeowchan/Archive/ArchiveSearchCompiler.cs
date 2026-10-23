using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

/// <summary>One parameterized predicate for count, pages, IDs and summaries. No desktop work state lives in SQL.</summary>
internal sealed class ArchiveSearchCompiler
{
    private readonly List<(string Name, object Value)> _parameters = [];
    internal ArchiveSearchCompiler(ArchiveSearchRequest request, string textPredicate, bool fullText)
    {
        ArchiveSearchSpec spec = request.Spec;
        List<string> terms = [textPredicate];
        string Bind(object value)
        {
            string name = "$filter" + _parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _parameters.Add((name, value));
            return name;
        }
        if (spec.Sources.Count > 0) terms.Add("t.source IN (" + string.Join(",", spec.Sources.Select(value => Bind(value))) + ")");
        if (spec.Boards.Count > 0) terms.Add("(" + string.Join(" OR ", spec.Boards.Select(board => board.Source == null
            ? "t.board=" + Bind(board.Board)
            : "(t.source=" + Bind(board.Source) + " AND t.board=" + Bind(board.Board) + ")")) + ")");
        if (spec.SourceStates.Count > 0) terms.Add("t.source_state IN (" + string.Join(",", spec.SourceStates.Select(state => Bind((int)state))) + ")");
        if (spec.CaptureEnabled.HasValue) terms.Add("t.capture_enabled=" + Bind(spec.CaptureEnabled.Value ? 1 : 0));
        if (spec.HasPosts.HasValue) terms.Add("EXISTS(SELECT 1 FROM archive_posts p WHERE p.source=t.source AND p.board=t.board AND p.thread=t.thread)=" + Bind(spec.HasPosts.Value ? 1 : 0));
        if (request.DateWindow?.StartInclusive is { } start) terms.Add("meowchan_utc(t.first_seen_at)>=" + Bind(ArchiveSql.Date(start)));
        if (request.DateWindow?.EndExclusive is { } end) terms.Add("meowchan_utc(t.first_seen_at)<" + Bind(ArchiveSql.Date(end)));
        Where = "WHERE " + string.Join(" AND ", terms.Select(term => "(" + term + ")"));
        Order = spec.Sort switch
        {
            ArchiveThreadSort.OldestFirst => "t.first_seen_at ASC,t.source,t.board,t.thread",
            ArchiveThreadSort.Title => "meowchan_text(t.title) COLLATE meowchan_case ASC,t.source,t.board,t.thread",
            ArchiveThreadSort.LatestCapture => "COALESCE(t.last_captured_at,t.first_seen_at) DESC,t.source,t.board,t.thread",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        if (fullText && spec.Text.Trim().Length > 0)
            _parameters.Add(("$match", "\"" + spec.Text.Trim().Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    }
    internal string Where { get; }
    internal string Order { get; }
    internal void Bind(SqliteCommand command)
    {
        foreach ((string name, object value) in _parameters) command.Parameters.AddWithValue(name, value);
    }
}
