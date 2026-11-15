using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>How a quick setting is edited in the popover.</summary>
internal enum QuickSettingKind { Choice, Integer, Toggle }

/// <summary>One option of a choice setting: the value stored in the configuration and the key of its label.</summary>
internal sealed record QuickSettingChoice(string Value, string LabelKey);

/// <summary>
/// One setting offered by a quick settings popover. It names the setting by its <see cref="SettingsFieldId"/> (the same id the
/// settings page and its search use), borrows the label and hint texts of the settings page by key, and knows how to read and
/// change the value in a <see cref="Config"/>. The popover never holds its own copy of a value: it reads the configuration and
/// saves a change through the host (<see cref="QuickSettingsApplier"/>). Values travel as text so one applier serves every kind.
/// </summary>
internal abstract class QuickSettingField
{
    protected QuickSettingField(string id, string labelKey, string? hintKey)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A quick setting needs the id of its setting.", nameof(id));
        if (string.IsNullOrWhiteSpace(labelKey)) throw new ArgumentException("A quick setting needs a label key.", nameof(labelKey));
        Id = id;
        LabelKey = labelKey;
        HintKey = hintKey;
    }

    internal string Id { get; }
    internal string LabelKey { get; }
    /// <summary>The key of the explanation shown as tooltip and accessible description; null for none.</summary>
    internal string? HintKey { get; }
    internal abstract QuickSettingKind Kind { get; }

    /// <summary>The value the configuration holds now, as text.</summary>
    internal abstract string Read(Config config);

    /// <summary>
    /// The change that stores <paramref name="value"/> in a configuration; false (and no change) when the text is not a
    /// value of this setting. The configuration's own validation still runs before anything is saved.
    /// </summary>
    internal abstract bool TryCreateChange(string value, out Action<Config> change);
}

/// <summary>A setting with a few named options (a combo box).</summary>
internal sealed class QuickChoiceField : QuickSettingField
{
    private readonly Func<Config, string> _get;
    private readonly Action<Config, string> _set;

    internal QuickChoiceField(string id, string labelKey, string? hintKey, IReadOnlyList<QuickSettingChoice> choices,
        Func<Config, string> get, Action<Config, string> set) : base(id, labelKey, hintKey)
    {
        if (choices == null || choices.Count < 2) throw new ArgumentException("A choice needs at least two options.", nameof(choices));
        if (choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal).Count() != choices.Count)
            throw new ArgumentException("The options of a choice must differ.", nameof(choices));
        Choices = choices;
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
    }

    internal IReadOnlyList<QuickSettingChoice> Choices { get; }
    internal override QuickSettingKind Kind => QuickSettingKind.Choice;
    internal override string Read(Config config) => _get(config);

    internal override bool TryCreateChange(string value, out Action<Config> change)
    {
        change = static _ => { };
        if (!Choices.Any(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))) return false;
        string chosen = value;
        change = config => _set(config, chosen);
        return true;
    }
}

/// <summary>A whole-number setting with its own range (the limits are those of the setting itself, never a second copy).</summary>
internal sealed class QuickIntegerField : QuickSettingField
{
    private readonly Func<Config, int> _get;
    private readonly Action<Config, int> _set;

    internal QuickIntegerField(string id, string labelKey, string? hintKey, int minimum, int maximum, int step,
        Func<Config, int> get, Action<Config, int> set) : base(id, labelKey, hintKey)
    {
        if (minimum >= maximum) throw new ArgumentOutOfRangeException(nameof(minimum), "The minimum must be below the maximum.");
        if (step < 1) throw new ArgumentOutOfRangeException(nameof(step));
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
    }

    internal int Minimum { get; }
    internal int Maximum { get; }
    internal int Step { get; }
    internal override QuickSettingKind Kind => QuickSettingKind.Integer;
    internal override string Read(Config config) => _get(config).ToString(CultureInfo.InvariantCulture);

