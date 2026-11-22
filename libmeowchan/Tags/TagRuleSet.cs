using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tags;

internal enum TagRuleKind { Alias, Parent, Namespace, Ignore }

/// <summary>Unspecified fields match any value. A board is always scoped to a source.</summary>
internal sealed record TagRuleScope(string? Source = null, string? Board = null, string? PluginId = null);

/// <summary>Exact name keys. Value is an alias target, a parent, or a namespace prefix, according to Kind.</summary>
internal sealed record TagRule(TagRuleKind Kind, string Name, string? Value = null, TagRuleScope? Scope = null);

internal abstract record TagRuleOrigin
{
    private TagRuleOrigin() { }

    internal sealed record Provider(string? PluginId = null) : TagRuleOrigin;
    internal sealed record Private : TagRuleOrigin;
    internal sealed record Model(StoredTagSuggestion Suggestion, long Revision) : TagRuleOrigin;
}

internal sealed record TagRuleInput(string OriginalName, MediaTagSubject Subject, TagRuleOrigin Origin)
{
    internal ResourceId Resource => Subject switch
    {
        MediaTagSubject.Bound bound => bound.Target.Reference.Resource,
        MediaTagSubject.Local local => local.Observation.Locator.Thread,
        _ => throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget)
    };
}

/// <summary>The original term, provenance and exact occurrence stay available even if the rule hides the tag.</summary>
internal sealed record TagRuleProjection(TagRuleInput Input, PrivateTagName? Canonical, IReadOnlyList<PrivateTagName> Parents)
{
    internal bool IsIgnored => Canonical is null;

    internal IEnumerable<PrivateTagName> MappedNames()
        => Canonical is { } canonical ? Parents.Prepend(canonical) : [];
}

/// <summary>The occurrence the reviewer saw, with the same identities used by the rule projection.</summary>
internal sealed record TagReviewTarget(string Sha256, TagReviewBoundTarget? Bound, TagReviewLocalTarget? Local)
{
    internal static TagReviewTarget From(MediaTagSubject subject) => subject switch
    {
        MediaTagSubject.Bound { Target: var target } =>
            new(target.Sha256, new(target.Reference, target.MediaId, target.Variant, target.Role), null),
        MediaTagSubject.Local { Observation: var observation, Expected: var expected } =>
            new(observation.Sha256, null, new(observation.Locator, observation.AssetId, observation.Length,
                expected?.Occurrence.Value, expected?.Revision.Value)),
        _ => throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget)
    };

    internal bool Matches(StoredTagSuggestion suggestion) =>
        Sha256 == suggestion.MediaSha256 && (Bound, Local) switch
        {
            ({ } bound, null) => PrivateMediaTagTarget.TryCreate(bound.Reference, bound.MediaId, bound.Variant, bound.Role,
                Sha256, out _, out _),
            (null, { Locator: { } locator } local) => local.Length == suggestion.MediaLength
                && (local.Occurrence is null && local.Revision is null
                    || local.Occurrence is { } occurrence && occurrence != Guid.Empty
                    && local.Revision is { } revision && revision != Guid.Empty)
                && LocalMediaObservation.TryCreate(locator.Thread, locator.Directory, locator.RequestedFileName,
                    local.AssetId, local.Length, Sha256, out _),
            _ => false
        };
}

internal sealed record TagReviewBoundTarget(AttachmentRevisionReference Reference, string MediaId, string Variant, TransferMediaRole Role);
internal sealed record TagReviewLocalTarget(LocalMediaLocator Locator, string AssetId, long Length, Guid? Occurrence, Guid? Revision);

internal enum TagRuleError { InvalidRule, InvalidName, AmbiguousMapping, Cycle, UnassignedPrivateTag }

/// <summary>Rule indices are zero-based. Diagnostics contain codes and indices only.</summary>
internal sealed class TagRuleException(TagRuleError error, int? ruleIndex = null, int? relatedRuleIndex = null)
    : Exception("The tag rule operation was rejected: " + error + ".")
{
    internal TagRuleError Error { get; } = error;
    internal int? RuleIndex { get; } = ruleIndex;
    internal int? RelatedRuleIndex { get; } = relatedRuleIndex;
}

/// <summary>
/// Immutable, caller-supplied rules. All matching scopes contribute. Different canonical targets for the same
/// name are ambiguous. Ignore hides a raw name or its canonical name, or cuts an inherited parent branch.
/// Parents are followed after alias and namespace resolution. Original names remain on the inputs.
/// </summary>
internal sealed class TagRuleSet
{
    private readonly Rule[] _rules;

