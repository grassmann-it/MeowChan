using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Modals plan KS2: the one commit path of shortcut bindings, shared by Settings' Save and the shortcuts window.
public sealed class ShortcutBindingsCommitTests
{
    private static ShortcutBindingsSection Conflicting() => new ShortcutBindingsSection()
        .Set("navigation.archive", new[] { ShortcutGesture.Parse("Primary+Shift+K") })
        .Set("navigation.rules", new[] { ShortcutGesture.Parse("Primary+Shift+K") });

    [Fact]
    public async Task TheWindowCommitsOnlyTheBindingsThroughTheSettingsPath()
    {
        var committed = new Config { RecheckTimer = 77 };
        var draft = new ShortcutBindingsSection().Set("navigation.archive", Array.Empty<ShortcutGesture>());
        Config? applied = null;
        await ShortcutBindingsCommit.CommitAsync(committed, draft, candidate => { applied = candidate; return Task.CompletedTask; });
        Assert.NotNull(applied);
        Assert.NotSame(committed, applied);
        Assert.Equal(77, applied!.RecheckTimer);
        Assert.Equal(draft.Raw.GetRawText(), applied.ShortcutBindings.Raw.GetRawText());
        Assert.NotSame(draft, applied.ShortcutBindings);
        Assert.Equal(draft.Raw.GetRawText(), committed.ShortcutBindings.Raw.GetRawText());
    }

    [Fact]
    public async Task ABlockingConflictIsRefusedBeforeAnythingIsPersisted()
    {
        var committed = new Config();
        string before = committed.ShortcutBindings.Raw.GetRawText();
        bool called = false;
        await Assert.ThrowsAsync<ArgumentException>(() => ShortcutBindingsCommit.CommitAsync(committed, Conflicting(),
            _ => { called = true; return Task.CompletedTask; }));
        Assert.False(called);
        Assert.Equal(before, committed.ShortcutBindings.Raw.GetRawText());
        Assert.Throws<ArgumentException>(() => ShortcutBindingsCommit.ApplyTo(new Config(), Conflicting()));
    }

    [Fact]
    public async Task PersistWithTheSettingsPathOnlyHandsTheCandidateOver()
    {
        var candidate = new Config();
        Config? seen = null;
        await ShortcutBindingsCommit.PersistAsync(candidate, c => { seen = c; return Task.CompletedTask; });
        Assert.Same(candidate, seen);
    }
}
