using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class RebindingHonestyTests
{
    [Theory]
    [InlineData("video.loop", "post.hide", "H")]
    [InlineData("video.loop", "media.details", "I")]
    [InlineData("plugins.refresh", "gallery.rotate", "Shift+R")]
    public void ShadowingAndDisjointUnrelatedMeaningsBlockSaveAndAppearOnBothRows(string target, string other, string key)
    {
        ShortcutDraftSession draft = new(new ShortcutBindingsSection());
        Assert.Equal(ShortcutAddResult.Applied, draft.TryAdd(target, ShortcutGesture.Parse(key), false, replace: true));
        Assert.True(draft.HasBlockingIssues);
        ShortcutTableModel table = ShortcutTableModel.Build(draft.Draft, draft.Snapshot());
        Assert.Contains(table.Rows.Single(row => row.CommandId == target).Conflicts, conflict => conflict.OtherCommandId == other && conflict.BlocksSave);
        Assert.Contains(table.Rows.Single(row => row.CommandId == other).Conflicts, conflict => conflict.OtherCommandId == target && conflict.BlocksSave);
        Assert.Throws<ArgumentException>(() => ShortcutService.ValidateForCommit(draft.Draft));

        Assert.Equal(ShortcutAddResult.Applied, draft.TryAdd(target, ShortcutGesture.Parse(key), true, replace: true));
        Assert.False(draft.HasBlockingIssues);
        Assert.DoesNotContain(draft.Snapshot().For(other), gesture => EffectiveShortcutBindings.Equivalent(gesture, ShortcutGesture.Parse(key), draft.Snapshot().Platform));
        ShortcutService.ValidateForCommit(draft.Draft);
    }

    [Fact]
    public void SameMeaningOnDisjointTaggingSurfacesIsNotAConflict()
    {
        var section = new ShortcutBindingsSection().Set("tags.queue.skip", [ShortcutGesture.Parse("Alt+F9")])
            .Set("tagging.mark-skipped", [ShortcutGesture.Parse("Alt+F9")]);
        var bindings = EffectiveShortcutBindings.Build(section);
        Assert.Empty(bindings.Issues);
        ShortcutService.ValidateForCommit(section);
    }
}