    internal override bool TryCreateChange(string value, out Action<Config> change)
    {
        change = static _ => { };
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < Minimum || number > Maximum)
            return false;
        change = config => _set(config, number);
        return true;
    }
}

/// <summary>An on/off setting (a check box).</summary>
internal sealed class QuickToggleField : QuickSettingField
{
    private readonly Func<Config, bool> _get;
    private readonly Action<Config, bool> _set;

    internal QuickToggleField(string id, string labelKey, string? hintKey, Func<Config, bool> get, Action<Config, bool> set)
        : base(id, labelKey, hintKey)
    {
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
    }

    internal override QuickSettingKind Kind => QuickSettingKind.Toggle;
    internal override string Read(Config config) => _get(config) ? bool.TrueString : bool.FalseString;

    internal override bool TryCreateChange(string value, out Action<Config> change)
    {
        change = static _ => { };
        if (!bool.TryParse(value, out bool on)) return false;
        change = config => _set(config, on);
        return true;
    }
}

/// <summary>Short factory names for the registration lines of a surface (see <see cref="ReaderQuickSettings"/>).</summary>
internal static class QuickSetting
{
    internal static QuickSettingField Choice(string id, string labelKey, string? hintKey, IReadOnlyList<QuickSettingChoice> choices,
        Func<Config, string> get, Action<Config, string> set) => new QuickChoiceField(id, labelKey, hintKey, choices, get, set);

    internal static QuickSettingField Integer(string id, string labelKey, string? hintKey, int minimum, int maximum, int step,
        Func<Config, int> get, Action<Config, int> set) => new QuickIntegerField(id, labelKey, hintKey, minimum, maximum, step, get, set);

    internal static QuickSettingField Toggle(string id, string labelKey, string? hintKey, Func<Config, bool> get, Action<Config, bool> set) =>
        new QuickToggleField(id, labelKey, hintKey, get, set);

    /// <summary>The options of a choice as (value, label key) pairs, in display order.</summary>
    internal static QuickSettingChoice[] Options(params (string Value, string LabelKey)[] options) =>
        options.Select(option => new QuickSettingChoice(option.Value, option.LabelKey)).ToArray();
}

/// <summary>One line of a popover's layout (RL1): a section heading, a setting, or a switch or action of the session.</summary>
internal abstract record QuickSettingsEntry;

/// <summary>A heading over the rows that follow, up to the next heading.</summary>
internal sealed record QuickSettingsSection(string TitleKey) : QuickSettingsEntry;

/// <summary>
/// A setting row. <paramref name="WhenFieldId"/> names an earlier on/off setting of the same popover: the row exists only while that one
/// is on and stands indented under it (the pattern of the settings page's conditional rows). <paramref name="ShortcutCommandId"/> names the
/// command whose key is shown at the right end of the row, for a setting that also has a command with a key.
/// </summary>
internal sealed record QuickSettingsFieldEntry(QuickSettingField Field, string? WhenFieldId = null, string? ShortcutCommandId = null) : QuickSettingsEntry;

/// <summary>
/// A row that is not a saved setting but a switch (<see cref="QuickSettingsSessionItem.IsChecked"/> given) or an action of the open view:
/// the page supplies it by command id through <see cref="QuickSettingsSession"/>; the label and the key shown are the command's.
/// A command the page does not offer (no post filters in a bare page) leaves no row.
/// </summary>
internal sealed record QuickSettingsSessionEntry(string CommandId, bool IsAction = false) : QuickSettingsEntry;

/// <summary>What the page offers for one session row: whether it is on (null for an action), what it does, whether it can run, and the note shown while it cannot.</summary>
internal sealed record QuickSettingsSessionItem(string CommandId, Func<bool>? IsChecked, Action Invoke, Func<bool> IsEnabled, Func<string?>? Note = null);

