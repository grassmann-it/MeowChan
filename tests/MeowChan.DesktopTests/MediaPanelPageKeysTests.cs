using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Media page audit 16.11.2026: the page's own keys (R, T, Q, I, Primary+E, Primary+O, Primary+Shift+C, Primary+R, Shift+0 to 5) with the keyboard focus inside the media
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
    [InlineData("Primary+E", "media.export-original")]
    [InlineData("Primary+O", "media.show-in-folder")]
    [InlineData("Primary+Shift+C", "media.copy-path")]
    [InlineData("I", "media.details")]
    public void TheMenuCommandKeysRunOnThePanelsMediumWithTheFocusInThePanel(string shortcut, string id) => WithPanel((fixture, recorder, open) =>
    {
        KeyGesture gesture = ShortcutGesture.Parse(shortcut).Resolve(EffectiveShortcutBindings.CurrentPlatform);
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyModifiers other = OperatingSystem.IsMacOS() ? KeyModifiers.Control : KeyModifiers.Meta;
        KeyModifiers wrong = shortcut.Contains("Primary") ? (gesture.KeyModifiers & ~primary) | other : KeyModifiers.Shift;
        fixture.Press(open, gesture.Key, wrong);
        Assert.False(fixture.LastHandled);
        Assert.Empty(recorder.Actions);
        Assert.Empty(recorder.Threads);
        Assert.Empty(recorder.Queued);

        fixture.Press(open, gesture.Key, gesture.KeyModifiers);
        Assert.True(fixture.LastHandled);
        (string actual, MediaLibraryEntry entry) = Assert.Single(recorder.Actions);
        Assert.Equal(id, actual);
        Assert.Same(fixture.Entries[1], entry);
    });

    [Theory]
    [InlineData(ShortcutPlatform.Windows, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.Linux, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta)]
    public void PanelMenuBindingsRequireTheExactPlatformModifiers(ShortcutPlatform platform, KeyModifiers primary)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        foreach (var (id, key, modifiers) in new[]
        {
            ("media.export-original", Key.E, primary),
            ("media.show-in-folder", Key.O, primary),
            ("media.copy-path", Key.C, primary | KeyModifiers.Shift),
            ("media.details", Key.I, KeyModifiers.None)
        })
        {
            KeyGesture[] gestures = bindings.For(id).Select(g => g.Resolve(platform)).ToArray();
            KeyModifiers[] expected = id == "media.details" && platform == ShortcutPlatform.MacOS
                ? [KeyModifiers.None, KeyModifiers.Meta] : [modifiers]; // macOS also offers Command+I; bare I remains valid.
            Assert.Equal(expected, gestures.Select(g => g.KeyModifiers));
            Assert.All(gestures, gesture =>
            {
                Assert.Equal(key, gesture.Key);
                Assert.True(bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = gesture.KeyModifiers }, id, "media"));
            });
            Assert.True(bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = modifiers }, id, "media"));
            KeyModifiers other = primary == KeyModifiers.Meta ? KeyModifiers.Control : KeyModifiers.Meta;
            KeyModifiers wrong = modifiers == KeyModifiers.None ? KeyModifiers.Shift : (modifiers & ~primary) | other;
            Assert.False(bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = wrong }, id, "media"));
            Assert.False(bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = modifiers | KeyModifiers.Alt }, id, "media"));
        }
    }

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
    [InlineData("TextBox", "I")]
    [InlineData("ComboBox", "I")]
    [InlineData("NumericUpDown", "Shift+2")]
    [InlineData("TextBox", "Primary+E")]
    public void SharedPanelCommandsDoNotEscapeNativeEditors(string kind, string shortcut) => WithPanel((fixture, recorder, _) =>
    {
        Control editor = kind switch
        {
            "TextBox" => new TextBox(), "ComboBox" => new ComboBox(), "NumericUpDown" => new NumericUpDown(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        fixture.PanelView!.VideoRow.Children.Add(editor);
        KeyGesture gesture = ShortcutGesture.Parse(shortcut).Resolve(EffectiveShortcutBindings.CurrentPlatform);
        fixture.Press(editor, gesture.Key, gesture.KeyModifiers);
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
