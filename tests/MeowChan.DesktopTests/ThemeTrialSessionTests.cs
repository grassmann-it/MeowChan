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

    // ---- M4: applies are coalesced while drafts keep changing ----

    // A scheduler by hand: Fire() ends the current interval, like the dialog's timer would after 120 ms.
    private sealed class Clock
    {
        private Action? _callback;
        public int Armed { get; private set; }
        public int Cancelled { get; private set; }
        public bool IsArmed => _callback != null;
        public Action Schedule(Action callback)
        {
            Armed++;
            _callback = callback;
            return () => { if (_callback == callback) { _callback = null; Cancelled++; } };
        }
        public void Fire() { Action? callback = _callback; _callback = null; callback?.Invoke(); }
    }

    private static (ThemeTrialSession Session, List<ThemeColorOverrides> Live, Clock Clock) Coalescing(ThemeConfiguratorModel model)
    {
        List<ThemeColorOverrides> live = [];
        Clock clock = new();
        return (new ThemeTrialSession(model, live.Add, clock.Schedule), live, clock);
    }

    [Fact]
    public void ADragThatChangesTheDraftManyTimesAppliesOncePerIntervalAndTheLastDraftArrives()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live, Clock clock) = Coalescing(model);
        session.SetOn(true);
        Assert.Single(live); // switching on shows the draft at once
        clock.Fire();
        // The first change after a quiet interval applies at once.
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.DraftChanged();
        Assert.Equal(2, live.Count);
        // Forty slider ticks inside the interval apply nothing yet.
        for (int tick = 0; tick < 40; tick++)
        {
            model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C(tick % 2 == 0 ? "#1D5E3A" : "#1E5F3B"));
            session.DraftChanged();
        }
        Assert.Equal(2, live.Count);
        Assert.True(session.HasPending);
        ThemeColorOverrides newest = model.Draft;
        clock.Fire(); // the interval ends: one apply with the newest draft
        Assert.Equal(3, live.Count);
        Assert.Same(newest, live[^1]);
        Assert.False(session.HasPending);
        clock.Fire(); // a quiet interval: nothing more
        Assert.Equal(3, live.Count);
        Assert.False(clock.IsArmed);
    }

    [Fact]
    public void AChangeAfterAQuietIntervalAppliesImmediatelyAgain()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live, Clock clock) = Coalescing(model);
        session.SetOn(true);
        clock.Fire();
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#6DC793"));
        session.DraftChanged();
        clock.Fire();
        Assert.False(clock.IsArmed);
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#70C896"));
        session.DraftChanged();
        Assert.Same(model.Draft, live[^1]);
        Assert.True(clock.IsArmed);
    }

    [Fact]
    public void SwitchingOffAndClosingDropWhatWaitsAndPutTheSavedColoursBackAtOnce()
    {
        ThemeColorOverrides saved = ThemeColorOverrides.Empty.WithRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#6DC793"));
        ThemeConfiguratorModel model = Model(saved);
        (ThemeTrialSession session, List<ThemeColorOverrides> live, Clock clock) = Coalescing(model);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.SetOn(true);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1D5E3A"));
        session.DraftChanged();
        Assert.True(session.HasPending);
        session.SetOn(false);
        Assert.Same(saved, live[^1]); // restored without waiting for the interval
        Assert.False(session.HasPending);
        Assert.False(clock.IsArmed);
        int calls = live.Count;
        clock.Fire();
        Assert.Equal(calls, live.Count); // the dropped change never arrives later

        session.SetOn(true);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1E5F3B"));
        session.DraftChanged();
        session.Close(); // Cancel, Escape, the cross and the owner closing
        Assert.Same(saved, live[^1]);
        Assert.False(clock.IsArmed);
        calls = live.Count;
        clock.Fire();
        Assert.Equal(calls, live.Count);
        session.DraftChanged(); // a change after closing does nothing
        Assert.Equal(calls, live.Count);
        Assert.False(clock.IsArmed);
    }

    [Fact]
    public void ARefusedNewestDraftWaitingAtTheEndOfTheIntervalLeavesTheLastAcceptedOneOnTheWindow()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live, Clock clock) = Coalescing(model);
        session.SetOn(true);
        clock.Fire();
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.DraftChanged();
        ThemeColorOverrides accepted = model.Draft;
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Text, WeakText(model));
        Assert.NotNull(model.Block);
        session.DraftChanged();
        clock.Fire();
        Assert.Same(accepted, live[^1]);
        Assert.DoesNotContain(live, shown => shown.Equals(model.Draft));
        Assert.False(clock.IsArmed);
    }

    [Fact]
    public void ApplyAfterACoalescedChangeLeavesTheWindowToTheCommitAndNothingArrivesLater()
    {
        ThemeConfiguratorModel model = Model();
        (ThemeTrialSession session, List<ThemeColorOverrides> live, Clock clock) = Coalescing(model);
        session.SetOn(true);
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        session.DraftChanged(); // waits: the interval of switching on is still running
        ThemeColorOverrides applied = model.Draft;
        model.MarkSaved(applied);
        session.DraftChanged();
        session.Close();
        // The waiting change is dropped: the commit of Apply puts the saved colours on the window itself (ThemeRuntime.ApplyOverrides), the trial adds nothing.
        Assert.DoesNotContain(live, shown => shown.Equals(applied));
        Assert.False(clock.IsArmed);
        int calls = live.Count;
        clock.Fire();
        Assert.Equal(calls, live.Count);
    }

    [Fact]
    public void TheDialogCoalescesWithATimerOfTheSessionsIntervalAndTheIntervalIsAFewHundredthsOfASecond()
    {
        string dialog = File.ReadAllText(Root("Desktop/Views/Settings/ThemeColorsDialog.cs"));
        Assert.Contains("new ThemeTrialSession(model, applyLive, ScheduleTrialApply)", dialog);
        Assert.Contains("DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(ThemeTrialSession.CoalesceMilliseconds) };", dialog);
        Assert.Contains("return timer.Stop;", dialog);
        Assert.InRange(ThemeTrialSession.CoalesceMilliseconds, 100, 150);
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
