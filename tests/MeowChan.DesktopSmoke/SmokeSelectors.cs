namespace MeowChan.DesktopSmoke;

// Harness-only entry guard. Program dispatches on dozens of MEOWCHAN_*_ONLY variables; a
// misspelled, unregistered or misplaced one used to fall through to the normal suite and still
// print SMOKE PASS. Every selector the driver reads is listed here with what it needs to act.
// Pure: no window, no profile; DesktopTests compile this file directly.
internal static class SmokeSelectors
{
    internal const string NormalMode = "normal";

    // Own a top-level RunAsync branch: valid only in the plain run without a command-line mode,
    // and only one of them (or of BranchSelectors) per run.
    private static readonly string[] Roots =
    [
        "MEOWCHAN_ACTION_DYNAMIC_ONLY", "MEOWCHAN_ACTION_OVERFLOW_ONLY", "MEOWCHAN_ARCHIVE_MEDIA_COUNTS_ONLY",
        "MEOWCHAN_BOARD_LANGUAGE_REFRESH_ONLY", "MEOWCHAN_CLIPBOARD_MONITOR_ONLY", "MEOWCHAN_COMMAND_CONTRAST_ONLY",
        "MEOWCHAN_COMPACT_DIAGNOSTICS_ONLY", "MEOWCHAN_DIAGNOSTIC_EVIDENCE_ONLY", "MEOWCHAN_DIAGNOSTIC_FEEDBACK_ONLY",
        "MEOWCHAN_DIALOG_FRAME_ONLY", "MEOWCHAN_DOWNLOAD_SAFETY_ONLY", "MEOWCHAN_DOWNLOAD_SEARCH_ONLY",
        "MEOWCHAN_EXPORT_STATUS_LANGUAGE_ONLY", "MEOWCHAN_FEEDBACK_ONLY", "MEOWCHAN_FILENAME_SEARCH_HOST_ONLY",
        "MEOWCHAN_FILENAME_SEARCH_LARGE_READER_ONLY", "MEOWCHAN_FILENAME_SEARCH_ONLY", "MEOWCHAN_FIRST_RUN_REOPEN_ONLY",
        "MEOWCHAN_FIRST_RUN_SETTINGS_ONLY", "MEOWCHAN_FIRST_RUN_SETUP_ONLY", "MEOWCHAN_FOOTER_NAVIGATION_ONLY",
        "MEOWCHAN_GALLERY_ERROR_ONLY", "MEOWCHAN_GALLERY_FAVORITES_ONLY", "MEOWCHAN_GALLERY_FIT_ONLY",
        "MEOWCHAN_GALLERY_HIDDEN_FAVORITE_ONLY", "MEOWCHAN_GALLERY_INTEGRITY_GUI_ONLY", "MEOWCHAN_GALLERY_MEDIA_TAGS_ONLY",
        "MEOWCHAN_GALLERY_MUTE_ONLY", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY", "MEOWCHAN_GALLERY_THEME_ONLY",
        "MEOWCHAN_GALLERY_VOLUME_ROUTED_ONLY", "MEOWCHAN_GIF_PLAYBACK_ONLY", "MEOWCHAN_HTML_EXPORT_ACTION_LAYOUT_ONLY",
        "MEOWCHAN_HTML_EXPORT_LABELS_ONLY", "MEOWCHAN_HTML_EXPORT_LANGUAGE_ONLY", "MEOWCHAN_INTERRUPTED_STARTUP_ONLY",
        "MEOWCHAN_LANGUAGE_SWITCH_FOOTER_ONLY", "MEOWCHAN_LOCAL_SEARCH_LANGUAGE_ONLY", "MEOWCHAN_MEDIA_HASH_SEARCH_ONLY",
        "MEOWCHAN_MEDIA_INTEGRATION_ONLY", "MEOWCHAN_MEDIA_INTERACTION_ONLY", "MEOWCHAN_MEDIA_PAGE_ONLY",
        "MEOWCHAN_MEDIA_PANEL_ONLY", "MEOWCHAN_MEDIA_PERFORMANCE_ONLY", "MEOWCHAN_MEDIA_RATINGS_ONLY",
        "MEOWCHAN_NATIVE_IME_ONLY", "MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY", "MEOWCHAN_NUMERIC_FOCUS_ONLY",
        "MEOWCHAN_PAGE_REFRESH_ONLY", "MEOWCHAN_PATH_INPUT_ONLY", "MEOWCHAN_PICKER_LIFETIME_ONLY",
        "MEOWCHAN_PLUGIN_MENU_LANGUAGE_ONLY", "MEOWCHAN_PLUGIN_SETTINGS_LANGUAGE_ONLY", "MEOWCHAN_POST_BOOKMARK_COMPARISON_ONLY",
        "MEOWCHAN_POST_BOOKMARK_LOCALE_ONLY", "MEOWCHAN_POST_BOOKMARKS_ONLY", "MEOWCHAN_POST_BOOKMARK_TRANSITIONS_ONLY",
        "MEOWCHAN_POWERUSER_ONLY", "MEOWCHAN_PREFERENCE_STARTUP_ONLY", "MEOWCHAN_PREVIEW_LANGUAGES_ONLY",
        "MEOWCHAN_PRIVATE_TAGS_ONLY", "MEOWCHAN_RATING_LANGUAGE_REFRESH_ONLY", "MEOWCHAN_READER_FIND_ONLY",
        "MEOWCHAN_READER_GROUPS_BINDING_ONLY", "MEOWCHAN_SAVED_SEARCHES_ONLY", "MEOWCHAN_SECONDARY_SETTINGS_LANGUAGE_ONLY",
        "MEOWCHAN_SECTION_COMPARISON_ONLY", "MEOWCHAN_SETTINGS_CATEGORY_ONLY", "MEOWCHAN_SETTINGS_FOOTER_ONLY",
        "MEOWCHAN_SETTINGS_LANGUAGE_REFRESH_ONLY", "MEOWCHAN_SETTINGS_LIFETIME_ONLY", "MEOWCHAN_SHORTCUT_ISSUE_LANGUAGE_ONLY",
        "MEOWCHAN_SHORTCUT_PRESENTATION_ONLY", "MEOWCHAN_SMOKE_ALPHA2_COPY_ONLY", "MEOWCHAN_SOURCE_FILTER_WAVE_ONLY",
        "MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY", "MEOWCHAN_STARTUP_PREVIEW_ONLY", "MEOWCHAN_SUGGESTIONS_ONLY",
        "MEOWCHAN_TEXT_SELECTION_ONLY", "MEOWCHAN_UI_REFINEMENT_ONLY", "MEOWCHAN_URLTEXT_EXPORT_ONLY",
        "MEOWCHAN_URLTEXT_PAGE_BOUNDARY_ONLY", "MEOWCHAN_WINDOW_CHROME_ONLY"
    ];

