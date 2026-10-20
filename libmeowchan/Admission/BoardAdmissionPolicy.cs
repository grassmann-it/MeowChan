using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MeowChan.Admission;

public enum ThreadTextField { Title, OpeningPost, TitleAndOpeningPost }
public enum ThreadTextAction { Include, Exclude }
public enum ThreadAdmissionException { Include, Exclude }

public sealed record ThreadTextRule(ThreadTextField Field, ThreadTextAction Action, string Contains);
public sealed record BoardCatalogThread(string Id, string Url, string Title, string OpeningPost, bool Suppressed = false, bool AlreadyWatching = false, bool ManuallyWatching = false);
public sealed record BoardAdmissionDecision(bool Included, string Reason);

/// <summary>Literal ordinal case-insensitive matching. Includes are ORed; excludes win. Explicit choices override text rules.</summary>
public sealed class BoardAdmissionPolicy
{
    public IReadOnlyList<ThreadTextRule> Rules { get; }
    public IReadOnlyDictionary<string, ThreadAdmissionException> Exceptions { get; }
    public static BoardAdmissionPolicy All { get; } = new(Array.Empty<ThreadTextRule>());

    public BoardAdmissionPolicy(IEnumerable<ThreadTextRule> rules,
        IReadOnlyDictionary<string, ThreadAdmissionException>? exceptions = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ThreadTextRule[] copied = rules.Select(rule =>
        {
            if (rule == null || !Enum.IsDefined(rule.Field) || !Enum.IsDefined(rule.Action)
                || string.IsNullOrWhiteSpace(rule.Contains)) throw new ArgumentException("Every text rule needs a field, action and search text.");
            return rule with { };
        }).ToArray();
        Dictionary<string, ThreadAdmissionException> choices = new(StringComparer.Ordinal);
        foreach (var pair in exceptions ?? new Dictionary<string, ThreadAdmissionException>())
        {
            if (!Uri.TryCreate(pair.Key, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" && uri.Scheme != "http"
                || !Enum.IsDefined(pair.Value)) throw new ArgumentException("Invalid thread exception.");
            choices.Add(pair.Key, pair.Value);
        }
        Rules = Array.AsReadOnly(copied);
        Exceptions = new ReadOnlyDictionary<string, ThreadAdmissionException>(choices);
    }

    public BoardAdmissionDecision Evaluate(BoardCatalogThread thread, bool honorSuppression = true)
    {
        if (honorSuppression && thread.Suppressed) return new(false, "suppressed");
        if (Exceptions.TryGetValue(thread.Url, out ThreadAdmissionException choice))
            return new(choice == ThreadAdmissionException.Include, "manual");
        bool matches(ThreadTextRule rule)
        {
            string title = thread.Title ?? string.Empty;
            string op = thread.OpeningPost ?? string.Empty;
            return (rule.Field != ThreadTextField.OpeningPost && title.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                || (rule.Field != ThreadTextField.Title && op.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase));
        }
        if (Rules.Any(rule => rule.Action == ThreadTextAction.Exclude && matches(rule))) return new(false, "excludeRule");
        bool hasIncludes = Rules.Any(rule => rule.Action == ThreadTextAction.Include);
        if (hasIncludes && !Rules.Any(rule => rule.Action == ThreadTextAction.Include && matches(rule))) return new(false, "noInclude");
        return new(true, hasIncludes ? "includeRule" : "all");
    }
}

public sealed record BoardAdmissionTarget(string Url, bool IsBoard, bool AlreadyWatching);
public sealed record BoardAdmissionPreview(string Url, string Name, BoardAdmissionPolicy Policy,
    IReadOnlyList<BoardCatalogThread> Threads);