    private sealed record Rule(TagRuleKind Kind, PrivateTagName Name, PrivateTagName? Target, TagRuleScope Scope, int Index);
    private sealed record Edge(PrivateTagName Target, int RuleIndex);

    internal TagRuleSet(IEnumerable<TagRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.Select(Parse).ToArray();
    }

    internal TagRuleProjection Project(TagRuleInput input) => Project([input], null, CancellationToken.None)[0];

    internal TagRuleProjection[] Project(IReadOnlyList<TagRuleInput> inputs,
        Func<PrivateTagName, PrivateTagName?>? resolvePrivateName, CancellationToken token)
    {
        Dictionary<TagRuleScope, Graph> graphs = new();
        var projections = new TagRuleProjection[inputs.Count];
        for (int index = 0; index < inputs.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            TagRuleInput input = inputs[index];
            string? plugin = input.Origin switch
            {
                TagRuleOrigin.Provider provider => provider.PluginId,
                TagRuleOrigin.Model model => model.Suggestion.Provenance.PluginId,
                TagRuleOrigin.Private => null,
                _ => throw new TagRuleException(TagRuleError.InvalidRule)
            };
            ResourceId resource = input.Resource;
            TagRuleScope context = new(resource.Source, resource.Board, plugin);
            if (!graphs.TryGetValue(context, out Graph? graph))
            {
                graph = new Graph(_rules.Where(rule => Matches(rule.Scope, context)), resolvePrivateName, token);
                graphs.Add(context, graph);
            }
            projections[index] = graph.Project(input, token);
        }
        return projections;
    }

    private static bool Matches(TagRuleScope scope, TagRuleScope context) =>
        (scope.Source is null || scope.Source == context.Source)
        && (scope.Board is null || scope.Board == context.Board)
        && (scope.PluginId is null || scope.PluginId == context.PluginId);

    private static Rule Parse(TagRule rule, int index)
    {
        if (rule is null || !Enum.IsDefined(rule.Kind)) throw new TagRuleException(TagRuleError.InvalidRule, index);
        TagRuleScope scope = rule.Scope ?? new();
        if (scope.Source is not null && string.IsNullOrWhiteSpace(scope.Source)
            || scope.Board is not null && (scope.Source is null || string.IsNullOrWhiteSpace(scope.Board))
            || scope.PluginId is not null && string.IsNullOrWhiteSpace(scope.PluginId))
            throw new TagRuleException(TagRuleError.InvalidRule, index);
        PrivateTagName name = RequireName(rule.Name, index);
        if (rule.Kind == TagRuleKind.Ignore)
        {
            if (rule.Value is not null) throw new TagRuleException(TagRuleError.InvalidRule, index);
            return new(rule.Kind, name, null, scope, index);
        }

        PrivateTagName target = RequireName(rule.Value, index);
        if (rule.Kind == TagRuleKind.Namespace)
        {
            if (name.Display.Contains(':') || target.Display.Contains(':') || target.Display.Any(char.IsWhiteSpace))
                throw new TagRuleException(TagRuleError.InvalidRule, index);
            target = RequireName(target.Display + ":" + name.Display, index);
        }
        return new(rule.Kind, name, target, scope, index);
    }

    private static PrivateTagName RequireName(string? text, int? ruleIndex = null) =>
        PrivateTagName.TryCreate(text, out PrivateTagName? name, out _) && name is not null
            ? name : throw new TagRuleException(TagRuleError.InvalidName, ruleIndex);

    private sealed class Graph
    {
        private readonly Dictionary<PrivateTagName, PrivateTagName> _canonical = new();
        private readonly Dictionary<PrivateTagName, Edge> _aliases = new();
        private readonly Dictionary<PrivateTagName, List<Edge>> _parents = new();
        private readonly HashSet<PrivateTagName> _ignored = new();
        private readonly Func<PrivateTagName, PrivateTagName?>? _resolvePrivateName;