/// <summary>How a popover meets the open view for its session rows (the page fills it in; the popover knows no page).</summary>
internal sealed class QuickSettingsSession
{
    /// <summary>The rows the page offers right now (read again whenever the popover looks).</summary>
    internal required Func<IReadOnlyList<QuickSettingsSessionItem>> Items { get; init; }
    /// <summary>The page's session state changed (a switch, a thread change): open popovers read their rows again.</summary>
    internal event Action? Changed;
    internal void RaiseChanged() => Changed?.Invoke();
}

/// <summary>
/// What a quick settings popover shows: the surface it belongs to ("reader", later "gallery" and "media"), the keys of its
/// title and of its "all settings" link, and its fields in display order. A popover with a <paramref name="Layout"/> shows that,
/// with headings, conditional rows and session rows, and <paramref name="Fields"/> are its settings in that order (<see cref="WithLayout"/>);
/// without one it is the plain list of its fields.
/// </summary>
internal sealed record QuickSettingsSpec(string Surface, string TitleKey, string AllSettingsKey, IReadOnlyList<QuickSettingField> Fields,
    IReadOnlyList<QuickSettingsEntry>? Layout = null)
{
    internal static QuickSettingsSpec WithLayout(string surface, string titleKey, string allSettingsKey, IReadOnlyList<QuickSettingsEntry> layout)
    {
        QuickSettingField[] fields = layout.OfType<QuickSettingsFieldEntry>().Select(entry => entry.Field).ToArray();
        string[] seen = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            if (seen.Contains(fields[i].Id, StringComparer.Ordinal)) throw new ArgumentException("A setting is in the layout once: " + fields[i].Id, nameof(layout));
            seen[i] = fields[i].Id;
        }
        foreach (QuickSettingsFieldEntry entry in layout.OfType<QuickSettingsFieldEntry>().Where(entry => entry.WhenFieldId != null))
        {
            int parent = Array.IndexOf(seen, entry.WhenFieldId), self = Array.IndexOf(seen, entry.Field.Id);
            if (parent < 0 || parent >= self || fields[parent].Kind != QuickSettingKind.Toggle)
                throw new ArgumentException("A conditional row follows the on/off setting it depends on: " + entry.Field.Id, nameof(layout));
        }
        return new QuickSettingsSpec(surface, titleKey, allSettingsKey, fields, layout);
    }
}

/// <summary>
/// The registry of the surfaces that have a quick settings popover. A new surface is one entry here and one file like
/// <see cref="ReaderQuickSettings"/>; a new setting on an existing surface is one line in that file.
/// </summary>
internal static class QuickSettingsSurfaces
{
    internal const string Reader = "reader";

    private static readonly QuickSettingsSpec[] Specs =
    {
        ReaderQuickSettings.Spec,
        // The gallery (fit upscale, slideshow, video repeat, ...) and the media page register here when their plans land.
    };

    internal static IReadOnlyList<QuickSettingsSpec> All => Specs;

