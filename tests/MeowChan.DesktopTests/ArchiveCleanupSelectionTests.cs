using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveCleanupSelectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void VerifiedCandidatesStartUnselectedAndRequireAnExplicitChoice(int count)
    {
        var row = Row("eligible", count);
        ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
        Assert.Empty(selection.SelectedRows);
        Assert.Throws<InvalidOperationException>(() => selection.FreezeSelected());
        Assert.True(selection.SetSelected(row.Id, true));
        Assert.Equal(new[] { row.Id }, selection.FreezeSelected());
        Assert.Equal(count, Assert.Single(selection.SelectedRows).Media.SavedAssetCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(3)]
    public void UnknownOrOutOfRangeCountsCannotBeSelectedEvenWithACandidateLabel(int? count)
    {
        var row = Row("invalid", count);
        ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
        Assert.False(selection.SetSelected(row.Id, true));
        Assert.Empty(selection.SelectedRows);
    }

    [Fact]
    public void ProtectionActivityAndUncertaintyAlwaysWinOverASmallCount()
    {
        foreach (ArchiveCleanupEligibility state in new[] { ArchiveCleanupEligibility.Protected,
            ArchiveCleanupEligibility.Active, ArchiveCleanupEligibility.Kept, ArchiveCleanupEligibility.Unknown })
        {
            var row = Row("protected", 0) with { Eligibility = state };
            ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
            Assert.False(selection.SetSelected(row.Id, true));
        }
        foreach (ArchiveCleanupReason reason in Enum.GetValues<ArchiveCleanupReason>())
        {
            var row = Row("reason", 1) with { Reasons = new[] { reason } };
            ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
            Assert.False(selection.SetSelected(row.Id, true));
        }
        var error = Row("io", 0);
        error = error with { Media = error.Media with { Error = "Unconfirmed storage" } };
        ArchiveCleanupSelection blocked = new(); blocked.Load(Preview(error));
        Assert.False(blocked.SetSelected(error.Id, true));
    }

    [Fact]
    public void PartialStaleFuturePolicyAndDuplicateIdentityPreviewsAreLocked()
    {
        var row = Row("same", 0); var ready = Preview(row);
        foreach (var preview in new[] { ready with { Complete = false }, ready with { IsStale = true },
            ready with { PolicyVersion = 2 }, ready with { Rows = new[] { row, row } } })
        {
            ArchiveCleanupSelection selection = new(); selection.Load(preview);
            Assert.False(selection.CanChoose);
            Assert.False(selection.SetSelected(row.Id, true));
            Assert.Throws<InvalidOperationException>(() => selection.FreezeSelected());
        }
    }

    [Fact]
    public void InvalidationAndEveryRefreshDiscardOldChoicesEvenWhenIdsRemainTheSame()
    {
        var row = Row("unchanged-id", 2); var preview = Preview(row);
        ArchiveCleanupSelection selection = new(); selection.Load(preview); selection.SetSelected(row.Id, true);
        selection.Invalidate();
        Assert.Empty(selection.SelectedRows); Assert.False(selection.CanChoose);
        Assert.Throws<InvalidOperationException>(() => selection.FreezeSelected());
        selection.Load(preview with { ProfileBinding = "another-profile", EvidenceToken = "another-generation" });
        Assert.True(selection.CanChoose); Assert.Empty(selection.SelectedRows);
        Assert.False(selection.SetSelected(new("unseen", "board", "42"), true));
        selection.SetSelected(row.Id, true);
        selection.Load(preview);
        Assert.Empty(selection.SelectedRows);
    }

    [Fact]
    public void FrozenChoicesAreExactCopiedIdsInPreviewOrderAndPreserveVersionLoss()
    {
        var first = Row("one", 1); var second = Row("two", 2);
        ArchiveCleanupSelection selection = new(); selection.Load(Preview(first, second));
        selection.SetSelected(second.Id, true); selection.SetSelected(first.Id, true);
        ResourceId[] frozen = selection.FreezeSelected();
        Assert.Equal(new[] { first.Id, second.Id }, frozen);
        Assert.Equal(3, selection.SelectedRows.Sum(row => row.Media.SavedAssetCount));
        Assert.Equal(30, selection.SelectedRows.Sum(row => row.Media.VerifiedBytes));
        frozen[0] = new("foreign", "board", "thread");
        Assert.Equal(first.Id, selection.FreezeSelected()[0]);
        selection.SetSelected(first.Id, false);
        Assert.Equal(new[] { second.Id }, selection.FreezeSelected());
        selection.Clear(); Assert.Empty(selection.SelectedRows);
    }

    [Fact]
    public void InspectingSevenSelectedRowsRestoresTheFilterAndKeepsExactIdsForLossPreview() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.SetLanguage("de");
        ArchiveCleanupPreviewRow[] candidates = Enumerable.Range(0, 7)
            .Select(index => Row((index < 4 ? "shown-" : "hidden-") + index, index % 3)).ToArray();
        ArchiveCleanupPreview preview = Preview([.. candidates, Row("kept", 3) with { Eligibility = ArchiveCleanupEligibility.Kept }]);
        ResourceId[] selected = candidates.Select(row => row.Id).ToArray();
        ResourceId[]? prepared = null;
        Window owner = new() { Width = 640, Height = 480 };
        ArchiveCleanupDialog dialog = new(
            (_, _) => Task.FromResult(preview),
            (snapshot, ids, _) =>
            {
                prepared = ids.ToArray();
                return Task.FromResult(new ArchiveCleanupExecutionPlan(snapshot, prepared,
                    snapshot.Rows.Where(row => ids.Contains(row.Id)).ToArray(),
                    new Dictionary<ResourceId, IReadOnlyList<ArchiveCleanupDirectoryFiles>>(), []));
            },
            (_, _) => throw new InvalidOperationException("This test ends at the loss preview."), () => [])
        { Width = 608, Height = 448 };
        owner.Show();
        try
        {
            _ = dialog.ShowDialog(owner);
            T Find<T>(string name) where T : Control => dialog.FindControl<T>("ArchiveCleanup" + name)!;
            ListBox list = Find<ListBox>("List");
            TextBox search = Find<TextBox>("Search");
            Button inspect = Find<Button>("InspectSelection");
            Button back = Find<Button>("Back");
            Button review = Find<Button>("Review");
            TextBlock count = Find<TextBlock>("SelectionSummary");
            Button selectShown = Find<Button>("SelectShown");

            WaitUntil(() => selectShown.IsEnabled, "inventory");
            Assert.Equal("0 gewählt · 0 ausgeblendet", count.Text);
            Assert.False(inspect.IsEnabled);
            Assert.False(review.IsEnabled);
            Click(selectShown);
            search.Text = "shown-";
            WaitUntil(() => list.ItemCount == 4, "filtered inventory");
            Assert.Equal("7 gewählt · 3 ausgeblendet", count.Text);
            Assert.True(count.IsEffectivelyVisible && count.Bounds.Height > 0);
            Assert.InRange(count.TranslatePoint(default, dialog)!.Value.Y + count.Bounds.Height, 1, dialog.ClientSize.Height + 1);

            Click(inspect);
            WaitUntil(() => ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), back)
                && list.GetVisualDescendants().OfType<ListBoxItem>().Any(item => item.Bounds.Height > 0), "inspection layout and focus");
            Assert.Equal(selected, list.Items.Cast<ArchiveCleanupPreviewRow>().Select(row => row.Id));
            Assert.Equal("Gewählte Threads", AutomationProperties.GetName(list));
            Assert.Empty(list.GetVisualDescendants().OfType<CheckBox>());
            Assert.Null(prepared);
            Assert.False(review.IsVisible);

            back.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            WaitUntil(() => ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), inspect), "return focus");
            Assert.Equal("shown-", search.Text);
            Assert.Equal(selected.Take(4), list.Items.Cast<ArchiveCleanupPreviewRow>().Select(row => row.Id));
            Assert.Equal("7 gewählt · 3 ausgeblendet", count.Text);

            Click(review);
            Button confirm = Find<Button>("Confirm");
            WaitUntil(() => confirm.IsVisible, "loss preview");
            Assert.Equal(selected, prepared);
            Assert.False(confirm.IsEnabled);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }

        static void Click(Button button)
        {
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        void WaitUntil(Func<bool> condition, string stage) =>
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Dispatcher.UIThread.RunJobs();
                dialog.UpdateLayout();
                return condition();
            }, TimeSpan.FromSeconds(4)), stage);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void LabelsExplainStorageUncertaintyAndNeverPresentUnknownAsZero(string language)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            var unknown = Row("unknown", null) with { Eligibility = ArchiveCleanupEligibility.Unknown };
            Assert.Contains(language == "de" ? "unbekannt" : "unknown", ArchiveCleanupText.Media(unknown));
            Assert.Contains(language == "de" ? "Speicherort" : "storage location", ArchiveCleanupText.Reason(ArchiveCleanupReason.MediaUnknown));
            foreach (var reason in Enum.GetValues<ArchiveCleanupReason>()) Assert.False(string.IsNullOrWhiteSpace(ArchiveCleanupText.Reason(reason)));
            Assert.Equal(language == "de" ? "Archiv aufräumen…" : "Clean up archive…", CommandCatalog.Label("archive.cleanup"));
            Assert.Null(CommandCatalog.Get("archive.cleanup").Shortcut);
        }
        finally { Texts.SetLanguage(previous); }
    }

    private static ArchiveCleanupPreviewRow Row(string thread, int? count)
    {
        ResourceId id = new("source", "board", thread);
        var summary = new ArchiveThreadSummary(id, "https://source.test/board/" + thread, thread,
            "/not-opened-by-this-test/" + thread, ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, 4, 1);
        return new(summary, new(count, (count ?? 0) * 10, Array.Empty<ArchiveCleanupDirectoryEvidence>(), "media", null),
            ArchiveCleanupEligibility.Candidate, Array.Empty<ArchiveCleanupReason>(), "row");
    }
    private static ArchiveCleanupPreview Preview(params ArchiveCleanupPreviewRow[] rows) => new("profile", 1, rows,
        Array.Empty<ResourceId>(), "evidence", "protection", "work", true, false, Array.Empty<string>());
}
