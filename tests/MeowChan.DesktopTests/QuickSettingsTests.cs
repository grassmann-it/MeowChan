using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// R7 (PLAN-READER-MEDIEN-UND-LEISTE §2.6): the quick settings popover driven by settings ids. Model and registry, the applier that
// saves edits live, the settings page contract (every field
// id is a real input of the page, so "All settings" and the search reach it) and the twelve languages.
[Collection("Localization")]
public sealed class QuickSettingsTests : IDisposable
{
    public QuickSettingsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static QuickSettingsSpec Reader => QuickSettingsSurfaces.Get(QuickSettingsSurfaces.Reader);
    private static QuickSettingField Field(string id) => Reader.Fields.Single(field => field.Id == id);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 1000 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition(), "The condition did not become true in time.");
    }

    // ---- registry and fields -----------------------------------------------------------------------------------------

    [Fact]
    public void TheReaderPopoverOffersTheMostNeededDisplayOptionsUnderUniqueIds()
    {
        Assert.Equal(new[]
        {
            SettingsFieldId.ReaderPostWidth, SettingsFieldId.ReaderThumbnailWidth, SettingsFieldId.ReadingFontSize,
            SettingsFieldId.ReaderDateStyle, SettingsFieldId.ReaderSpoilers, SettingsFieldId.ReaderPageSize,
            SettingsFieldId.ReadingHistoryLimit, SettingsFieldId.ReaderTextlessLargeImages, SettingsFieldId.ReaderTextlessMediaWidth,
            SettingsFieldId.ReaderFocusedMediaEnlarge, SettingsFieldId.ReaderFocusedMediaScalePercent, SettingsFieldId.ReaderHoverEnlarge,
            SettingsFieldId.ReaderHoverEnlargeSize, SettingsFieldId.ReaderHoverEnlargeDelayMs
        }, Reader.Fields.Select(field => field.Id));
        Assert.Equal(Reader.Fields.Count, Reader.Fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(QuickSettingsSurfaces.Contains(SettingsFieldId.ReaderPostWidth));
        Assert.False(QuickSettingsSurfaces.Contains("DownloadPath"));
        Assert.Throws<KeyNotFoundException>(() => QuickSettingsSurfaces.Get("nothing"));
        Assert.Equal("ReaderPostWidth", SettingsFieldId.FromInputName("ReaderPostWidthInput"));
        Assert.Equal("Input", SettingsFieldId.FromInputName("Input"));
        Assert.Equal("ReaderPostWidthInput", SettingsFieldId.InputName(SettingsFieldId.ReaderPostWidth));
        Assert.Null(SettingsFieldId.FromInputName(null));
    }

    [Fact]
    public void ChoicesAreStoredByTheConfigurationAsTheyAreAndReadBack()
    {
        foreach (QuickChoiceField field in Reader.Fields.OfType<QuickChoiceField>())
            foreach (QuickSettingChoice choice in field.Choices)
            {
                Config config = new();
                Assert.True(field.TryCreateChange(choice.Value, out Action<Config>? change), field.Id + " " + choice.Value);
                change(config);
                // The configuration normalises its own values: an option must survive that unchanged, or it is not a real option.
                Assert.Equal(choice.Value, field.Read(config));
                config.Validate();
            }
    }

    [Fact]
    public void ChoicesRefuseTextThatIsNoOptionAndIntegersTheirRangeAndNonNumbers()
    {
        QuickSettingField width = Field(SettingsFieldId.ReaderPostWidth);
        Assert.False(width.TryCreateChange("Wide", out _));
        Assert.False(width.TryCreateChange("full", out _)); // values are exact, the configuration's spelling
        QuickIntegerField thumbs = (QuickIntegerField)Field(SettingsFieldId.ReaderThumbnailWidth);
        Assert.Equal(Config.ReaderThumbnailWidthMinimum, thumbs.Minimum);
        Assert.Equal(Config.ReaderThumbnailWidthMaximum, thumbs.Maximum);
        Assert.True(thumbs.TryCreateChange(thumbs.Minimum.ToString(), out _));
        Assert.True(thumbs.TryCreateChange(thumbs.Maximum.ToString(), out _));
        Assert.False(thumbs.TryCreateChange((thumbs.Minimum - 1).ToString(), out _));
        Assert.False(thumbs.TryCreateChange((thumbs.Maximum + 1).ToString(), out _));
        Assert.False(thumbs.TryCreateChange("12 px", out _));
        Assert.False(thumbs.TryCreateChange("", out _));
        QuickIntegerField pages = (QuickIntegerField)Field(SettingsFieldId.ReaderPageSize);
        Assert.Equal((Config.ReaderPageSizeMinimum, Config.ReaderPageSizeMaximum), (pages.Minimum, pages.Maximum));
        Config config = new();
        Assert.True(pages.TryCreateChange("100", out Action<Config>? change));
        change(config);
        Assert.Equal(100, config.ReaderPageSize);
        Assert.Equal("100", pages.Read(config));
        config.Validate();
    }

    [Fact]
    public void FieldsRefuseBrokenDefinitions()
    {
        Assert.Throws<ArgumentException>(() => QuickSetting.Choice("X", "k", null, QuickSetting.Options(("a", "k")), _ => "a", (_, _) => { }));
        Assert.Throws<ArgumentException>(() => QuickSetting.Choice("X", "k", null, QuickSetting.Options(("a", "k"), ("a", "k2")), _ => "a", (_, _) => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuickSetting.Integer("X", "k", null, 5, 5, 1, _ => 5, (_, _) => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuickSetting.Integer("X", "k", null, 1, 5, 0, _ => 5, (_, _) => { }));
        Assert.Throws<ArgumentException>(() => QuickSetting.Toggle(" ", "k", null, _ => true, (_, _) => { }));
        QuickSettingField toggle = QuickSetting.Toggle("T", "k", null, c => c.ReaderSpoilers == "Always", (c, on) => c.ReaderSpoilers = on ? "Always" : "Hide");
        Config config = new();
        Assert.True(toggle.TryCreateChange("True", out Action<Config>? on));
        on(config);
        Assert.Equal("Always", config.ReaderSpoilers);
        Assert.Equal("True", toggle.Read(config));
        Assert.False(toggle.TryCreateChange("maybe", out _));
    }

    [Fact]
    public void EveryLabelHintAndOptionKeyExistsInBothBuiltInLanguages()
    {
        foreach (QuickSettingsSpec spec in QuickSettingsSurfaces.All)
        {
            List<string> keys = new() { spec.TitleKey, spec.AllSettingsKey };
            foreach (QuickSettingField field in spec.Fields)
            {
                keys.Add(field.LabelKey);
                if (field.HintKey != null) keys.Add(field.HintKey);
                if (field is QuickChoiceField choice) keys.AddRange(choice.Choices.Select(option => option.LabelKey));
            }
            foreach (string key in keys)
            {
                Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? en) && !string.IsNullOrWhiteSpace(en), "en lacks " + key);
                Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? de) && !string.IsNullOrWhiteSpace(de), "de lacks " + key);
            }
        }
    }

    [Fact]
    public void TheSettingsPageHasAnInputForEveryFieldIdAndTheSearchAliasNamesThePopover()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> names = markup.Descendants().Select(e => (string?)e.Attribute(x + "Name")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (QuickSettingsSpec spec in QuickSettingsSurfaces.All)
            foreach (QuickSettingField field in spec.Fields)
            {
                Assert.Contains(SettingsFieldId.InputName(field.Id), names); // the search key and the reveal target of this setting
                Assert.Contains("popover", SettingsSearchAliases.For(field.Id, "settings.reading-media").Single(text => text.Contains("popover")));
            }
        Assert.DoesNotContain(SettingsSearchAliases.For("DownloadPath", "settings.general"), text => text.Contains("popover"));
    }

    // ---- applier -----------------------------------------------------------------------------------------------------

    private sealed class Gates
    {
        internal readonly List<TaskCompletionSource> Open = new();
        internal Task Delay(TimeSpan span, CancellationToken token)
        {
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => gate.TrySetCanceled(token));
            lock (Open) Open.Add(gate);
            return gate.Task;
        }
        internal int Count { get { lock (Open) return Open.Count; } }
        internal void Release(int index) { lock (Open) Open[index].TrySetResult(); }
    }

    [Fact]
    public async Task EditsInsideThePauseBecomeOneChangeWithTheLastValueOfEachSetting()
    {
        Gates gates = new();
        List<Config> saved = new();
        List<IReadOnlyList<string>> applied = new();
        QuickSettingsApplier applier = new(change => { Config config = new(); change(config); lock (saved) saved.Add(config); return Task.CompletedTask; }, delay: gates.Delay);
        applier.Applied += applied.Add;

        Assert.True(applier.Set(Field(SettingsFieldId.ReaderPostWidth), "Full"));
        await WaitUntilAsync(() => gates.Count == 1);
        Assert.True(applier.Set(Field(SettingsFieldId.ReaderPostWidth), "Column")); // moves the pause
        await WaitUntilAsync(() => gates.Count == 2);
        Assert.True(applier.Set(Field(SettingsFieldId.ReaderSpoilers), "Always"));
        await WaitUntilAsync(() => gates.Count == 3);
        Assert.Empty(saved);
        Assert.Equal(2, applier.PendingCount);

        gates.Release(2);
        await applier.FlushAsync();
        await WaitUntilAsync(() => applied.Count == 1);
        Config result = Assert.Single(saved);
        Assert.Equal("Column", result.ReaderPostWidth);
        Assert.Equal("Always", result.ReaderSpoilers);
        Assert.Equal(new[] { SettingsFieldId.ReaderPostWidth, SettingsFieldId.ReaderSpoilers }, applied[0]);
        Assert.Equal(0, applier.PendingCount);
    }

    [Fact]
    public async Task FlushSavesAtOnceWithoutWaitingForThePause()
    {
        Gates gates = new();
        int saves = 0;
        QuickSettingsApplier applier = new(_ => { Interlocked.Increment(ref saves); return Task.CompletedTask; }, delay: gates.Delay);
        applier.Set(Field(SettingsFieldId.ReaderPageSize), "60");
        await WaitUntilAsync(() => gates.Count == 1);
        await applier.FlushAsync();
        Assert.Equal(1, saves);
        Assert.Equal(0, applier.PendingCount);
        await applier.FlushAsync(); // nothing waiting: completes
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task OneSaveRunsAtATimeAndAnEditDuringItIsSavedAfterwards()
    {
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, saves = 0;
        List<string> values = new();
        QuickSettingsApplier applier = new(async change =>
        {
            int now = Interlocked.Increment(ref running);
            peak = Math.Max(peak, now);
            Config config = new(); change(config);
            lock (values) values.Add(config.ReaderPostWidth);
            if (Interlocked.Increment(ref saves) == 1) await first.Task;
            Interlocked.Decrement(ref running);
        }, delay: (_, _) => Task.CompletedTask);

        applier.Set(Field(SettingsFieldId.ReaderPostWidth), "Full");
        await WaitUntilAsync(() => saves == 1);
        applier.Set(Field(SettingsFieldId.ReaderPostWidth), "Column"); // while the first save is still running
        Assert.Equal(1, saves);
        first.SetResult();
        await applier.FlushAsync();
        Assert.Equal(1, peak);
        Assert.Equal(new[] { "Full", "Column" }, values);
    }

    [Fact]
    public async Task AFailedSaveIsReportedAndDroppedAndTheNextEditStillWorks()
    {
        int calls = 0;
        List<(IReadOnlyList<string> Ids, Exception Error)> failures = new();
        List<IReadOnlyList<string>> applied = new();
        QuickSettingsApplier applier = new(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new ArgumentOutOfRangeException("ReaderPageSize", "outside its range"); // thrown synchronously, as Validate does
            return Task.CompletedTask;
        }, delay: (_, _) => Task.CompletedTask);
        applier.Failed += (ids, error) => failures.Add((ids, error));
        applier.Applied += applied.Add;

        applier.Set(Field(SettingsFieldId.ReaderPageSize), "60");
        await applier.FlushAsync();
        Assert.Single(failures);
        Assert.Contains("outside its range", failures[0].Error.Message);
        Assert.Equal(new[] { SettingsFieldId.ReaderPageSize }, failures[0].Ids);
        Assert.Empty(applied);
        Assert.Equal(0, applier.PendingCount);

        applier.Set(Field(SettingsFieldId.ReaderPageSize), "80");
        await applier.FlushAsync();
        Assert.Equal(2, calls);
        Assert.Single(applied);
    }

    [Fact]
    public void ATextThatIsNoValueIsNotTakenAsAnEdit()
    {
        int saves = 0;
        QuickSettingsApplier applier = new(_ => { saves++; return Task.CompletedTask; }, delay: (_, _) => Task.CompletedTask);
        Assert.False(applier.Set(Field(SettingsFieldId.ReaderPostWidth), "Wide"));
        Assert.False(applier.Set(Field(SettingsFieldId.ReaderThumbnailWidth), "1"));
        Assert.Equal(0, applier.PendingCount);
        Assert.Equal(0, saves);
    }

    // The surface (structure, editing, failure, Esc, all settings, language change) needs controls that live on the UI thread of a
    // running application: it is exercised by the "reader-quick-settings" smoke (ReaderQuickSettingsChecks).

    // ---- languages ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryQuickSettingsKeyIsTranslatedInAllTwelveLanguagesWithTheSourcePlaceholders()
    {
        string[] keys = QuickSettingsCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.True(key.StartsWith("quicksettings.", StringComparison.Ordinal) || key == "command.reader.quick-settings", key));
        Assert.All(QuickSettingsCatalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
            Assert.Equal(Placeholders(entry.English), Placeholders(entry.German));
            Assert.Equal(entry.English, BuiltInCatalog.English[entry.Key]);
            Assert.Equal(entry.German, BuiltInCatalog.German[entry.Key]);
        });
        string[] codes = { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
        foreach (string code in codes)
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement strings = document.RootElement.GetProperty("strings");
            foreach ((string key, string english, _) in QuickSettingsCatalog.Entries)
            {
                Assert.True(strings.TryGetProperty(key, out JsonElement value), code + " lacks " + key);
                string text = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.Equal(Placeholders(english), Placeholders(text));
                // A translation that is the English text word for word has not been translated (the "Esc" key cap aside).
                if (key != "quicksettings.close.tip") Assert.NotEqual(english, text);
            }
        }
    }

    [Fact]
    public void TheCommandIsInTheViewMenuAndLabelledInEveryLanguage()
    {
        MeowChan.Desktop.Commands.CommandDefinition command = MeowChan.Desktop.Commands.CommandCatalog.Get("reader.quick-settings");
        Assert.Equal("command.reader.quick-settings", command.LabelKey);
        Assert.Contains("reader-view-menu", command.Contexts);
        Assert.Equal("Reader settings…", MeowChan.Desktop.Commands.CommandCatalog.Label("reader.quick-settings"));
        Texts.SetLanguage("fr");
        Assert.Equal("Paramètres du lecteur…", MeowChan.Desktop.Commands.CommandCatalog.Label("reader.quick-settings"));
    }

    private static string[] Placeholders(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
