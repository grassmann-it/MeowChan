using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class TaglineIntervalSaveTests
{
    [Fact]
    public void EmptyMinutesBlockIntervalModeAndPreserveSavedMinutesWhenSwitchedOff() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory temp = new();
        Config config = new() { DownloadPath = temp.Path, HeaderTagline = TaglinePicker.ModeInterval, HeaderTaglineIntervalMinutes = 17 };
        Config? saved = null;
        SettingsEditor editor = new(config, candidate => { saved = candidate; return Task.CompletedTask; });
        Window window = new() { Content = editor, Width = 1000, Height = 800 };
        window.Show();
        try
        {
            NumericUpDown minutes = editor.FindControl<NumericUpDown>("HeaderTaglineIntervalMinutesInput")!;
            Assert.True(editor.RevealSetting(nameof(Config.HeaderTaglineIntervalMinutes)));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(minutes.IsEffectivelyVisible);
            Assert.True(minutes.Bounds.Width > 0 && minutes.Bounds.Height > 0);
            Assert.Equal((1m, 1440m, 17m), (minutes.Minimum, minutes.Maximum, minutes.Value!.Value));
            editor.ResetDraft(SettingsEditor.SettingsResetScope.Interface);
            Assert.Equal(5m, minutes.Value);
            Assert.Equal(TaglinePicker.ModePerStart, editor.SelectedHeaderTagline!.Value);
            editor.SelectedHeaderTagline = editor.HeaderTaglineChoices.Single(choice => choice.Value == TaglinePicker.ModeInterval);

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
        finally { window.Close(); editor.EndLifetime(); }
    });
}
