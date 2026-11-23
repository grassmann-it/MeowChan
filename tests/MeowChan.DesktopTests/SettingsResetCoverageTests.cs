using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// ST-02 (final review 17.11.2026): "Reset all editable settings" and the section "Reset" buttons said "Defaults loaded into the draft", but the tag
/// queue limits, the media widths, the tagging group, the folder-source limits and the header tagline stayed as they were. These tests run the real
/// editor (no window): they enumerate the inputs from the control tree under the seven resettable pages, move every one of them away from its default,
/// run the reset and compare each input with a second editor that was loaded from <c>new Config()</c>, so a new input that no reset reaches fails
/// here by name. The default is read from that editor and not from a list in the test, because several inputs show a derived value (the bandwidth in
/// MiB, the language, the tagline quip that the language has).
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class SettingsResetCoverageTests : IDisposable
{
    public SettingsResetCoverageTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // The pages the section Resets and Reset all are responsible for, and the scope of the Reset button each one carries. The work-views page and the
    // shortcut editor are not here on purpose: they have no Reset (the shortcut bindings are never reset by this surface).
    private static readonly (string Page, SettingsEditor.SettingsResetScope Scope)[] Sections =
    [
        ("SettingsInterfacePage", SettingsEditor.SettingsResetScope.Interface),
        ("SettingsReadingMediaPage", SettingsEditor.SettingsResetScope.ReadingMedia),
        ("SettingsDownloadsPage", SettingsEditor.SettingsResetScope.Downloads),
        ("SettingsWorkerBudgetsPage", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("SettingsUpdatesPage", SettingsEditor.SettingsResetScope.Updates),
        ("SettingsNotificationsSection", SettingsEditor.SettingsResetScope.Notifications),
        ("SettingsBehaviorPage", SettingsEditor.SettingsResetScope.Behavior),
    ];

    // The one input that is hidden on purpose: the compatibility switch of the gallery overlay is the same stored value as the visible
    // "overlay controls" switch (Gallery auto-hide is one choice), so it has no reset of its own.
    private static readonly string[] HiddenAliases = ["GalleryAutoHideControlsInput"];

    private sealed record Probe(Control Control, string Name, SettingsEditor.SettingsResetScope Scope);

    /// <summary>Builds two editors over <c>new Config()</c>: the one the test moves and an untouched one that shows the defaults.</summary>
    private static void WithEditors(Action<SettingsEditor, SettingsEditor> test) => WithEditors(new Config(), test);

    private static void WithEditors(Config config, Action<SettingsEditor, SettingsEditor> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        SettingsEditor editor = new(config);
        SettingsEditor defaults = new(new Config());
        try { test(editor, defaults); }
        finally
        {
            editor.EndLifetime();
            defaults.EndLifetime();
        }
    });

    [Fact]
    public void CompatibilityEditorKeepsReadingDataUntilTheSettingsCommitSucceeds()
    {
        using TempDirectory profile = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(profile.Path);
        Config config = Config.LoadConfig(profile.Path);
        string backup = Path.Combine(profile.Path, "settings.json.bak");
        string[] readingPaths = ["reading-position.json", "reading-history.json", "reading-history.json.corrupt"];
        foreach (string file in readingPaths) File.WriteAllText(Path.Combine(profile.Path, file), file);
        File.WriteAllText(backup, """{"SchemaVersion":2}""");

        WithEditors(config, (editor, _) =>
        {
            editor.IsAvailable = true;
            editor.RememberReadingPosition = false;
            Button save = editor.FindControl<Button>("SaveSettingsButton")!;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.Wait(WaitForSaveAsync(editor));
            Assert.True(editor.HasError);
            Assert.True(config.RememberReadingPosition);
            Assert.All(readingPaths, file => Assert.Equal(file, File.ReadAllText(Path.Combine(profile.Path, file))));

            File.Delete(backup);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.Wait(WaitForSaveAsync(editor));
            Assert.False(editor.HasError, editor.ErrorMessage);
            Assert.False(config.RememberReadingPosition);
            Assert.False(Config.LoadConfig(profile.Path).RememberReadingPosition);
            Assert.Empty(Directory.GetFiles(profile.Path, "reading-*"));
        });
    }

    private static async Task<bool> WaitForSaveAsync(SettingsEditor editor)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (editor.IsBusy) await Task.Delay(1, timeout.Token);
        return true;
    }

    // ---- the control tree ------------------------------------------------------------------------------------------------

    /// <summary>Every control named "...Input" below one of the resettable pages (the control tree, not a list of names).</summary>
    private static List<Probe> Probes(SettingsEditor editor)
    {
        List<Probe> probes = [];
        foreach ((string page, SettingsEditor.SettingsResetScope scope) in Sections)
        {
            Control root = editor.FindControl<Control>(page) ?? throw new InvalidOperationException("The settings page " + page + " is missing.");
            foreach (Control control in root.GetLogicalDescendants().OfType<Control>())
                if (control.Name is { } name && name.EndsWith("Input", StringComparison.Ordinal))
                    probes.Add(new Probe(control, name, scope));
        }
        return probes;
    }

    private static IEnumerable<Probe> Movable(IEnumerable<Probe> probes) =>
        probes.Where(probe => !HiddenAliases.Contains(probe.Name, StringComparer.Ordinal));

    private static Control Twin(SettingsEditor defaults, string name) =>
        defaults.FindControl<Control>(name)
        ?? defaults.GetLogicalDescendants().OfType<Control>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("The default editor has no input " + name + ".");

    private static string StateOf(Control input) => input switch
    {
        NumericUpDown number => number.Value?.ToString(CultureInfo.InvariantCulture) ?? "(empty)",
        ToggleButton toggle => toggle.IsChecked == true ? "on" : "off",
        ComboBox box => "item " + box.SelectedIndex.ToString(CultureInfo.InvariantCulture) + (box.SelectedItem is { } item ? " (" + item + ")" : string.Empty),
        TextBox text => "\"" + text.Text + "\"",
        _ => throw new InvalidOperationException(input.GetType().Name + " " + input.Name + " is an input type this test does not know; teach StateOf and Perturb about it."),
    };

    // A comparison key without the label of a combo item, which can differ in text between two editors only by the instance.
    private static string KeyOf(Control input) => input is ComboBox box ? "item " + box.SelectedIndex.ToString(CultureInfo.InvariantCulture) : StateOf(input);

    /// <summary>Moves the input to a value other than the one the default editor shows. False when it cannot be moved (so the test would prove nothing).</summary>
    private static bool Perturb(Control input, Control twin)
    {
        switch (input)
        {
            case NumericUpDown number:
            {
                decimal? shown = ((NumericUpDown)twin).Value;
                foreach (decimal candidate in new[] { number.Maximum, number.Minimum, (shown ?? 0m) + 1m, (shown ?? 0m) - 1m })
                {
                    if (candidate == shown || candidate < number.Minimum || candidate > number.Maximum) continue;
                    number.Value = candidate;
                    if (number.Value != shown) return true;
                }
                return false;
            }
            case ToggleButton toggle:
            {
                bool wanted = ((ToggleButton)twin).IsChecked != true;
                toggle.IsChecked = wanted;
                return (toggle.IsChecked == true) == wanted;
            }
            case ComboBox box:
            {
                int shown = ((ComboBox)twin).SelectedIndex;
                if (box.ItemCount < 2) return false;
                int wanted = shown == 0 ? 1 : 0;
                box.SelectedIndex = wanted;
                return box.SelectedIndex == wanted;
            }
            case TextBox text:
                text.Text = "meowchan-reset-probe";
                return text.Text != ((TextBox)twin).Text;
            default:
                throw new InvalidOperationException(input.GetType().Name + " " + input.Name + " is an input type this test does not know; teach StateOf and Perturb about it.");
        }
    }

    /// <summary>The inputs are bound to the draft; without that the whole comparison below would be about nothing.</summary>
    private static void AssertBindingsFlow(SettingsEditor editor)
    {
        ToggleSwitch shrink = editor.FindControl<ToggleSwitch>("ShrinkWindowForBarebonesInput")!;
        bool before = editor.ShrinkWindowForBarebones;
        shrink.IsChecked = !before;
        Assert.True(editor.ShrinkWindowForBarebones == !before,
            "The inputs of the settings editor are not bound to its draft in this test host, so the reset coverage cannot be read from them.");
        shrink.IsChecked = before;
    }

    // ---- the tests -----------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryInputOfTheEditorIsFoundOnOneOfTheResettablePages() => WithEditors((editor, _) =>
    {
        Probe[] probes = Probes(editor).ToArray();
        string[] duplicates = probes.GroupBy(probe => probe.Name).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.True(duplicates.Length == 0, "Inputs found twice: " + string.Join(", ", duplicates));

        // The markup is the second witness: an input that sits outside the seven pages (or in a page without a Reset) would have no reset.
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] declared = XDocument.Load(Root("Desktop/Views/Settings/SettingsEditor.axaml")).Descendants()
            .Select(element => (string?)element.Attribute(x + "Name")).OfType<string>()
            .Where(name => name.EndsWith("Input", StringComparison.Ordinal)).ToArray();
        string[] outside = declared.Except(probes.Select(probe => probe.Name), StringComparer.Ordinal).ToArray();
        Assert.True(outside.Length == 0, "Inputs declared in SettingsEditor.axaml that are not below a page with a Reset button: " + string.Join(", ", outside));
        Assert.True(declared.Length >= 190, "The editor has about 195 inputs; only " + declared.Length + " were declared.");

        // The one input the tests skip really is the hidden compatibility switch, and it is bound to the same value as the visible one.
        foreach (string name in HiddenAliases)
            Assert.False(probes.Single(probe => probe.Name == name).Control.IsVisible, name + " is skipped because it is hidden, but it is shown.");
    });

    [Fact]
    public void ResetAllPutsEveryInputBackToTheValueOfANewConfig() => WithEditors((editor, defaults) =>
    {
        AssertBindingsFlow(editor);
        Probe[] probes = Movable(Probes(editor)).ToArray();

        string[] unmovable = probes.Where(probe => !Perturb(probe.Control, Twin(defaults, probe.Name))).Select(probe => probe.Name).ToArray();
        Assert.True(unmovable.Length == 0,
            "These inputs could not be moved away from their default, so the test cannot tell whether a reset reaches them: " + string.Join(", ", unmovable));

        editor.ResetDraft(SettingsEditor.SettingsResetScope.All);

        string[] left = probes
            .Where(probe => KeyOf(probe.Control) != KeyOf(Twin(defaults, probe.Name)))
            .Select(probe => probe.Name + " (" + probe.Scope + "): shows " + StateOf(probe.Control) + ", default is " + StateOf(Twin(defaults, probe.Name))).ToArray();
        Assert.True(left.Length == 0, "Reset all left " + left.Length + " input(s) as they were:\n" + string.Join("\n", left));
    });

    [Fact]
    public void EachSectionResetPutsTheInputsOfItsOwnSectionBackToTheDefault() => WithEditors((editor, defaults) =>
    {
        AssertBindingsFlow(editor);
        List<string> left = [];
        foreach (Probe probe in Movable(Probes(editor)))
        {
            Control twin = Twin(defaults, probe.Name);
            Assert.True(Perturb(probe.Control, twin), probe.Name + " could not be moved away from its default, so the test cannot tell whether its section reset reaches it.");
            editor.ResetDraft(probe.Scope);
            if (KeyOf(probe.Control) != KeyOf(twin))
                left.Add(probe.Name + ": the " + probe.Scope + " reset left " + StateOf(probe.Control) + ", default is " + StateOf(twin));
        }
        Assert.True(left.Count == 0, "A section Reset must reset what its section shows:\n" + string.Join("\n", left));
    });

    [Fact]
    public void ASectionResetAlsoReplacesTheRejectedRawTextOfANumericInput() => WithEditors((editor, defaults) =>
    {
        // A number box keeps text that was typed and rejected until it is corrected or discarded; a reset that only sets the bound value would leave
        // that text standing next to a default it does not show (this is why every reset calls ResetNumber for its inputs).
        AssertBindingsFlow(editor);
        List<string> left = [];
        foreach (Probe probe in Movable(Probes(editor)).Where(probe => probe.Control is NumericUpDown))
        {
            NumericUpDown number = (NumericUpDown)probe.Control;
            number.Text = "not a number";
            editor.ResetDraft(probe.Scope);
            decimal? expected = ((NumericUpDown)Twin(defaults, probe.Name)).Value;
            bool readable = decimal.TryParse(number.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal shown);
            if (!readable || shown != expected || number.Value != expected)
                left.Add(probe.Name + ": the " + probe.Scope + " reset left the text \"" + number.Text + "\" and the value " + number.Value + ", default is " + expected);
        }
        Assert.True(left.Count == 0, string.Join("\n", left));
    });

    // One representative of each group that the final review found untouched (tag queue limits, media widths, tagging group, folder-source limits,
    // header tagline, header monitor), named, so a regression reads as "the tagging group is not reset" and not as one of 195 lines.
    private static readonly (string Input, SettingsEditor.SettingsResetScope Scope)[] FormerlyMissing =
    [
        ("TagQueueLimitInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("TagQueueConfirmAboveInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("MediaTagListTopInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("BooruResultTagsShownInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("BooruSearchTabsInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("BooruGallerySampleMiBInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("BooruGalleryCacheMiBInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("MediaTagListWidthInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("MediaPanelWidthInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("MediaPanelMinimumPageWidthInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("TagStampSizeInput", SettingsEditor.SettingsResetScope.ReadingMedia),
        ("TagQueueWalkWindowInput", SettingsEditor.SettingsResetScope.ReadingMedia),
        ("TagQueueUndoDepthInput", SettingsEditor.SettingsResetScope.ReadingMedia),
        ("TaggingCardCountInput", SettingsEditor.SettingsResetScope.ReadingMedia),
        ("TaggingCardsDefaultInput", SettingsEditor.SettingsResetScope.ReadingMedia),
        ("TaggingCard3Input", SettingsEditor.SettingsResetScope.ReadingMedia),
        ("FolderSourceMaxFilesInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("FolderSourceHashMaxFileGiBInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("FolderSourceBackgroundHashingInput", SettingsEditor.SettingsResetScope.WorkerBudgets),
        ("HeaderTaglineInput", SettingsEditor.SettingsResetScope.Interface),
        ("HeaderTaglineFixedIdInput", SettingsEditor.SettingsResetScope.Interface),
        ("HeaderTaglineIntervalMinutesInput", SettingsEditor.SettingsResetScope.Interface),
        ("StatsHeaderMonitorInput", SettingsEditor.SettingsResetScope.Interface),
    ];

    [Fact]
    public void TheGroupsThatTheFinalReviewFoundUntouchedAreResetByTheirOwnSectionAndByResetAllButNotBySomeOtherSection() => WithEditors((editor, defaults) =>
    {
        AssertBindingsFlow(editor);
        List<string> problems = [];
        foreach ((string name, SettingsEditor.SettingsResetScope scope) in FormerlyMissing)
        {
            Control input = editor.FindControl<Control>(name) ?? throw new InvalidOperationException("Missing " + name);
            Control twin = Twin(defaults, name);
            Assert.True(Perturb(input, twin), name + " could not be moved away from its default.");

            // Another section (Updates has two switches and nothing else in common) must leave the value alone: the Reset of a section is that section.
            string moved = KeyOf(input);
            editor.ResetDraft(SettingsEditor.SettingsResetScope.Updates);
            if (KeyOf(input) != moved) problems.Add(name + ": the Updates reset changed it, a section reset must stay in its own section");

            editor.ResetDraft(scope);
            if (KeyOf(input) != KeyOf(twin)) problems.Add(name + ": the " + scope + " reset left " + StateOf(input) + ", default is " + StateOf(twin));

            Perturb(input, twin);
            editor.ResetDraft(SettingsEditor.SettingsResetScope.All);
            if (KeyOf(input) != KeyOf(twin)) problems.Add(name + ": Reset all left " + StateOf(input) + ", default is " + StateOf(twin));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    });

    // ---- what counts as unchanged -----------------------------------------------------------------------------------------

    private static T Baseline<T>(SettingsEditor editor, string field) =>
        (T)(typeof(SettingsEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(SettingsEditor), field)).GetValue(editor)!;

    [Fact]
    public void AResetIsAnEditOfTheDraftSoTheMediaWidthsAndTheCardStartKeepTheirLoadedBaselineUntilADiscardLoadsItAgain() => WithEditors(
        new Config
        {
            MediaLayout = (MediaPageLayout.Standard with { TagListWidth = 300, PanelWidth = 420 }).Format(),
            TaggingCardsDefault = true,
        },
        (editor, _) =>
        {
            // The loaded values are the baseline: Save writes a width, or the card start, only when the draft differs from it (M1; K in the gallery).
            Assert.Equal(300, Baseline<int>(editor, "_initialMediaTagListWidth"));
            Assert.Equal(420, Baseline<int>(editor, "_initialMediaPanelWidth"));
            Assert.True(Baseline<bool>(editor, "_initialTaggingCardsDefault"));

            editor.ResetDraft(SettingsEditor.SettingsResetScope.WorkerBudgets);
            Assert.Equal<decimal?>(Config.MediaTagListWidthDefault, editor.MediaTagListWidth);
            Assert.Equal<decimal?>(Config.MediaPanelWidthDefault, editor.MediaPanelWidth);
            Assert.Equal(300, Baseline<int>(editor, "_initialMediaTagListWidth"));
            Assert.Equal(420, Baseline<int>(editor, "_initialMediaPanelWidth"));

            editor.ResetDraft(SettingsEditor.SettingsResetScope.ReadingMedia);
            Assert.False(editor.TaggingCardsDefault);
            Assert.True(Baseline<bool>(editor, "_initialTaggingCardsDefault"));

            editor.ResetDraft(SettingsEditor.SettingsResetScope.All);
            Assert.Equal(300, Baseline<int>(editor, "_initialMediaTagListWidth"));
            Assert.True(Baseline<bool>(editor, "_initialTaggingCardsDefault"));

            // A discard loads the saved values again, draft and baseline together.
            editor.CancelChanges();
            Assert.Equal<decimal?>(300, editor.MediaTagListWidth);
            Assert.Equal<decimal?>(420, editor.MediaPanelWidth);
            Assert.True(editor.TaggingCardsDefault);
            Assert.Equal(300, Baseline<int>(editor, "_initialMediaTagListWidth"));
        });

    // A saved value inside the range of its setting and other than the default.
    private static int Away(int minimum, int maximum, int standard) => standard == minimum ? maximum : minimum;

    [Fact]
    public void ADiscardLoadsTheSavedValuesBackThroughTheSameLoadersThatTheResetUsesForTheDefaults()
    {
        Config standard = new();
        Config saved = new()
        {
            TagQueueLimit = Away(Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum, standard.TagQueueLimit),
            FolderSourceMaxFiles = Away(Config.FolderSourceMaxFilesMinimum, Config.FolderSourceMaxFilesMaximum, standard.FolderSourceMaxFiles),
            FolderSourceBackgroundHashing = !standard.FolderSourceBackgroundHashing,
            TagStampSize = Away(Config.TagStampSizeMinimum, Config.TagStampSizeMaximum, standard.TagStampSize),
            TaggingCardCount = Away(Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum, standard.TaggingCardCount),
        };
        WithEditors(saved, (editor, _) =>
        {
            NumericUpDown queue = editor.FindControl<NumericUpDown>("TagQueueLimitInput")!;
            NumericUpDown files = editor.FindControl<NumericUpDown>("FolderSourceMaxFilesInput")!;
            ToggleSwitch hashing = editor.FindControl<ToggleSwitch>("FolderSourceBackgroundHashingInput")!;
            NumericUpDown stamp = editor.FindControl<NumericUpDown>("TagStampSizeInput")!;
            NumericUpDown cards = editor.FindControl<NumericUpDown>("TaggingCardCountInput")!;

            void AssertShows(Config expected)
            {
                Assert.Equal<decimal?>(expected.TagQueueLimit, queue.Value);
                Assert.Equal<decimal?>(expected.FolderSourceMaxFiles, files.Value);
                Assert.Equal(expected.FolderSourceBackgroundHashing, hashing.IsChecked == true);
                Assert.Equal<decimal?>(expected.TagStampSize, stamp.Value);
                Assert.Equal<decimal?>(expected.TaggingCardCount, cards.Value);
            }

            // The saved values are shown at the start (they are the loaded baseline), a reset replaces them with the defaults, a discard brings them back.
            AssertShows(saved);
            editor.ResetDraft(SettingsEditor.SettingsResetScope.All);
            AssertShows(standard);
            editor.CancelChanges();
            AssertShows(saved);
        });
    }

    private static string Root(string relative, [CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative));
}
