using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Media page audit 16.11.2026: the page's own keys (R, T, Q, I, Ctrl+E, Ctrl+O, Ctrl+Shift+C, Ctrl+R, Shift+0 to 5) with the keyboard focus inside the media
// panel (after a click on its star or its "Open in gallery"). They were routed only from the grid's bubble handler, and the panel is not on that route, so each
// of them did nothing there. The panel's tunnel handler now takes them for the medium the panel shows. The page is built for real (the fixture of the tile Enter
// tests); the panel is shown by its own state, the key is raised on the panel's real "Open in gallery" button.
[Collection("Booru exact navigation")]
public sealed class MediaPanelPageKeysTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class Recorder
    {
        internal List<(string Id, MediaLibraryEntry Entry)> Actions { get; } = new();
        internal List<MediaLibraryEntry> Threads { get; } = new();
        internal List<IReadOnlyList<MediaLibraryEntry>> Queued { get; } = new();
    }

    private static void WithPanel(Action<MediaTileEnterTests.Fixture, Recorder, Control> test) => MediaTileEnterTests.Run(fixture =>
    {
        Recorder recorder = new();
        MediaPage page = fixture.Page;
        page.ExecuteMediaMenuActionAsync = (id, entry, _) => { recorder.Actions.Add((id, entry)); return Task.CompletedTask; };
        page.ShowInThread = entry => { recorder.Threads.Add(entry); return Task.CompletedTask; };
        page.AddToTagQueue = entries => { recorder.Queued.Add(entries); return Task.FromResult("queued"); };
        fixture.Grid.SelectedIndex = 1;
        Control open = fixture.ShowPanelOn(1);
        test(fixture, recorder, open);
    });

    [Fact]
    public void TheShowInThreadKeyActsOnThePanelsMediumWithTheFocusInThePanel() => WithPanel((fixture, recorder, open) =>
    {
        fixture.Press(open, Key.T);

        Assert.True(fixture.LastHandled);
        Assert.Same(fixture.Entries[1], Assert.Single(recorder.Threads));
    });

    [Theory]
    [InlineData(Key.E, KeyModifiers.Control, "media.export-original")]
    [InlineData(Key.O, KeyModifiers.Control, "media.show-in-folder")]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift, "media.copy-path")]
    [InlineData(Key.I, KeyModifiers.None, "media.details")]
    public void TheMenuCommandKeysRunOnThePanelsMediumWithTheFocusInThePanel(Key key, KeyModifiers modifiers, string id) => WithPanel((fixture, recorder, open) =>
    {
        fixture.Press(open, key, modifiers);

        Assert.True(fixture.LastHandled);
        (string actual, MediaLibraryEntry entry) = Assert.Single(recorder.Actions);
        Assert.Equal(id, actual);
        Assert.Same(fixture.Entries[1], entry);
    });

    [Theory]
    [InlineData(Key.D0, "media.rating.clear")]
    [InlineData(Key.D1, "media.rating.set-1")]
    [InlineData(Key.D2, "media.rating.set-2")]
    [InlineData(Key.D3, "media.rating.set-3")]
    [InlineData(Key.D4, "media.rating.set-4")]
    [InlineData(Key.D5, "media.rating.set-5")]
    public void DirectRatingsUseThePanelsRowNotAnUnrelatedGridSelection(Key key, string id) => WithPanel((fixture, recorder, _) =>
    {
        fixture.Rows[1] = fixture.Rows[1] with
        {
            StateOf = _ => new MediaLibraryEntryState(MediaLibraryFlag.No, true, null, false, [], [], null)
        };
        fixture.Grid.SelectedIndex = 0;
        Control open = fixture.ShowPanelOn(1);
        fixture.Press(open, key, KeyModifiers.Shift);
        Assert.True(fixture.LastHandled);
        (string actual, MediaLibraryEntry entry) = Assert.Single(recorder.Actions);
        Assert.Equal(id, actual);
        Assert.Same(fixture.Entries[1], entry);
        Assert.Equal(0, fixture.Grid.SelectedIndex);
    });

    [Theory]
    [InlineData("TextBox", Key.I, KeyModifiers.None)]
    [InlineData("ComboBox", Key.I, KeyModifiers.None)]
    [InlineData("NumericUpDown", Key.D2, KeyModifiers.Shift)]
    [InlineData("TextBox", Key.E, KeyModifiers.Control)]
    public void SharedPanelCommandsDoNotEscapeNativeEditors(string kind, Key key, KeyModifiers modifiers) => WithPanel((fixture, recorder, _) =>
    {
        Control editor = kind switch
        {
            "TextBox" => new TextBox(), "ComboBox" => new ComboBox(), "NumericUpDown" => new NumericUpDown(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        fixture.PanelView!.VideoRow.Children.Add(editor);
        fixture.Press(editor, key, modifiers);
        Assert.Empty(recorder.Actions);
        Assert.Empty(recorder.Threads);
    });

    [Fact]
    public void TheQueueKeyAddsThePanelsMediumToTheTaggingQueueWithTheFocusInThePanel() => WithPanel((fixture, recorder, _) =>
    {
        Control open = fixture.ShowPanelOn(MediaTileEnterTests.Fixture.LocalIndex);

        fixture.Press(open, Key.Q);

        Assert.True(fixture.LastHandled);
        Assert.Same(fixture.Entries[MediaTileEnterTests.Fixture.LocalIndex], Assert.Single(Assert.Single(recorder.Queued)));
    });

    [Fact]
    public void TheQueueKeyLeavesAProviderMediumOutOfTheQueue() => WithPanel((fixture, recorder, open) =>
    {
        fixture.Press(open, Key.Q);

        Assert.True(fixture.LastHandled);
        Assert.Empty(recorder.Queued);
    });

    [Fact]
    public void EnterAndSpaceStayWithTheFocusedPanelButton() => WithPanel((fixture, recorder, open) =>
    {
        fixture.Press(open, Key.Enter);
        fixture.Press(open, Key.Space);

        Assert.False(fixture.LastHandled);
        Assert.Empty(recorder.Actions);
        Assert.Empty(recorder.Threads);
        Assert.Empty(recorder.Queued);
    });

    [Fact]
    public void ATextFieldInThePanelKeepsItsLetters() => WithPanel((fixture, recorder, _) =>
    {
        TextBox field = new();
        fixture.PanelView!.VideoRow.Children.Add(field);

        fixture.Press(field, Key.T);

        Assert.Empty(recorder.Threads);
    });
}
