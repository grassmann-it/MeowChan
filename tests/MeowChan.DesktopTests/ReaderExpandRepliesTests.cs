using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderExpandRepliesTests : IDisposable
{
    public ReaderExpandRepliesTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

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
    public void TheQuickSettingChangesTheReaderExpandRepliesPreference()
    {
        Config config = new();
        QuickSettingField field = ReaderQuickSettings.Fields.Single(f => f.Id == SettingsFieldId.ReaderExpandReplies);
        Assert.Equal(QuickSettingKind.Toggle, field.Kind);
        Assert.Equal(bool.FalseString, field.Read(config));
        Assert.True(field.TryCreateChange(bool.TrueString, out Action<Config> change));
        change(config);
        Assert.True(config.ReaderExpandReplies);
        Assert.Equal(bool.TrueString, field.Read(config));
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
        Assert.True(second.RepliesExpanded);
        Assert.False(first.RepliesExpanded);
        List<string?> changes = new();
        second.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        policy.Focus(second);
        Assert.Empty(changes);
        policy.Focus(third);
        Assert.False(first.RepliesExpanded);
        Assert.False(second.RepliesExpanded);
        Assert.True(third.RepliesExpanded);
        policy.Focus(null);
        Assert.False(third.RepliesExpanded);
    }

    [Fact]
    public async Task ReaderSelectionClosesAutomaticRepliesAndKeepsManualChoices()
    {
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView view = await fixture.CaptureAsync(ThreadA, "Selection and replies",
        [
            new("100", 0, PostFieldPresence.All, Html: "<a href=\"#p102\" class=\"quotelink\">&gt;&gt;102</a> First"),
            new("101", 1, PostFieldPresence.All, Html: "<a href=\"#p100\" class=\"quotelink\">&gt;&gt;100</a> Second"),
            new("102", 2, PostFieldPresence.All, Html: "<a href=\"#p101\" class=\"quotelink\">&gt;&gt;101</a> Third")
        ]);
        fixture.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Window window = new() { Content = page, Width = 1320, Height = 760 };
            try
            {
                page.IsAvailable = true;
                window.Show();
                ReaderArchiveTestFixture.Load(page, view);
                window.UpdateLayout();
                ListBox posts = SidePanelDesignTests.ById<ListBox>(window, "ArchivePosts");
                ArchivePostRow[] rows = posts.Items.OfType<ArchivePostRow>().ToArray();
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    window.UpdateLayout();
                    return rows.All(row => row.HasBacklinks);
                }, "reply counts for all three posts");

                posts.SelectedItem = rows[0];
                page.ReaderExpandRepliesPreference = true;
                Assert.Equal(new[] { rows[0] }, rows.Where(row => row.RepliesExpanded));
                posts.SelectedItem = rows[1];
                Assert.Equal(new[] { rows[1] }, rows.Where(row => row.RepliesExpanded));

                BooruComponentDispatcher.Wait(page.SetRepliesExpandedAsync(rows[0], true));
                BooruComponentDispatcher.Wait(page.SetRepliesExpandedAsync(rows[1], false));
                posts.SelectedItem = rows[2];
                Assert.Equal(new[] { rows[0], rows[2] }, rows.Where(row => row.RepliesExpanded));
                posts.SelectedItem = rows[1];
                Assert.Equal(new[] { rows[0] }, rows.Where(row => row.RepliesExpanded));
            }
            finally { window.Close(); }
        });
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
        policy.Enabled = true;
        policy.Focus(automatic);
        Assert.True(automatic.RepliesExpanded);
        Assert.True(automatic.RepliesLoaded);
    }

    [Fact]
    public void ReturningInHistoryRestoresBothManualChoicesWithoutReopeningTheOldSelection()
    {
        ReplyAutoExpandPolicy policy = Open();
        ArchivePostRow manual = Row("1"), collapsed = Row("2"), automatic = Row("3");
        policy.RememberChoice(manual, open: true);
        policy.RememberChoice(collapsed, open: false);
        policy.Focus(automatic);
        KeyValuePair<string, bool>[] choices = policy.CaptureManualChoices();
        policy.SwitchThread(ThreadB);
        Assert.False(automatic.RepliesExpanded);

        policy.SwitchThread(ThreadA);
        policy.RestoreManualChoices(choices);
        ArchivePostRow restoredManual = Row("1"), restoredCollapsed = Row("2"), restoredAutomatic = Row("3");
        policy.RestoreChoice(restoredManual);
        policy.RestoreChoice(restoredCollapsed);
        policy.RestoreChoice(restoredAutomatic);
        policy.Focus(restoredCollapsed);
        Assert.True(restoredManual.RepliesExpanded);
        Assert.False(restoredCollapsed.RepliesExpanded);
        Assert.False(restoredAutomatic.RepliesExpanded);
    }
}
