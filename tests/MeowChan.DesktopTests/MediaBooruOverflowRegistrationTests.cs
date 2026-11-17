using System.Runtime.CompilerServices;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// Source wiring and pure policy contracts; native flyout focus/action behaviour is a later smoke gate.
public sealed class MediaBooruOverflowRegistrationTests
{
    [Fact]
    public void BooruUsesTheSameRegisteredOverflowListForMenuAndCount()
    {
        string view = Read("MediaPage.WorkView.cs"), toolbar = Read("MediaPage.Toolbar.cs");
        Assert.Contains("WorkViewElement.SetId(_booruOpen, \"media.toolbar.booru\")", view);
        Assert.Contains("Overflowed(_booruMode, _layoutButton, _focus, _refresh, _shuffle, _exportAll, _manageRatings, _booruOpen, _queueWalk, _taggingPageOpen, _queueAll, _mediaFolders)", view);
        Assert.Contains("UpdateMore(_more, HeaderOverflow())", view);
        // Opening freezes one projection for both the count and the entries. Requiring
        // another inline HeaderOverflow() call rejects that stronger same-list contract.
        Assert.Contains("List<Control> items = HeaderOverflow();\n"
            + "            UpdateMore(_more, items);\n"
            + "            FillMore((MenuFlyout)_more.Flyout!, items);", view.ReplaceLineEndings("\n"));
        Assert.Contains("WorkViewGate.Apply(_booruOpen, view, stateVisible: _canOpenBooru?.Invoke() == true)", view);
        Assert.DoesNotContain("AddBooruOverflow", toolbar);
        Assert.DoesNotContain("flyout.Items.Insert", toolbar);
        Assert.All(WorkViewSetting.All, mode =>
            Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("media.toolbar.booru", mode)));
    }

    [Fact]
    public void PresentationChangesCannotInvokeAnUnavailableBooruAction()
    {
        string source = Read("MediaPage.Toolbar.cs"), view = Read("MediaPage.WorkView.cs");
        Assert.Contains("if (!_lifetime.IsCancellationRequested && _canOpenBooru?.Invoke() == true)", source);
        Assert.Contains("_booruOpen.Click += (_, _) => InvokeBooruOverflow()", view);
        Assert.Contains("LocalizeBooruOverflow();", view);
        Assert.Contains("_exportAll, _manageRatings, _booruOpen,", source);
    }

    private static string Read(string file, [CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", "Desktop/Views/Media", file)));
}
