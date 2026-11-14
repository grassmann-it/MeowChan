using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan;

/// <summary>The three steps of the in-page rule wizard.</summary>
internal enum RuleWizardStep { Scope = 1, Settings = 2, Review = 3 }

/// <summary>
/// Which view the Rules page shows: the overview (default) or the wizard at one step. Pure state: drafts are not part of it,
/// so opening, closing or leaving the page never touches a draft (they live in the page's draft table as before).
/// </summary>
internal sealed class RuleWizardState
{
    internal const int StepCount = 3;
    internal bool IsOpen { get; private set; }
    internal RuleWizardStep Step { get; private set; } = RuleWizardStep.Scope;
    /// <summary>A scope was chosen in this wizard session (or the wizard was opened for one), so steps 2 and 3 are reachable.</summary>
    internal bool HasScope { get; private set; }
    /// <summary>The step to show once a scope is chosen ("Start from template" opens the templates helper in step 2).</summary>
    internal bool RevealTemplates { get; set; }

    /// <summary>"Create rule": a fresh wizard at step 1, no scope yet.</summary>
    internal void OpenNew(bool revealTemplates = false)
    {
        IsOpen = true; HasScope = false; Step = RuleWizardStep.Scope; RevealTemplates = revealTemplates;
    }

    /// <summary>Edit an existing rule or continue a draft: straight to step 2, the step chips stay usable.</summary>
    internal void OpenEdit()
    {
        IsOpen = true; HasScope = true; Step = RuleWizardStep.Settings;
    }

    internal bool CanGoTo(RuleWizardStep step) => IsOpen && (step == RuleWizardStep.Scope || HasScope);

    internal bool GoTo(RuleWizardStep step)
    {
        if (!CanGoTo(step) || Step == step) return false;
        Step = step;
        return true;
    }

    /// <summary>Next from step 2 (review); step 1 continues through choosing its scope instead.</summary>
    internal bool Next() => Step == RuleWizardStep.Settings && GoTo(RuleWizardStep.Review);

    internal bool Back() => Step switch
    {
        RuleWizardStep.Review => GoTo(RuleWizardStep.Settings),
        RuleWizardStep.Settings => GoTo(RuleWizardStep.Scope),
        _ => false
    };

    internal void Close() { IsOpen = false; RevealTemplates = false; }
}

internal enum RuleChipTone { Neutral, Success, Warning }

internal sealed record RuleChip(string Text, RuleChipTone Tone = RuleChipTone.Neutral);

/// <summary>One row of the overview: a saved rule (optionally with an unsaved draft on top) or a draft of a scope without a saved rule.</summary>
internal sealed record RuleOverviewRow(MediaRuleScope Scope, string ScopeLabel, bool Saved, bool Enabled, bool HasDraft, IReadOnlyList<RuleChip> Chips);

internal sealed record RuleOverviewGroup(MediaRuleScopeKind Kind, string Title, IReadOnlyList<RuleOverviewRow> Rows);

/// <summary>An unsaved draft as the overview needs it: its scope and, when its input is valid, the rule it would save.</summary>
internal sealed record RuleOverviewDraft(MediaRuleScope Scope, MediaRule? Rule, bool Stale);

internal sealed record RuleOverview(IReadOnlyList<RuleOverviewRow> Drafts, IReadOnlyList<RuleOverviewGroup> Groups, IReadOnlyList<RuleChip> Defaults)
{
    internal bool IsEmpty => Drafts.Count == 0 && Groups.Count == 0;
    internal int SavedCount => Groups.Sum(group => group.Rows.Count);
}

/// <summary>
/// Builds the Rules overview: saved rules grouped by scope kind in precedence order of breadth (Global, Source, Board, Thread),
/// each with compact chips of what it overrides; unsaved drafts; and the read-only built-in baseline every rule inherits from.
/// </summary>
internal static class RulesOverviewModel
{
    /// <summary>Display limit only (not a user limit): a chip lists this many extensions, then "+n".</summary>
    internal const int ChipExtensionCount = 4;