    // Top-level branches without the _ONLY suffix. They are parents of some selectors and part
    // of the scenario id; scripts/run-desktop-smoke.py mirrors this list.
    internal static readonly (string Name, string Value)[] BranchSelectors =
    [
        ("MEOWCHAN_TEMPLATE_COMPARISON", "1"), ("MEOWCHAN_ARCHIVE_COMPARISON", "1"),
        ("MEOWCHAN_MEDIA_FIT_BENCHMARK", "1"), ("MEOWCHAN_URSA_NATIVE_SUITE", "groups")
    ];

    // Read only inside a mode or another selector's branch. Any one listed requirement suffices:
    // "--mode" for a command-line scenario, a selector name, or NAME=value for a branch selector.
    private static readonly (string Name, string[] Requires)[] Children =
    [
        ("MEOWCHAN_GALLERY_CLOSE_ONLY", ["--reader-ux"]),
        ("MEOWCHAN_READER_MEDIA_INPUT_ONLY", ["--reader-ux"]),
        ("MEOWCHAN_READER_SHORTCUT_CONTEXTS_ONLY", ["--reader-ux"]),
        ("MEOWCHAN_READER_GROUPS_ONLY", ["--reader-ux"]),
        ("MEOWCHAN_READER_RETURNS_ONLY", ["--reader-ux"]),
        ("MEOWCHAN_READER_STABILITY_ONLY", ["--reader-ux"]),
        ("MEOWCHAN_READER_NAVIGATION_ONLY", ["--reader-ux"]),
        ("MEOWCHAN_READER_GROUPS_RESTORE_ONLY", ["--reader-ux", "MEOWCHAN_URSA_NATIVE_SUITE=groups"]),
        ("MEOWCHAN_PAGINATION_FOCUS_ONLY", ["MEOWCHAN_READER_SHORTCUT_CONTEXTS_ONLY"]),
        ("MEOWCHAN_SHELL_PREFERENCES_ONLY", ["--download-session"]),
        ("MEOWCHAN_SHELL_PROJECTION_ONLY", ["--download-session"]),
        ("MEOWCHAN_SHUTDOWN_ORDER_ONLY", ["--download-session"]),
        ("MEOWCHAN_PLUGIN_RESELECTION_ONLY", ["--plugins"]),
        ("MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY", ["--accessibility-probe"]),
        ("MEOWCHAN_OWNED_CONTROL_TRANSFERS_ONLY", ["MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"]),
        ("MEOWCHAN_CROSS_ROOT_MIGRATION_ONLY", ["MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"]),
        ("MEOWCHAN_NATIVE_OVERLAY_VISIBILITY_ONLY", ["MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"]),
        ("MEOWCHAN_GALLERY_EDGE_ONLY", ["MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"]),
        ("MEOWCHAN_RATINGS_LATE_ERROR_ONLY", ["MEOWCHAN_MEDIA_RATINGS_ONLY"]),
        ("MEOWCHAN_RATINGS_PRESENTATION_ONLY", ["MEOWCHAN_MEDIA_RATINGS_ONLY"]),
        ("MEOWCHAN_SECTION_PRESENTATION_ONLY", ["MEOWCHAN_SECTION_COMPARISON_ONLY"]),
        ("MEOWCHAN_TEMPLATE_COMPARISON_PRESENTATION_ONLY", ["MEOWCHAN_TEMPLATE_COMPARISON"]),
        ("MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY", ["MEOWCHAN_ARCHIVE_COMPARISON"]),
        ("MEOWCHAN_ARCHIVE_COMPARISON_LOCALE_ONLY", ["MEOWCHAN_ARCHIVE_COMPARISON"]),
        ("MEOWCHAN_ARCHIVE_COMPARISON_MATRIX_ONLY", ["MEOWCHAN_ARCHIVE_COMPARISON"]),
        ("MEOWCHAN_ARCHIVE_COMPARISON_STATE_ONLY", ["MEOWCHAN_ARCHIVE_COMPARISON"]),
        ("MEOWCHAN_EXPERIMENTAL_ACTIVATION_GUI_ONLY", ["MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY"]),
        ("MEOWCHAN_EXPERIMENTAL_ACTIVATION_STATUS_ONLY", ["MEOWCHAN_EXPERIMENTAL_ACTIVATION_GUI_ONLY"]),
        // GalleryViewerChecks runs in these branches; FAVORITES returns before FOCUS is read.
        ("MEOWCHAN_GALLERY_FOCUS_ONLY", ["MEOWCHAN_GALLERY_FIT_ONLY", "MEOWCHAN_MEDIA_PERFORMANCE_ONLY",
            "MEOWCHAN_GALLERY_THEME_ONLY", "--download-session"])
    ];

    internal static IEnumerable<string> RootNames => Roots;
    internal static IEnumerable<(string Name, string[] Requires)> ChildRequirements => Children;
    internal static IEnumerable<string> KnownNames => Roots.Concat(Children.Select(child => child.Name));

    // Returns null when the run is unambiguous, otherwise a message for exit code 2.
    // scenario: "<mode>" plus "+<selector>" for each active selector in ordinal order.
    internal static string? Check(IReadOnlyList<string> args, IEnumerable<KeyValuePair<string, string?>> environment,
        bool ignoreCase, out string scenario)
    {
        StringComparer names = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string mode = args.Count == 0 ? NormalMode : args[0].StartsWith("--", StringComparison.Ordinal) ? args[0][2..] : args[0];
        Dictionary<string, string> known = KnownNames.ToDictionary(name => name, name => name, names);
        Dictionary<string, string?> variables = new(names);
        foreach ((string key, string? value) in environment) variables[key] = value;
        scenario = mode;

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
        return null;
    }
}
