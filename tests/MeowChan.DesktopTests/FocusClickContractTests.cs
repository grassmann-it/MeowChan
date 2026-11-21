using System.Runtime.CompilerServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Native controls need a GUI backend. These are deliberately source/wiring contracts,
// following TaggingPageSelectionTests, not claims of native pointer acceptance.
// The focused managed-pointer smoke is MediaPageChecks.RunClickContractsAsync.
public sealed class FocusClickContractTests
{
    [Fact]
    public void DownloadsSingleClickSelectsWithoutOpeningAndRightClickOwnsTargetBeforeMenu()
    {
        string press = Method("Desktop/Shell/MainWindow.Selection.cs", "SelectDownloadContextRow");
        Assert.Contains("if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;", press);
        Assert.DoesNotContain("OpenDownloadReaderAsync", press);
        Assert.DoesNotContain("OpenGalleryAsync", press);
        Before(press, "if (!grid.SelectedItems.Contains(item)) grid.SelectedItem = item;", "hit?.Focus()");
        Before(press, "cell?.Focus(NavigationMethod.Pointer)", "OpenDownloadContextMenu(grid, contextItem)");
        Assert.Contains("GetVisualDescendants().OfType<DataGridCell>()", press);
        string open = Method("Desktop/Shell/MainWindow.Selection.cs", "OpenDownloadContextMenu");
        Before(open, "if (!grid.SelectedItems.Contains(contextItem))", "BuildDownloadMenu(grid, menu)");
    }

    [Theory]
    [InlineData("OnThreadDoubleTapped", "OpenDownloadReaderAsync(row.Snapshot.Id, target)")]
    [InlineData("OnBoardDoubleTapped", "OpenWorkspaceDetails()")]
    public void DownloadsDoubleClickOpensTheIntendedSurface(string handler, string action)
    {
        string body = Method("Desktop/Shell/MainWindow.axaml.cs", handler);
        Assert.Contains(action, body);
        Assert.Contains("e.Handled = true;", body);
        Assert.DoesNotContain("OpenGalleryAsync", body);
    }

    [Fact]
    public void ArchiveSingleClickOnlyPreviewsAndRightClickSelectsAndFocusesTheHitRow()
    {
        string selection = Method("Desktop/Views/Archive/ArchivePage.Selection.cs", "ArchiveSelectionChanged");
        Assert.Contains("await LoadThreadAsync(lead.Summary.Id)", selection);
        Assert.DoesNotContain("OpenReaderRequested", selection);
        Assert.DoesNotContain("OpenGallery", selection);
        string press = Method("Desktop/Views/Archive/ArchivePage.Selection.cs", "OnArchiveListPointerPressed");
        Before(press, "if (!_archiveSelectionIds.Contains(row.Summary.Id))", "SynchronizeArchiveSelection(ArchiveVisibleRows);\n            // This press");
        Before(press, "hit?.Focus()", "OpenArchiveSelectionMenu(owner");
    }

    [Theory]
    [InlineData("OnThreadDoubleTapped", "OpenReaderOnThreadAsync(row.Summary.Id")]
    [InlineData("OnThreadTableDoubleTapped", "OpenReaderOnThreadAsync(row.Summary.Id")]
    [InlineData("OnMediaDoubleTapped", "OpenAssetGalleryAsync(row, e.Source as Control)")]
    public void ArchiveDoubleClickUsesTheHitItemNotAnUnrelatedSelection(string handler, string action)
    {
        string body = Method("Desktop/Views/Archive/ArchivePage.Gallery.cs", handler);
        Assert.Contains(action, body);
        Assert.Contains("e.Handled = true;", body);
        Assert.Contains("e.Source", body);
    }

    [Fact]
    public void ArchiveMediaRightClickResolvesSelectionAndFocusBeforeFreezingMenuFacts()
    {
        string body = Method("Desktop/Views/Archive/ArchivePage.MediaCommands.cs", "OnArchiveMediaContextRequested");
        Assert.Contains("!keyboard && attachment == null && asset != null", body);
        Assert.Contains("ReferenceEquals(control, VisibleMediaList)", body);
        Before(body, "if (!SelectedMediaRows().Contains(asset)) SetMediaSelection(new[] { asset });", "BuildArchiveMediaMenu(asset");
        Before(body, "cell?.Focus(NavigationMethod.Pointer)", "BuildArchiveMediaMenu(asset");
        Assert.Contains("item?.Focus()", body);
    }

