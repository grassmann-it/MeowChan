using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Code hygiene package (1.1.0): the reader's video keys (revision 17) give way to a binding the person saved, like the Media page's panel keys do
// (review S-1 of the media split): a saved reader binding on a video key neither silences the key in the gallery nor conflicts with it; in the reader
// the saved binding wins.
public sealed class ReaderVideoKeysYieldTests
{
    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static bool Has(EffectiveShortcutBindings bindings, string id, ShortcutGesture gesture) =>
        bindings.For(id).Any(g => EffectiveShortcutBindings.Equivalent(g, gesture, bindings.Platform));

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedReaderBindingOnAVideoKeyLeavesTheKeyToTheGallery(ShortcutPlatform platform)
    {
        foreach ((string video, _) in ReaderVideoKeys.Commands)
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(CommandCatalog.Get(video), platform))
                foreach (string owner in new[] { "reader.sidebar.toggle", "post.hide" })
                {
                    var section = new ShortcutBindingsSection().Set(owner, [gesture]);
                    EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
                    string what = video + " / " + owner + " / " + gesture.Display(platform);
                    Assert.True(Has(bindings, video, gesture), what);
                    Assert.True(Has(bindings, owner, gesture), what);
                    Assert.DoesNotContain(bindings.Issues, issue => issue.CommandId == video || issue.OtherCommandId == video);
                }
    }

    [Fact]
    public void ShiftMSavedForAReaderCommandStaysTheGalleryMuteKeyButTheReaderGivesItToTheCommand()
    {
        // Mute is Shift+M since revision 20 (M adds a favourite).
        var section = new ShortcutBindingsSection().Set("post.hide", [ShortcutGesture.Parse("Shift+M")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        var m = new KeyEventArgs { Key = Key.M, KeyModifiers = KeyModifiers.Shift };
        Assert.True(bindings.Matches(m, "video.mute"));
        Assert.True(bindings.Matches(m, "post.hide"));
        Assert.Empty(bindings.Issues);
        // In the reader the person's command has the key: no video action.
        Assert.True(ReaderVideoKeys.ReaderClaims(id => bindings.Matches(m, id)));
        Assert.Equal(ReaderVideoAction.None, ReaderVideoKeys.ClassifyUnclaimed(id => bindings.Matches(m, id)));
        // Without the saved binding the same key mutes the selected post's video.
        EffectiveShortcutBindings plain = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.False(ReaderVideoKeys.ReaderClaims(id => plain.Matches(m, id)));
        Assert.Equal(ReaderVideoAction.Mute, ReaderVideoKeys.ClassifyUnclaimed(id => plain.Matches(m, id)));
        // The gallery's own bindings still make a video default yield.
        var galleryKey = new ShortcutBindingsSection().Set("gallery.slideshow-toggle", [ShortcutGesture.Parse("Shift+M")]);
        Assert.False(Has(EffectiveShortcutBindings.Build(galleryKey, ShortcutPlatform.Linux), "video.mute", ShortcutGesture.Parse("Shift+M")));
    }

    [Fact]
    public void TheReaderClaimsAKeyThroughItsOwnCommandsButNotThroughTheVideoKeys()
    {
        Assert.True(ReaderVideoKeys.ReaderClaims(id => id == "reader.sidebar.toggle"));
        Assert.True(ReaderVideoKeys.ReaderClaims(id => id == "reader.next-post"));
        Assert.True(ReaderVideoKeys.ReaderClaims(id => id == "post.hide"));
        Assert.False(ReaderVideoKeys.ReaderClaims(id => ReaderVideoKeys.IsVideoCommand(id)));
        Assert.False(ReaderVideoKeys.ReaderClaims(id => id == "gallery.slideshow-toggle"));
        Assert.False(ReaderVideoKeys.ReaderClaims(_ => false));
    }

    [Fact]
    public void EveryVideoKeyCarriesTheReaderMarkAndNoOtherCommandDoes()
    {
        foreach ((string id, _) in ReaderVideoKeys.Commands)
        {
            Assert.Contains(EffectiveShortcutBindings.ReaderPostVideoKeys, CommandCatalog.Get(id).Contexts);
            Assert.Contains("reader", CommandCatalog.Get(id).ExecutionContexts);
            Assert.DoesNotContain("reader", EffectiveShortcutBindings.ConflictContexts(id));
            Assert.False(EffectiveShortcutBindings.ContextsOverlap(id, "reader.sidebar.toggle"), id);
            Assert.False(EffectiveShortcutBindings.ContextsOverlap(id, "post.hide"), id);
        }
        Assert.All(CommandCatalog.All.Where(command => command.Contexts.Contains(EffectiveShortcutBindings.ReaderPostVideoKeys)),
            command => Assert.True(ReaderVideoKeys.IsVideoCommand(command.Id), command.Id));
        Assert.Equal(new[] { "reader" }, EffectiveShortcutBindings.ConflictContexts("reader.sidebar.toggle"));
    }

    [Fact]
    public void TheReaderAndSidebarRoutesUseTheUnclaimedClassificationAndNotTheScrollFiles()
    {
        string inline = Source("Desktop/Views/Archive/ArchivePage.InlineVideo.cs");
        Assert.Contains("ReaderVideoKeys.ClassifyUnclaimed(id => ShortcutService.Current.Matches(e, id))", inline);
        Assert.Contains("ReaderVideoKeys.ClassifyUnclaimed(id => CommandBindings.Matches(e, id))", inline);
        Assert.DoesNotContain("ReaderVideoKeys.Classify(", inline);
        Assert.Contains("OwnerClaims = MediaPanelKeys.PageClaims", Source("Desktop/Views/Media/MediaPage.Panel.cs"));
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));
}