    internal static RuleOverview Build(MediaRuleSet confirmed, IEnumerable<RuleOverviewDraft> drafts,
        Func<MediaImageFilterRule, string>? filterName = null)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        filterName ??= DefaultFilterName;
        RuleOverviewDraft[] open = (drafts ?? Array.Empty<RuleOverviewDraft>()).ToArray();
        HashSet<MediaRuleScope> drafted = open.Select(draft => draft.Scope).ToHashSet();

        RuleOverviewRow[] draftRows = Order(open, draft => draft.Scope).Select(draft =>
        {
            MediaRule? saved = confirmed.Rules.FirstOrDefault(rule => rule.Scope == draft.Scope);
            List<RuleChip> chips = new() { new RuleChip(Texts.T(saved == null ? "rules.notSaved" : "rules.unsaved"), RuleChipTone.Warning) };
            if (draft.Stale) chips.Add(new RuleChip(Texts.T("rules.overview.changedElsewhere"), RuleChipTone.Warning));
            else if (draft.Rule == null) chips.Add(new RuleChip(Texts.T("rules.overview.invalidInput"), RuleChipTone.Warning));
            else chips.AddRange(RuleChips(draft.Rule, filterName));
            return new RuleOverviewRow(draft.Scope, ScopeLabel(draft.Scope), saved != null, draft.Rule?.Enabled ?? saved?.Enabled ?? true, true, chips);
        }).ToArray();

        RuleOverviewGroup[] groups = Order(confirmed.Rules, rule => rule.Scope)
            .GroupBy(rule => rule.Scope.Kind)
            .Select(group => new RuleOverviewGroup(group.Key, KindLabel(group.Key), group.Select(rule =>
            {
                List<RuleChip> chips = new() { StateChip(rule.Enabled) };
                if (drafted.Contains(rule.Scope)) chips.Add(new RuleChip(Texts.T("rules.unsaved"), RuleChipTone.Warning));
                chips.AddRange(RuleChips(rule, filterName));
                // Under its kind's heading a row names only the identity ("4chan/g"); drafts are ungrouped and keep the full label.
                return new RuleOverviewRow(rule.Scope, IdentityLabel(rule.Scope), true, rule.Enabled, drafted.Contains(rule.Scope), chips);
            }).ToArray())).ToArray();

