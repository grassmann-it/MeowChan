using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// FAV-01: keyboard F never removes without a question; U/explicit Remove is the only remove.
// The viewer is a real instance but not constructed: its constructor needs the Avalonia asset
// loader and a rendering platform. Only the confirmation's own controls and the few fields the
// key routing reads are injected, then its internal routing methods are driven. No window, no display.
[Collection("Media shortcut routing")]
public sealed class GalleryFavoriteConfirmationTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class Harness
    {
        internal GalleryViewer Viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        internal List<(GalleryAssetKey Key, bool Selected)> Sets = new();
        internal List<GalleryAssetKey> Toggles = new();

        internal Harness()
        {
            Set("_favoriteConfirmPanel", new Border());
            Set("_favoriteConfirmTitle", new TextBlock());
            Set("_favoriteConfirmHint", new TextBlock());
            Set("_favoriteConfirmKeep", new Button { IsDefault = true });
            Set("_favoriteConfirmRemove", new Button());
            Set("_playbackPreferences", new GalleryPlaybackPreferences());
            Set("_interactionTimer", new Avalonia.Threading.DispatcherTimer());
            Viewer.FavoriteSetRequested += (entry, selected) => Sets.Add((entry.Key, selected));
            Viewer.FavoriteToggleRequested += entry => Toggles.Add(entry.Key);
            Set("_interactionAvailable", true);
            Show(Entry("a"));
        }

        internal static GalleryEntry Entry(string id)
        {
            var entry = (GalleryEntry)RuntimeHelpers.GetUninitializedObject(typeof(GalleryEntry));
            typeof(GalleryEntry).GetField("<Key>k__BackingField", Fields)!
                .SetValue(entry, new GalleryAssetKey(new("fixture", "g", "42"), "dir", id));
            typeof(GalleryEntry).GetField("<Asset>k__BackingField", Fields)!.SetValue(entry,
                new MediaAssetReference(id, "m", "original", id + ".png", "/x/" + id + ".png", 1, new string('a', 64), "", id + ".png", true));
            return entry;
        }

        internal void Set(string field, object value) => typeof(GalleryViewer).GetField(field, Fields)!.SetValue(Viewer, value);
        internal T Get<T>(string field) => (T)typeof(GalleryViewer).GetField(field, Fields)!.GetValue(Viewer)!;

        // What ShowMediaAsync does to the identity/generation, without decoding media.
        internal void Show(GalleryEntry entry)
        {
            typeof(GalleryViewer).GetMethod("CloseFavoriteConfirm", Fields)!.Invoke(Viewer, null);
            Set("_request", Get<int>("_request") + 1);
            Set("_entry", entry);
            State(false);
        }

        // SetFavorite minus the toolbar/badge refresh, which needs real controls.
        internal void State(bool selected, bool pending = false)
        {
            Set("_favoriteKnown", true); Set("_favoritePending", pending); Set("_favoriteSelected", selected);
            Invoke("ReconcileFavoriteConfirm", selected, pending);
        }

        internal bool Dialog => Viewer.IsFavoriteConfirmOpen;
        internal Button Keep => Get<Button>("_favoriteConfirmKeep");
        internal Button Remove => Get<Button>("_favoriteConfirmRemove");

        internal KeyEventArgs Down(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = Viewer };
            if (Dialog) Viewer.RouteFavoriteConfirmKey(e);
            else if (CommandBindings.Matches(e, "favorites.toggle")) Viewer.RouteFavoriteToggleKey(e);
            else if (CommandBindings.Matches(e, "favorites.remove")) { Invoke("RequestFavorite", false); e.Handled = true; }
            return e;
        }

        internal void Up(Key key) => typeof(GalleryViewer).GetMethod("OnFavoriteKeyUp", Fields)!
            .Invoke(Viewer, new object?[] { Viewer, new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key } });
        internal void Tap(Key key) { Down(key); Up(key); }
        // The Click handlers of Keep/Remove are these two methods (wired by the constructor).
        internal void Invoke(string method, params object[] args) => typeof(GalleryViewer).GetMethod(method, Fields)!.Invoke(Viewer, args);
    }

    private static Harness Favorite()
    {
        var h = new Harness();
        h.State(true);
        return h;
    }

    [Fact]
    public void FOnNonFavoriteAddsWithoutQuestionAndNeverToggles()
    {
        var h = new Harness();
        KeyEventArgs e = h.Down(Key.F);
        Assert.True(e.Handled);
        Assert.False(h.Dialog);
        Assert.Equal(new[] { (Harness.Entry("a").Key, true) }, h.Sets);
        Assert.Empty(h.Toggles);
        // Pending add: another F is still set-true, never a take-back.
        h.State(false, pending: true);
        h.Up(Key.F); h.Tap(Key.F);
        Assert.All(h.Sets, set => Assert.True(set.Selected));
        Assert.Empty(h.Toggles);
    }

    [Fact]
    public void SecondFOnSavedFavoriteOpensConfirmationAndQueuesNothing()
    {
        var h = Favorite();
        Assert.True(h.Down(Key.F).Handled);
        Assert.True(h.Dialog);
        Assert.Empty(h.Sets);
        Assert.Empty(h.Toggles);
        Assert.True(h.Viewer.IsOverlayOpen);
        Assert.True(h.Keep.IsDefault);
        Assert.False(h.Remove.IsDefault);
    }

    [Fact]
    public void HeldFRepeatsNeitherKeepNorReopen()
    {
        var h = Favorite();
        h.Down(Key.F);
        for (int repeat = 0; repeat < 10; repeat++) Assert.True(h.Down(Key.F).Handled);
        Assert.True(h.Dialog);
        Assert.Empty(h.Sets);
        h.Up(Key.F);
        h.Down(Key.F); // a deliberate second press keeps
        Assert.False(h.Dialog);
        Assert.Empty(h.Sets);
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.F)]
    public void EnterAndFKeepWithoutAnyWrite(Key key)
    {
        var h = Favorite();
        h.Tap(Key.F);
        Assert.True(h.Dialog);
        Assert.True(h.Down(key).Handled);
        Assert.False(h.Dialog);
        Assert.Empty(h.Sets);
        Assert.Empty(h.Toggles);
    }

    [Fact]
    public void UInDialogRemovesExactlyOnceForTheConfirmedMedium()
    {
        var h = Favorite();
        h.Tap(Key.F);
        Assert.True(h.Down(Key.U).Handled);
        Assert.False(h.Dialog);
        Assert.Equal(new[] { (Harness.Entry("a").Key, false) }, h.Sets);
        Assert.Empty(h.Toggles);
        h.Up(Key.U);
    }

    [Fact]
    public void ActivatingRemoveRemovesOnceAndKeepNeverWrites()
    {
        var h = Favorite();
        h.Tap(Key.F);
        h.Invoke("ConfirmFavoriteRemove");
        Assert.Equal(new[] { (Harness.Entry("a").Key, false) }, h.Sets);
        h.State(true);
        h.Up(Key.F); h.Tap(Key.F);
        h.Invoke("KeepFavoriteFromConfirm");
        Assert.False(h.Dialog);
        Assert.Single(h.Sets);
    }

    [Fact]
    public void EnterKeepsEvenWhenRemoveHasFocus()
    {
        var h = Favorite();
        h.Tap(Key.F);
        // Focus placement is not observable without a window; Enter is routed by the dialog,
        // not by whichever control would receive it, so the key is consumed as Keep.
        KeyEventArgs enter = h.Down(Key.Enter);
        Assert.True(enter.Handled);
        Assert.False(h.Dialog);
        Assert.Empty(h.Sets);
    }

    [Fact]
    public void EscapeStyleDismissDiscardsWithoutRemoval()
    {
        var h = Favorite();
        h.Tap(Key.F);
        Assert.True(h.Viewer.TryDismissOverlay());
        Assert.False(h.Dialog);
        Assert.Empty(h.Sets);
        Assert.False(h.Viewer.TryDismissOverlay());
    }

    [Fact]
    public void MediumChangeDiscardsStaleConfirmationAndNeverTouchesTheNewMedium()
    {
        var h = Favorite();
        h.Tap(Key.F);
        h.Show(Harness.Entry("b"));
        Assert.False(h.Dialog);
        h.State(true);
        h.Tap(Key.U); // direct unfavorite of the NEW medium is the user's own explicit U
        Assert.Equal(new[] { (Harness.Entry("b").Key, false) }, h.Sets);

        // Identity changes without the dialog hearing about it: confirm must still refuse.
        var stale = Favorite();
        stale.Tap(Key.F);
        stale.Set("_entry", Harness.Entry("c"));
        stale.Invoke("ConfirmFavoriteRemove");
        Assert.Empty(stale.Sets);
        Assert.False(stale.Dialog);

        // Same medium but a newer viewer generation (resume/replay) is stale too.
        var generation = Favorite();
        generation.Tap(Key.F);
        generation.Set("_request", generation.Get<int>("_request") + 1);
        generation.Down(Key.U);
        Assert.Empty(generation.Sets);
        Assert.False(generation.Dialog);
    }

    [Fact]
    public void FavoriteStateLeavingSettledFavoriteClosesTheQuestion()
    {
        var h = Favorite();
        h.Tap(Key.F);
        h.State(true, pending: true);
        Assert.False(h.Dialog);
        h.Up(Key.F);
        h.State(true);
        h.Tap(Key.F);
        Assert.True(h.Dialog);
        h.State(false);
        Assert.False(h.Dialog);
        Assert.Empty(h.Sets);
    }

    [Fact]
    public void FWhileSavedFavoriteWriteIsPendingNeitherAsksNorWrites()
    {
        var h = Favorite();
        h.State(true, pending: true);
        Assert.True(h.Down(Key.F).Handled);
        Assert.False(h.Dialog);
        Assert.Empty(h.Sets);
        Assert.Empty(h.Toggles);
    }

    [Fact]
    public void UOutsideDialogStaysDirectAndIdempotent()
    {
        var h = Favorite();
        h.Tap(Key.U); h.Tap(Key.U);
        Assert.False(h.Dialog);
        Assert.All(h.Sets, set => Assert.False(set.Selected));
        Assert.Equal(2, h.Sets.Count);
    }

    [Fact]
    public void RebindingFavoritesToggleIsHonoured()
    {
        var h = Favorite();
        EffectiveShortcutBindings original = ShortcutService.Current;
        try
        {
            SetBindings(EffectiveShortcutBindings.Build(
                new ShortcutBindingsSection().Set("favorites.toggle", [ShortcutGesture.Parse("G")]), ShortcutPlatform.Linux));
            Assert.False(h.Down(Key.F).Handled);
            Assert.False(h.Dialog);
            Assert.True(h.Down(Key.G).Handled);
            Assert.True(h.Dialog);
            Assert.True(h.Down(Key.Enter).Handled);
            Assert.Empty(h.Sets);
        }
        finally { SetBindings(original); }
    }

    // Publish posts to the UI dispatcher, which does not run here; swap the snapshot directly.
    private static void SetBindings(EffectiveShortcutBindings value) =>
        typeof(ShortcutService).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
}