        internal Graph(IEnumerable<Rule> rules, Func<PrivateTagName, PrivateTagName?>? resolvePrivateName, CancellationToken token)
        {
            _resolvePrivateName = resolvePrivateName;
            List<Rule> parents = new();
            foreach (Rule rule in rules)
            {
                token.ThrowIfCancellationRequested();
                switch (rule.Kind)
                {
                    case TagRuleKind.Ignore:
                        _ignored.Add(rule.Name);
                        break;
                    case TagRuleKind.Parent:
                        parents.Add(rule);
                        break;
                    default:
                        if (_aliases.TryGetValue(rule.Name, out Edge? previous) && previous.Target != rule.Target)
                            throw new TagRuleException(TagRuleError.AmbiguousMapping, rule.Index, previous.RuleIndex);
                        _aliases.TryAdd(rule.Name, new(rule.Target!, rule.Index));
                        break;
                }
            }
            foreach (PrivateTagName alias in _aliases.Keys) Canonical(alias, token);
            foreach (Rule rule in parents)
            {
                token.ThrowIfCancellationRequested();
                PrivateTagName child = Canonical(rule.Name, token);
                if (!_parents.TryGetValue(child, out List<Edge>? edges))
                    _parents.Add(child, edges = new());
                edges.Add(new(Canonical(rule.Target!, token), rule.Index));
            }
            CheckParentCycles(token);
        }

        private PrivateTagName Canonical(PrivateTagName name, CancellationToken token)
        {
            if (_canonical.TryGetValue(name, out PrivateTagName? mapped)) return mapped;
            HashSet<PrivateTagName> path = new();
            int? lastRule = null;
            while (!_canonical.TryGetValue(name, out mapped))
            {
                token.ThrowIfCancellationRequested();
                if (!path.Add(name)) throw new TagRuleException(TagRuleError.Cycle, lastRule);
                if (_aliases.TryGetValue(name, out Edge? edge))
                {
                    name = edge.Target;
                    lastRule = edge.RuleIndex;
                    continue;
                }

                // Existing private aliases are the fallback at each step, after explicit rules for that name.
                mapped = _resolvePrivateName?.Invoke(name);
                if (mapped is null)
                {
                    // An unaliased name keeps its input spelling, even if a rule mentions the same key.
                    path.Remove(name);
                    mapped = name;
                    break;
                }
                if (mapped == name) break;
                name = mapped;
            }
            foreach (PrivateTagName alias in path) _canonical.Add(alias, mapped);
            return mapped;
        }

        private void CheckParentCycles(CancellationToken token)
        {
            HashSet<PrivateTagName> visited = new();
            HashSet<PrivateTagName> active = new();
            Stack<(PrivateTagName Name, int NextEdge)> pending = new();
            foreach (PrivateTagName root in _parents.Keys)
            {
                if (!visited.Add(root)) continue;
                active.Add(root);
                pending.Push((root, 0));
                while (pending.TryPop(out var frame))
                {
                    token.ThrowIfCancellationRequested();
                    List<Edge> edges = _parents[frame.Name];
                    if (frame.NextEdge == edges.Count)
                    {
                        active.Remove(frame.Name);
                        continue;
                    }
                    Edge edge = edges[frame.NextEdge];
                    pending.Push((frame.Name, frame.NextEdge + 1));
                    if (active.Contains(edge.Target)) throw new TagRuleException(TagRuleError.Cycle, edge.RuleIndex);
                    if (!_parents.ContainsKey(edge.Target) || !visited.Add(edge.Target)) continue;
                    active.Add(edge.Target);
                    pending.Push((edge.Target, 0));
                }
            }
        }

        internal TagRuleProjection Project(TagRuleInput input, CancellationToken token)
        {
            PrivateTagName raw = RequireName(input.OriginalName);
            if (_ignored.Contains(raw)) return new(input, null, []);
            PrivateTagName canonical = Canonical(raw, token);
            if (_ignored.Contains(canonical)) return new(input, null, []);
            if (!_parents.ContainsKey(canonical)) return new(input, canonical, []);

            List<PrivateTagName> parents = new();
            HashSet<PrivateTagName> seen = new() { canonical };
            Queue<PrivateTagName> pending = new();
            pending.Enqueue(canonical);
            while (pending.TryDequeue(out PrivateTagName? name))
            {
                token.ThrowIfCancellationRequested();
                if (!_parents.TryGetValue(name, out List<Edge>? edges)) continue;
                foreach (Edge edge in edges)
                {
                    if (_ignored.Contains(edge.Target) || !seen.Add(edge.Target)) continue;
                    parents.Add(edge.Target);
                    pending.Enqueue(edge.Target);
                }
            }
            return new(input, canonical, parents.AsReadOnly());
        }
    }
}
