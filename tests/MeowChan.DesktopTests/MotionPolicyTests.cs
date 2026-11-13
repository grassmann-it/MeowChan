using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

// D1: one motion policy. Environment > setting > operating system; the reader's two environment names keep working; the setting
// persists like the other interface settings; the transition styles only exist below a TopLevel without motion-off.
public sealed class MotionPolicyTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    private static Func<string, string?> Env(string? motion = null, string? reduced = null) => name => name switch
    {
        "MEOWCHAN_MOTION" => motion,
        "MEOWCHAN_REDUCED_MOTION" => reduced,
        _ => null
    };

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("off", null, true)]
    [InlineData("OFF", null, true)]
    [InlineData("on", null, false)]
    [InlineData(null, "1", true)]
    [InlineData(null, "true", true)]
    [InlineData(null, "yes", true)]
    [InlineData(null, "0", false)]
    [InlineData(null, "false", false)]
    [InlineData("on", "1", true)] // off wins when both are set
    [InlineData("junk", "junk", null)]
    public void TheEnvironmentSaysOffOnOrNothing(string? motion, string? reduced, bool? expected) =>
        Assert.Equal(expected, MotionPolicy.FromEnvironment(Env(motion, reduced)));

    [Theory]
    // environment, setting, OS signal -> off?
    [InlineData(null, "System", false, false)]
    [InlineData(null, "System", true, true)]
    [InlineData(null, "On", true, false)]   // the setting beats the OS signal
    [InlineData(null, "Off", false, true)]
    [InlineData(true, "On", false, true)]   // the environment beats the setting
    [InlineData(false, "Off", true, false)]
    [InlineData(false, "System", true, false)]
    [InlineData(null, "garbage", true, true)] // unknown setting is System
    public void EffectiveIsEnvironmentThenSettingThenOs(bool? environment, string setting, bool os, bool expectedOff) =>
        Assert.Equal(expectedOff, MotionPolicy.Resolve(environment, setting, os));

    [Theory]
    [InlineData(null, "System"), InlineData("", "System"), InlineData("system", "System"), InlineData(" off ", "Off"), InlineData("ON", "On"), InlineData("x", "System")]
    public void SettingNormalisesToSystemOnOrOff(string? value, string expected) => Assert.Equal(expected, MotionPolicy.NormalizeSetting(value));

    [Fact]
    public void ReaderMotionIsTheSamePolicyAndKeepsBothEnvironmentNames()
    {
        Assert.True(ReaderMotion.IsReduced(Env(motion: "off")));
        Assert.True(ReaderMotion.IsReduced(Env(reduced: "1")));
        Assert.False(ReaderMotion.IsReduced(Env()));
        Assert.False(ReaderMotion.IsReduced(Env(motion: "on")));
        Assert.Equal(400, ReaderMotion.Highlight.TotalMilliseconds);
        string reader = File.ReadAllText(Root("Desktop/ReaderMotion.cs"));
        Assert.Contains("MotionPolicy.IsOff", reader);
        Assert.DoesNotContain("GetEnvironmentVariable", reader);
    }

    [Fact]
    public void ApplyPutsAndRemovesTheClassFollowingTheLivePolicy()
    {
        string? saved = Environment.GetEnvironmentVariable("MEOWCHAN_MOTION");
        string savedSetting = MotionPolicy.Setting;
        try
        {
            Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", null);
            MotionPolicy.SetSetting(MotionPolicy.On);
            Border element = new();
            MotionPolicy.Apply(element);
            Assert.DoesNotContain(MotionPolicy.OffClass, element.Classes);
            Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", "off");
            MotionPolicy.Apply(element);
            Assert.Contains(MotionPolicy.OffClass, element.Classes);
            Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", "on");
            MotionPolicy.SetSetting(MotionPolicy.Off);
            MotionPolicy.Apply(element);
            Assert.DoesNotContain(MotionPolicy.OffClass, element.Classes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", saved);
            MotionPolicy.SetSetting(savedSetting);
        }
    }

    [Fact]
    public void AnimationsSettingDefaultsToSystemAndRoundTripsThroughCloneAndCopy()
    {
        Config config = new();
        Assert.Equal("System", config.Animations);
        config.Animations = "off";
        Assert.Equal("Off", config.Animations);
        Assert.Equal("Off", config.Clone().Animations);
        Config copy = new();
        copy.CopyFrom(config);
        Assert.Equal("Off", copy.Animations);
        Config settings = new();
        settings.CopySettingsFieldsFrom(config);
        Assert.Equal("Off", settings.Animations);
        config.Animations = "nonsense";
        Assert.Equal("System", config.Animations);
    }

    [Fact]
    public void AnimationsSettingIsPersistedInTheConfigFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "meowchan-motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            new Config { Animations = "On" }.SaveConfig(directory);
            string json = File.ReadAllText(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).First(path => File.ReadAllText(path).Contains("\"Animations\"")));
            Assert.Contains("\"Animations\": \"On\"", json.Replace("\":\"", "\": \""));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SettingsPageHasAnAnimationsRowWithSystemOnOffAndALabelledInput()
    {
        string axaml = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.axaml"));
        Assert.Contains("x:Name=\"AnimationsInput\"", axaml);
        Assert.Contains("AutomationProperties.LabeledBy=\"{Binding ElementName=AnimationsFieldLabel}\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedAnimations, Mode=TwoWay}\"", axaml);
        string code = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.axaml.cs"));
        Assert.Contains("candidate.Animations = SelectedAnimations?.Value ?? _config.Animations;", code);
        foreach (string key in new[] { "settings.animations", "settings.animations.hint", "settings.animations.system", "settings.animations.on", "settings.animations.off" })
            Assert.Contains("(\"" + key + "\"", File.ReadAllText(Root("Desktop/Localization/R08dSettingsCatalog.cs")));
    }

    [Fact]
    public void EveryTransitionStyleIsScopedBelowATopLevelWithoutMotionOff()
    {
        XElement[] styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style").ToArray();
        XElement[] motion = styles.Where(style => style.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "Transitions")
            || style.Element(A + "Style.Animations") != null).ToArray();
        Assert.NotEmpty(motion);
        foreach (XElement style in motion)
        {
            string selector = (string)style.Attribute("Selector")!;
            if (style.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "Transitions" && (string?)setter.Attribute("Value") == "{x:Null}"))
                Assert.StartsWith(":is(TopLevel).motion-off ", selector);
            else
                Assert.StartsWith(":is(TopLevel):not(.motion-off) ", selector);
        }
    }

    [Fact]
    public void MotionBudgetOnlyNamesAllowedPropertiesAndDurations()
    {
        XElement root = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        string[] allowed = { "Background", "BorderBrush", "Foreground", "Opacity" };
        foreach (XElement transition in root.Elements(A + "Style").SelectMany(style => style.Descendants())
                     .Where(element => element.Name.LocalName.EndsWith("Transition", StringComparison.Ordinal)))
        {
            Assert.Contains((string)transition.Attribute("Property")!, allowed);
            TimeSpan duration = TimeSpan.Parse((string)transition.Attribute("Duration")!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(duration <= TimeSpan.FromMilliseconds(260), "A transition is longer than 260 ms.");
        }
        foreach (XElement animation in root.Descendants(A + "Animation"))
            Assert.True(TimeSpan.Parse((string)animation.Attribute("Duration")!, System.Globalization.CultureInfo.InvariantCulture) <= TimeSpan.FromMilliseconds(260));
        // No transition or animation on size, margin, radius or shadow.
        string text = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        foreach (string forbidden in new[] { "Property=\"Width\" Duration", "Property=\"Height\" Duration", "Property=\"Margin\" Duration", "Property=\"CornerRadius\" Duration", "BoxShadowsTransition" })
            Assert.DoesNotContain(forbidden, text);
    }

    [Fact]
    public void TheToastCarriesItsMotionClassAndTheInstallHookIsWired()
    {
        Assert.Contains("Classes.Add(\"toast-content\")", File.ReadAllText(Root("Desktop/Shell/MainWindow.Notifications.cs")));
        Assert.Contains("MotionPolicy.Install()", File.ReadAllText(Root("App.axaml.cs")));
        string main = File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml.cs"));
        Assert.Contains("MotionPolicy.SetSetting(_config.Animations)", main);
        Assert.Contains("ImageFade.Show(ArchivePreviewImage, bitmap, arrivedAsync: true)", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml.cs")));
    }

    [Fact]
    public void ThumbnailTilesDoNotFadeBecauseNoCacheHitSignalExists()
    {
        foreach (string tile in new[] { "Desktop/MediaLibraryTileView.cs", "Desktop/ArchiveMediaTileView.cs" })
            Assert.DoesNotContain("ImageFade", File.ReadAllText(Root(tile)));
    }

    [Theory]
    [InlineData("[KDE]\nAnimationDurationFactor=0\n", true)]
    [InlineData("[KDE]\nAnimationDurationFactor=0.5\n", false)]
    [InlineData("[General]\nAnimationDurationFactor=0\n[KDE]\nSingleClick=false\n", null)]
    [InlineData("", null)]
    public void KdeInstantAnimationsMeanReducedMotion(string content, bool? expected) =>
        Assert.Equal(expected, OsMotionSignal.ParseKdeGlobals(content.Split('\n')));

    [Fact]
    public void PortalValueIsAnimationsEnabledSoReducedIsItsNegation()
    {
        Assert.True(OsMotionSignal.ReducedFromEnableAnimations(false));
        Assert.False(OsMotionSignal.ReducedFromEnableAnimations(true));
    }

    [Theory]
    [InlineData("Dark"), InlineData("Light")]
    public void ImageFadeOnlyRunsForVisibleAsynchronousArrivalsWithMotionOn(string _)
    {
        Assert.True(ImageFade.ShouldFade(true, true, true, false));
        Assert.False(ImageFade.ShouldFade(false, true, true, false));
        Assert.False(ImageFade.ShouldFade(true, true, true, true));
        Assert.False(ImageFade.ShouldFade(true, false, true, false));
        Assert.False(ImageFade.ShouldFade(true, true, false, false));
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
