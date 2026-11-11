namespace MeowChan.DesktopSmoke;

// Harness-only entry guard. Program dispatches on dozens of MEOWCHAN_*_ONLY variables; a
// misspelled, unregistered or misplaced one used to fall through to the normal suite and still
// print SMOKE PASS. Every selector the driver reads is registered in SmokeScenarios with what it needs to act.
// Pure: no window, no profile; DesktopTests compile this file directly.
internal static class SmokeSelectors
{
    internal const string NormalMode = "normal";

    // The inventory comes from SmokeScenarios: Root entries own a top-level run, valid only in the
    // plain run without a command-line mode and only one of them (or of BranchSelectors) per run.
    private static readonly string[] Roots = SmokeScenarios.All
        .Where(scenario => scenario.Kind == SmokeScenarioKind.Root).Select(scenario => scenario.Selector).ToArray();

    // Top-level branches without the _ONLY suffix. They are parents of some selectors and part
    // of the scenario id; scripts/run-desktop-smoke.py mirrors this list.
    internal static readonly (string Name, string Value)[] BranchSelectors = SmokeScenarios.All
        .Where(scenario => scenario.Kind == SmokeScenarioKind.Branch)
        .Select(scenario => scenario.Selector.Split('=') is [string name, string value] ? (name, value) : (scenario.Selector, "1"))
        .ToArray();

    // Read only inside a mode or another selector's branch. Any one listed requirement suffices:
    // "--mode" for a command-line scenario, a selector name, or NAME=value for a branch selector.
    private static readonly (string Name, string[] Requires)[] Children = SmokeScenarios.All
        .Where(scenario => scenario.Kind == SmokeScenarioKind.Nested).Select(scenario => (scenario.Selector, scenario.Requires))
        .ToArray();

    internal static IEnumerable<string> RootNames => Roots;
    internal static IEnumerable<(string Name, string[] Requires)> ChildRequirements => Children;
    internal static IEnumerable<string> KnownNames => Roots.Concat(Children.Select(child => child.Name));

    // Returns null when the run is unambiguous, otherwise a message for exit code 2.
    // scenario: "<mode>" plus "+<selector>" for each active selector in ordinal order.
    internal static string? Check(IReadOnlyList<string> args, IEnumerable<KeyValuePair<string, string?>> environment,
        bool ignoreCase, out string scenario) => Check(args, environment, ignoreCase, out scenario, out _);

    // selected: the registry entry whose run and PASS claim apply (the root or branch selector,
    // otherwise the mode); null on error or for an unregistered mode.
    internal static string? Check(IReadOnlyList<string> args, IEnumerable<KeyValuePair<string, string?>> environment,
        bool ignoreCase, out string scenario, out SmokeScenario? selected)
    {
        StringComparer names = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string mode = args.Count == 0 ? NormalMode : args[0].StartsWith("--", StringComparison.Ordinal) ? args[0][2..] : args[0];
        Dictionary<string, string> known = KnownNames.ToDictionary(name => name, name => name, names);
        Dictionary<string, string?> variables = new(names);
        foreach ((string key, string? value) in environment) variables[key] = value;
        scenario = mode;
        selected = null;

        List<string> errors = [];
        SortedSet<string> active = new(StringComparer.Ordinal);
        foreach ((string key, string? value) in variables)
        {
            // Match the shape case-insensitively everywhere: a wrongly cased name is a typo too.
            if (!key.StartsWith("MEOWCHAN_", StringComparison.OrdinalIgnoreCase)
                || !key.EndsWith("_ONLY", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(value) || value == "0")
                continue;
            if (!known.TryGetValue(key, out string? name))
                errors.Add("unknown selector " + key + "=" + value + " (not read by MeowChan.DesktopSmoke; check the spelling)");
            else if (value != "1")
                errors.Add(name + "=" + value + " is ignored by the driver; use 1");
            else
                active.Add(name);
        }
        foreach ((string name, string value) in BranchSelectors)
            if (variables.TryGetValue(name, out string? actual) && actual == value)
                active.Add(value == "1" ? name : name + "=" + value);
        if (errors.Count > 0) return string.Join("; ", errors);

        HashSet<string> conditions = new(active, StringComparer.Ordinal);
        if (args.Count != 0) conditions.Add("--" + mode);
        string[] roots = active.Where(name => !Children.Any(child => child.Name == name)).ToArray();
        if (roots.Length > 1)
            return "scenario selectors are mutually exclusive, but " + roots.Length + " are set: " + string.Join(", ", roots);
        if (roots.Length == 1 && args.Count != 0)
            return roots[0] + " runs only without a command-line scenario, not with " + args[0];
        foreach ((string name, string[] requires) in Children)
            if (active.Contains(name) && !requires.Any(conditions.Contains))
                return name + " has no effect without " + string.Join(" or ", requires)
                    + (args.Count == 0 ? " (got the normal scene)" : " (got " + args[0] + ")");

        scenario = string.Concat(active.Prepend(mode).Select((part, index) => index == 0 ? part : "+" + part));
        string selector = roots.Length == 1 ? roots[0] : args.Count == 0 ? "" : args[0];
        selected = SmokeScenarios.All.FirstOrDefault(entry => entry.Kind != SmokeScenarioKind.Nested && entry.Selector == selector);
        return null;
    }
}
