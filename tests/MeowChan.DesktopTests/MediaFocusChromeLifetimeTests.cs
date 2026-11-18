using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class MediaFocusChromeLifetimeTests
{
    [Fact]
    public void LiveTextsRefreshAndDisposeRemovesTheActualStaticSubscriptionIdempotently() => BooruComponentDispatcher.Run(() =>
    {
        Delegate[] before = LanguageSubscribers();
        Border strip = new();
        using MainWindow.MediaFocusChromeTextBinding binding = new(strip);
        Assert.Same(binding, Assert.Single(LanguageSubscribers().Except(before)).Target);
        AssertStripText(strip, Texts.T("media.focus.headerStrip"));
        string english = AutomationProperties.GetName(strip);

        Texts.SetLanguage("de");
        string german = Texts.T("media.focus.headerStrip");
        Assert.NotEqual(english, german);
        AssertStripText(strip, german);

        binding.Dispose();
        binding.Dispose();
        Assert.Equal(before, LanguageSubscribers());
        Texts.SetLanguage("en");
        // Keep both objects alive: removal is explicit, not a weak target disappearing after a collection.
        AssertStripText(strip, german);
        Assert.DoesNotContain(LanguageSubscribers(), handler => ReferenceEquals(handler.Target, binding));
    });

    [Fact]
    public void EndingOneShellLeavesTheOtherLiveShellSubscribed() => BooruComponentDispatcher.Run(() =>
    {
        Delegate[] before = LanguageSubscribers();
        Border endedStrip = new(), liveStrip = new();
        using MainWindow.MediaFocusChromeTextBinding ended = new(endedStrip);
        using MainWindow.MediaFocusChromeTextBinding live = new(liveStrip);
        string english = Texts.T("media.focus.headerStrip");
        Assert.Equal(before.Length + 2, LanguageSubscribers().Length);

        ended.Dispose();
        ended.Dispose();
        Assert.Same(live, Assert.Single(LanguageSubscribers().Except(before)).Target);
        Texts.SetLanguage("de");
        AssertStripText(endedStrip, english);
        AssertStripText(liveStrip, Texts.T("media.focus.headerStrip"));

        live.Dispose();
        Assert.Equal(before, LanguageSubscribers());
    });

    [Fact]
    public void SixteenEndedShellBindingsReturnToTheExactSubscriberBaselineWithoutCollection() => BooruComponentDispatcher.Run(() =>
    {
        Delegate[] before = LanguageSubscribers();
        var retained = new List<(Border Strip, MainWindow.MediaFocusChromeTextBinding Binding, string Text)>();
        try
        {
            for (int cycle = 0; cycle < 16; cycle++)
            {
                Texts.SetLanguage(cycle % 2 == 0 ? "en" : "de");
                Border strip = new();
                using MainWindow.MediaFocusChromeTextBinding binding = new(strip);
                string text = Texts.T("media.focus.headerStrip");
                retained.Add((strip, binding, text));
                AssertStripText(strip, text);
                Assert.Same(binding, Assert.Single(LanguageSubscribers().Except(before)).Target);
                binding.Dispose();
                Assert.Equal(before, LanguageSubscribers());
            }

            Texts.SetLanguage("en");
            Texts.SetLanguage("de");
            foreach (var ended in retained)
            {
                ended.Binding.Dispose();
                AssertStripText(ended.Strip, ended.Text);
            }
            Assert.Equal(before, LanguageSubscribers());
        }
        finally
        {
            foreach (var ended in retained) ended.Binding.Dispose();
        }
    });

    [Fact]
    public void WorkspaceDisposalReleasesTheBindingEvenBeforeWorkspaceInitializationCompleted()
    {
        // The binding tests above exercise the real static event with live controls. This wiring contract checks the
        // shell's constructor-failure path without constructing a native MainWindow in the offline test host.
        string chrome = Source("Desktop/Shell/MainWindow.MediaFocusChrome.cs");
        Assert.Contains("_mediaFocusChromeTexts = new MediaFocusChromeTextBinding(strip);", chrome);
        Assert.Contains("_mediaFocusChromeTexts?.Dispose();\n        _mediaFocusChromeTexts = null;", chrome);
        Assert.Contains("InitializeMediaFocusChrome();", Source("Desktop/Shell/MainWindow.Navigation.cs"));
        string presentation = Source("Desktop/Shell/MainWindow.Presentation.cs");
        int start = presentation.IndexOf("private void DisposeWorkspace()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = presentation.IndexOf("private T WorkspaceControl", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string dispose = presentation[start..end];
        int detach = dispose.IndexOf("DisposeMediaFocusChrome();", StringComparison.Ordinal);
        Assert.True(detach >= 0);
        foreach (string later in new[] { "CloseGallery(", "CloseLargeReader(", "if (!_workspaceInitialized) return;" })
            Assert.True(detach < dispose.IndexOf(later, StringComparison.Ordinal), later);
    }

    private static Delegate[] LanguageSubscribers() =>
        ((Delegate?)typeof(Texts).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null))
        ?.GetInvocationList() ?? Array.Empty<Delegate>();

    private static void AssertStripText(Border strip, string expected)
    {
        Assert.Equal(expected, AutomationProperties.GetName(strip));
        Assert.Equal(expected, ToolTip.GetTip(strip));
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative))).Replace("\r\n", "\n");
}
