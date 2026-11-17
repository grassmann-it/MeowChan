using System.Reflection;
using Ursa.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using MeowChan.Desktop.Controls;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Tags;
using MeowChan.Gallery;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// UX-LEDGER-28 package 1: real controls and design resources, without a native window.
[Collection("Gallery viewer construction")]
public sealed class TaggingLayoutRhythmTests
{
    private static void WithPage(Action<TaggingPage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        Texts.Initialize();
        Texts.SetLanguage("en");
        using TempDirectory temp = new();
        DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        TaggingPage page = new(host);
        // A detached UserControl has no application resource parent. Give this fixture the product's actual resource scope.
        page.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MeowChan/"))
            { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
        try { test(page); }
        finally { page.End(); host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private static T Find<T>(TaggingPage page, string name) where T : Control =>
        ((ILogical)page).GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static double Token(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, out object? value), key);
        return Assert.IsType<double>(value);
    }

    [Fact]
    public void MajorSectionsAndRelatedRowsUseDifferentTokenRhythms() => WithPage(page =>
    {
        StackPanel sections = Find<StackPanel>(page, "TaggingSections");
        StackPanel queue = Find<StackPanel>(page, "TaggingQueueControls");
        Assert.Equal(Token(sections, "Workspace.Space.16"), sections.Spacing);
        Assert.Equal(Token(sections, "Workspace.Space.16"), sections.Margin.Bottom);
        Assert.Equal(PageHeader.PageTopOffset, sections.Margin.Top);
        Assert.Equal(Token(sections, "Workspace.Space.12"), sections.Margin.Left);
        Assert.Equal(Token(sections, "Workspace.Space.12"), sections.Margin.Right);
        Assert.Equal(Token(queue, "Workspace.Space.8"), queue.Spacing);
        Assert.Equal(new[] { "TaggingProgress", "TaggingActions", "TaggingSecondaryToolbar" }, queue.Children.Select(c => c.Name));
        StackPanel heading = Find<StackPanel>(page, "TaggingHeading");
        Assert.Equal(Token(sections, "Workspace.Space.8"), heading.Spacing);
        Assert.Equal(Avalonia.Layout.Orientation.Vertical, heading.Orientation);
        Assert.Equal(new[] { "TaggingTitle", "TaggingSubtitle" }, heading.Children.Select(c => c.Name));
        StackPanel error = Assert.IsType<StackPanel>(page.ErrorPanel.Child);
        Assert.Equal(Token(error, "Workspace.Space.8"), error.Spacing);
        WrapPanel stamp = Find<WrapPanel>(page, "TaggingStamp");
        Assert.Equal(Token(stamp, "Workspace.Space.12"), stamp.ItemSpacing);
        Assert.Equal(Token(stamp, "Workspace.Space.12"), stamp.LineSpacing);
        // Hidden feedback/secondary rows must not add phantom spacing.
        Assert.False(page.ErrorPanel.IsVisible);
        Assert.False(page.SecondaryToolbar.IsVisible);
    });
}
