using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class TaglineIntervalSaveTests
{
    [Fact]
    public void EmptyMinutesBlockIntervalModeAndPreserveSavedMinutesWhenSwitchedOff() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        using TempDirectory temp = new();
        Config config = new() { DownloadPath = temp.Path, HeaderTagline = TaglinePicker.ModeInterval, HeaderTaglineIntervalMinutes = 17 };
        Config? saved = null;
        SettingsEditor editor = new(config, candidate => { saved = candidate; return Task.CompletedTask; });
        try
        {
            NumericUpDown minutes = editor.FindControl<NumericUpDown>("HeaderTaglineIntervalMinutesInput")!;
            Button save = editor.FindControl<Button>("SaveSettingsButton")!;
            minutes.Value = null;
            minutes.Text = string.Empty;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(saved);
            Assert.True(editor.FindControl<TextBlock>("HeaderTaglineIntervalMinutesInputError")!.IsVisible);

            editor.SelectedHeaderTagline = editor.HeaderTaglineChoices.Single(choice => choice.Value == TaglinePicker.ModeOff);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(saved != null, editor.ErrorMessage);
            Assert.Equal(TaglinePicker.ModeOff, saved.HeaderTagline);
            Assert.Equal(17, saved.HeaderTaglineIntervalMinutes);
        }
        finally { editor.EndLifetime(); }
    });
}