    internal static QuickSettingsSpec Get(string surface) =>
        Specs.FirstOrDefault(spec => string.Equals(spec.Surface, surface, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException("No quick settings surface " + surface + ".");

    /// <summary>Whether any popover offers the setting <paramref name="fieldId"/>.</summary>
    internal static bool Contains(string fieldId) =>
        Specs.Any(spec => spec.Fields.Any(field => string.Equals(field.Id, fieldId, StringComparison.Ordinal)));
}

/// <summary>
/// Where a popover meets the application: the current configuration, the way a change is saved, and what happens afterwards.
/// The shell fills it in (its saving path is the one every direct preference takes), so the popover itself knows no window.
/// </summary>
internal sealed class QuickSettingsHost
{
    /// <summary>The confirmed configuration the editors show.</summary>
    internal required Func<Config> CurrentConfig { get; init; }
    /// <summary>Saves one combined change and applies it to the running session; throws when it cannot be saved.</summary>
    internal required Func<Action<Config>, Task> SaveAsync { get; init; }
    /// <summary>After a change was saved: the ids of the settings it touched (the settings page refreshes those editors).</summary>
    internal Action<IReadOnlyList<string>>? Applied { get; init; }
    /// <summary>"All settings": opens the settings page at the setting with this id (null: just the page).</summary>
    internal Action<string?>? ShowAllSettings { get; init; }
    /// <summary>The pause after the last edit before the saved change; see <see cref="QuickSettingsApplier.DefaultDebounce"/>.</summary>
    internal TimeSpan Debounce { get; init; } = QuickSettingsApplier.DefaultDebounce;
}

/// <summary>
/// Collects the edits of a popover and saves them once the editing pauses: every edit replaces an earlier one of the same
/// setting, the others join one combined change, and one save runs at a time. The save is the host's, so the result is applied
/// live. A failed save is reported and dropped (the popover shows the confirmed values again); closing the popover flushes.
/// </summary>
internal sealed class QuickSettingsApplier
{
    /// <summary>
    /// The pause after the last edit before it is saved (a UI timing constant, not a limit of the user's data): short enough that
    /// a change is on screen well within a fifth of a second, long enough that typing a number or holding a spinner saves once.
    /// </summary>
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly Func<Action<Config>, Task> _save;
    private readonly TimeSpan _debounce;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly List<string> _order = new();
    private readonly Dictionary<string, Action<Config>> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? _wait;
    private Task _run = Task.CompletedTask;
    private bool _running, _flushNow;
    private long _generation;

    internal QuickSettingsApplier(Func<Action<Config>, Task> save, TimeSpan? debounce = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _debounce = debounce ?? DefaultDebounce;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>A change was saved; the ids of the settings in it.</summary>
    internal event Action<IReadOnlyList<string>>? Applied;
    /// <summary>A change could not be saved (and is dropped); the ids of the settings in it and the reason.</summary>
    internal event Action<IReadOnlyList<string>, Exception>? Failed;

    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Takes an edit; false when <paramref name="value"/> is not a value of <paramref name="field"/>.</summary>
    internal bool Set(QuickSettingField field, string value)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!field.TryCreateChange(value, out Action<Config>? change)) return false;
        bool start = false;
        lock (_gate)
        {
            if (!_pending.ContainsKey(field.Id)) _order.Add(field.Id);
            _pending[field.Id] = change;
            ++_generation;
            _wait?.Cancel();
            if (!_running) _running = start = true;
        }
        if (start) _run = RunAsync();
        return true;
    }

    /// <summary>Saves what is waiting without the pause; completes when nothing is waiting or running.</summary>
    internal Task FlushAsync()
    {
        lock (_gate)
        {
            if (_pending.Count > 0) { _flushNow = true; _wait?.Cancel(); }
            return _run;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            CancellationTokenSource? wait = null;
            long generation;
            lock (_gate)
            {
                if (_pending.Count == 0) { _running = false; return; }
                generation = _generation;
                if (!_flushNow) wait = _wait = new CancellationTokenSource();
            }
            if (wait != null)
            {
                try { await _delay(_debounce, wait.Token).ConfigureAwait(true); }
                catch (OperationCanceledException) { }
                lock (_gate) { _wait = null; }
                wait.Dispose();
                // A newer edit moved the pause: wait again, unless a flush asked for the save now.
                lock (_gate) { if (generation != _generation && !_flushNow) continue; }
            }
            string[] ids;
            Action<Config>[] changes;
            lock (_gate)
            {
                ids = _order.ToArray();
                changes = ids.Select(id => _pending[id]).ToArray();
                _order.Clear();
                _pending.Clear();
                _flushNow = false;
            }
            try
            {
                await _save(config => { foreach (Action<Config> change in changes) change(config); }).ConfigureAwait(true);
            }
            catch (Exception error)
            {
                Failed?.Invoke(ids, error);
                continue;
            }
            Applied?.Invoke(ids);
        }
    }
}
