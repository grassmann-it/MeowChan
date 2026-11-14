using System.Text.Json;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Modals plan KS1: the one shortcut draft that the Settings card and the shortcuts window share.
public sealed class ShortcutDraftSessionTests
{
    private const string Archive = "navigation.archive";
    private const string Rules = "navigation.rules";
    private static ShortcutGesture G(string text) => ShortcutGesture.Parse(text);

    [Fact]
    public void LoadIsCleanAndEveryEditMarksDirtyAndRaisesEdited()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        var changes = new List<ShortcutDraftChange>();
        session.Changed += (_, change) => changes.Add(change);
        Assert.False(session.IsDirty);
        Assert.Equal(0, session.ChangeCount);
        session.Clear(Archive);
        Assert.True(session.IsDirty);
        Assert.Empty(session.Requested(Archive));
        Assert.Equal(1, session.ChangeCount);
        session.Load(new ShortcutBindingsSection());
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
        Assert.Equal(new[] { ShortcutDraftChange.Edited, ShortcutDraftChange.Reloaded }, changes);
    }

    [Fact]
    public void LoadClonesAndKeepsUnknownDataVerbatim()
    {
        using var document = JsonDocument.Parse("""{"SchemaVersion":1,"Overrides":{"future.command":{"future":[1,2]}}}""");
        var committed = new ShortcutBindingsSection(document.RootElement);
        var session = new ShortcutDraftSession(committed);
        Assert.NotSame(committed, session.Draft);
        session.Clear(Archive);
        Assert.Equal("{\"future\":[1,2]}", session.Draft.Raw.GetProperty("Overrides").GetProperty("future.command").GetRawText());
        Assert.Equal(1, session.CustomCount); // the unknown entry is not counted as a custom binding
    }

    [Fact]
    public void UndoTakesBackEditsInOrderAndStopsAtTheLoad()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        string initial = session.Draft.Raw.GetRawText();
        session.Clear(Archive);
        string afterFirst = session.Draft.Raw.GetRawText();
        session.Set(Rules, new[] { G("Primary+Shift+K") });
        Assert.True(session.Undo());
        Assert.Equal(afterFirst, session.Draft.Raw.GetRawText());
        Assert.True(session.Undo());
        Assert.Equal(initial, session.Draft.Raw.GetRawText());
        Assert.False(session.Undo());
    }

    [Fact]
    public void ANoOpEditIsNotAChange()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        session.Clear(Archive);
        session.Clear(Archive);
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        session.SetSingleKeyShortcuts(true); // already on
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryAddAppendsOrTakesOverAConflictingGesture(bool takeOver)
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection()
            .Set(Archive, Array.Empty<ShortcutGesture>()).Set(Rules, new[] { G("Shift+F10") }));
        Assert.Equal(ShortcutAddResult.Applied, session.TryAdd(Archive, G("Shift+F10"), takeOver, platform: ShortcutPlatform.Linux));
        Assert.Single(session.Requested(Archive));
        Assert.Equal(takeOver ? 0 : 1, session.Requested(Rules).Count);
        Assert.Equal(!takeOver, session.HasBlockingIssues);
    }

    [Fact]
    public void TryAddReplacesKeepsEquivalentsOnceAndRejectsTheNinthGesture()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        Assert.Equal(ShortcutAddResult.Applied, session.TryAdd(Archive, G("Shift+F9"), false, replace: true, platform: ShortcutPlatform.Linux));
        Assert.Equal("F9", Assert.Single(session.Requested(Archive)).Key);
        Assert.Equal(ShortcutAddResult.Applied, session.TryAdd(Archive, G("Shift+F9"), false, platform: ShortcutPlatform.Linux));
        Assert.Single(session.Requested(Archive));
        session.Set(Archive, Enumerable.Range(1, ShortcutDraftSession.GestureLimit).Select(n => G("Shift+F" + n)).ToArray());
        string raw = session.Draft.Raw.GetRawText();
        Assert.Equal(ShortcutAddResult.Limit, session.TryAdd(Archive, G("Shift+F12"), false, platform: ShortcutPlatform.Linux));
        Assert.Equal(raw, session.Draft.Raw.GetRawText());
        Assert.Equal(ShortcutAddResult.Invalid, session.TryAdd(Archive, new ShortcutGesture("LeftCtrl", Array.Empty<string>()), false, platform: ShortcutPlatform.Linux));
    }

    [Fact]
    public void RemoveRestoreResetAndSingleKeysEditTheDraftOnly()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        session.Set(Archive, new[] { G("Shift+F9"), G("Shift+F10") });
        session.RemoveGesture(Archive, 0);
        Assert.Equal("F10", Assert.Single(session.Requested(Archive)).Key);
        session.RestoreDefault(Archive);
        Assert.False(session.Draft.HasOverride(Archive));
        session.Clear(Rules);
        session.ResetKnown();
        Assert.Equal(0, session.CustomCount);
        session.SetSingleKeyShortcuts(false);
        Assert.False(session.Draft.SingleKeyShortcuts);
        Assert.Equal(1, session.ChangeCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.RemoveGesture(Archive, 5));
    }

    [Fact]
    public void CommitMakesTheDraftTheBaselineAndDiscardReturnsToIt()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        session.Clear(Archive);
        session.MarkCommitted();
        Assert.False(session.IsDirty);
        Assert.Equal(0, session.ChangeCount);
        Assert.True(session.Baseline.HasOverride(Archive));
        session.Clear(Rules);
        session.Discard();
        Assert.False(session.Draft.HasOverride(Rules));
        Assert.True(session.Draft.HasOverride(Archive));
    }

    [Fact]
    public void AnUnsupportedSchemaStaysReadOnly()
    {
        using var document = JsonDocument.Parse("""{"SchemaVersion":99,"Overrides":{}}""");
        var session = new ShortcutDraftSession(new ShortcutBindingsSection(document.RootElement));
        string raw = session.Draft.Raw.GetRawText();
        session.Clear(Archive);
        session.ResetKnown();
        session.SetSingleKeyShortcuts(false);
        Assert.Equal(ShortcutAddResult.ReadOnly, session.TryAdd(Archive, G("Shift+F9"), false));
        Assert.Equal(raw, session.Draft.Raw.GetRawText());
        Assert.False(session.IsDirty);
        Assert.False(session.IsEditable);
    }
}
