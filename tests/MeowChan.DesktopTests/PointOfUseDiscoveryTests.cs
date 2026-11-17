using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
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
