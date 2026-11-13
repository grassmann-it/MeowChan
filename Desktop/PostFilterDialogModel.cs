using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal enum PostFilterScope { All, Source, Board }

/// <summary>
/// What the post filter dialog shows and does, without any control: the draft of a new rule with its validation, the
/// stored rules and individually hidden posts, and the changes, each made through the shared
/// <see cref="PostFilterSession"/> against the revision the dialog shows. Every outcome ends in <see cref="Status"/>.
/// </summary>
internal sealed class PostFilterDialogModel
{
    private readonly PostFilterSession _session;
    private readonly ResourceId? _context;
    private int _busy;

    internal sealed record RuleRow(PostFilterRule Rule, string Label, string Details, bool Supported);
    internal sealed record HiddenRow(HiddenPost Post, string Label);

    /// <param name="context">The open thread: "Only this source/board" refer to it. Without one a rule applies everywhere.</param>
    internal PostFilterDialogModel(PostFilterSession session, ResourceId? context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context;
        Scopes = context == null ? new[] { PostFilterScope.All } : new[] { PostFilterScope.All, PostFilterScope.Source, PostFilterScope.Board };
    }

    internal event EventHandler? Changed;

    // ---- The draft ----------------------------------------------------------------------------------------------------
    internal PostFilterField Field { get; set; } = PostFilterField.Text;
    internal PostFilterMatch Match { get; set; } = PostFilterMatch.Contains;
    internal string Pattern { get; set; } = string.Empty;
    internal PostFilterAction Action { get; set; } = PostFilterAction.Stub;
    internal bool Replies { get; set; }
    internal PostFilterScope Scope { get; set; } = PostFilterScope.All;
    internal IReadOnlyList<PostFilterScope> Scopes { get; }

    /// <summary>MD5 always compares the whole value; "Also hide replies" does not apply to highlighting.</summary>
    internal bool MatchSelectable => Field != PostFilterField.Md5;
    internal bool RepliesSelectable => Action != PostFilterAction.Highlight;

    /// <summary>Why the draft cannot be added; null when it is valid or still empty.</summary>
    internal string? DraftError
    {
        get
        {
            if (Pattern.Length == 0) return null;
            if (Field == PostFilterField.Md5)
                return PostFilterRules.TryParseMd5(Pattern, out _) ? null : Texts.T("postFilter.invalidMd5");
            try { PostFilterRules.ValidatePattern(Pattern); return null; }
            catch (ArgumentException) { return Texts.T("postFilter.invalidPattern"); }
        }
    }

    internal bool IsBusy => Volatile.Read(ref _busy) != 0;
    internal bool CanWrite => _session.Current?.CanWrite == true;
    internal bool CanAdd => !IsBusy && CanWrite && Pattern.Length > 0 && DraftError == null;

    internal PostFilterRuleDraft Draft()
    {
        string? source = Scope != PostFilterScope.All ? _context?.Source : null;
        string? board = Scope == PostFilterScope.Board ? _context?.Board : null;
        return new PostFilterRuleDraft(Field, Field == PostFilterField.Md5 ? PostFilterMatch.Exact : Match, Pattern, Action,
            RepliesSelectable && Replies, source, board);
    }

    // ---- What is stored -----------------------------------------------------------------------------------------------
    internal string? Status { get; private set; }
    internal bool StatusIsError { get; private set; }

    internal IReadOnlyList<RuleRow> Rules => (_session.Current?.Rules ?? Array.Empty<PostFilterRule>()).Select(Row).ToArray();
    internal IReadOnlyList<HiddenRow> HiddenPosts => (_session.Current?.HiddenPosts ?? Array.Empty<HiddenPost>())
        .OrderByDescending(post => post.Added).Select(post => new HiddenRow(post, PostLabel(post))).ToArray();
    internal HiddenPostsAction HiddenAction => _session.Current?.HiddenPostsAction ?? HiddenPostsAction.Stub;
    internal bool CanRecover => !IsBusy && _session.Current?.State == PostFilterStoreState.RecoveryAvailable;

    /// <summary>A sentence about the store itself (damaged with a backup, read-only, not loadable), or null when it is ready.</summary>
    internal string? StoreNotice
    {
        get
        {
            PostFilterSnapshot? current = _session.Current;
            if (current == null) return _session.LoadError is { } error ? Texts.T("postFilter.loadFailed", error) : null;
            return current.State switch
            {
                PostFilterStoreState.RecoveryAvailable => Texts.T("postFilter.recovery.available", current.Rules.Count, current.HiddenPosts.Count),
                PostFilterStoreState.Unsupported or PostFilterStoreState.Failed => Texts.T("postFilter.readonly", current.Error ?? string.Empty),
                _ => null
            };
        }
    }

    internal static string FieldLabel(PostFilterField field) => Texts.T("postFilter.field." + field switch
    {
        PostFilterField.Subject => "subject", PostFilterField.Filename => "filename", PostFilterField.Md5 => "md5", _ => "text"
    });

