using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaPageChecks
{
    // Intentionally separate from the broad media-page journey: three still images,
    // no video decoder, network, toolbar/layout matrix or full smoke baseline.
    internal static async Task RunClickContractsAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("focus-click-contracts-");
        Texts.SetLanguage("en");
        try
        {
            new Config().SaveConfig(profile);
            await SeedAsync(profile);
            await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
            await host.StartAsync().WaitAsync(Deadline);
            List<(IReadOnlyList<MediaLibraryEntry> Window, int Start)> opened = new();
            MediaPage page = new(host.OpenArchiveReaderAsync)
            {
                OpenGallery = (entries, start) => { opened.Add((entries, start)); return Task.CompletedTask; }
            };
            page.InitializeSharedMediaMenus();
            Window window = new() { Width = 1400, Height = 900, Content = page, ShowInTaskbar = false };
            try
            {
                window.Show(owner);
                page.IsAvailable = true;
                await page.ActivateAsync().WaitAsync(Deadline);
                Require(page.Grid.ItemCount == 3, "Click contracts require exactly three distinct saved tiles.");
                await ClickTileAsync(page, window, 0);
                Require(page.Grid.SelectedIndex == 0 && opened.Count == 0, "Single-click must select without opening gallery.");
                Require((page.Grid.ContainerFromIndex(0) as Control)?.IsKeyboardFocusWithin == true,
                    "Single-click did not focus the clicked tile.");

                // Right-click outside the old two-item selection must replace it.
                page.Grid.Selection.Select(1);
                await RightContractClickAsync(await ClickContractTileAsync(page, 2), window);
                Require(page.Grid.Selection.Count == 1 && page.Grid.SelectedIndex == 2,
                    "Right-click outside selection must select only the target, before composing the menu.");
                Require(page.MediaMenu is { IsOpen: true }, "Right-click did not open the media menu.");
                page.MediaMenu!.Close();
                await UntilAsync(() => (page.Grid.ContainerFromIndex(2) as Control)?.IsKeyboardFocusWithin == true);

                // Inside a multi-selection the group stays, but focus moves to the hit tile.
                page.Grid.Selection.Select(0);
                await RightContractClickAsync(await ClickContractTileAsync(page, 0), window);
                Require(page.Grid.Selection.SelectedIndexes.Order().SequenceEqual(new[] { 0, 2 }),
                    "Right-click inside a multi-selection must preserve it.");
                Require(page.MediaMenu is { IsOpen: true }, "Second right-click did not open the media menu.");
                page.MediaMenu!.Close();
                await UntilAsync(() => (page.Grid.ContainerFromIndex(0) as Control)?.IsKeyboardFocusWithin == true);
                Require(opened.Count == 0, "Right-click must not open gallery.");

                // Existing related gesture smoke: double-click target, Enter parity,
                // interactive child/empty-space shielding and tile-size input.
                await TileActivationParityAsync(page, window, opened);

                // The authoritative sidebar contract beats the old Gallery reader
                // preference, without changing Reader media activation itself.
                Config config = new();
                config.ChooseReaderMediaClick(ReaderMediaClickMode.Gallery);
                page.ConfigurePanel(ReaderInlineVideoOptions.From(config), new GalleryFitPreferences());
                page.Grid.SelectedIndex = 0;
                await UntilAsync(() => page.IsPanelShown && page.Panel is { Shown: not null } && page.PanelTask.IsCompleted);
                ReaderMediaSidebar panel = page.Panel!;
                int count = opened.Count;
                await LeftContractClickAsync(panel.Frame, window);
                Require(opened.Count == count && panel.Frame.IsKeyboardFocusWithin,
                    "Media sidebar single-click must only preview/focus even with Gallery reader preference.");
                await DoubleClickAsync(panel.Frame, window);
                Require(opened.Count == count + 1 && opened[^1].Window[opened[^1].Start] == page.PanelRow!.Entry,
                    "Media sidebar double-click must open gallery at the previewed medium.");

                Console.WriteLine("SMOKE focus-click-contracts: Media grid single/double/right clicks; outside-selection replacement, inside-selection preservation, menu target focus after close; sidebar single-click stays preview with Gallery preference, double-click opens gallery. Managed pointer input only.");
            }
            finally { page.EndLifetime(); window.Close(); }
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task<Control> ClickContractTileAsync(MediaPage page, int index)
    {
        page.Grid.ScrollIntoView(index);
        Control? tile = null;
        await UntilAsync(() => (tile = page.Grid.ContainerFromIndex(index)) is
            { IsArrangeValid: true, IsEffectivelyVisible: true, Bounds.Width: > 0, DataContext: MediaLibraryTileRow row }
            && row.Index == index);
        return tile!;
    }

    private static Task RightContractClickAsync(Control target, Window root) => ContractClickAsync(target, root, right: true);
    private static Task LeftContractClickAsync(Control target, Window root) => ContractClickAsync(target, root, right: false);

    private static async Task ContractClickAsync(Control target, Window root, bool right)
    {
        Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, Math.Min(55, target.Bounds.Height / 2)), root)
            ?? throw new InvalidOperationException("Click target detached during coordinate conversion.");
        // Managed routed input with real item ancestry, like TileActivationParityAsync.
        // This does not claim XTest or native OS focus acceptance.
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, root, at, 100,
            new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
                right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, root, at, 110,
            new PointerPointProperties(RawInputModifiers.None,
                right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, right ? MouseButton.Right : MouseButton.Left));
        pointer.Capture(null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
