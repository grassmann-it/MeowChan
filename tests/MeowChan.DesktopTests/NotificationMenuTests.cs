using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The context menu of a toast and of the shell banner.</summary>
[Collection("Localization")]
public sealed class NotificationMenuTests : IDisposable
{
    public NotificationMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void ANotificationCopiesDismissesAndClosesAll()
    {
        List<string> log = [];
        ContextMenuModel model = NotificationMenu.Compose("Download failed", () => log.Add("copy"), () => log.Add("dismiss"), () => log.Add("all")).ToModel();
        Assert.Equal(new[] { "feedback.copy-text", "feedback.dismiss", "feedback.dismiss-all" }, model.Entries.Select(entry => entry.CommandId));
        Assert.Equal(new[] { "Copy message text", "Dismiss message", "Close all notifications" }, model.Entries.Select(entry => entry.Label));
        Assert.Equal(2, model.Sections.Count);
        Assert.All(model.Entries, entry => Assert.True(CommandCatalog.TryGet(entry.CommandId!, out _)));
        foreach (ContextMenuEntry entry in model.Entries) entry.Invoke!();
        Assert.Equal(new[] { "copy", "dismiss", "all" }, log);
    }

    [Fact]
    public void ACopyThatFailsHasAGenericSentenceInEveryLanguage()
    {
        foreach (string language in new[] { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            Assert.NotEqual("ctxmenu.copyFailed", Texts.T("ctxmenu.copyFailed"));
        }
    }
}
