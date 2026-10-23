using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaRatingChecks
{
    private static object? SelectionIdentity(object? item)
    {
        if (item is ArchiveAssetRow row)
            return new { type = item.GetType().Name, row.Asset.OriginalFileName, row.Asset.AssetId, key = KeyOf(row) };
        return item == null ? null : new { type = item.GetType().Name };
    }

    private static void SelectionSnapshot(Control list, object requested, string stage, Control? target = null, Exception? error = null)
    {
        object[] items = list switch
        {
            ListBox box => box.Items.Cast<object>().ToArray(),
            DataGrid grid => grid.ItemsSource?.Cast<object>().ToArray() ?? Array.Empty<object>(),
            _ => Array.Empty<object>()
        };
        object? selected = list switch { ListBox box => box.SelectedItem, DataGrid grid => grid.SelectedItem, _ => null };
        bool SameIdentity(object? candidate) => requested is ArchiveAssetRow wanted && candidate is ArchiveAssetRow row
            && KeyOf(row).Equals(KeyOf(wanted));
        object State(Control control) => new
        {
            type = control.GetType().Name, control.Name, control.IsLoaded, control.IsVisible,
            control.IsEffectivelyVisible, control.IsEnabled, control.IsEffectivelyEnabled,
            control.Focusable, control.IsFocused, control.IsKeyboardFocusWithin,
            bounds = control.Bounds.ToString(), control.ClipToBounds, clip = control.Clip?.Bounds.ToString(),
            originInList = control.TranslatePoint(default, list)?.ToString(),
            topLevelType = TopLevel.GetTopLevel(control)?.GetType().Name,
            inMainTopLevel = ReferenceEquals(TopLevel.GetTopLevel(control), _window)
        };
        Control[] containers = list.GetVisualDescendants().OfType<Control>()
            .Where(control => control is ListBoxItem or DataGridRow).ToArray();
        var snapshot = new
        {
            sequence = Evidence.Count + 1, ratingSelectionReadiness = stage,
            language = Texts.Language, mode = _window.SessionHost.Config.WorkspaceMode,
            requested = SelectionIdentity(requested), selected = SelectionIdentity(selected),
            selectedIsRequestedReference = ReferenceEquals(selected, requested), selectedHasRequestedKey = SameIdentity(selected),
            itemsCount = items.Length, requestedReferenceInItems = items.Any(item => ReferenceEquals(item, requested)),
            exactKeyMatchesInItems = items.Count(SameIdentity),
            list = State(list), target = target == null ? null : State(target), currentReaderTree = ReaderTreeState(),
            topLevel = TopLevel.GetTopLevel(list) is Window window ? new
            {
                type = window.GetType().Name, window.IsVisible, window.IsActive, window.IsEnabled,
                focus = Focus(window), correctMainWindow = ReferenceEquals(window, _window),
                ownedWindows = window.OwnedWindows.Select(owned => new
                { type = owned.GetType().Name, owned.IsVisible, owned.IsActive, owned.IsEnabled }).ToArray()
            } : null,
            referenceContainerCount = containers.Count(control => ReferenceEquals(control.DataContext, requested)),
            exactKeyContainerCount = containers.Count(control => SameIdentity(control.DataContext)),
            containers = containers.Take(40).Select(control => new
            {
                state = State(control), dataContext = SelectionIdentity(control.DataContext),
                requestedReference = ReferenceEquals(control.DataContext, requested), requestedKey = SameIdentity(control.DataContext)
            }).ToArray(),
            clippingAncestors = list.GetVisualAncestors().OfType<Control>().Take(16).Select(State).ToArray(),
            scrolls = list.GetVisualDescendants().OfType<ScrollViewer>().Take(4).Select(scroll => new
            { scroll.Name, bounds = scroll.Bounds.ToString(), viewport = scroll.Viewport.ToString(),
                extent = scroll.Extent.ToString(), offset = scroll.Offset.ToString(), scroll.ClipToBounds }).ToArray(),
            error = error?.ToString(),
            scope = "actual current item/container/geometry/focus state; no native-input or human acceptance claim"
        };
        Evidence.Add(snapshot);
        Console.WriteLine("URSA16 selection-readiness: " + JsonSerializer.Serialize(snapshot));
        SaveEvidence(error == null ? "running selection readiness" : "failed selection readiness");
    }

    private static object? ReaderControlState(Control? control) => control == null ? null : new
    {
        type = control.GetType().Name, control.Name,
        instance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(control),
        control.IsLoaded, control.IsVisible, control.IsEffectivelyVisible, control.IsEffectivelyEnabled,
        bounds = control.Bounds.ToString(), topLevel = TopLevel.GetTopLevel(control)?.GetType().Name,
        inOwner = ReferenceEquals(TopLevel.GetTopLevel(control), _window)
    };

    private static object ReaderTreeState()
    {
        Control[] live = _window.GetVisualDescendants().OfType<Control>().Where(control => control.Name is
            "ReaderHost" or "ArchiveReaderPane" or "ArchiveContentTabs" or "ArchivePosts"
            or "ArchiveMediaTiles" or "ArchiveMediaGrid" or "ArchiveMediaTab").Take(32).ToArray();
        return new
        {
            pageStillCurrent = ReferenceEquals(_page, _window.ArchivePage), page = ReaderControlState(_page),
            readerHostContent = ReaderControlState(_window.FindControl<ContentControl>("ReaderHost")?.Content as Control),
            namedMediaTiles = ReaderControlState(_page.FindControl<ListBox>("ArchiveMediaTiles")),
            namedMediaTabContent = ReaderControlState(_page.FindControl<TabItem>("ArchiveMediaTab")?.Content as Control),
            actualWindowTree = live.Select(control => new
            {
                state = ReaderControlState(control),
                selectedIndex = control is TabControl tabs ? tabs.SelectedIndex : (int?)null,
                selectedItem = control is TabControl tabControl ? ReaderControlState(tabControl.SelectedItem as Control) : null,
                selectedContent = control is TabControl currentTabs ? ReaderControlState(currentTabs.SelectedContent as Control) : null
            }).ToArray()
        };
    }

    private static void ReaderTreeSnapshot(string stage, Exception? error = null)
    {
        var snapshot = new { sequence = Evidence.Count + 1, ratingReaderTree = stage,
            language = Texts.Language, mode = _window.SessionHost.Config.WorkspaceMode,
            state = ReaderTreeState(), error = error?.ToString() };
        Evidence.Add(snapshot);
        Console.WriteLine("URSA16 reader-tree: " + JsonSerializer.Serialize(snapshot));
        SaveEvidence(error == null ? "running actual reader-tree inspection" : "failed actual reader-tree inspection");
    }

    private static TabControl CurrentReaderTabs()
    {
        TabControl[] matches = _window.GetVisualDescendants().OfType<TabControl>()
            .Where(tabs => tabs.Name == "ArchiveContentTabs" && tabs.IsLoaded && tabs.IsEffectivelyVisible).ToArray();
        Require(matches.Length == 1, "Expected one attached visible reader TabControl in the actual window tree");
        Require(ReferenceEquals(matches[0], _page.FindControl<TabControl>("ArchiveContentTabs")),
            "Actual reader TabControl must belong to the current archive page");
        return matches[0];
    }

    private static async Task<ListBox> CurrentMediaTilesAsync()
    {
        try
        {
            await Program.WaitUntilAsync(() => _window.GetVisualDescendants().OfType<ListBox>().Any(list =>
                list.Name == "ArchiveMediaTiles" && list.IsLoaded && list.IsEffectivelyVisible
                && ReferenceEquals(TopLevel.GetTopLevel(list), _window)));
            return CurrentMediaTiles();
        }
        catch (Exception error)
        {
            try { ReaderTreeSnapshot("actual media-tree readiness failure", error); } catch (Exception) { }
            try
            {
                if (_window.IsVisible) Program.CaptureWindow(_window, "smoke-rating-media-tree-readiness-failure.png");
            }
            catch (Exception) { }
            throw;
        }
    }

    private static ListBox CurrentMediaTiles()
    {
        ListBox[] matches = _window.GetVisualDescendants().OfType<ListBox>()
            .Where(list => list.Name == "ArchiveMediaTiles" && list.IsLoaded && list.IsEffectivelyVisible
                && ReferenceEquals(TopLevel.GetTopLevel(list), _window)).ToArray();
        Require(matches.Length == 1, "Expected one attached visible media list in the actual window tree");
        Require(ReferenceEquals(matches[0], _page.FindControl<ListBox>("ArchiveMediaTiles")),
            "Actual media list must belong to the current archive page");
        return matches[0];
    }
}