    [Fact]
    public void MediaSingleClickUsesNativeSelectionAndDoubleClickOpensTheHitTile()
    {
        string interaction = Read("Desktop/Views/Media/MediaPage.Interaction.cs");
        Assert.Contains("_grid.DoubleTapped += OnTileDoubleTapped;", interaction);
        Assert.DoesNotContain("_grid.Tapped +=", interaction);
        string tapped = Method("Desktop/Views/Media/MediaPage.Interaction.cs", "OnTileDoubleTapped");
        Assert.Contains("ItemActivation.IsItemDoubleClick(e, _grid)", tapped);
        Assert.Contains("RowAt(e.Source) is not { } row", tapped);
        Assert.Contains("OpenTileAsync(row)", tapped);
        string open = Method("Desktop/Views/Media/MediaPage.Interaction.cs", "OpenTileAsync");
        Before(open, "_grid.SelectedItem = row", "await OpenGallery(window, start)");
    }

    [Theory]
    [InlineData("OnSharedPointerPressed")]
    [InlineData("OnSharedContextRequested")]
    public void MediaRightClickPreservesOnlyASelectionContainingTheTargetAndFocusesBeforeMenu(string handler)
    {
        string body = Method("Desktop/Views/Media/MediaPage.cs", handler);
        Before(body, "TargetForMenu(RowAt(e.Source))", "OpenSharedMediaMenu(row, origin)");
        string target = Method("Desktop/Views/Media/MediaPage.cs", "TargetForMenu");
        Before(target, "_grid.Selection.IsSelected(row.Index)) return row;", "_grid.SelectedItem = row");
        Assert.DoesNotContain("_grid.Selection.Clear()", target); // no transient empty selection/recycled menu origin
        string menu = Method("Desktop/Views/Media/MediaPage.cs", "OpenSharedMediaMenu");
        Assert.Contains("HashSet<int> selection = _grid.Selection.SelectedIndexes.ToHashSet();", menu);
        Assert.Contains("selection.SetEquals(_grid.Selection.SelectedIndexes)", menu);
        Before(body, "(_grid.ContainerFromItem(row) as Control ?? origin).Focus()", "OpenSharedMediaMenu(row, origin)");
        Assert.Contains("e.Handled = true;", body);
        // A paired release must not reopen a menu against a new selection.
        Assert.Contains(handler == "OnSharedPointerPressed" ? "MenuOpenedByPress()" : "TryConsume()", body);
    }

    [Fact]
    public void MediaSidebarSingleClickNeverOpensGalleryEvenWithGalleryReaderPreference()
    {
        string click = Method("Desktop/Views/Media/MediaPage.Panel.cs", "OnPanelPictureActivated");
        Assert.Contains("panel.Frame.Focus()", click);
        Assert.Contains("slot.ToggleAsync()", click);
        Assert.DoesNotContain("StartOpenPanelMedia", click);
        Assert.DoesNotContain("OpensGalleryOnPictureClick", click);
        Assert.Contains("panel.GalleryRequested += (_, _) => StartOpenPanelMedia();", Read("Desktop/Views/Media/MediaPage.Panel.cs"));
        string right = Method("Desktop/Views/Media/MediaPage.Panel.cs", "OnPanelPointerPressed");
        Before(right, "if (!_grid.Selection.SelectedItems.Contains(_panelRow))", "OpenSharedMediaMenu(_panelRow");
        Before(right, "panel.Frame.Focus()", "OpenSharedMediaMenu(_panelRow");
    }

    [Fact]
    public void GalleryImageAndVideoDoubleClickRequestFullscreenWithoutChangingFitOrZoom()
    {
        string body = Method("Desktop/GalleryViewer.ViewTransform.cs", "OnViewDoubleTapped");
        Assert.Contains("FullscreenRequested?.Invoke()", body);
        Assert.Contains("IsOverVideoSurface(e.Source, root)", body);
        Assert.Contains("e.Handled = true;", body);
        Assert.DoesNotContain("ActualPixels()", body);
        Assert.DoesNotContain("Fit()", body);
        string viewer = Read("Desktop/GalleryViewer.cs");
        Assert.Contains("_viewport.DoubleTapped += OnViewDoubleTapped;", viewer);
        Assert.Contains("_nativeVideoOverlay.DoubleTapped += OnViewDoubleTapped;", viewer);
        Assert.Contains("Focus();", Method("Desktop/GalleryViewer.cs", "OnViewportPointerPressed"));
        Assert.Contains("ShowMediaMenu();", Method("Desktop/GalleryViewer.MediaMenu.cs", "OnMediaContextRequested"));
    }

