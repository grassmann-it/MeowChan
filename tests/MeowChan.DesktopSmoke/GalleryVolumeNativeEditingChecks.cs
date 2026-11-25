using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Attached, laid-out routed-event boundary, NOT OS input or full GalleryViewer playback.
/// Root hook (UI thread, isolated desktop): await GalleryVolumeNativeEditingChecks.RunAsync(owner).
/// No media, player, profile, effective-binding changes or reflection.
/// </summary>
internal static class GalleryVolumeNativeEditingChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (OperatingSystem.IsLinux()
            ? Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            : Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") != "1")
            throw new PlatformNotSupportedException("Requires Root's disposable authorized desktop.");
        Require(!ShortcutService.IsCapturing, "An unrelated shortcut capture is already active.");

        const string volumeUp = "video.volume-up";
        const string volumeDown = "video.volume-down";
        (Key Key, string Command, int Direction)[] arrows = [(Key.Up, volumeUp, 1), (Key.Down, volumeDown, -1)];
        var seek = new GalleryTransportSlider { Minimum = 0, Maximum = 100, Value = 40, Width = 320 };
        var volume = new GalleryVolumeSlider { Minimum = 0, Maximum = 100, Value = 60, SmallChange = 5, Width = 320 };
        var nativeSlider = new Slider { Minimum = 0, Maximum = 100, Value = 50, SmallChange = 2, Width = 320 };
        var content = new StackPanel { Children = { seek, volume, nativeSlider } };
        var window = new Window
        {
            Title = "Synthetic gallery transport/editor routed boundary (no media)",
            Width = 540, Height = 360, Content = content, ShowInTaskbar = false
        };
        int accepted = 0, genericExecutions = 0, tunnelVisits = 0, bubbleVisits = 0;
        bool lastAccepted = false, lastBubbleHandled = false;
        using var router = new CommandRouter(window);
        using var upRegistration = router.Register(new CommandRegistration(volumeUp, window,
            () => true, () => true, () => genericExecutions++));
        using var downRegistration = router.Register(new CommandRegistration(volumeDown, window,
            () => true, () => true, () => genericExecutions++));

        // The synthetic owner applies the admitted step to the fixture's volume slider.
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            tunnelVisits++;
            lastAccepted = (e.Key is Key.Up or Key.Down)
                && CommandBindings.MatchesGalleryTransport(e, e.Key == Key.Up ? volumeUp : volumeDown, seek, volume);
            if (!lastAccepted) return;
            accepted++;
            volume.Value += (e.Key == Key.Up ? 1 : -1) * volume.SmallChange;
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            router.Route(e);
            bubbleVisits++;
            lastBubbleHandled = e.Handled;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        async Task PressAsync(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None, bool prehandled = false)
        {
            Require(source.Focus(), "Real focus acquisition failed.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), source), "Focus was not retained.");
            Require(ReferenceEquals(TopLevel.GetTopLevel(source), window), "Source detached from native root.");
            Require(CommandRouter.IsRoutingContextActive(window, source), "Routing context is inactive.");
            int tunnelBefore = tunnelVisits, bubbleBefore = bubbleVisits;
            var press = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Source = source,
                Key = key, KeyModifiers = modifiers, Handled = prehandled
            };
            source.RaiseEvent(press); // Actual Avalonia tunnel/class-handler/bubble dispatch.
            Require(tunnelVisits == tunnelBefore + 1 && bubbleVisits == bubbleBefore + 1,
                "Key did not traverse exactly one owner tunnel and bubble.");
            Require(press.Handled == lastBubbleHandled, "Terminal handled observation disagrees.");
        }

        try
        {
            window.Show(owner);
            window.Activate();
            await Program.WaitUntilAsync(() => window.IsActive && seek.Bounds.Width > 0);
            var macBindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
            foreach (var (key, command, direction) in arrows)
            {
                var bare = new KeyEventArgs { Key = key };
                var shifted = new KeyEventArgs { Key = key, KeyModifiers = KeyModifiers.Shift };
                Require(ShortcutService.Current.Matches(shifted, command) && !ShortcutService.Current.Matches(bare, command),
                    $"The isolated profile must bind Shift+{key} to {command} and leave the bare arrow free of that command.");
                Require(macBindings.Matches(shifted, command) && !macBindings.Matches(bare, command),
                    $"The macOS defaults must bind Shift+{key} to {command} and leave the bare arrow free of that command.");

                var transportBefore = (Seek: seek.Value, Volume: volume.Value);
                double nativeBefore = nativeSlider.Value;
                int before = accepted;
                await PressAsync(nativeSlider, key);
                Require(!lastAccepted && accepted == before && lastBubbleHandled,
                    $"The native slider must handle bare {key} independently of gallery volume.");
                double nativeAfter = nativeSlider.Value;
                Require(nativeAfter == nativeBefore + direction * nativeSlider.SmallChange
                    && (seek.Value, volume.Value) == transportBefore,
                    $"Bare {key} must change only the native slider by one step.");
                await PressAsync(nativeSlider, key, KeyModifiers.Shift);
                Require(!lastAccepted && accepted == before && nativeSlider.Value == nativeAfter
                    && (seek.Value, volume.Value) == transportBefore,
                    $"Shift+{key} escaped the native slider boundary.");

                foreach (Control transport in new Control[] { seek, volume })
                {
                    transportBefore = (seek.Value, volume.Value);
                    before = accepted;
                    await PressAsync(transport, key);
                    Require(!lastAccepted && accepted == before && (seek.Value, volume.Value) == transportBefore,
                        $"Bare {key} changed the gallery transport.");

                    await PressAsync(transport, key, KeyModifiers.Shift);
                    Require(lastAccepted && accepted == before + 1 && lastBubbleHandled,
                        $"The synthetic owner must admit and consume Shift+{key} from the gallery transport.");
                    Require(volume.Value == transportBefore.Volume + direction * volume.SmallChange
                        && seek.Value == transportBefore.Seek && nativeSlider.Value == nativeAfter,
                        $"Shift+{key} must change only gallery volume by one step.");

                    var transportAfter = (seek.Value, volume.Value);
                    await PressAsync(transport, key, KeyModifiers.Shift, prehandled: true);
                    Require(!lastAccepted && accepted == before + 1, "Prehandled event escaped admission guard.");
                    using (ShortcutService.BeginCapture())
                    {
                        await PressAsync(transport, key, KeyModifiers.Shift);
                        Require(!lastAccepted && accepted == before + 1, "Capture escaped admission guard.");
                    }
                    Require((seek.Value, volume.Value) == transportAfter,
                        "A prehandled or captured volume gesture changed a transport value.");
                }
            }

            // Synthetic template only: production's shipped sliders do not contain
            // this TextBox. Use an actual native TextBox template/presenter, not a
            // Border masquerading as editor source or a property-only matcher call.
            foreach (Slider transport in new Slider[] { seek, volume })
            {
                var editor = new TextBox
                {
                    Text = "alpha line\nbeta line\ngamma line", AcceptsReturn = true,
                    Height = 110, Width = 300, TextWrapping = Avalonia.Media.TextWrapping.NoWrap
                };
                TextPresenter? presenter = null;
                editor.TemplateApplied += (_, e) => presenter = e.NameScope.Get<TextPresenter>("PART_TextPresenter");
                var previousTemplate = transport.Template;
                transport.Template = new FuncControlTemplate<Slider>((_, _) => editor);
                try
                {
                    window.UpdateLayout();
                    await Program.WaitUntilAsync(() => editor.GetVisualAncestors().Contains(transport)
                        && presenter is { Bounds.Height: > 0 } && editor.Bounds.Height > 0);
                    foreach (var (key, _, _) in arrows)
                    foreach (var modifiers in new[] { KeyModifiers.None, KeyModifiers.Shift })
                    {
                        // Shift+arrow moves the presenter to SelectionEnd while CaretIndex stays at the anchor.
                        // Collapse at the active end first so resetting to the middle line also moves the presenter.
                        const int middle = 14;
                        editor.CaretIndex = editor.SelectionEnd;
                        editor.CaretIndex = middle;
                        Require(editor.CaretIndex == middle && presenter!.CaretIndex == middle
                            && editor.SelectionStart == middle && editor.SelectionEnd == middle,
                            $"Could not establish a collapsed caret on the middle line before {modifiers}+{key}.");
                        int caretBefore = editor.CaretIndex, before = accepted;
                        double seekBefore = seek.Value, volumeBefore = volume.Value;
                        await PressAsync(editor, key, modifiers);
                        Require(!lastAccepted && accepted == before, "Nearer TextBox lost its native boundary.");
                        bool selecting = modifiers == KeyModifiers.Shift;
                        int endpoint = selecting ? editor.SelectionEnd : editor.CaretIndex;
                        string observation = $"{transport.GetType().Name} {modifiers}+{key} from {caretBefore}: "
                            + $"caret={editor.CaretIndex}, selection={editor.SelectionStart}..{editor.SelectionEnd}, presenter={presenter!.CaretIndex}";
                        Console.WriteLine("GALLERY_VOLUME_EDITOR " + observation);
                        Require(key == Key.Up ? endpoint < caretBefore : endpoint > caretBefore,
                            "Native TextBox did not move in the dispatched arrow direction: " + observation);
                        Require(selecting ? editor.SelectionStart == caretBefore : editor.SelectionStart == editor.SelectionEnd,
                            "Native TextBox selection disagreed with the arrow modifiers.");
                        Require(editor.Text == "alpha line\nbeta line\ngamma line"
                            && seek.Value == seekBefore && volume.Value == volumeBefore,
                            "Editing arrow changed text or transport value.");
                        // Do not demand !Handled: native TextBox/ancestor slider may consume.
                    }
                }
                finally { transport.Template = previousTemplate; window.UpdateLayout(); }
            }
            Require(genericExecutions == 0, "Generic CommandRouter stole native transport/editor arrows.");
            Console.WriteLine("GALLERY_VOLUME_ROUTED_BOUNDARY PASS synthetic-owner; real Slider/TextBox dispatch; no playback/OS-input claim");
            Console.WriteLine("GALLERY_VOLUME_PLAYBACK_CONTINUITY NOT_COVERED: active video/audio required; no seam fabricated");
        }
        finally { window.Close(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Gallery volume routed boundary: " + message);
    }
}
