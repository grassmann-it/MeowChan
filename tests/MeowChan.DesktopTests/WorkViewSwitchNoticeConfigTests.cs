using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

public sealed class WorkViewSwitchNoticeConfigTests
{
    [Fact]
    public void DefaultAndMissingFieldAreEightWithoutChangingTheLegacyNoticeRules()
    {
        Assert.Equal(8, new Config().WorkViewSwitchNoticeSeconds);
        Assert.False(new Config().WorkViewNoticePending);
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(8, loaded.WorkViewSwitchNoticeSeconds);
        Assert.True(loaded.WorkViewNoticePending);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Null(loaded.LoadWarning);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(60)]
    public void CloneBothCopyPathsAndPersistenceKeepTheDuration(int seconds)
    {
        using TempDirectory directory = new();
        Config source = new() { WorkViewSwitchNoticeSeconds = seconds, DownloadPath = Path.Combine(directory.Path, "media") };
        Config cloned = source.Clone();
        Config copied = new(); copied.CopyFrom(cloned);
        Config settings = new(); settings.CopySettingsFieldsFrom(cloned);
        Assert.Equal(seconds, cloned.WorkViewSwitchNoticeSeconds);
        Assert.Equal(seconds, copied.WorkViewSwitchNoticeSeconds);
        Assert.Equal(seconds, settings.WorkViewSwitchNoticeSeconds);
        copied.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(seconds, loaded.WorkViewSwitchNoticeSeconds);
        Assert.Null(loaded.LoadWarning);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(int.MaxValue, 60)]
    public void LoadClampsOnlyTheStoredIntegerAndRetainsUnrelatedSettings(int stored, int expected)
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"WorkViewSwitchNoticeSeconds\":" + stored + ",\"RecheckTimer\":120,\"WorkViewNoticePending\":false}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(expected, loaded.WorkViewSwitchNoticeSeconds);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.False(loaded.WorkViewNoticePending);
        Assert.Null(loaded.LoadWarning);
        loaded.Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void EditedCandidatesAreRejectedInsteadOfSilentlyClamped(int seconds)
    {
        Config candidate = new() { WorkViewSwitchNoticeSeconds = seconds };
        var error = Assert.Throws<ArgumentOutOfRangeException>(candidate.Validate);
        Assert.Equal(nameof(Config.WorkViewSwitchNoticeSeconds), error.ParamName);
        Assert.Equal(seconds, candidate.WorkViewSwitchNoticeSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopyFrom(candidate));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
    }

    [Fact]
    public void NoticeIdentityRetainsNoConfigurationSnapshotAndViewOnlyUndoKeepsAllProfilesAndNewSettings()
    {
        Config current = new() { WorkspaceMode = WorkView.Simple.ToSetting(), WorkViewNoticePending = true };
        foreach (WorkView view in WorkViewSetting.All)
        {
            PresentationProfile profile = current.GetLayoutProfile(view.ToSetting());
            profile.ColumnVisibility["saved-column"] = view == WorkView.Advanced;
            profile.ArchiveSplitWidth = 300 + (int)view;
        }
        WorkViewSwitchNoticeState state = new();
        state.BeginSwitch();
        current.WorkspaceMode = WorkView.Barebones.ToSetting();
        var notice = Assert.IsType<WorkViewSwitchNoticeState.Notice>(state.Applied(WorkView.Simple, WorkView.Barebones, 8, true));
        // Changes made after switching must not be rolled back by Undo.
        current.Theme = "Light";
        current.WorkViewSwitchNoticeSeconds = 23;
        current.GetLayoutProfile(WorkView.Simple.ToSetting()).ColumnVisibility["new-choice"] = true;
        Config candidate = current.Clone();
        var undo = Assert.IsType<WorkViewSwitchNoticeState.UndoRequest>(state.BeginUndo(notice, WorkView.Barebones));
        candidate.WorkspaceMode = undo.To.ToSetting(); // The same view-only mutation supplied by SetWorkViewAsync.
        current.CopyFrom(candidate);
        Assert.Null(state.Applied(undo.From, undo.To, current.WorkViewSwitchNoticeSeconds, true));
        state.EndUndo(undo);
        Assert.Equal(WorkView.Simple, WorkViewSetting.Parse(current.WorkspaceMode));
        Assert.Equal("Light", current.Theme);
        Assert.Equal(23, current.WorkViewSwitchNoticeSeconds);
        Assert.True(current.WorkViewNoticePending);
        Assert.True(current.GetLayoutProfile(WorkView.Simple.ToSetting()).ColumnVisibility["new-choice"]);
        foreach (WorkView view in WorkViewSetting.All)
        {
            PresentationProfile profile = current.GetLayoutProfile(view.ToSetting());
            Assert.Equal(view == WorkView.Advanced, profile.ColumnVisibility["saved-column"]);
            Assert.Equal(300 + (int)view, profile.ArchiveSplitWidth);
        }
    }
}