    [Theory]
    [InlineData("Commands", "_list")]
    [InlineData("Grid", "_grid")]
    public void TaggingSingleClickStaysNativeAndDoubleClickContinues(string file, string list)
    {
        string body = Read($"Desktop/Views/Tagging/TaggingPage.{file}.cs");
        Assert.Contains(list + ".SelectionChanged += OnSelectionChanged;", body);
        Assert.Contains(list + ".DoubleTapped +=", body);
        Assert.Contains(file == "Grid" ? "ExecuteRowCommandAsync(TaggingCommands.ContinueHere, explain: true)"
            : "ExecuteRowCommandAsync(\"tagging.continue-here\", explain: true)", body);
        Assert.Contains("ComposeRowMenu", body);
        Assert.DoesNotContain(list + ".Tapped +=", body);
    }

    [Fact]
    public void ReaderSidebarSingleClickMustNotOpenGalleryForAnyReaderPreference()
    {
        string body = Method("Desktop/Views/Archive/ArchivePage.InlineVideo.cs", "OnMediaSidebarPictureActivated");
        Assert.DoesNotContain("OpenMediaSidebarMedia()", body);
        Assert.DoesNotContain("OpensGalleryOnPictureClick", body);
        Assert.Contains("slot.ToggleAsync()", body);
        Assert.Contains("sidebar.SingleClickActs = slot.IsOffered;", Read("Desktop/Views/Archive/ArchivePage.InlineVideo.cs"));
        Assert.Contains("sidebar.GalleryRequested += (_, _) => OpenMediaSidebarMedia();", Read("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs"));
    }

    [Theory]
    [InlineData("Commands", "_list")]
    [InlineData("Grid", "_grid")]
    public void TaggingRightClickMustSelectAndFocusTargetBeforeComposingMenu(string file, string list)
    {
        string body = Read($"Desktop/Views/Tagging/TaggingPage.{file}.cs");
        Assert.Contains(list + ".AddHandler(PointerPressedEvent, OnContextPointerPressed", body);
    }

    [Fact]
    public void GalleryViewportRightClickMustFocusSurfaceBeforeOpeningMenu()
    {
        string body = Method("Desktop/GalleryViewer.MediaMenu.cs", "OnMediaContextRequested");
        Before(body, "FocusGallerySurface()", "ShowMediaMenu()");
    }

    [Fact]
    public void GalleryFilmstripRightClickMustSelectTargetNotOnlyRetargetTheMenu()
    {
        string body = Method("Desktop/GalleryViewer.Filmstrip.cs", "OnFilmstripTileContextRequested");
        Assert.Contains("OnFilmstripTileActivated(index)", body);
        Assert.Contains("_filmstripContextTarget = entry", body);
        Assert.DoesNotContain("ShowMediaMenu(entry)", body);
        Before(body, "_filmstripContextTarget = entry", "OnFilmstripTileActivated(index)");
        string show = Method("Desktop/GalleryViewer.cs", "ShowMediaAsync");
        Assert.Contains("CompleteFilmstripContextRequest(entry, filmstripContextRequest)", show);
        Assert.Contains("CancelFilmstripContextRequest()", Method("Desktop/GalleryViewer.cs", "SetMediaUnavailable"));
        Assert.Contains("CancelFilmstripContextRequest()", Method("Desktop/GalleryViewer.cs", "Move"));
        Assert.Contains("CancelFilmstripContextRequest()", Method("Desktop/GalleryViewer.Filmstrip.cs", "RetireFilmstrip"));
    }

    private static void Before(string body, string first, string second)
    {
        int a = body.IndexOf(first, StringComparison.Ordinal), b = body.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, $"Expected '{first}' before '{second}'.");
    }

    private static string Read(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)))
            .ReplaceLineEndings("\n");

    // Resolve the declaration, not an earlier subscription/call. These handlers all
    // use block bodies; balancing keeps assertions scoped to the actual handler.
    private static string Method(string relative, string name)
    {
        string text = Read(relative);
        var declaration = System.Text.RegularExpressions.Regex.Match(text,
            @"(?:private|internal)\s+(?:async\s+)?(?:void|Task|MediaLibraryTileRow\?)\s+" + name + @"\([^\n]*\)\s*\{");
        Assert.True(declaration.Success, $"Missing handler {name} in {relative}.");
        int start = text.IndexOf('{', declaration.Index), depth = 1, end = start + 1;
        for (; end < text.Length && depth > 0; end++)
        {
            if (text[end] == '{') depth++;
            else if (text[end] == '}') depth--;
        }
        return text[start..end];
    }
}
