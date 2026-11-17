using System.Reflection;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>Exercise the editor's deferred notifications and restoration, not just the expander ledger.</summary>
[Collection("Gallery viewer construction")]
public sealed class SettingsSearchRestoreTests
{
    private static void Notify(SettingsEditor editor, RoutedEvent routedEvent, Control source) =>
        typeof(SettingsEditor).GetMethod("OnSettingValueChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [null, new RoutedEventArgs(routedEvent, source)]);

    private static void WithEditor(Action<SettingsEditor> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        var editor = new SettingsEditor(new Config());
        var box = (SearchBox)editor.FindControl<ContentControl>("SettingsSearchHost")!.Content!;
        box.Debounce = TimeSpan.Zero; // No wall-clock timer in this fixture.
        try { test(editor); }
        finally { box.FlushQuery(); editor.EndLifetime(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredNumericTemplateTextDoesNotTurnAJumpIntoAnEdit(bool endVisit)
    {
        WithEditor(editor =>
        {
            var advanced = editor.FindControl<Expander>("SettingsWorkerBudgetsHeading")!;
            var number = editor.FindControl<NumericUpDown>("ProductionWorkersInput")!;
            var box = (SearchBox)editor.FindControl<ContentControl>("SettingsSearchHost")!.Content!;
            Assert.False(advanced.IsExpanded);
            Assert.NotNull(editor.ProbeSearchJump("production workers"));
            Assert.True(advanced.IsExpanded);

            // A collapsed section materializes its numeric template after the jump.
            // Its initial formatted text raises a deferred TextChanged, not a user edit.
            TextBox text = null!;
            number.Template = new FuncControlTemplate<NumericUpDown>((_, scope) =>
            {
                text = new TextBox { Name = "PART_TextBox", Text = number.Value?.ToString() };
                scope.Register(text.Name, text);
                return text;
            });
            number.ApplyTemplate();
            Assert.Same(number, text.TemplatedParent);
            // A window-free editor has no visual route up to the page: deliver the
            // notification to the same handler explicitly, after the jump guard ended.
            Notify(editor, TextBox.TextChangedEvent, text);
            Dispatcher.UIThread.RunJobs();

            if (endVisit) editor.EndSearchVisit();
            else { box.Text = string.Empty; box.FlushQuery(); }
            Assert.False(advanced.IsExpanded);
            Dispatcher.UIThread.RunJobs();
            Assert.False(advanced.IsExpanded);
        });
    }

    [Fact]
    public void RepeatedJumpsAndClearsRestoreOnlySearchOpenedGroups()
    {
        WithEditor(editor =>
        {
            var advanced = editor.FindControl<Expander>("SettingsWorkerBudgetsHeading")!;
            var box = (SearchBox)editor.FindControl<ContentControl>("SettingsSearchHost")!.Content!;
            foreach (bool userOpened in new[] { false, true, false })
            {
                advanced.IsExpanded = userOpened;
                for (int jump = 0; jump < 3; jump++)
                    Assert.NotNull(editor.ProbeSearchJump("production workers"));
                box.Text = string.Empty;
                box.FlushQuery();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(userOpened, advanced.IsExpanded);
            }
        });
    }

    [Fact]
    public void ClosingTheEditorRestoresSearchOpenedGroups()
    {
        WithEditor(editor =>
        {
            var advanced = editor.FindControl<Expander>("SettingsWorkerBudgetsHeading")!;
            Assert.NotNull(editor.ProbeSearchJump("production workers"));
            Assert.True(advanced.IsExpanded);
            editor.EndLifetime();
            Assert.False(advanced.IsExpanded);
        });
    }

    [Fact]
    public void ARealNumericValueEditKeepsItsGroupOpen()
    {
        WithEditor(editor =>
        {
            var advanced = editor.FindControl<Expander>("SettingsWorkerBudgetsHeading")!;
            var number = editor.FindControl<NumericUpDown>("ProductionWorkersInput")!;
            Assert.NotNull(editor.ProbeSearchJump("production workers"));
            number.Value += 1;
            Notify(editor, NumericUpDown.ValueChangedEvent, number);
            editor.EndSearchVisit();
            Assert.True(advanced.IsExpanded);
        });
    }
}
