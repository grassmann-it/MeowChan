using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// TH5: "Try in window" of the colour dialog. The session decides what reaches the live palette; the dialog only forwards the switch, the
// model's changes and its closing (source contract below), and the application of the draft is ThemeRuntime.ApplyOverrides, the call a saved
// change makes. A fake live palette records every call, so the rules are tested without a window.
public sealed class ThemeTrialSessionTests
{
    private static Color C(string hex) => ThemeCustomization.ParseHex(hex);
    private static ThemeConfiguratorModel Model(ThemeColorOverrides? saved = null) => new(saved ?? ThemeColorOverrides.Empty, ThemePaletteTests.Base);
    private static Color WeakText(ThemeConfiguratorModel model) => model.Preview(ThemeTokenTheme.Dark).Palette.Colors["Workspace.Panel"];

    private static (ThemeTrialSession Session, List<ThemeColorOverrides> Live) Session(ThemeConfiguratorModel model)
    {
        List<ThemeColorOverrides> live = [];
        return (new ThemeTrialSession(model, live.Add), live);
    }

    [Fact]
    public void NothingReachesTheWindowWhileTheSwitchIsOff()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live) = Session(model);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.DraftChanged();
        session.Close();
        Assert.Empty(live);
        Assert.False(session.IsOn || session.IsShowing);
    }

    [Fact]
    public void SwitchingOnShowsTheDraftAndEveryAcceptedChangeFollows()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live) = Session(model);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.SetOn(true);
        Assert.Same(model.Draft, Assert.Single(live));
        Assert.True(session.IsShowing);
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#6DC793"));
        session.DraftChanged();
        Assert.Equal(2, live.Count);
        Assert.Same(model.Draft, live[^1]);
    }

    [Fact]
    public void ARefusedDraftIsNeverShownAndTheWindowKeepsTheLastAcceptedOne()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live) = Session(model);
        session.SetOn(true);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.DraftChanged();
        ThemeColorOverrides accepted = model.Draft;
        // Text as light as its panel: below 4.5 : 1, so the gate refuses it and the runtime must not see it.
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Text, WeakText(model));
        Assert.NotNull(model.Block);
        session.DraftChanged();
        Assert.DoesNotContain(live, shown => shown.Equals(model.Draft));
        Assert.Same(accepted, live[^1]);
        // Dropped back below the gate it follows again.
        model.ResetRole(ThemeTokenTheme.Dark, ThemeUserColors.Text);
        session.DraftChanged();
        Assert.Same(model.Draft, live[^1]);
    }

    [Fact]
    public void SwitchingOffAndClosingPutTheSavedColoursBackOnlyWhenADraftWasShown()
    {
        ThemeColorOverrides saved = ThemeColorOverrides.Empty.WithRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#6DC793"));
        ThemeConfiguratorModel model = Model(saved);
        (ThemeTrialSession session, List<ThemeColorOverrides> live) = Session(model);
        session.SetOn(true); // draft == saved: shown, but nothing differs
        Assert.False(session.IsShowing);
        session.SetOn(false);
        Assert.Single(live); // no restore needed: nothing but the saved colours was ever on the window
        model.ResetAll();
        session.SetOn(true);
        Assert.True(session.IsShowing);
        session.SetOn(false);
        Assert.Same(saved, live[^1]); // the switch off restores
        Assert.False(session.IsShowing);
        session.SetOn(true);
        session.Close(); // Cancel, Escape, the cross and the owner closing
        Assert.Same(saved, live[^1]);
        Assert.False(session.IsOn || session.IsShowing);
        int calls = live.Count;
        session.Close();
        Assert.Equal(calls, live.Count); // a second close does nothing
    }

    [Fact]
    public void AfterApplyTheSavedColoursAreTheDraftSoClosingChangesNothing()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live) = Session(model);
        session.SetOn(true);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.DraftChanged();
        ThemeColorOverrides applied = model.Draft;
        model.MarkSaved(applied); // the dialog does this after the commit
        session.DraftChanged();
        Assert.False(session.IsShowing);
        session.Close();
        Assert.Same(applied, live[^1]); // the last thing on the window is what was saved
    }

    [Fact]
    public void TheDialogForwardsTheSwitchTheDraftAndEveryWayOfClosingAndTheEditorUsesTheRuntime()
    {
        string dialog = File.ReadAllText(Root("Desktop/Views/Settings/ThemeColorsDialog.cs"));
        Assert.Contains("_try.IsCheckedChanged += (_, _) => _trial?.SetOn(_try.IsChecked == true);", dialog);
        Assert.Contains("_trial?.DraftChanged();", dialog);
        Assert.Contains("_trial?.Close();", dialog); // the Closed event covers Cancel, Escape, the cross and the owner closing
        Assert.Contains("HelpKey = \"help.themeColors\"", dialog);
        Assert.Contains("_try.IsVisible = _trial != null;", dialog);
        string editor = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.ThemeColors.cs"));
        Assert.Contains("overrides => ThemeRuntime.ApplyOverrides(overrides)", editor);
    }

    private static string Root(string path, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