    internal static string MatchLabel(PostFilterMatch match) => Texts.T(match == PostFilterMatch.Exact ? "postFilter.match.exact" : "postFilter.match.contains");

    internal static string ActionLabel(PostFilterAction action) => Texts.T("postFilter.action." + action switch
    {
        PostFilterAction.Hide => "hide", PostFilterAction.Highlight => "highlight", _ => "stub"
    });

    internal string ScopeLabel(PostFilterScope scope) => scope switch
    {
        PostFilterScope.Source when _context != null => Texts.T("postFilter.scope.source", _context.Source),
        PostFilterScope.Board when _context != null => Texts.T("postFilter.scope.board", _context.Source + " /" + _context.Board + "/"),
        _ => Texts.T("postFilter.scope.all")
    };

    internal static string RuleScopeLabel(PostFilterRule rule) =>
        rule.Source == null ? Texts.T("postFilter.scope.all")
        : rule.Board == null ? Texts.T("postFilter.scope.source", rule.Source)
        : Texts.T("postFilter.scope.board", rule.Source + " /" + rule.Board + "/");

    private static RuleRow Row(PostFilterRule rule)
    {
        string label = Texts.T("postFilter.ruleRow", FieldLabel(rule.Field), MatchLabel(rule.Match), rule.Pattern);
        if (!rule.CanApply) return new RuleRow(rule, RawPattern(rule.RawDefinition) is { } raw ? "“" + raw + "”" : Texts.T("postFilter.unsupported"),
            Texts.T("postFilter.unsupported"), false);
        List<string> details = new() { ActionLabel(rule.Action), RuleScopeLabel(rule) };
        if (rule.Replies && rule.Action != PostFilterAction.Highlight) details.Add(Texts.T("postFilter.withReplies"));
        return new RuleRow(rule, label, string.Join(" · ", details), true);
    }

    // The pattern of a rule this version keeps unchanged (stage 2 "regex"), only for showing it.
    private static string? RawPattern(string? raw)
    {
        if (raw == null) return null;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("pattern", out System.Text.Json.JsonElement pattern)
                && pattern.ValueKind == System.Text.Json.JsonValueKind.String && pattern.GetString() is { Length: > 0 and <= 512 } text ? text : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    internal static string PostLabel(HiddenPost post)
    {
        string thread = post.Thread.Source + " /" + post.Thread.Board + "/ " + post.Thread.Thread;
        string label = Texts.T("postFilter.postRow", post.Post, thread) + " · " + post.Added.ToLocalTime().ToString("g", Texts.Culture);
        return post.Replies ? label + " · " + Texts.T("postFilter.withReplies") : label;
    }

    // ---- Changes ------------------------------------------------------------------------------------------------------
    internal Task LoadAsync(CancellationToken token = default) => RunAsync(async () =>
    {
        await _session.LoadAsync(token);
        return null;
    });

    /// <summary>Adds the draft; true when it was stored (the pattern is then cleared for the next rule).</summary>
    internal async Task<bool> AddAsync(CancellationToken token = default)
    {
        if (!CanAdd) { Report(DraftError ?? (CanWrite ? null : StoreNotice), error: true); return false; }
        bool added = false;
        await RunAsync(async () =>
        {
            await _session.AddRuleAsync(Draft(), token);
            Pattern = string.Empty;
            added = true;
            return Texts.T("postFilter.saved");
        });
        return added;
    }

    internal Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token = default) => RunAsync(async () =>
    {
        await _session.SetRuleEnabledAsync(id, enabled, token);
        return Texts.T("postFilter.saved");
    });

    internal Task RemoveAsync(Guid id, CancellationToken token = default) => RunAsync(async () =>
    {
        await _session.RemoveRuleAsync(id, token);
        return Texts.T("postFilter.saved");
    });

    internal Task UnhideAsync(HiddenPost post, CancellationToken token = default) => RunAsync(async () =>
    {
        await _session.UnhidePostAsync(post.Thread, post.Post, token);
        return Texts.T("postFilter.unhideDone", post.Post);
    });

    internal Task SetHiddenActionAsync(HiddenPostsAction action, CancellationToken token = default) => action == HiddenAction
        ? Task.CompletedTask
        : RunAsync(async () =>
        {
            await _session.SetHiddenPostsActionAsync(action, token);
            return Texts.T("postFilter.saved");
        });

    internal Task RecoverAsync(CancellationToken token = default) => RunAsync(async () =>
    {
        await _session.RecoverAsync(token);
        return Texts.T("postFilter.recovery.done");
    });

    private async Task RunAsync(Func<Task<string?>> work)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            string? done = await work();
            Report(done, error: false);
        }
        catch (OperationCanceledException) { Report(null, error: false); }
        catch (Exception error) { Report(PostFilterSession.ErrorText(error), error: true); }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Report(string? text, bool error)
    {
        Status = text;
        StatusIsError = error && text != null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
