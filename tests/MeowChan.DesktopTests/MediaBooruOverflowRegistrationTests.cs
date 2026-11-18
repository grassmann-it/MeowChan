using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        Assert.Contains("WorkViewElement.SetId(_panelToggle, \"media.toolbar.panel\")", view);
        Match overflow = Regex.Match(view,
            @"private List<Control> HeaderOverflow\(\) => Overflowed\((?<controls>.*?)\)\s*\.Where",
            RegexOptions.Singleline);
        Assert.True(overflow.Success, "HeaderOverflow must keep one explicit registered control list.");
        Assert.Equal(
            new[]
            {
                "_booruMode", "_layoutButton", "_panelToggle", "_focus", "_refresh", "_shuffle",
                "_exportAll", "_manageRatings", "_booruOpen", "_queueWalk", "_taggingPageOpen", "_queueAll", "_mediaFolders"
            },
            Regex.Matches(overflow.Groups["controls"].Value, @"\b_[A-Za-z][A-Za-z0-9]*\b")
                .Select(match => match.Value)
                .ToArray());
        Assert.Contains("UpdateMore(_more, HeaderOverflow())", view);
        Match opening = Regex.Match(view,
            @"\(\(MenuFlyout\)_more\.Flyout!\)\.Opening.*?\n\s*};\s*\n\s*\(\(MenuFlyout\)_selMore",
            RegexOptions.Singleline);
        Assert.True(opening.Success, "The header More opening handler must remain source-visible.");
        Assert.Equal(1, Regex.Matches(opening.Value, @"\bHeaderOverflow\(\)").Count);
        Assert.Contains("List<Control> items = HeaderOverflow();", opening.Value);
        Assert.Contains("UpdateMore(_more, items);", opening.Value);
        Assert.Contains("FillMore((MenuFlyout)_more.Flyout!, items);", opening.Value);
        Assert.Contains("WorkViewGate.Apply(_booruOpen, view, stateVisible: _canOpenBooru?.Invoke() == true)", view);
        Assert.Contains("WorkViewGate.Apply(_panelToggle, view, stateVisible: CanTogglePanel)", view);
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
        Assert.Contains("_layoutButton, _panelToggle, _focus", source);
    }

    private static string Read(string file, [CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", "Desktop/Views/Media", file)));
}
