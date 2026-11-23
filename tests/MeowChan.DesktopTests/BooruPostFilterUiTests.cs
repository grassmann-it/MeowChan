using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class BooruPostFilterUiTests
{
    [Fact]
    public void ControlsFilterLoadedCardsRestoreTabsAndLocalizeWithoutRequests() => BooruComponentDispatcher.Run(() =>
    {
        BooruUiHarness h = new();
        h.Serve(perPage: 3);
        using BooruWorkbenchModel model = new(h.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false) { PostFilterScoreDefault = () => 25 };
        model.Search.SetInput("cat");
        model.Search.SubmitAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("3 of 3 visible", Field<TextBlock>(view, "_filterCount").Text);
        CheckBox enabled = Field<CheckBox>(view, "_filterScoreEnabled");
        enabled.IsChecked = true;
        enabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(25, model.Search.View.PostFilter.MinimumScore);
        Assert.Equal("0 of 3 visible", Field<TextBlock>(view, "_filterCount").Text);
        Assert.True(Field<TextBlock>(view, "_filterEmpty").IsVisible);
        Assert.Empty(Field<StackPanel>(view, "_results").Children);
        int tab = view.SearchTabs.Add(false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(enabled.IsChecked);
        view.SearchTabs.Select(0);
        Dispatcher.UIThread.RunJobs();
        Assert.True(enabled.IsChecked);
        Texts.SetLanguage("de");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("0 von 3 sichtbar", Field<TextBlock>(view, "_filterCount").Text);
        Field<Button>(view, "_filterReset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, Field<StackPanel>(view, "_results").Children.Count);
        Assert.Equal("3 von 3 sichtbar", Field<TextBlock>(view, "_filterCount").Text);
        Assert.False(Field<TextBlock>(view, "_filterEmpty").IsVisible);
        ComboBox ratings = Field<ComboBox>(view, "_filterRating");
        ratings.SelectedIndex = 1; // Unknown, unlike the fixture's General rating.
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(model.Search.View.VisibleRows);
        Field<Button>(view, "_filterReset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        CheckBox cat = Field<Dictionary<string, CheckBox>>(view, "_filterTagChecks")["cat"];
        cat.IsChecked = false;
        cat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(model.Search.View.VisibleRows);
        cat.IsChecked = null;
        cat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, model.Search.View.VisibleRows.Length);
        Assert.Equal(1, h.ProviderRequests);
        view.SearchTabs.Close(tab);
    });

    [Fact]
    public void SettingsResetRestoresTheThresholdAndUsesTheValidatedDraft() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        SettingsEditor editor = new(new Config { BooruPostFilterScore = 77 });
        try
        {
            Assert.Equal(77m, editor.BooruPostFilterScore);
            editor.BooruPostFilterScore = -12;
            Config candidate = new();
            typeof(SettingsEditor).GetMethod("ApplyBooruPostFilterDraft", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [candidate]);
            Assert.Equal(-12, candidate.BooruPostFilterScore);
            editor.ResetDraft(SettingsEditor.SettingsResetScope.WorkerBudgets);
            Assert.Equal((decimal)Config.BooruPostFilterScoreDefault, editor.BooruPostFilterScore);
        }
        finally { editor.EndLifetime(); }
    });

    private static T Field<T>(BooruWorkbenchView view, string name) =>
        (T)typeof(BooruWorkbenchView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
}
