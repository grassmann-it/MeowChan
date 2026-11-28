using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BooruContentDialogTests
{
    private static BooruContentProfileSnapshot Snapshot(BooruContentProfileState state)
    {
        BooruContentSettings settings = new(BooruPostClaimRating.Sensitive, string.Empty, []);
        return new BooruContentProfileSnapshot(state, settings, new BooruContentFilter(1, settings));
    }

    private static T ById<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>()
        .First(control => AutomationProperties.GetAutomationId(control) == id);

    [Fact]
    public void DialogUsesTheSharedDialogLanguageAndHasNoImplicitDefaultAction() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruContentDialog dialog = new(Snapshot(BooruContentProfileState.Missing), (_, _) => Task.CompletedTask);
        dialog.Show();
        try
        {
            Button confirm = ById<Button>(dialog, "BooruContentConfirm");
            Assert.False(confirm.IsDefault);
            Assert.Contains("primary", confirm.Classes);
            Assert.False(dialog.ShowInTaskbar);
            TextBlock status = dialog.GetVisualDescendants().OfType<TextBlock>().First(block => block.Classes.Contains("workspace-error"));
            Assert.False(status.IsVisible);
            Assert.True(ById<ComboBox>(dialog, "BooruContentMaximum").IsEnabled);
        }
        finally { dialog.Close(false); }
    });

    [Fact]
    public void DialogCannotBeClosedAndInputsAreLockedWhileSaving() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TaskCompletionSource gate = new();
        BooruContentDialog dialog = new(Snapshot(BooruContentProfileState.Missing), (_, _) => gate.Task);
        dialog.Show();
        try
        {
            ComboBox maximum = ById<ComboBox>(dialog, "BooruContentMaximum");
            TextBox blacklist = ById<TextBox>(dialog, "BooruContentBlacklist");
            Button confirm = ById<Button>(dialog, "BooruContentConfirm");
            confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(maximum.IsEnabled);
            Assert.False(blacklist.IsEnabled);
            dialog.Close(false);
            Assert.True(dialog.IsVisible);
            gate.SetResult();
            BooruComponentDispatcher.WaitUntil(() => !dialog.IsVisible, "the dialog to close after saving");
        }
        finally { if (dialog.IsVisible) { dialog.Close(false); } }
    });

    [Fact]
    public void RevisionConflictStaysRecoverableAndTheNextConfirmUsesTheCurrentRevision() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        List<long> revisions = [];
        BooruContentDialog dialog = new(Snapshot(BooruContentProfileState.Missing), (_, revision) =>
        {
            revisions.Add(revision);
            return revisions.Count == 1 ? Task.FromException(new BooruContentRevisionException(revision, 7)) : Task.CompletedTask;
        });
        dialog.Show();
        try
        {
            Button confirm = ById<Button>(dialog, "BooruContentConfirm");
            confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.WaitUntil(() => confirm.IsEnabled, "the failed save to settle");
            Assert.True(dialog.IsVisible);
            confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.WaitUntil(() => !dialog.IsVisible, "the retry to close the dialog");
            Assert.Equal([1L, 7L], revisions);
        }
        finally { if (dialog.IsVisible) dialog.Close(false); }
    });

    [Theory]
    [InlineData(BooruContentProfileState.Unreadable)]
    [InlineData(BooruContentProfileState.Unsupported)]
    public void UnreadableDocumentOffersNoDeadSaveButAWayOut(BooruContentProfileState state) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruContentDialog dialog = new(Snapshot(state), (_, _) => Task.CompletedTask);
        dialog.Show();
        try
        {
            Assert.False(ById<Button>(dialog, "BooruContentConfirm").IsVisible);
            Assert.True(ById<Button>(dialog, "BooruContentCancel").IsEnabled);
            Assert.True(dialog.GetVisualDescendants().OfType<TextBlock>()
                .First(block => block.Classes.Contains("workspace-error")).IsVisible);
        }
        finally { dialog.Close(false); }
    });
}
