using Avalonia.Controls;
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

        var seek = new GalleryTransportSlider { Minimum = 0, Maximum = 100, Value = 40, Width = 320 };
        var volume = new GalleryVolumeSlider { Minimum = 0, Maximum = 100, Value = 60, Width = 320 };
        var content = new StackPanel { Children = { seek, volume } };
        var window = new Window
        {
            Title = "Synthetic gallery transport/editor routed boundary (no media)",
            Width = 540, Height = 360, Content = content, ShowInTaskbar = false
        };
        int accepted = 0, genericExecutions = 0, tunnelVisits = 0, bubbleVisits = 0;
        bool lastAccepted = false, lastBubbleHandled = false;
        using var router = new CommandRouter(window);
        using var registration = router.Register(new CommandRegistration("video.volume-up", window,
            () => true, () => true, () => genericExecutions++));

        // Explicit synthetic owner adapter. This is deliberately NOT presented as
        // GalleryViewer.OnKeyDown: that handler requires an actual selected video/player.
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            tunnelVisits++;
            lastAccepted = CommandBindings.MatchesGalleryTransport(e,
                e.Key == Key.Up ? "video.volume-up" : "video.volume-down", seek, volume);
            if (lastAccepted) { accepted++; e.Handled = true; }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            router.Route(e);
            bubbleVisits++;
            lastBubbleHandled = e.Handled;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        async Task PressAsync(Control source, Key key, bool prehandled = false)
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
                Key = key, KeyModifiers = KeyModifiers.None, Handled = prehandled
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
            foreach (Control transport in new Control[] { seek, volume })
            foreach (Key key in new[] { Key.Up, Key.Down })
            {
                Require(ShortcutService.Current.Matches(new KeyEventArgs { Key = key },
                    key == Key.Up ? "video.volume-up" : "video.volume-down"),
                    "Root must supply default Up/Down bindings; helper does not rewrite configuration.");
                double seekBefore = seek.Value, volumeBefore = volume.Value;
                int before = accepted;
                await PressAsync(transport, key);
                Require(lastAccepted && accepted == before + 1 && lastBubbleHandled,
                    "Owned transport was not admitted/consumed by the synthetic owner adapter.");
                Require(seek.Value == seekBefore && volume.Value == volumeBefore,
                    "Native slider class handler changed a command-owned arrow value.");
                await PressAsync(transport, key, prehandled: true);
                Require(!lastAccepted && accepted == before + 1, "Prehandled event escaped admission guard.");
                using (ShortcutService.BeginCapture())
                {
                    await PressAsync(transport, key);
                    Require(!lastAccepted && accepted == before + 1, "Capture escaped admission guard.");
                }
                Require(seek.Value == seekBefore && volume.Value == volumeBefore,
                    "Suppressed arrow fell back to native slider value editing.");
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
                var previousTemplate = transport.Template;
                transport.Template = new FuncControlTemplate<Slider>((_, _) => editor);
                try
                {
                    window.UpdateLayout();
                    await Program.WaitUntilAsync(() => editor.GetVisualAncestors().Contains(transport)
                        && editor.GetVisualDescendants().Any() && editor.Bounds.Height > 0);
                    foreach (Key key in new[] { Key.Up, Key.Down })
                    {
                        editor.CaretIndex = 14; // Middle line, movement possible in both directions.
                        editor.SelectionStart = editor.SelectionEnd = 14;
                        int caretBefore = editor.CaretIndex, before = accepted;
                        double seekBefore = seek.Value, volumeBefore = volume.Value;
                        await PressAsync(editor, key);
                        Require(!lastAccepted && accepted == before, "Nearer TextBox lost its native boundary.");
                        Require(key == Key.Up ? editor.CaretIndex < caretBefore : editor.CaretIndex > caretBefore,
                            "Native TextBox did not move its caret in the dispatched arrow direction.");
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
