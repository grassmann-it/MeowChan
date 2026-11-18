using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class MediaSelectionCounterEvidenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1099)]
    [InlineData(1100)]
    public void ExactSelectionCountRetainsSingleLineStructureReservationAndFullAccessibleClearName(int count) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        MediaPage page = new(_ => throw new InvalidOperationException("No archive access in the counter contract."));
        try
        {
            MediaLibraryEntry[] entries = Enumerable.Range(0, 1100).Select(index => new MediaLibraryEntry(
                BooruMediaIdentity.Thread(index.ToString()), "Counter fixture", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                null, null, null, null, null, "/synthetic/not-opened", "asset-" + index, new string('b', 64), 7,
                BooruMediaIdentity.MediaIdentifier(index.ToString()), "original", index + ".png", SavedMediaKind.Image,
                MediaLibraryPresence.Present, true, false)).ToArray();
            MediaLibraryTile[] tiles = entries.Select(entry => new MediaLibraryTile(entry, [entry])).ToArray();
            Set("_tiles", tiles);
            Set("_shown", entries);
            page.Grid.ItemsSource = tiles.Select((tile, index) => new MediaLibraryTileRow(tile, index)).ToArray();
            page.Grid.Selection.SelectRange(0, count - 1);
            Assert.Equal(count, page.Grid.Selection.Count); // never label a clamped fixture as a passing 1100 selection
            ReservedTextBlock counter = Get<ReservedTextBlock>("_selectionText");
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                Invoke("LocalizeSelection");
                Assert.Equal(Texts.T("media.selection.count", count), counter.Text);
                Assert.Equal(Texts.T("media.selection.count", 8888), counter.ReserveText);
                Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, counter.TextWrapping);
                Assert.Equal(Orientation.Horizontal, Get<StackPanel>("_selectionActions").Orientation);
                // Exercise every pressure level, not just whichever label happened to fit this machine's fonts.
                for (int level = 0; level <= 4; level++)
                {
                    Set("_selectionLevel", level);
                    Invoke("ApplyClearLabel");
                    Button clear = Get<Button>("_selClear");
                    Assert.Equal(Texts.T(level == 0 ? "media.selection.clear" : "media.selection.clear.short"), clear.Content);
                    Assert.Equal(Texts.T("media.selection.clear"), AutomationProperties.GetName(clear));
                    Assert.Equal(Texts.T("media.selection.clear"), ToolTip.GetTip(clear));
                }
            }
            void Set(string name, object value) => typeof(MediaPage).GetField(name, Private)!.SetValue(page, value);
            T Get<T>(string name) => (T)typeof(MediaPage).GetField(name, Private)!.GetValue(page)!;
            void Invoke(string name) => typeof(MediaPage).GetMethod(name, Private)!.Invoke(page, null);
        }
        finally { page.EndLifetime(); }
    });
}
