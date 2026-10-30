using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
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

    [Fact]
    public void DismissalSuppressesRepeatedSnapshotsButKeepsDiagnosticIdentityAndCount()
    {
        SessionFeedbackStore store = new();
        DateTimeOffset first = DateTimeOffset.Parse("2026-10-28T10:00:00Z");
        SessionFeedback a = store.Record(FeedbackCategory.Error, "target.open", "A", "Cannot open",
            "Original details", first)!;
        int changes = 0;
        store.Changed += () => ++changes;

        store.Dismiss(a);
        store.Dismiss(a); // Idempotent presentation action, not a diagnostic mutation.
        Assert.Equal(0, changes);
        Assert.Equal(a, Assert.Single(store.Entries));
        SessionFeedback repeat = store.Record(a.Category, a.Operation, a.Context, a.Summary, a.Details,
            first.AddDays(10))!; // No guessed timeout or new episode.

        Assert.Equal(a.Id, repeat.Id);
        Assert.Equal(2, repeat.Count);
        Assert.Equal(1, changes);
        Assert.True(store.IsDismissed(a));
        Assert.True(store.IsDismissed(repeat));
        Assert.Equal(repeat, Assert.Single(store.Entries));
    }

    [Fact]
    public void PendingDuplicateEligibilityIsCheckedAfterDismissalAndDiagnosticClear()
    {
        SessionFeedbackStore store = new();
        SessionFeedback a = store.Record(FeedbackCategory.Warning, "session.warning", "A", "Unavailable")!;
        SessionFeedback repeat = store.Record(a.Category, a.Operation, a.Context, a.Summary)!;
        SessionFeedback b = store.Record(a.Category, a.Operation, "B", a.Summary)!;
        Queue<SessionFeedback> pending = new(new[] { a, repeat, b });

        store.Dismiss(a);
        store.Clear();
        List<long> rendered = new();
        while (pending.TryDequeue(out SessionFeedback? entry))
            if (!store.IsDismissed(entry)) rendered.Add(entry.Id); // Same gate as RenderNotification.

        Assert.Equal(new[] { b.Id }, rendered);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void CapturedCloseSurvivesClearAndCannotDismissDifferentNewerEvent()
    {
        SessionFeedbackStore store = new();
        SessionFeedback a = store.Record(FeedbackCategory.Error, "target.open", "A", "Cannot open")!;
        store.Clear();
        SessionFeedback b = store.Record(a.Category, a.Operation, "B", a.Summary)!;

        store.Dismiss(a); // Captured banner/command can outlive its diagnostic row.
        Assert.True(store.IsDismissed(a));
        Assert.False(store.IsDismissed(b));
        Assert.Equal(b, Assert.Single(store.Entries));
        SessionFeedback repeatedA = store.Record(a.Category, a.Operation, a.Context, a.Summary)!;
        Assert.True(repeatedA.Id > b.Id); // Diagnostic IDs are never recycled by Clear.
        Assert.True(store.IsDismissed(repeatedA));
        Assert.False(store.IsDismissed(b));
    }

    [Fact]
    public void PopupTogglesAndClearDoNotResetDismissalAndANewStoreDoes()
    {
        Config config = new() { ShowErrorNotifications = false };
        SessionFeedbackStore store = new();
        SessionFeedback a = store.Record(FeedbackCategory.Error, "clipboard.read", null, "Failed")!;
        store.Dismiss(a);
        store.Clear();
        config.ShowErrorNotifications = true;
        SessionFeedback repeat = store.Record(a.Category, a.Operation, a.Context, a.Summary)!;

        Assert.True(SessionFeedbackStore.AllowsPopup(config, repeat.Category));
        Assert.True(store.IsDismissed(repeat));
        Assert.Equal(repeat, Assert.Single(store.Entries)); // New diagnostics remain available.
        config.ShowErrorNotifications = false;
        config.ShowErrorNotifications = true;
        Assert.True(store.IsDismissed(repeat));
        SessionFeedbackStore nextSession = new();
        SessionFeedback next = nextSession.Record(a.Category, a.Operation, a.Context, a.Summary)!;
        Assert.False(nextSession.IsDismissed(next));
    }

    [Theory]
    [InlineData((int)FeedbackCategory.Warning, "target.open", "A", "Cannot open", "Details")]
    [InlineData((int)FeedbackCategory.Error, "directory.open", "A", "Cannot open", "Details")]
    [InlineData((int)FeedbackCategory.Error, "target.open", "B", "Cannot open", "Details")]
    [InlineData((int)FeedbackCategory.Error, "target.open", "A", "Different failure", "Details")]
    [InlineData((int)FeedbackCategory.Error, "target.open", "A", "Cannot open", "Different details")]
    public void DismissalDoesNotBroadenTheExistingRecordIdentity(int categoryValue, string operation,
        string context, string summary, string details)
    {
        SessionFeedbackStore store = new();
        SessionFeedback a = store.Record(FeedbackCategory.Error, "target.open", "A", "Cannot open", "Details")!;
        store.Dismiss(a);
        SessionFeedback other = store.Record((FeedbackCategory)categoryValue, operation, context, summary, details)!;
        Assert.NotEqual(a.Id, other.Id);
        Assert.False(store.IsDismissed(other));
    }

    [Fact]
    public void PresentationLanguageChangesDoNotResetDismissalAndCloseLabelsAreLocalized()
    {
        string originalLanguage = Texts.Language;
        try
        {
            SessionFeedbackStore store = new();
            SessionFeedback a = store.Record(FeedbackCategory.Warning, "session.warning", "A", "Source payload")!;
            store.Dismiss(a);
            Texts.SetLanguage("en");
            Assert.Equal("Dismiss message", Texts.T("feedback.dismiss"));
            Texts.SetLanguage("de");
            Assert.Equal("Meldung ausblenden", Texts.T("feedback.dismiss"));
            SessionFeedback repeat = store.Record(a.Category, a.Operation, a.Context, a.Summary)!;
            Assert.True(store.IsDismissed(repeat));
            Assert.Empty(Texts.ValidateBuiltInCatalogs());
        }
        finally { Texts.SetLanguage(originalLanguage); }
    }

    [Fact]
    public void ShellNotificationReflectionAndCapturedDismissalContractsRemainStable()
    {
        MethodInfo publish = typeof(MainWindow).GetMethod("ShowNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(new[] { typeof(string), typeof(string), typeof(Avalonia.Controls.Notifications.NotificationType),
            typeof(string), typeof(string) }, publish.GetParameters().Select(parameter => parameter.ParameterType));
        MethodInfo dismiss = typeof(MainWindow).GetMethod("DismissShellFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(SessionFeedback), Assert.Single(dismiss.GetParameters()).ParameterType);
        Assert.True(dismiss.IsAssembly);
    }

    [Fact]
    public void StableFaultKeyCountsTranslatedSummariesWithoutReplayingDismissedOccurrence()
    {
        SessionFeedbackStore store = new();
        SessionFeedback english = store.Record(FeedbackCategory.Error, "clipboard.read", null,
            "The clipboard could not be read.", faultKey: "clipboard.readFailed")!;
        store.Dismiss(english);
        SessionFeedback german = store.Record(FeedbackCategory.Error, english.Operation, english.Context,
            "Die Zwischenablage konnte nicht gelesen werden.", faultKey: english.FaultKey)!;
        Assert.Equal(english.Id, german.Id);
        Assert.Equal(2, german.Count);
        Assert.Equal("Die Zwischenablage konnte nicht gelesen werden.", german.Summary);
        Assert.True(store.IsDismissed(german));
        Assert.True(SessionFeedbackStore.SameOccurrence(english, german));
        store.Clear();
        SessionFeedback afterClear = store.Record(german.Category, german.Operation, german.Context,
            german.Summary, faultKey: german.FaultKey)!;
        Assert.NotEqual(german.Id, afterClear.Id);
        Assert.True(store.IsDismissed(afterClear));
        Assert.True(SessionFeedbackStore.SameOccurrence(english, afterClear));
    }

    [Fact]
    public void StableFaultKeyDoesNotMergeOtherFaultsContextsOperationsCategoriesOrDetails()
    {
        SessionFeedbackStore store = new();
        SessionFeedback a = store.Record(FeedbackCategory.Error, "open", "source-A", "Failed",
            "access denied", faultKey: "access.denied")!;
        store.Dismiss(a);
        SessionFeedback[] different =
        {
            store.Record(a.Category, a.Operation, a.Context, a.Summary, a.Details, faultKey: "path.invalid")!,
            store.Record(a.Category, a.Operation, "source-B", a.Summary, a.Details, faultKey: a.FaultKey)!,
            store.Record(a.Category, "save", a.Context, a.Summary, a.Details, faultKey: a.FaultKey)!,
            store.Record(FeedbackCategory.Warning, a.Operation, a.Context, a.Summary, a.Details, faultKey: a.FaultKey)!,
            store.Record(a.Category, a.Operation, a.Context, a.Summary, "disk full", faultKey: a.FaultKey)!,
            store.Record(a.Category, a.Operation, a.Context, a.Summary, a.Details)! // no key is a separate identity
        };
        Assert.All(different, entry =>
        {
            Assert.NotEqual(a.Id, entry.Id);
            Assert.False(store.IsDismissed(entry));
            Assert.False(SessionFeedbackStore.SameOccurrence(a, entry));
        });
        Assert.Equal(7, store.Entries.Count);
        Assert.Throws<ArgumentException>(() => store.Record(a.Category, "open", null, "Failed", faultKey: " "));
    }

    [Theory]
    [InlineData((int)NotificationType.Error, "clipboard.read", "clipboard.readFailed")]
    [InlineData((int)NotificationType.Error, "clipboard.write", "clipboard.writeFailed")]
    [InlineData((int)NotificationType.Error, "target.open", "location.openFailed")]
    [InlineData((int)NotificationType.Warning, "directory.open", "location.missing")]
    [InlineData((int)NotificationType.Error, "session.shutdown", "shutdown.failed")]
    public void OnlyVerifiedFixedCallerWordingGetsStableIdentityAcrossLanguages(int type, string operation, string key)
    {
        string original = Texts.Language;
        try
        {
            SessionFeedbackStore store = new();
            SessionFeedback? first = null;
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                string message = Texts.T(key);
                string? faultKey = MainWindow.KnownNotificationFaultKey((NotificationType)type, operation, message);
                Assert.Equal(key, faultKey);
                SessionFeedback entry = store.Record(type == (int)NotificationType.Error
                    ? FeedbackCategory.Error : FeedbackCategory.Warning, operation, "frozen-target", message,
                    faultKey: faultKey)!;
                if (first == null) { first = entry; store.Dismiss(first); }
                else { Assert.Equal(first.Id, entry.Id); Assert.True(store.IsDismissed(entry)); }
                Assert.Null(MainWindow.KnownNotificationFaultKey((NotificationType)type, operation, "Different error"));
                Assert.Null(MainWindow.KnownNotificationFaultKey((NotificationType)type, "other.operation", message));
                Assert.Null(MainWindow.KnownNotificationFaultKey(NotificationType.Information, operation, message));
            }
            Assert.Equal(2, Assert.Single(store.Entries).Count);
        }
        finally { Texts.SetLanguage(original); }
    }

    [Theory]
    [InlineData("session.warning")]
    [InlineData("session.restore")]
    [InlineData("config.load")]
    [InlineData("preferences.appearance")]
    [InlineData("board.admission")]
    [InlineData("gallery.action")]
    public void UnstructuredFailureFamiliesDoNotGainBlanketIdentity(string operation)
    {
        Assert.Null(MainWindow.KnownNotificationFaultKey(NotificationType.Error, operation, "Failure A"));
        Assert.Null(MainWindow.KnownNotificationFaultKey(NotificationType.Error, operation, "Failure B"));
    }

    [Fact]
    public void ToastExplicitCloseCommandCapturesOnlyItsOwnEntryAndHasAccessibleX()
    {
        // This checks content/action wiring, not native pointer input or theme rendering.
        SessionFeedbackStore store = new();
        SessionFeedback a = store.Record(FeedbackCategory.Error, "open", "A", "Failed")!;
        SessionFeedback b = store.Record(FeedbackCategory.Error, "open", "B", "Failed")!;
        FeedbackToastContent content = new("Title", a.Summary, a, toast => store.Dismiss(toast.Entry!));
        Assert.Same(a, content.Entry);
        Assert.Equal("FeedbackToastDismissButton", content.DismissButton.Name);
        Assert.Equal("×", content.DismissButton.Content);
        Assert.Equal(Texts.T("feedback.dismiss"), AutomationProperties.GetName(content.DismissButton));
        Assert.Equal(Texts.T("feedback.dismiss"), ToolTip.GetTip(content.DismissButton));
        Assert.False(store.IsDismissed(a)); // Publication alone is not acknowledgement.
        content.DismissButton.Command!.Execute(null);
        Assert.True(store.IsDismissed(a));
        Assert.False(store.IsDismissed(b));
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void ToastCardCloseOrExpiryDoesNotAcknowledgeItsEntry()
    {
        SessionFeedbackStore store = new();
        SessionFeedback a = store.Record(FeedbackCategory.Warning, "session.warning", "A", "Warning")!;
        int acknowledgements = 0;
        FeedbackToastContent content = new("Title", a.Summary, a, toast =>
        {
            acknowledgements++;
            store.Dismiss(toast.Entry!);
        });
        NotificationCard card = new() { Content = content };
        card.Close(); // The same Close path used by expiry, capacity eviction and manager CloseAll.
        card.IsClosed = true;
        Assert.Equal(0, acknowledgements);
        Assert.False(store.IsDismissed(a));
        Assert.Equal(a, Assert.Single(store.Entries));
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
