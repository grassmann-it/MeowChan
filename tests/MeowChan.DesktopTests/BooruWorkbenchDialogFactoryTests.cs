using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// MD3 seam tests for the modeless Booru host. The actual window, focus routing and attach/detach evidence stay in DesktopSmoke;
// these tests keep the factory/lifetime contract local and do not construct a provider, profile or native window.
public sealed class BooruWorkbenchDialogFactoryTests
{
    [Fact]
    public void WorkbenchUsesTheModelessXlFactoryAndSharedFrame()
    {
        string source = WindowSource();

        Assert.Contains("DialogWindow.Configure(this, DialogSize.XL, modal: false);", source);
        Assert.Contains("DialogWindow.Frame(this, _view, modal: false);", source);
        Assert.Contains("DialogFooter.Build(_close, status: notes)", source);
        Assert.Contains("DialogFrame.Build(header, new Border { Child = body, Classes = { \"workspace-dialog-body\" } },", source);
        Assert.Contains("DialogFrame.ErrorStrip(new TextBlock())", source);

        // The factory owns these policies; a later host edit must not silently bring back a private copy.
        Assert.DoesNotContain("Width = 1000", source);
        Assert.DoesNotContain("MinWidth = 600", source);
        Assert.DoesNotContain("DialogFit.OnOpen(this)", source);
        Assert.DoesNotContain("WindowChrome.Frame(this, _view)", source);
        Assert.DoesNotContain("WindowChromeKind.Dialog", source);
    }

    [Fact]
    public void HostKeepsTheViewOwnershipAndLifetimeBoundary()
    {
        string source = WindowSource();

        foreach (string required in new[]
        {
            "BooruWorkbenchWindow(BooruWorkbenchServices services)",
            "BooruWorkbenchWindow(BooruWorkbenchModel model, bool ownsModel)",
            "new BooruWorkbenchView(model, ownsModel,",
            "Opened += async (_, _) => await _view.ActivateAsync();",
            "Closed += (_, _) => Dispose();",
            "if (_disposed) return;",
            "_view.ChromeChanged -= OnChromeChanged;",
            "_view.Dispose();"
        })
            Assert.Contains(required, source);

        foreach (string forwarded in new[]
        {
            "BooruWorkbenchModel Model => _view.Model",
            "ShowInMedia { get => _view.ShowInMedia; set => _view.ShowInMedia = value; }",
            "IsPostSavedInMedia { get => _view.IsPostSavedInMedia; set => _view.IsPostSavedInMedia = value; }",
            "ApplyWorkView(WorkView view, bool viewChanged) => _view.ApplyWorkView(view, viewChanged)",
            "ShowQuery(string query) => _view.ShowQuery(query)",
            "AddTagToSearch(string tag) => _view.AddTagToSearch(tag)"
        })
            Assert.Contains(forwarded, source);

        // Requests, clocks, bitmaps, and model subscriptions stay in the reusable view/model.
        foreach (string forbidden in new[] { "Search.SubmitAsync", "Watchers.Changed", "DispatcherTimer", "Bitmap", "SKCodec", "Texts.Changed" })
            Assert.DoesNotContain(forbidden, source);
    }

    [Fact]
    public void EscapeIsDelegatedToTheSharedPolicyAtTheExistingLevels()
    {
        string source = WindowSource();

        Assert.Contains("DialogEscape.Attach(this, tryCancelLocal: CancelEscapeLocally);", source);
        Assert.Contains("private bool CancelEscapeLocally() =>", source);
        Assert.Contains("ShortcutService.IsCapturing || _view.IsModeDropDownOpen || _view.TryCancelRemoval();", source);
        Assert.DoesNotContain("private void OnKeyDown", source);
        Assert.DoesNotContain("Key.Escape", source);

        bool localCancelled = false;
        Assert.Equal(DialogEscapeOutcome.Ignored,
            DialogEscape.Decide(Key.Escape, KeyModifiers.None, handled: true,
                tryCancelLocal: () => localCancelled = true, isBusy: () => false));
        Assert.False(localCancelled);
        Assert.Equal(DialogEscapeOutcome.CancelledLocal,
            DialogEscape.Decide(Key.Escape, KeyModifiers.None, handled: false,
                tryCancelLocal: () => true, isBusy: () => false));
        Assert.Equal(DialogEscapeOutcome.Close,
            DialogEscape.Decide(Key.Escape, KeyModifiers.None, handled: false,
                tryCancelLocal: () => false, isBusy: () => false));
        Assert.Equal(DialogEscapeOutcome.Ignored,
            DialogEscape.Decide(Key.Escape, KeyModifiers.Shift, handled: false,
                tryCancelLocal: () => true, isBusy: () => false));
    }

    [Fact]
    public void ModelessFactoryAndForwardedNavigationApiRemainStable()
    {
        Assert.Equal(WindowChromeKind.Window, DialogWindow.KindFor(modal: false));
        Assert.Equal(new Avalonia.Size(980, 720), WorkspaceMetrics.DialogPreferredSize(DialogSize.XL));
        Assert.Equal(new Avalonia.Size(640, 560), WorkspaceMetrics.DialogMinimumSize(DialogSize.XL));

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type window = typeof(BooruWorkbenchWindow);
        Assert.NotNull(window.GetProperty("Model", flags));
        Assert.NotNull(window.GetProperty("ShowInMedia", flags));
        Assert.NotNull(window.GetProperty("IsPostSavedInMedia", flags));
        Assert.NotNull(window.GetMethod("ApplyWorkView", flags));
        Assert.NotNull(window.GetMethod("ShowQuery", flags));
        Assert.NotNull(window.GetMethod("AddTagToSearch", flags));
    }

    private static string WindowSource([CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../Desktop/Views/Booru/BooruWorkbenchWindow.cs")));
}
