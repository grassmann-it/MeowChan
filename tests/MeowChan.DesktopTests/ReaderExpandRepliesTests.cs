using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderExpandRepliesTests : IDisposable
{
    public ReaderExpandRepliesTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static readonly ResourceId ThreadA = new("4chan", "g", "100");
    private static readonly ResourceId ThreadB = new("4chan", "g", "200");

    private static ArchivePostRow Row(string id, int? replies = 3, bool stub = false, bool suppress = false)
    {
        ArchivePostRow row = new(id, string.Empty, "text", Array.Empty<ArchiveAttachmentRow>()) { IsStub = stub, SuppressReplyToggle = suppress };
        if (replies.HasValue) row.SetBacklinkCount(replies.Value);
        return row;
    }

    private static ReplyAutoExpandPolicy Open(bool enabled = true)
    {
        ReplyAutoExpandPolicy policy = new() { Enabled = enabled };
        policy.SwitchThread(ThreadA);
        return policy;
    }

    // ---- the setting ----

    [Fact]
    public void TheSettingIsOffByDefaultAndRoundTripsThroughTheProfileAndEveryCopy()
    {
        Assert.False(new Config().ReaderExpandReplies);
        Assert.False(JsonSerializer.Deserialize<Config>("{}")!.ReaderExpandReplies);
        using TempDirectory directory = new();
        Config original = new() { ReaderExpandReplies = true, DownloadPath = Path.Combine(directory.Path, "media") };
        Config clone = original.Clone();
        Config settingsCopy = new();
        settingsCopy.CopySettingsFieldsFrom(clone);
        Config fullCopy = new();
        fullCopy.CopyFrom(settingsCopy);
        fullCopy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config candidate in new[] { original, clone, settingsCopy, fullCopy, loaded })
        {
            Assert.True(candidate.ReaderExpandReplies);
            candidate.Validate();
        }
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.True(saved.RootElement.GetProperty(nameof(Config.ReaderExpandReplies)).GetBoolean());
    }

    [Fact]
    public void TheSettingsTransferExportsAndImportsIt()
    {
        Config source = new() { ReaderExpandReplies = true };
        Assert.True(SettingsTransfer.CreatePortableSnapshot(source).ReaderExpandReplies);
        Assert.False(SettingsTransfer.CreatePortableSnapshot(new Config()).ReaderExpandReplies);
        JsonObject exported = SettingsTransfer.Export(source);
        Assert.True((bool?)exported[nameof(Config.ReaderExpandReplies)]);
        Config imported = JsonSerializer.Deserialize<Config>(exported.ToJsonString())!;
        Assert.True(imported.ReaderExpandReplies);
        Config target = new();
        target.CopySettingsFieldsFrom(imported);
        Assert.True(target.ReaderExpandReplies);
    }

    [Fact]
    public void ThePopoverRowTheSettingsRowTheResetTheSearchAndTheShellHandOverAreWired()
    {
        // The popover offers it as its own entry in the text and layout section, after the page size, as the same setting under the same id.
        Assert.Contains(SettingsFieldId.ReaderExpandReplies, ReaderQuickSettings.Fields.Select(field => field.Id));
        Config config = new();
        QuickSettingField field = ReaderQuickSettings.Fields.Single(f => f.Id == SettingsFieldId.ReaderExpandReplies);
        Assert.Equal(QuickSettingKind.Toggle, field.Kind);
        Assert.Equal(bool.FalseString, field.Read(config));
        Assert.True(field.TryCreateChange(bool.TrueString, out Action<Config> change));
        change(config);
        Assert.True(config.ReaderExpandReplies);
        Assert.Equal(bool.TrueString, field.Read(config));
        string settings = Source("Desktop/Views/Settings/SettingsEditor.axaml");
        Assert.Contains("x:Name=\"ReaderExpandRepliesInput\" IsChecked=\"{Binding ReaderExpandReplies, Mode=TwoWay}\"", settings);
        XDocument markup = XDocument.Parse(settings);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement input = Assert.Single(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == "ReaderExpandRepliesInput");
        Assert.Equal("ToggleSwitch", input.Name.LocalName);
        Assert.Contains("Flag(ReaderExpandRepliesInput, () => ReaderExpandReplies, value => ReaderExpandReplies = value, config => config.ReaderExpandReplies),",
            Source("Desktop/Views/Settings/SettingsEditor.RowMenu.cs"));
        Assert.Contains("[\"ReaderExpandReplies\"] =", Source("Desktop/Views/Settings/SettingsSearchAliases.cs"));
        string display = Source("Desktop/Views/Settings/SettingsEditor.ReaderDisplay.cs");
        Assert.Contains("ReaderExpandReplies = config.ReaderExpandReplies;", display);         // start, discard, reset
        Assert.Contains("candidate.ReaderExpandReplies = ReaderExpandReplies;", display);       // save
        Assert.Contains("case SettingsFieldId.ReaderExpandReplies:", display);                  // the popover confirmed it
        // The shell hands the saved value to the page with the other reader display preferences (also after a popover change).
        Assert.Contains("reader.ReaderExpandRepliesPreference = config.ReaderExpandReplies;", Source("Desktop/ReaderSettingsPreferences.cs"));
    }

    [Fact]
    public void TheLabelAndHintExistInEveryLanguage()
    {
        string[] keys = { "settings.reader.expandReplies", "settings.reader.expandReplies.hint" };
        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            foreach (string key in keys) Assert.NotEqual(key, Texts.T(key));
        }
    }

    [Fact]
    public void OnlyTheSelectedPostsRepliesOpenAndMovingOnClosesThem()
    {
        ReplyAutoExpandPolicy policy = Open();
        ArchivePostRow first = Row("1"), second = Row("2"), third = Row("3");
        policy.Focus(first);
        Assert.True(first.RepliesExpanded);
        Assert.False(second.RepliesExpanded);
        policy.Focus(second);
        policy.Focus(third);
        Assert.False(first.RepliesExpanded);
        Assert.False(second.RepliesExpanded);
        Assert.True(third.RepliesExpanded);
        policy.Focus(null);
        Assert.False(third.RepliesExpanded);
    }

    [Fact]
    public void LateCountsCanOpenTheCurrentRowButStubsAndConversationRowsStayClosed()
    {
        ReplyAutoExpandPolicy policy = Open();
        ArchivePostRow late = Row("1", replies: null);
        foreach (ArchivePostRow row in new[] { late, Row("2", replies: 0), Row("3", stub: true), Row("4", suppress: true) })
        {
            policy.Focus(row);
            Assert.False(row.RepliesExpanded);
        }
        late.SetBacklinkCount(2);
        policy.Focus(late);
        Assert.True(late.RepliesExpanded);
    }

    [Fact]
    public void ManualChoicesSurviveFocusChangesAndRowRebuildsWithinTheThread()
    {
        ReplyAutoExpandPolicy policy = Open();
        ArchivePostRow manual = Row("1"), collapsed = Row("2"), automatic = Row("3");
        policy.RememberChoice(manual, open: true);
        policy.Focus(collapsed);
        Assert.True(collapsed.RepliesExpanded);
        policy.RememberChoice(collapsed, open: false);
        policy.Focus(automatic);
        Assert.True(manual.RepliesExpanded);
        Assert.False(collapsed.RepliesExpanded);
        Assert.True(automatic.RepliesExpanded);

        ArchivePostRow rebuiltManual = Row(manual.Id), rebuiltCollapsed = Row(collapsed.Id);
        policy.SwitchThread(ThreadA);
        policy.RestoreChoice(rebuiltManual);
        policy.RestoreChoice(rebuiltCollapsed);
        policy.Focus(rebuiltCollapsed);
        Assert.True(rebuiltManual.RepliesExpanded);
        Assert.False(rebuiltCollapsed.RepliesExpanded);
        Assert.False(automatic.RepliesExpanded);

        policy.SwitchThread(ThreadB);
        policy.Focus(rebuiltCollapsed);
        Assert.True(rebuiltCollapsed.RepliesExpanded);
    }

    [Fact]
    public void DisablingTheSettingClosesOnlyItsOwnExpansionAndKeepsTheLoadedReplies()
    {
        ReplyAutoExpandPolicy policy = Open(enabled: false);
        ArchivePostRow manual = Row("1"), automatic = Row("2");
        policy.RememberChoice(manual, open: true);
        policy.Focus(automatic);
        Assert.False(automatic.RepliesExpanded);

        policy.Enabled = true;
        policy.Focus(automatic);
        automatic.SetReplies(Array.Empty<ArchiveQuoteRow>(), 3, 0);
        Assert.True(automatic.RepliesExpanded);

        policy.Enabled = false;
        policy.Focus(automatic);
        Assert.True(manual.RepliesExpanded);
        Assert.False(automatic.RepliesExpanded);
        Assert.True(automatic.RepliesLoaded);
    }
}
