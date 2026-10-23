using MeowChan.Desktop.Feedback;

namespace MeowChan.DesktopTests;

public sealed class SessionFeedbackTests
{
    [Fact]
    public void RepeatsUpdateTheirOriginalIdentityAndKeepOtherContexts()
    {
        SessionFeedbackStore store = new();
        DateTimeOffset first = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
        SessionFeedback a = store.Record(FeedbackCategory.Error, "target.open", "A", "Cannot open", timestamp: first)!;
        SessionFeedback b = store.Record(FeedbackCategory.Error, "target.open", "B", "Cannot open", timestamp: first)!;
        SessionFeedback repeat = store.Record(FeedbackCategory.Error, "target.open", "A", "Cannot open", timestamp: first.AddMinutes(1))!;
        Assert.Equal(a.Id, repeat.Id);
        Assert.NotEqual(b.Id, repeat.Id);
        Assert.Equal(2, repeat.Count);
        Assert.Equal(first.AddMinutes(1), repeat.Timestamp);
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal("B", store.Entries[1].Context);
    }

    [Fact]
    public void NoUnresolvedMessageIsEvictedAndClearingDoesNotReuseIds()
    {
        SessionFeedbackStore store = new();
        for (int i = 0; i < 1500; ++i)
            store.Record(FeedbackCategory.Warning, "session.warning", i.ToString(), "Warning");
        Assert.Equal(1500, store.Entries.Count);
        Assert.Equal(1, store.Entries[0].Id);
        long last = store.Entries[^1].Id;
        store.Clear();
        Assert.Empty(store.Entries);
        Assert.True(store.Record(FeedbackCategory.Error, "clipboard.write", null, "Unavailable")!.Id > last);
    }

    [Fact]
    public void PopupSuppressionCannotSuppressCaptureAndDoesNotJournalSuccesses()
    {
        Config config = new() { ShowErrorNotifications = false, ShowWarningNotifications = false, ShowInformationNotifications = false };
        SessionFeedbackStore store = new();
        Assert.False(SessionFeedbackStore.AllowsPopup(config, FeedbackCategory.Error));
        Assert.NotNull(store.Record(FeedbackCategory.Error, "clipboard.read", null, "Failed"));
        Assert.NotNull(store.Record(FeedbackCategory.Warning, "config.load", null, "Recovered"));
        Assert.Null(store.Record(FeedbackCategory.Information, "admission.empty", null, "Empty"));
        Assert.Null(store.Record(FeedbackCategory.Success, "action.success", null, "Success"));
        Assert.Equal(2, store.Entries.Count);
        config.ShowErrorNotifications = true;
        Assert.Equal(2, store.Entries.Count);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void CategoriesAreIndependent(bool information, bool warning, bool error)
    {
        Config config = new() { ShowInformationNotifications = information, ShowWarningNotifications = warning, ShowErrorNotifications = error };
        Assert.Equal(information, SessionFeedbackStore.AllowsPopup(config, FeedbackCategory.Information));
        Assert.Equal(information, SessionFeedbackStore.AllowsPopup(config, FeedbackCategory.Success));
        Assert.Equal(warning, SessionFeedbackStore.AllowsPopup(config, FeedbackCategory.Warning));
        Assert.Equal(error, SessionFeedbackStore.AllowsPopup(config, FeedbackCategory.Error));
    }
}
