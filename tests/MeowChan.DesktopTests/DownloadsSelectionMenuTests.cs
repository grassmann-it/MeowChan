using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Diagnostics;
using MeowChan.Persistence;
using static MeowChan.DesktopTests.DownloadWindowFixture;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DownloadsSelectionMenuTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothGesturesShowTheExecutableSubsetOfTheSelection(bool keyboard) => Run(fixture =>
    {
        foreach ((int eligible, int selected) in new[] { (0, 9), (1, 9), (9, 9), (1, 1) })
        {
            long[] ids = Enumerable.Range(1, selected).Select(id => (long)id).ToArray();
            fixture.ShowThreads(ids.Select(id => Thread(id, status: id <= eligible
                ? MediaWorkStatus.NeedsIntervention : MediaWorkStatus.Excluded)).ToArray(), ids.Take(eligible).ToArray());
            fixture.Select(ids);
            ContextMenu menu = OpenMenu(fixture, keyboard);
            foreach (string command in new[] { "watch.check", "watch.retry-media" })
            {
                MenuItem item = FindItem(menu, command);
                string label = CommandCatalog.Label(command);
                if (selected > 1) label += $" ({eligible} of {selected})";
                Assert.Equal(label, item.Header);
                Assert.Equal(label, AutomationProperties.GetName(item));
                Assert.Equal(eligible > 0, item.Command!.CanExecute(null));
                if (eligible == 0) Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(item)));
            }
            menu.Close();
            fixture.Layout();
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ActivationSkipsRemovedAndNewlyIneligibleTargetsAndNeverAddsNewOnes(bool keyboard, bool retry) => Run(async fixture =>
    {
        long[] ids = Enumerable.Range(1, 10).Select(id => (long)id).ToArray();
        fixture.ShowThreads(ids.Select(id => Thread(id, status: id <= 3
            ? MediaWorkStatus.NeedsIntervention : MediaWorkStatus.Excluded)).ToArray(), 1, 2, 3);
        fixture.Select(ids[..9]);
        MenuItem action = FindItem(OpenMenu(fixture, keyboard), retry ? "watch.retry-media" : "watch.check");
        Assert.EndsWith("(3 of 9)", (string)action.Header!);

        // Remove the menu lead, revoke another target's eligibility, and make two other rows
        // eligible, including one that was outside the selection when the menu opened.
        fixture.ShowThreads(ids[1..].Select(id => Thread(id, status: id is 2 or 4 or 10
            ? MediaWorkStatus.NeedsIntervention : MediaWorkStatus.Excluded)).ToArray(), 2, 4, 10);
        fixture.Select(ids[1..]);

        using PerformanceDiagnostics.DiagnosticRun trace = PerformanceDiagnostics.BeginRun("downloads-menu", "fixture", "offline-rows");
        Assert.True(action.Command!.CanExecute(null));
        action.Command.Execute(null);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (fixture.WindowField<bool>("_selectionCommandRunning")) await Task.Delay(10, deadline.Token);
        string?[] accepted = trace.Snapshot().Events
            .Where(entry => entry.Phase == "host.command.admission" && entry.Outcome == "accepted")
            .Select(entry => entry.Watch).ToArray();
        string expected = retry ? "2" : new DiagnosticTarget(DiagnosticTargetKind.Thread, 2).ToString();
        Assert.Equal(new[] { expected }, accepted);
    });

    [Fact]
    public void TheOpenMenuRelocalizesTheFrozenCount() => Run(fixture =>
    {
        fixture.ShowThreads([Thread(1), Thread(2)], 1);
        fixture.Select(1, 2);
        ContextMenu menu = OpenMenu(fixture, keyboard: true);
        MenuItem check = FindItem(menu, "watch.check");
        Assert.Equal("Check now (1 of 2)", check.Header);
        Texts.SetLanguage("de");
        fixture.Layout();
        Assert.Equal("Jetzt prüfen (1 von 2)", check.Header);
        menu.Close();
        return Task.CompletedTask;
    });

    private static ContextMenu OpenMenu(DownloadWindowFixture fixture, bool keyboard)
    {
        MainWindow window = fixture.Window;
        DataGrid grid = fixture.Threads;
        Assert.True(grid.Focus());
        if (keyboard)
        {
            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        }
        else
        {
            object item = grid.SelectedItems[0]!;
            DataGridRow row = grid.GetVisualDescendants().OfType<DataGridRow>()
                .First(candidate => ReferenceEquals(candidate.DataContext, item) && candidate.IsEffectivelyVisible);
            Point point = row.TranslatePoint(new Point(32, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
        }
        fixture.Layout();
        ContextMenu menu = grid.ContextMenu!;
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem FindItem(ContextMenu menu, string command) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, command));
}
