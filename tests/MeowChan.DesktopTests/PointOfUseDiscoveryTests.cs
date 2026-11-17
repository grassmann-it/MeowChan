using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class PointOfUseDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekNamesEveryEffectiveSeekBinding(bool rebound) => WithControls(() =>
    {
        string[] commands = ["video.step-back", "video.step-forward", "video.seek-back", "video.seek-forward", "video.seek-back-far", "video.seek-forward-far"];
        var section = new ShortcutBindingsSection();
        if (rebound)
            for (int i = 0; i < commands.Length; i++) section = section.Set(commands[i], [ShortcutGesture.Parse("Alt+F" + (i + 1))]);
        ShortcutService.Publish(section);
        GalleryVideoPlayer player = new();
        try
        {
            Refresh(player);
            AssertHelp(Field<Control>(player, "_seek"), "gallery", commands);
        }
        finally { player.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VolumeNamesBothEffectiveBindingsInTooltipAndAccessibleHelp(bool rebound) => WithControls(() =>
    {
        var section = new ShortcutBindingsSection();
        if (rebound) section = section.Set("video.volume-down", [ShortcutGesture.Parse("Alt+F7")])
            .Set("video.volume-up", [ShortcutGesture.Parse("Alt+F8")]);
        ShortcutService.Publish(section);
        GalleryVideoPlayer player = new();
        try
        {
            Refresh(player);
            AssertHelp(Field<Control>(player, "_volume"), "gallery", "video.volume-down", "video.volume-up");
        }
        finally { player.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    [Theory]
    [InlineData("reader", "reader.next-post")]
    [InlineData("media", "media.rating.edit")]
    public void VideoHelpOmitsGesturesClaimedByItsHost(string context, string owner) => WithControls(() =>
    {
        // A page binding shadows video without removing its gesture in the gallery.
        Assert.True(CommandCatalog.TryGet(owner, out _));
        var gesture = ShortcutService.Current.For("video.loop").First();
        ShortcutService.Publish(new ShortcutBindingsSection().Set(owner, [gesture]));
        GalleryVideoPlayer player = new();
        try
        {
            player.ShortcutContext = context;
            string key = gesture.Display(OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows);
            var loop = Field<Control>(player, "_loop");
            Assert.DoesNotContain(key, Assert.IsType<string>(ToolTip.GetTip(loop)));
            Assert.DoesNotContain(key, AutomationProperties.GetHelpText(loop));
            player.ShortcutContext = "gallery";
            Assert.Contains(key, Assert.IsType<string>(ToolTip.GetTip(loop)));
        }
        finally { player.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderActivationHelpUsesEffectiveBindingsInEveryLanguage(bool unbound) => WithControls(() =>
    {
        var section = new ShortcutBindingsSection()
            .Set("reader.media.activate", unbound ? [] : [ShortcutGesture.Parse("Alt+F8")])
            .Set("video.play-pause", unbound ? [] : [ShortcutGesture.Parse("Alt+F9")]);
        ShortcutService.Publish(section);
        foreach (string language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(language);
            string hint = ReaderShortcutHelp.PlayHint(), note = ReaderShortcutHelp.ActivationNote();
            Assert.DoesNotContain("{0}", hint); Assert.DoesNotContain("{1}", hint);
            Assert.DoesNotContain("{0}", note);
            foreach (string id in new[] { "reader.media.activate", "video.play-pause" })
                foreach (var gesture in ShortcutService.Current.For(id))
                {
                    string key = gesture.Display(OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows);
                    Assert.Contains(key, hint); Assert.Contains(key, note);
                }
            if (!unbound) Assert.DoesNotContain("Space", hint);
        }
        Texts.SetLanguage("en");
    });

    [Theory]
    [InlineData(ShortcutPlatform.MacOS)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    public void SuggestionChipsUseCatalogueDefaultsOrTheEffectiveOverride(ShortcutPlatform platform) => WithControls(() =>
    {
        ShortcutService.Publish(new ShortcutBindingsSection());
        for (int index = 0; index < QuickTagCommands.SuggestionCount; index++)
        {
            string id = QuickTagCommands.Suggestion(index + 1);
            string expected = string.Join(" / ", EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform).Select(g => g.Display(platform)));
            Assert.Equal(expected, QuickTagKeys.SuggestionGesture(index, platform));
        }
        string command = QuickTagCommands.Suggestion(1);
        ShortcutService.Publish(new ShortcutBindingsSection().Set(command, [ShortcutGesture.Parse("Primary+F9")]));
        Assert.Equal(ShortcutGesture.Parse("Primary+F9").Display(platform), QuickTagKeys.SuggestionGesture(0, platform));
        ShortcutService.Publish(new ShortcutBindingsSection().Set(command, []));
        Assert.Null(QuickTagKeys.SuggestionGesture(0, platform));
    });

    [Fact]
    public void RatingGroupAndStarsExposeAllEffectiveRatingBindings() => WithControls(() =>
    {
        ShortcutService.Publish(new ShortcutBindingsSection().Set("media.rating.set-3", [ShortcutGesture.Parse("Alt+F9")]));
        GalleryDecisionRow row = new("DiscoveryRating") { KeyContext = "reader" };
        string help = AutomationProperties.GetHelpText(Field<Control>(row, "_group"));
        foreach (string id in Enumerable.Range(1, 5).Select(n => "media.rating.set-" + n).Append("media.rating.clear"))
            foreach (var gesture in ShortcutService.Current.For(id))
                Assert.Contains(gesture.Display(OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows), help);
        foreach (var star in row.StarButtons)
            Assert.Equal(ToolTip.GetTip(star), AutomationProperties.GetHelpText(star));
    });

    private static void AssertHelp(Control control, string context, params string[] commands)
    {
        string tip = Assert.IsType<string>(ToolTip.GetTip(control));
        Assert.Equal(tip, AutomationProperties.GetHelpText(control));
        foreach (string command in commands)
        {
            var gestures = ShortcutService.Current.For(command);
            Assert.NotEmpty(gestures);
            foreach (var gesture in gestures)
            {
                string key = gesture.Display(OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows);
                if (ShortcutService.Current.YieldedContexts(command, gesture).Contains(context, StringComparer.Ordinal))
                    Assert.DoesNotContain(key, tip);
                else Assert.Contains(key, tip);
            }
        }
    }

    private static void Refresh(GalleryVideoPlayer player) => typeof(GalleryVideoPlayer)
        .GetMethod("RefreshTransportPresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, null);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void WithControls(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        Texts.Initialize(); Texts.SetLanguage("en");
        var previous = ShortcutService.CurrentSection;
        try { test(); }
        finally { ShortcutService.Publish(previous); }
    });
}
