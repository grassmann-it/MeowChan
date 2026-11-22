using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tests.PersistenceCorruption;

namespace MeowChan.DesktopTests;

// MP5: the media page's layout. The value (MediaPageLayout), its presets, its saved text, the plan that turns it into visible blocks and the tile
// measures are pure; the profile's settings keep it and carry it through the settings export; the menu factory composes the entries as data. The page
// itself (columns, handle, realized tiles) is driven in the media-page smoke; here its source contracts are pinned (applying a layout never presents the grid).
[Collection("Localization")]
public sealed class MediaPageLayoutTests : IDisposable
{
    public MediaPageLayoutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Read(string relative, [CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", relative))).ReplaceLineEndings("\n");

    // ---- The value and its presets ----

    [Fact]
    public void TheStandardLayoutIsTodaysPageAndTheWidthRangeIsASetting()
    {
        MediaPageLayout standard = MediaPageLayout.Standard;
        Assert.False(standard.TagList);
        Assert.Equal(MediaTagListSide.Left, standard.TagListSide);
        Assert.True(standard.FilterChips && standard.StatusLine && standard.Subtitle);
        Assert.Equal(MediaTileCaption.All, standard.Caption);
        Assert.Equal(MediaTileSize.Medium, standard.TileSize);
        Assert.Equal((256, 180, 480), (standard.TagListWidth, Config.MediaTagListWidthMinimum, Config.MediaTagListWidthMaximum));
        Assert.Equal(MediaPageLayout.Standard, MediaPageLayout.Reset);
    }

    [Fact]
    public void ThePresetsMakeTheBlockStatesTheBriefNames()
    {
        MediaPageLayout own = MediaPageLayout.Standard with { TagListWidth = 300, TagListSide = MediaTagListSide.Right };
        // Standard: today's page; the user's own width and side stay.
        MediaPageLayout standard = MediaPageLayout.For(MediaLayoutPreset.Standard, own);
        Assert.Equal(MediaPageLayout.Standard with { TagListWidth = 300, TagListSide = MediaTagListSide.Right }, standard);
        // Booru view: tag list on the left, compact tiles with the tag count, chips on, status line on, subtitle off.
        MediaPageLayout booru = MediaPageLayout.For(MediaLayoutPreset.BooruView, own);
        Assert.True(booru.TagList && booru.FilterChips && booru.StatusLine);
        Assert.False(booru.Subtitle);
        Assert.Equal(MediaTagListSide.Left, booru.TagListSide);
        Assert.Equal(300, booru.TagListWidth);
        Assert.Equal(MediaTileSize.Small, booru.TileSize);
        Assert.True(booru.Caption.HasFlag(MediaTileCaption.TagCount));
        Assert.False(booru.Caption.HasFlag(MediaTileCaption.Name));
        // Grid + media sidebar: everything above the tiles off, with the selected medium kept visible beside the grid.
        MediaPageLayout grid = MediaPageLayout.For(MediaLayoutPreset.GridWithSidebar, own with { Panel = false });
        Assert.False(grid.TagList || grid.FilterChips || grid.StatusLine || grid.Subtitle);
        Assert.Equal(MediaTileCaption.None, grid.Caption);
        Assert.True(grid.Panel);
        Assert.Equal((MediaPanelSide.Right, MeowChan.Config.MediaPanelWidthDefault), (grid.PanelSide, grid.PanelWidth));
        // Reset takes the default width back too.
        Assert.Equal(256, MediaPageLayout.Reset.TagListWidth);
        Assert.NotEqual(own, MediaPageLayout.Reset);
    }

    [Fact]
    public void ALayoutKnowsWhichPresetItIs()
    {
        foreach (MediaLayoutPreset preset in Enum.GetValues<MediaLayoutPreset>())
        {
            MediaPageLayout layout = MediaPageLayout.For(preset, MediaPageLayout.Standard with { TagListWidth = 400 });
            Assert.Equal(preset, layout.MatchingPreset);
        }
        Assert.Equal(MediaLayoutPreset.Standard, MediaPageLayout.Standard.MatchingPreset);
        Assert.Null((MediaPageLayout.Standard with { Subtitle = false }).MatchingPreset);
        // The Standard preset preserves the user's panel preference; only the blocks that the preset owns are compared.
        Assert.Equal(MediaLayoutPreset.Standard, (MediaPageLayout.Standard with { Panel = false }).MatchingPreset);
        Assert.Null((MediaPageLayout.For(MediaLayoutPreset.BooruView, MediaPageLayout.Standard) with { TileSize = MediaTileSize.Large }).MatchingPreset);
    }

    [Fact]
    public void TheWidthIsBroughtIntoItsRange()
    {
        Assert.Equal(180, (MediaPageLayout.Standard with { TagListWidth = 0 }).Normalized().TagListWidth);
        Assert.Equal(180, (MediaPageLayout.Standard with { TagListWidth = -50 }).Normalized().TagListWidth);
        Assert.Equal(480, (MediaPageLayout.Standard with { TagListWidth = int.MaxValue }).Normalized().TagListWidth);
        Assert.Equal(333, (MediaPageLayout.Standard with { TagListWidth = 333 }).Normalized().TagListWidth);
        MediaPageLayout broken = MediaPageLayout.Standard with { TagListSide = (MediaTagListSide)9, TileSize = (MediaTileSize)7, Caption = (MediaTileCaption)255 };
        MediaPageLayout fixedUp = broken.Normalized();
        Assert.Equal((MediaTagListSide.Left, MediaTileSize.Medium, MediaTileCaption.All), (fixedUp.TagListSide, fixedUp.TileSize, fixedUp.Caption));
    }

    // ---- The saved text ----

    private static IEnumerable<MediaPageLayout> LayoutSamples()
    {
        yield return MediaPageLayout.Standard;
        yield return MediaPageLayout.For(MediaLayoutPreset.BooruView, MediaPageLayout.Standard);
        yield return MediaPageLayout.For(MediaLayoutPreset.GridWithSidebar, MediaPageLayout.Standard);
        yield return new MediaPageLayout { TagList = true, TagListSide = MediaTagListSide.Right, TagListWidth = 480, FilterChips = false, StatusLine = true,
            Subtitle = false, Caption = MediaTileCaption.Name | MediaTileCaption.Source, TileSize = MediaTileSize.Large };
        yield return new MediaPageLayout { TagListWidth = 180, Caption = MediaTileCaption.QueueMark };
    }

    public static IEnumerable<object[]> LayoutIndexes() => LayoutSamples().Select((_, i) => new object[] { i });

    [Theory]
    [MemberData(nameof(LayoutIndexes))]
    public void TheSavedTextRoundTrips(int index)
    {
        MediaPageLayout layout = LayoutSamples().ElementAt(index);
        string text = layout.Format();
        Assert.Equal(layout, MediaPageLayout.Parse(text, layout.TagList));
        Assert.Equal(text, MediaPageLayout.Canonical(text)); // writing it again changes nothing
        Assert.True(text.Length < 100, text);
        Assert.DoesNotContain("tags=1;", text.Replace("caption=", "c="), StringComparison.Ordinal); // the tag list switch is not part of the text
    }

    [Fact]
    public void TheStandardIsWrittenAsOneShortText() =>
        Assert.Equal("side=left;width=256;chips=1;status=1;subtitle=1;caption=name,tags,queue,source;size=m", MediaPageLayout.Standard.Format());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData(";;;===;;")]
    [InlineData("side=;width=;chips=;status=;subtitle=;caption=;size=")]
    [InlineData("width=abc;chips=maybe;size=xl;caption=name,bogus;side=middle")]
    [InlineData("{\"json\":true}")]
    public void ADamagedTextReadsAsTheStandard(string? text)
    {
        MediaPageLayout read = MediaPageLayout.Parse(text, tagList: false);
        // "side=middle" reads as the default side (left) like every unknown value: the result is the standard layout.
        Assert.Equal(MediaPageLayout.Standard, read);
        Assert.Equal(MediaPageLayout.Standard.Format(), MediaPageLayout.Canonical(text));
    }

    [Fact]
    public void ADamagedPartCostsOnlyThatPart()
    {
        MediaPageLayout read = MediaPageLayout.Parse("side=right;width=999999;chips=0;status=banana;subtitle=0;caption=name,nonsense;size=l;unknown=1", tagList: true);
        Assert.Equal(MediaTagListSide.Right, read.TagListSide);
        Assert.Equal(480, read.TagListWidth);        // out of range: brought into it
        Assert.False(read.FilterChips);
        Assert.True(read.StatusLine);                 // "banana": the standard for that part
        Assert.False(read.Subtitle);
        Assert.Equal(MediaTileCaption.All, read.Caption); // one unknown part damages the whole value
        Assert.Equal(MediaTileSize.Large, read.TileSize);
        Assert.True(read.TagList);
        MediaPageLayout narrow = MediaPageLayout.Parse("width=3", tagList: false);
        Assert.Equal(180, narrow.TagListWidth);
        Assert.Equal(MediaTileCaption.None, MediaPageLayout.Parse("caption=none", false).Caption);
        Assert.Equal(MediaTileCaption.TagCount | MediaTileCaption.QueueMark, MediaPageLayout.Parse(" CAPTION = Tags , QUEUE ;", false).Caption);
        // Later entries of a key win; keys and values ignore case.
        Assert.Equal(MediaTileSize.Small, MediaPageLayout.Parse("SIZE=l;size=S", false).TileSize);
    }

    [Fact]
    public void AnOverlongTextIsNotRead()
    {
        string huge = "size=l;" + new string('x', MediaPageLayout.MaximumTextLength);
        Assert.Equal(MediaPageLayout.Standard, MediaPageLayout.Parse(huge, false));
    }

    // ---- The plan ----

    [Fact]
    public void ThePlanShowsTheBlocksTheLayoutChoseAndFocusModeTakesTheRowsAway()
    {
        MediaPageLayout layout = MediaPageLayout.For(MediaLayoutPreset.BooruView, MediaPageLayout.Standard) with { TagListSide = MediaTagListSide.Right, TagListWidth = 320 };
        MediaPageLayoutPlan plan = MediaPageLayoutPlan.Of(layout, new MediaPageLayoutState(false, false));
        Assert.True(plan.Title && plan.Toolbar && plan.FilterChips && plan.StatusLine && plan.TagList);
        Assert.False(plan.Subtitle);
        Assert.Equal((MediaTagListSide.Right, 320d), (plan.TagListSide, plan.TagListWidth));
        Assert.Equal(MediaTileMetrics.For(MediaTileSize.Small, MediaTileCaption.TagCount | MediaTileCaption.QueueMark), plan.Tiles);
        // Focus mode: the rows go, the tag list and the tiles stay as chosen.
        MediaPageLayoutPlan focus = MediaPageLayoutPlan.Of(layout, new MediaPageLayoutState(true, false));
        Assert.False(focus.Title || focus.Toolbar || focus.FilterChips || focus.StatusLine || focus.Subtitle);
        Assert.True(focus.TagList);
        Assert.Equal((plan.TagListSide, plan.TagListWidth, plan.Tiles), (focus.TagListSide, focus.TagListWidth, focus.Tiles));
        // And they come back as chosen when it ends.
        Assert.Equal(plan, MediaPageLayoutPlan.Of(layout, new MediaPageLayoutState(false, false)));
    }

    [Fact]
    public void AStatusLineTheUserLeftOutStillShowsWhatIsNotACount()
    {
        MediaPageLayout quiet = MediaPageLayout.For(MediaLayoutPreset.GridWithSidebar, MediaPageLayout.Standard);
        Assert.False(MediaPageLayoutPlan.Of(quiet, new MediaPageLayoutState(false, false)).StatusLine);
        Assert.True(MediaPageLayoutPlan.Of(quiet, new MediaPageLayoutState(false, true)).StatusLine);
        Assert.False(MediaPageLayoutPlan.Of(quiet, new MediaPageLayoutState(true, true)).StatusLine); // the focus row carries the counter
        Assert.False(MediaPageLayoutPlan.Of(quiet, new MediaPageLayoutState(false, true)).FilterChips);
    }

    [Fact]
    public void EveryPresetGivesAPlanWithoutAnythingThatIsHiddenTwice()
    {
        foreach (MediaLayoutPreset preset in Enum.GetValues<MediaLayoutPreset>())
        {
            MediaPageLayoutPlan plan = MediaPageLayoutPlan.Of(MediaPageLayout.For(preset, MediaPageLayout.Standard), new MediaPageLayoutState(false, false));
            Assert.True(plan.Title && plan.Toolbar, preset.ToString());
        }
        MediaPageLayoutPlan grid = MediaPageLayoutPlan.Of(MediaPageLayout.For(MediaLayoutPreset.GridWithSidebar, MediaPageLayout.Standard), new MediaPageLayoutState(false, false));
        Assert.False(grid.TagList || grid.Subtitle || grid.FilterChips || grid.StatusLine);
        Assert.True(grid.Toolbar);
    }

    [Fact]
    public void TheTagListNeverTakesTheRoomOfTheGrid()
    {
        Assert.Equal(480, MediaPageLayoutPlan.EffectiveTagListMaximum(double.NaN));
        Assert.Equal(480, MediaPageLayoutPlan.EffectiveTagListMaximum(0));
        Assert.Equal(480, MediaPageLayoutPlan.EffectiveTagListMaximum(1400));
        Assert.Equal(600 - MediaPageLayoutPlan.MinimumGridWidth - MediaPageLayoutPlan.BodyChrome, MediaPageLayoutPlan.EffectiveTagListMaximum(600));
        Assert.Equal(180, MediaPageLayoutPlan.EffectiveTagListMaximum(300)); // never below the range's minimum
        Assert.Equal(256, MediaPageLayoutPlan.EffectiveTagListWidth(256, 1400));
        Assert.Equal(180, MediaPageLayoutPlan.EffectiveTagListWidth(100, 1400));
        Assert.Equal(480, MediaPageLayoutPlan.EffectiveTagListWidth(9999, 1400));
        Assert.Equal(700 - MediaPageLayoutPlan.MinimumGridWidth - MediaPageLayoutPlan.BodyChrome, MediaPageLayoutPlan.EffectiveTagListWidth(480, 700));
    }

    // ---- The tile measures ----

    [Fact]
    public void TheMediumTileWithEveryPartIsTodaysTile()
    {
        MediaTileAppearance standard = MediaTileMetrics.Standard;
        Assert.Equal((190d, 226d, 120d), (standard.CellWidth, standard.CellHeight, standard.PreviewHeight));
        Assert.Equal(MediaLibraryTileView.TileHeight, standard.CellHeight);
        Assert.Equal(MediaTilePanel.CellWidth, standard.CellWidth);
        Assert.Equal(174d, standard.PictureWidth);
    }

    [Fact]
    public void TheTileSizesGrowAndALeftOutLineGivesItsRoomBack()
    {
        MediaTileAppearance small = MediaTileMetrics.For(MediaTileSize.Small, MediaTileCaption.All), medium = MediaTileMetrics.Standard, large = MediaTileMetrics.For(MediaTileSize.Large, MediaTileCaption.All);
        Assert.True(small.CellWidth < medium.CellWidth && medium.CellWidth < large.CellWidth);
        Assert.True(small.CellHeight < medium.CellHeight && medium.CellHeight < large.CellHeight);
        Assert.True(small.PreviewHeight < medium.PreviewHeight && medium.PreviewHeight < large.PreviewHeight);
        MediaTileAppearance noName = MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.All & ~MediaTileCaption.Name);
        MediaTileAppearance noSource = MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.All & ~MediaTileCaption.Source);
        MediaTileAppearance bare = MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.None);
        Assert.Equal(medium.CellHeight - MediaTileMetrics.NameLine, noName.CellHeight);
        Assert.Equal(medium.CellHeight - MediaTileMetrics.SourceLine, noSource.CellHeight);
        Assert.Equal(medium.CellHeight - MediaTileMetrics.NameLine - MediaTileMetrics.SourceLine, bare.CellHeight);
        // The corner marks do not change the cell: the tag count and the queue mark sit on the picture.
        Assert.Equal(medium.CellHeight, MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.Name | MediaTileCaption.Source).CellHeight);
        Assert.True(medium.Shows(MediaTileCaption.TagCount) && !bare.Shows(MediaTileCaption.TagCount));
        // A tile is never shorter than its picture.
        foreach (MediaTileSize size in Enum.GetValues<MediaTileSize>())
            Assert.True(MediaTileMetrics.For(size, MediaTileCaption.None).CellHeight > MediaTileMetrics.For(size, MediaTileCaption.None).PreviewHeight);
    }

    // ---- The profile ----

    [Fact]
    public void TheConfigKeepsTheLayoutAndTheTagListSwitchStaysInItsOldKey()
    {
        Config config = new();
        Assert.Equal(MediaPageLayout.Standard.Format(), config.MediaLayout);
        Assert.False(config.MediaBooruMode);
        MediaPageLayout layout = MediaPageLayout.For(MediaLayoutPreset.BooruView, MediaPageLayout.Standard) with { TagListSide = MediaTagListSide.Right, TagListWidth = 301 };
        config.MediaLayout = layout.Format();
        config.MediaBooruMode = layout.TagList;
        Assert.Equal(layout, MediaPageLayout.Parse(config.MediaLayout, config.MediaBooruMode));
        Config clone = config.Clone();
        Assert.Equal((config.MediaLayout, true), (clone.MediaLayout, clone.MediaBooruMode));
        Config copy = new();
        copy.CopyFrom(config);
        Assert.Equal(config.MediaLayout, copy.MediaLayout);
        // The setter reads whatever it is given and keeps the canonical text.
        config.MediaLayout = "width=9999; size=L";
        Assert.Equal("side=left;width=480;chips=1;status=1;subtitle=1;caption=name,tags,queue,source;size=l", config.MediaLayout);
        config.Validate();
        // An earlier profile (only the old switch) shows the standard layout with the tag list as it was.
        Assert.Equal(MediaPageLayout.Standard with { TagList = true }, MediaPageLayout.Parse(new Config { MediaBooruMode = true }.MediaLayout, true));
    }

    [Fact]
    public void TheLayoutSurvivesSavingAndLoadingTheSettings()
    {
        using TempDirectory directory = new();
        MediaPageLayout layout = new() { TagList = true, TagListSide = MediaTagListSide.Right, TagListWidth = 411, FilterChips = false, Subtitle = false,
            Caption = MediaTileCaption.TagCount, TileSize = MediaTileSize.Large };
        new Config { MediaLayout = layout.Format(), MediaBooruMode = layout.TagList, BooruCatalogLimitMiB = 17 }.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(ProfileOrigin.ExistingValid, loaded.Origin);
        Assert.Equal(layout, MediaPageLayout.Parse(loaded.MediaLayout, loaded.MediaBooruMode));
        Assert.Equal(17, loaded.BooruCatalogLimitMiB);
    }

    [Theory]
    [InlineData("\"garbage\"")]
    [InlineData("\"\"")]
    [InlineData("\"width=1;size=xl\"")]
    [InlineData("12345")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{\"width\":300}")]
    [InlineData("[1,2,3]")]
    public void ADamagedLayoutEntryNeverMakesTheSettingsInvalid(string damaged)
    {
        using TempDirectory directory = new();
        new Config { BooruCatalogLimitMiB = 17, MediaBooruMode = true, MediaLayout = MediaPageLayout.For(MediaLayoutPreset.GridWithSidebar, MediaPageLayout.Standard).Format() }
            .SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["MediaLayout"] = JsonNode.Parse(damaged);
        File.WriteAllText(path, document.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(ProfileOrigin.ExistingValid, loaded.Origin);
        Assert.Equal(17, loaded.BooruCatalogLimitMiB);   // the other settings are kept
        Assert.True(loaded.MediaBooruMode);
        MediaPageLayout read = MediaPageLayout.Parse(loaded.MediaLayout, loaded.MediaBooruMode);
        Assert.Equal(read.Normalized(), read);            // whatever it reads as, it is a valid layout
        loaded.Validate();
    }

    [Fact]
    public void TheSettingsExportCarriesTheLayoutAndAnImportKeepsIt()
    {
        MediaPageLayout layout = MediaPageLayout.For(MediaLayoutPreset.BooruView, MediaPageLayout.Standard) with { TagListWidth = 222 };
        Config source = new() { MediaLayout = layout.Format(), MediaBooruMode = layout.TagList };
        JsonObject export = SettingsTransfer.Export(source, "1.1.0");
        Assert.Equal(layout.Format(), export[nameof(Config.MediaLayout)]!.GetValue<string>());
        Assert.True(export[nameof(Config.MediaBooruMode)]!.GetValue<bool>());
        // Read back with the settings serializer (the shape an import applies): the layout is the same.
        Config imported = System.Text.Json.JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.Equal(layout, MediaPageLayout.Parse(imported.MediaLayout, imported.MediaBooruMode));
        Assert.Contains("MediaLayout = source.MediaLayout,", Read("Desktop/Settings/SettingsTransfer.cs"));
    }

    // ---- The menu ----

    [Fact]
    public void TheMenuOffersTheBlocksTheSizeThePresetsAndReset()
    {
        LayoutMenuModel rows = MediaLayoutMenu.Rows(MediaPageLayout.Standard);
        ContextMenuModel model = LayoutMenuBuilder.ToContextMenu(rows, _ => { }, MediaLayoutMenu.MenuName).ToModel();
        string[] ids = model.Entries.Select(entry => entry.Id).ToArray();
        Assert.Equal(
        [
            MediaLayoutMenu.TagListId, MediaLayoutMenu.SideLeftId, MediaLayoutMenu.SideRightId,
            MediaLayoutMenu.PanelId, MediaLayoutMenu.PanelSideLeftId, MediaLayoutMenu.PanelSideRightId,
            MediaLayoutMenu.ChipsId, MediaLayoutMenu.StatusId, MediaLayoutMenu.SubtitleId,
            MediaLayoutMenu.CaptionNameId, MediaLayoutMenu.CaptionTagsId, MediaLayoutMenu.CaptionQueueId, MediaLayoutMenu.CaptionSourceId,
            MediaLayoutMenu.SizeSmallId, MediaLayoutMenu.SizeMediumId, MediaLayoutMenu.SizeLargeId,
            MediaLayoutMenu.PresetStandardId, MediaLayoutMenu.PresetBooruId, MediaLayoutMenu.PresetGridSidebarId, MediaLayoutMenu.ResetId
        ], ids);
        // The flyout reads the same rows: the interactive ones are the context menu's entries, in order, without a title line.
        Assert.Equal(ids, rows.Interactive.Select(row => row.Id).ToArray());
        Assert.Equal(string.Empty, rows.Title);
        Assert.Empty(model.OversizedSections);
        Assert.Equal(7, model.Sections.Count);
        // The state of today's page: tag list off, left side, all rows and captions on, medium tiles, Standard preset.
        Dictionary<string, bool> checkedState = model.Entries.ToDictionary(entry => entry.Id, entry => entry.Checked);
        Assert.False(checkedState[MediaLayoutMenu.TagListId]);
        Assert.True(checkedState[MediaLayoutMenu.SideLeftId] && !checkedState[MediaLayoutMenu.SideRightId]);
        Assert.True(checkedState[MediaLayoutMenu.ChipsId] && checkedState[MediaLayoutMenu.StatusId] && checkedState[MediaLayoutMenu.SubtitleId]);
        Assert.True(checkedState[MediaLayoutMenu.CaptionNameId] && checkedState[MediaLayoutMenu.CaptionTagsId] && checkedState[MediaLayoutMenu.CaptionQueueId] && checkedState[MediaLayoutMenu.CaptionSourceId]);
        Assert.True(checkedState[MediaLayoutMenu.SizeMediumId] && !checkedState[MediaLayoutMenu.SizeSmallId] && !checkedState[MediaLayoutMenu.SizeLargeId]);
        Assert.True(checkedState[MediaLayoutMenu.PresetStandardId] && !checkedState[MediaLayoutMenu.PresetBooruId] && !checkedState[MediaLayoutMenu.PresetGridSidebarId]);
        Assert.All(model.Entries, entry => Assert.True(entry.Available && entry.Label.Length > 0 && entry.Label != entry.Id, entry.Id));
        Assert.All(rows.Interactive, row => Assert.True(row.Enabled && row.DisabledReason == null, row.Id));
    }

    [Fact]
    public void EveryRowAsksForTheLayoutItNames()
    {
        MediaPageLayout current = MediaPageLayout.Standard with { TagListWidth = 350 };
        Dictionary<string, MediaPageLayout> next = new();
        foreach (LayoutMenuRow row in MediaLayoutMenu.Rows(current).Interactive)
            next[row.Id] = MediaLayoutMenu.Apply(row.Id, current) ?? throw new InvalidOperationException(row.Id);
        Assert.Null(MediaLayoutMenu.Apply("media.layout.nothing", current));
        Assert.True(next[MediaLayoutMenu.TagListId].TagList);
        Assert.Equal(MediaTagListSide.Right, next[MediaLayoutMenu.SideRightId].TagListSide);
        Assert.False(next[MediaLayoutMenu.ChipsId].FilterChips);
        Assert.False(next[MediaLayoutMenu.StatusId].StatusLine);
        Assert.False(next[MediaLayoutMenu.SubtitleId].Subtitle);
        Assert.Equal(MediaTileCaption.All & ~MediaTileCaption.Name, next[MediaLayoutMenu.CaptionNameId].Caption);
        Assert.Equal(MediaTileCaption.All & ~MediaTileCaption.TagCount, next[MediaLayoutMenu.CaptionTagsId].Caption);
        Assert.Equal(MediaTileCaption.All & ~MediaTileCaption.QueueMark, next[MediaLayoutMenu.CaptionQueueId].Caption);
        Assert.Equal(MediaTileCaption.All & ~MediaTileCaption.Source, next[MediaLayoutMenu.CaptionSourceId].Caption);
        Assert.Equal(MediaTileSize.Small, next[MediaLayoutMenu.SizeSmallId].TileSize);
        Assert.Equal(MediaTileSize.Large, next[MediaLayoutMenu.SizeLargeId].TileSize);
        Assert.Equal(MediaPageLayout.For(MediaLayoutPreset.BooruView, current), next[MediaLayoutMenu.PresetBooruId]);
        Assert.Equal(MediaPageLayout.For(MediaLayoutPreset.GridWithSidebar, current), next[MediaLayoutMenu.PresetGridSidebarId]);
        Assert.Equal(current, next[MediaLayoutMenu.PresetStandardId]);
        Assert.Equal(MediaPageLayout.Standard, next[MediaLayoutMenu.ResetId]); // the default width too
        // Nothing but the asked part differs: a switch flips one block and leaves every other as it was.
        Assert.Equal(current with { StatusLine = false }, next[MediaLayoutMenu.StatusId]);
    }

    [Fact]
    public void TheContextMenuFormInvokesTheSameApplyAsTheRows()
    {
        MediaPageLayout current = MediaPageLayout.Standard with { TagListWidth = 350 };
        LayoutMenuModel rows = MediaLayoutMenu.Rows(current);
        foreach (LayoutMenuRow row in rows.Interactive)
        {
            MediaPageLayout? chosen = null;
            ContextMenuModel one = LayoutMenuBuilder.ToContextMenu(rows, picked => chosen = MediaLayoutMenu.Apply(picked.Id, current)).ToModel();
            one.Entries.Single(candidate => candidate.Id == row.Id).Invoke!();
            Assert.Equal(MediaLayoutMenu.Apply(row.Id, current), chosen);
        }
    }

    [Fact]
    public void TheMenuChecksTheMatchingPresetAndTheSwitchesOfALayoutOfTheUsersOwn()
    {
        MediaPageLayout booru = MediaPageLayout.For(MediaLayoutPreset.BooruView, MediaPageLayout.Standard);
        LayoutMenuModel rows = MediaLayoutMenu.Rows(booru);
        Assert.Equal([MediaLayoutMenu.PresetBooruId], rows.Rows.Where(row => row.Group == MediaLayoutMenu.PresetGroup && row.Checked).Select(row => row.Id));
        Assert.True(rows.Find(MediaLayoutMenu.TagListId)!.Checked);
        Assert.True(rows.Find(MediaLayoutMenu.SizeSmallId)!.Checked);
        // The groups are the exclusive choices of the context menu too.
        ContextMenuModel model = LayoutMenuBuilder.ToContextMenu(rows, _ => { }).ToModel();
        Assert.Equal([MediaLayoutMenu.PresetBooruId], model.Entries.Where(entry => entry.Group == "preset" && entry.Checked).Select(entry => entry.Id));
        // A layout that is none of the presets checks none (and never two).
        LayoutMenuModel own = MediaLayoutMenu.Rows(booru with { Subtitle = true });
        Assert.Empty(own.Rows.Where(row => row.Group == MediaLayoutMenu.PresetGroup && row.Checked));
        Assert.Empty(LayoutMenuBuilder.ToContextMenu(own, _ => { }).ToModel().Entries.Where(entry => entry.Group == "preset" && entry.Checked));
    }

    // ---- The page's wiring (source contracts; the page needs the asset loader, its behaviour is the media-page smoke's) ----

    [Fact]
    public void ApplyingALayoutOnlyChangesVisibilityAndSizesAndNeverPresentsTheGridAgain()
    {
        string layout = Read("Desktop/Views/Media/MediaPage.Layout.cs");
        // No presentation, no rebuild of the rows, no new count: only the plan's visibility, columns and the realized tiles' own appearance.
        Assert.DoesNotContain("Present(", layout);
        Assert.DoesNotContain("BuildAsync(", layout);
        Assert.DoesNotContain("_grid.ItemsSource", layout);
        Assert.DoesNotContain("ScheduleTagCount", layout);
        Assert.Contains("foreach (Control container in _grid.GetRealizedContainers())", layout);
        Assert.Contains("tile.Appearance = appearance;", layout);
        Assert.Contains("if (before == null || before.Tiles != plan.Tiles) ApplyTileAppearance(plan.Tiles);", layout);
        // The tag list switch is the one Tag list mode: counting starts only through it and only while the list is on.
        Assert.Contains("if (tagListChanged) SetBooruMode(layout.TagList, save: false);", layout);
        string mode = Read("Desktop/Views/Media/MediaPage.BooruMode.cs");
        Assert.Contains("if (!_booruModeOn || _lifetime.IsCancellationRequested) return;", mode);
        Assert.Contains("_layout = _layout with { TagList = on };", mode);
        // The tile only changes its own parts: no bound row is replaced and its bitmap is not reloaded.
        string tile = Read("Desktop/MediaLibraryTileView.cs");
        int start = tile.IndexOf("private void ApplyAppearance()", StringComparison.Ordinal), end = tile.IndexOf("}\n", start + 40, StringComparison.Ordinal);
        string apply = tile[start..end];
        Assert.DoesNotContain("Reset()", apply);
        Assert.DoesNotContain("LoadAsync", apply);
        Assert.DoesNotContain("DataContext =", apply);
    }

    [Fact]
    public void TheLayoutIsKeptPerProfileAndRestoredWithoutSaving()
    {
        string host = Read("Desktop/Shell/MainWindow.Media.cs"), layout = Read("Desktop/Views/Media/MediaPage.Layout.cs");
        Assert.Contains("_mediaPage.RestoreLayout(MediaPageLayout.Parse(_session.Config.MediaLayout, _session.Config.MediaBooruMode));", host);
        Assert.Contains("config.MediaBooruMode = layout.TagList; config.MediaLayout = layout.Format();", host);
        Assert.Contains("SetBooruMode(_layout.TagList, save: false);", layout);
        Assert.Contains("if (save) SaveLayout();", layout);
        // The width is saved once, when the drag (or the arrow keys) ends, not on every step.
        Assert.Contains("_tagHandle.DragCompleted += (_, _) => CommitTagListWidth();", layout);
        Assert.DoesNotContain("DragDelta", layout);
    }

    [Fact]
    public void TheLayoutButtonTheContextMenuAndMoreShareOneMenu()
    {
        string layout = Read("Desktop/Views/Media/MediaPage.Layout.cs"), page = Read("Desktop/Views/Media/MediaPage.cs"), view = Read("Desktop/Views/Media/MediaPage.WorkView.cs");
        // C15: one description (MediaLayoutMenu.Rows / Apply), three forms: the flyout's LayoutMenuPanel, the submenu and the context menu.
        Assert.Contains("new LayoutMenuPanel(\"MediaLayoutMenu\", () => MediaLayoutMenu.Rows(_layout, PanelHint()), ApplyLayoutRow);", layout);
        Assert.Contains("LayoutMenuBuilder.ToContextMenu(MediaLayoutMenu.Rows(_layout, PanelHint()), ApplyLayoutRow, MediaLayoutMenu.MenuName)", layout);
        Assert.Contains("ItemsSource = LayoutMenuItems()", layout);
        // The flyout keeps the keyboard behaviour of a menu: Escape closes (handled), the arrows move, the focus returns to the button.
        Assert.Contains("if (_layoutPanel.HandleMenuKey(e)) e.Handled = true;", layout);
        Assert.Contains("_layoutButton.Focus(_layoutMenuKeyboard ? NavigationMethod.Tab : NavigationMethod.Pointer);", layout);
        Assert.Contains("_layoutPanel.CloseRequested += () => _layoutFlyout.Hide();", layout);
        Assert.DoesNotContain("BuildItems", layout + page + view + Read("Desktop/Views/Media/MediaLayoutMenu.cs"));
        Assert.DoesNotContain("Compose(", Read("Desktop/Views/Media/MediaLayoutMenu.cs"));
        // The right click on empty grid space (no tile under the pointer) opens the page's blank menu, which carries the layout entries below its two actions.
        // The base branch now resolves the origin before focusing it and opening the menu.
        Assert.Contains("Control origin = ResolveMenuOrigin(e.Source, row);", page);
        Assert.Contains("OpenSharedMediaMenu(row, origin);", page);
        Assert.Contains("blankItems.AddRange(LayoutMenuItems());", page);
        // Under More (Barebones or a narrow page) the button is a submenu with the same entries.
        Assert.Contains("if (ReferenceEquals(control, _layoutButton)) { flyout.Items.Add(LayoutSubmenu()); continue; }", view);
        Assert.Contains("WorkViewElement.SetId(_layoutButton, \"media.toolbar.layout\");", view);
        Assert.Contains("WorkViewGate.Apply(_layoutButton, view);", view);
        // The focus mode leaves the subtitle (and the other rows) to the plan.
        Assert.Contains("ApplyLayoutPlan();", Read("Desktop/Views/Media/MediaPage.Focus.cs"));
        Assert.DoesNotContain("_subtitle.IsVisible = !on", Read("Desktop/Views/Media/MediaPage.Focus.cs"));
    }

    [Fact]
    public void TheWorkViewsPutTheLayoutButtonUnderMoreInBarebonesAndKeepBothSidebarSegments()
    {
        Assert.Equal([Presence.Overflow, Presence.Direct, Presence.Direct],
            new[] { WorkView.Barebones, WorkView.Simple, WorkView.Advanced }.Select(view => WorkViewPolicy.Resolve("media.toolbar.layout", view)));
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("media.toolbar.tag-list", view)));
        Assert.True(WorkViewPolicy.Get("media.toolbar.layout").OverflowOrder < WorkViewPolicy.Get("media.toolbar.focus").OverflowOrder);
        Assert.Equal(WorkViewPage.Media, WorkViewPolicy.Get("media.toolbar.layout").Page);
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("media.toolbar.panel", view)));
        Assert.Equal("media.panel.toggle", WorkViewPolicy.Get("media.toolbar.panel").CommandId);
    }

    [Fact]
    public void TheSettingsEditorOffersTheTagListWidthAsALimitNextToTheTagCount()
    {
        string editor = Read("Desktop/Views/Settings/SettingsEditor.TagQueue.cs"), axaml = Read("Desktop/Views/Settings/SettingsEditor.axaml");
        Assert.Contains("Config.MediaTagListWidthMinimum, Config.MediaTagListWidthMaximum", editor);
        Assert.Contains("with { TagListWidth = tagListWidth, PanelWidth = panelWidth }", editor);
        Assert.Contains("MediaTagListWidthInput, MediaTagListWidth,", editor); // part of the numeric validation list
        Assert.Contains("x:Name=\"MediaTagListWidthInput\"", axaml);
        Assert.True(axaml.IndexOf("MediaTagListTopInput", StringComparison.Ordinal) < axaml.IndexOf("MediaTagListWidthInput", StringComparison.Ordinal));
        Assert.Equal((256, 180, 480), (Config.MediaTagListWidthDefault, Config.MediaTagListWidthMinimum, Config.MediaTagListWidthMaximum));
    }

    // ---- M1: the width of the settings page reaches the saved layout without a stale draft rolling a dragged width back ----

    private static Config LiveWithWidth(int width, MediaTagListSide side = MediaTagListSide.Left) =>
        new() { MediaLayout = (MediaPageLayout.Standard with { TagListWidth = width, TagListSide = side }).Format() };

    private static int WidthOf(Config config) => MediaPageLayout.Parse(config.MediaLayout, config.MediaBooruMode).TagListWidth;

    private static Config DraftWithBox(Config live, int box)
    {
        Config draft = live.Clone(); // a fresh draft captures the loaded width as its baseline
        draft.MediaLayout = (MediaPageLayout.Parse(draft.MediaLayout, draft.MediaBooruMode) with { TagListWidth = box }).Format();
        return draft;
    }

    [Fact]
    public void SavingAChangedTagListWidthBoxTakesOnlyTheWidthOverAndKeepsTheRestOfTheLiveLayout()
    {
        Config live = LiveWithWidth(256);
        Config draft = DraftWithBox(live, 300);
        Assert.Equal(256, draft.SettingsBaselineMediaTagListWidth);
        // The person switched the side from the media page while the settings were open: the stale draft must not bring the old side back.
        live.MediaLayout = (MediaPageLayout.Parse(live.MediaLayout, false) with { TagListSide = MediaTagListSide.Right }).Format();
        live.CopySettingsFieldsFrom(draft);
        MediaPageLayout saved = MediaPageLayout.Parse(live.MediaLayout, live.MediaBooruMode);
        Assert.Equal(300, saved.TagListWidth);
        Assert.Equal(MediaTagListSide.Right, saved.TagListSide);
    }

    [Fact]
    public void SavingWithTheTagListWidthBoxUntouchedKeepsTheWidthTheHandleSetWhileTheSettingsWereOpen()
    {
        Config live = LiveWithWidth(256);
        Config draft = live.Clone(); // loaded with 256, the box is never touched
        draft.RecheckTimer = 120;
        // The drag on the media page committed 340 in the meantime.
        live.MediaLayout = (MediaPageLayout.Parse(live.MediaLayout, false) with { TagListWidth = 340 }).Format();
        live.CopySettingsFieldsFrom(draft);
        Assert.Equal(340, WidthOf(live));
        Assert.Equal(120, live.RecheckTimer);
    }

    [Fact]
    public void ABoxChangedBackToTheLiveWidthAndADraftWithoutABaselineLeaveTheLayoutAlone()
    {
        Config live = LiveWithWidth(300);
        Config bare = live.Clone();
        bare.SettingsBaselineMediaTagListWidth = null; // built by hand: compared with the live value
        bare.MediaLayout = (MediaPageLayout.Parse(bare.MediaLayout, false) with { TagListWidth = 300 }).Format();
        live.CopySettingsFieldsFrom(bare);
        Assert.Equal(300, WidthOf(live));
        bare.MediaLayout = (MediaPageLayout.Parse(bare.MediaLayout, false) with { TagListWidth = 210 }).Format();
        live.CopySettingsFieldsFrom(bare);
        Assert.Equal(210, WidthOf(live));
    }

    [Fact]
    public void TheTagListWidthBaselineIsDraftStateAndNeverSaved()
    {
        Config draft = LiveWithWidth(333).Clone();
        Assert.Equal(333, draft.SettingsBaselineMediaTagListWidth);
        Assert.DoesNotContain(nameof(Config.SettingsBaselineMediaTagListWidth), JsonSerializer.Serialize(draft, ConfigJsonContext.Default.Config));
    }

    [Fact]
    public void ASavedTagListWidthSurvivesSavingAndLoadingTheSettings()
    {
        using TempDirectory directory = new();
        Config live = LiveWithWidth(256);
        live.CopySettingsFieldsFrom(DraftWithBox(live, 417));
        live.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(ProfileOrigin.ExistingValid, loaded.Origin);
        Assert.Equal(417, WidthOf(loaded));
    }

    [Fact]
    public void TheEditorKeepsTheLoadedWidthAsBaselineAndTheShellShowsAChangedWidthLive()
    {
        string editor = Read("Desktop/Views/Settings/SettingsEditor.TagQueue.cs"), media = Read("Desktop/Shell/MainWindow.Media.cs"),
            shell = Read("Desktop/Shell/MainWindow.axaml.cs"), page = Read("Desktop/Views/Media/MediaPage.Layout.cs");
        Assert.Contains("MediaPageLayout layout = MediaPageLayout.Parse(config.MediaLayout, config.MediaBooruMode);", editor);
        Assert.Contains("if (captureBaseline)", editor); // a section reset loads the defaults into the box but keeps the loaded baseline
        Assert.Contains("_initialMediaTagListWidth = layout.TagListWidth;", editor);
        Assert.Contains("candidate.SettingsBaselineMediaTagListWidth = _initialMediaTagListWidth;", editor);
        Assert.Contains("frozen.SettingsBaselineMediaTagListWidth = settings.SettingsBaselineMediaTagListWidth;", Read("Desktop/DesktopSessionHost.cs"));
        Assert.Contains("ApplyMediaTagListWidthAfterSettings(mediaTagListWidthBefore);", shell);
        Assert.Contains("if (width != widthBefore) _mediaPage?.ApplySavedTagListWidth(width);", media);
        Assert.Contains("ApplyLayout(_layout with { TagListWidth = width }, save: false);", page);
    }

    // ---- The texts ----

    private static readonly string[] Keys =
    [
        "media.tagList", "media.tagList.tip", "settings.limits.mediaTagListTop", "settings.limits.mediaTagListWidth", "settings.limits.mediaTagListWidth.hint", "media.layout.name", "media.layout.tip", "media.layout.menuName",
        "media.layout.side.left", "media.layout.side.right", "media.layout.chips", "media.layout.status", "media.layout.subtitle",
        "media.layout.caption.name", "media.layout.caption.tags", "media.layout.caption.queue", "media.layout.caption.source",
        "media.layout.size.small", "media.layout.size.medium", "media.layout.size.large",
        "media.layout.preset.standard", "media.layout.preset.booru", "media.layout.preset.gridSidebar", "media.layout.reset", "media.layout.resize"
    ];

    [Fact]
    public void EveryLanguageHasTheTagListAndLayoutTextsAndNoneStillSaysBooruMode()
    {
        string[] languages = new[] { "en", "de" }.Concat(Texts.BundledPackCodes()).ToArray();
        Assert.Equal(12, languages.Length);
        foreach (string code in languages)
        {
            Texts.SetLanguage(code);
            foreach (string key in Keys)
            {
                string text = Texts.T(key);
                Assert.True(text != key && text.Length > 0, code + ": " + key);
            }
            // The switch and its tooltip are named for the tag list, not for a "Booru mode"; "Booru" stays only in the one preset that names the booru view.
            Assert.DoesNotContain("Booru", Texts.T("media.tagList"));
            Assert.DoesNotContain("Booru", Texts.T("settings.limits.mediaTagListTop"));
            Assert.Contains("Booru", Texts.T("media.layout.preset.booru"));
            Assert.NotEqual(Texts.T("media.tagList"), Texts.T("media.layout.name"));
        }
        Texts.SetLanguage("en");
        Assert.Equal("Tag list", Texts.T("media.tagList"));
        Assert.Equal("Layout", Texts.T("media.layout.name"));
        Texts.SetLanguage("de");
        Assert.Equal("Tag-Liste", Texts.T("media.tagList"));
        Assert.Equal("Raster + Medienleiste", Texts.T("media.layout.preset.gridSidebar"));
        // The old keys are gone from every pack and the catalog.
        Assert.Equal("media.booruMode", Texts.T("media.booruMode"));
    }
}
