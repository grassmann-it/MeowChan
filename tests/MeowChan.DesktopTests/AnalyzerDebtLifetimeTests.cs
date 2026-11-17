using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class AnalyzerDebtLifetimeTests
{
    [Theory]
    [InlineData("archive")]
    [InlineData("media")]
    [InlineData("rules")]
    public void EndedPageNoLongerInstallsAKeyContext(string kind) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        Control page;
        Action end;
        if (kind == "archive")
        {
            ArchivePage archive = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
            page = archive; end = archive.EndLifetime;
        }
        else if (kind == "rules")
        {
            RulesPage rules = new(); page = rules; end = rules.EndLifetime;
        }
        else
        {
            MediaPage media = new(_ => Task.FromResult<ArchiveReader>(null!)); page = media; end = media.EndLifetime;
        }
        try
        {
            KeyEventArgs before = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.None };
            page.RaiseEvent(before);
            Assert.Equal(kind, KeyContextHost.ContextOf(before));
            end(); end();
            KeyEventArgs after = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.None };
            page.RaiseEvent(after);
            Assert.Null(KeyContextHost.ContextOf(after));
        }
        finally { end(); }
    });

    [Fact]
    public async Task UpdateFixtureDisposesTheServiceItCreates()
    {
        UpdateService service;
        using (UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("1.0.0")))
            service = fixture.Service;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetStateAsync());
    }

    [Fact]
    public void CleanupWatchHasAnExplicitTerminalLifetime()
    {
        object watch = new VideoCleanupWatch("test", schedule: false);
        IDisposable owner = Assert.IsAssignableFrom<IDisposable>(watch);
        using (owner)
        {
            var typed = (VideoCleanupWatch)watch;
            Assert.True(typed.Begin(1));
            owner.Dispose(); owner.Dispose();
            Assert.False(typed.Evidence.Pending);
            Assert.False(typed.Begin(2));
        }
    }
}