        return new RuleOverview(draftRows, groups, DefaultChips(filterName));
    }

    /// <summary>The scope kinds from broadest to most specific, then identities in ordinal order (as the scope list orders them).</summary>
    internal static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, MediaRuleScope> scope) =>
        items.OrderBy(item => scope(item).Kind).ThenBy(item => scope(item).Source, StringComparer.Ordinal)
            .ThenBy(item => scope(item).Board, StringComparer.Ordinal).ThenBy(item => scope(item).Thread, StringComparer.Ordinal);

    internal static RuleChip StateChip(bool enabled) =>
        enabled ? new RuleChip(Texts.T("rules.overview.active"), RuleChipTone.Success) : new RuleChip(Texts.T("rules.overview.disabled"), RuleChipTone.Warning);

    /// <summary>What a rule sets itself; inherited fields have no chip. A rule that sets nothing says so.</summary>
    internal static IReadOnlyList<RuleChip> RuleChips(MediaRule rule, Func<MediaImageFilterRule, string>? filterName = null)
    {
        filterName ??= DefaultFilterName;
        List<RuleChip> chips = new();
        if (TypesChip(rule.Types) is { } types) chips.Add(new RuleChip(types));
        if (SizeChip(rule.SizeLimit) is { } size) chips.Add(new RuleChip(size));
        if (FilterChip(rule.ImageFilter, filterName) is { } filter) chips.Add(new RuleChip(filter));
        if (chips.Count == 0) chips.Add(new RuleChip(Texts.T("rules.overview.inheritsAll")));
        return chips;
    }

    /// <summary>The built-in values a rule without own values resolves to (the bottom of the precedence chain).</summary>
    internal static IReadOnlyList<RuleChip> DefaultChips(Func<MediaImageFilterRule, string>? filterName = null)
    {
        EffectiveMediaRule builtIn = new MediaRuleResolver(new MediaRuleSet("built-in", Array.Empty<MediaRule>())).Resolve(MediaRuleScope.Global);
        return new[] { TypesChip(builtIn.Types), SizeChip(builtIn.SizeLimit), FilterChip(builtIn.ImageFilter, filterName ?? DefaultFilterName) }
            .Where(text => text != null).Select(text => new RuleChip(text!)).ToArray();
    }

    internal static string? TypesChip(MediaTypeSelection types) => types.Mode switch
    {
        MediaTypeSelectionMode.All => Texts.T("rules.allTypes"),
        MediaTypeSelectionMode.Selected when types.Extensions.Count == 0 => Texts.T("rules.noMedia"),
        MediaTypeSelectionMode.Selected when types.Extensions.Count > ChipExtensionCount =>
            Texts.T("rules.overview.typesMore", string.Join(", ", types.Extensions.Take(ChipExtensionCount)), types.Extensions.Count - ChipExtensionCount),
        MediaTypeSelectionMode.Selected => string.Join(", ", types.Extensions),
        _ => null
    };

    internal static string? SizeChip(MediaSizeLimit size) => size.Mode switch
    {
        MediaSizeLimitMode.Unlimited => Texts.T("rules.overview.noSizeLimit"),
        MediaSizeLimitMode.Limited => Texts.T("rules.overview.sizeAtMost", RulesPage.FormatMebibytes(size.Bytes ?? 0, Texts.Culture)),
        _ => null
    };

    internal static string? FilterChip(MediaImageFilterRule filter, Func<MediaImageFilterRule, string> filterName) => filter.Mode switch
    {
        MediaImageFilterMode.Disabled => Texts.T("rules.overview.filterOff"),
        MediaImageFilterMode.Plugin => Texts.T("rules.overview.filter", filterName(filter)),
        _ => null
    };

    internal static string IdentityLabel(MediaRuleScope scope) => scope.Kind switch
    {
        MediaRuleScopeKind.Global => Texts.T("rules.global"),
        MediaRuleScopeKind.Source => scope.Source!,
        MediaRuleScopeKind.Board => scope.Source + "/" + scope.Board,
        _ => scope.Source + "/" + scope.Board + "/" + scope.Thread
    };

    internal static string ScopeLabel(MediaRuleScope scope) => MeowChan.Desktop.Plugins.PluginGrantPresentation.ScopeLabel(scope);

    internal static string KindLabel(MediaRuleScopeKind kind) => Texts.T(kind switch
    {
        MediaRuleScopeKind.Source => "rules.source",
        MediaRuleScopeKind.Board => "rules.board",
        MediaRuleScopeKind.Thread => "rules.thread",
        _ => "rules.global"
    });

    private static string DefaultFilterName(MediaImageFilterRule filter) =>
        string.IsNullOrEmpty(filter.FilterKey) ? filter.PluginId ?? string.Empty : filter.FilterKey!;

    /// <summary>A stable fingerprint of what the overview shows, so it is rebuilt (and focus restored) only when that changes.</summary>
    internal static string Fingerprint(RuleOverview overview) => string.Join("\u001f",
        overview.Drafts.Concat(overview.Groups.SelectMany(group => group.Rows)).Select(row => row.Scope + "|" + row.Saved + "|" + row.Enabled
            + "|" + row.HasDraft + "|" + string.Join("\u001e", row.Chips.Select(chip => chip.Tone + ":" + chip.Text))))
        + "\u001d" + string.Join("\u001e", overview.Defaults.Select(chip => chip.Text)) + "\u001d" + Texts.Language
        + "\u001d" + CultureInfo.CurrentCulture.Name;
}
