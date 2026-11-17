using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MeowChan.DesktopSmoke;

internal static partial class ReviewArtifacts
{
    private static readonly long Epoch = Stopwatch.GetTimestamp();
    private static double Now => Stopwatch.GetElapsedTime(Epoch).TotalMilliseconds;
    private static int _gestureDepth;

    // The caller owns completion: reuse the scene's real waits/assertions, never an invented sleep.
    // ack must describe visible feedback, not simply a completed task or an input event.
    internal static async Task GestureAsync(Window window, string name, Action input, Func<bool> ack, Func<Task> settle) =>
        await GestureAsync(window, name, () => { input(); return Task.CompletedTask; }, ack, settle);

    internal static async Task GestureAsync(Window window, string name, Func<Task> input, Func<bool> ack, Func<Task> settle)
    {
        if (string.IsNullOrWhiteSpace(Output)) { await input(); await settle(); return; }
        if (_gestureDepth != 0) throw new InvalidOperationException("Review gestures must not overlap.");
        _gestureDepth++;
        using var heartbeat = new Heartbeat();
        double? firstAck = null;
        double started = Now;
        bool observing = true;
        var focus = new List<object>();
        void RecordFocus(Control? control) => focus.Add(new
        {
            t = Now, control = control == null ? null : Identity(control),
            accessible_name = control == null ? null : AutomationProperties.GetName(control),
            scope = control == null ? null : TopLevel.GetTopLevel(control)?.GetType().Name
        });
        void Focused(object? sender, FocusChangedEventArgs e) => RecordFocus(e.Source as Control);
        RecordFocus(window.FocusManager?.GetFocusedElement() as Control);
        window.AddHandler(InputElement.GotFocusEvent, Focused, RoutingStrategies.Bubble, handledEventsToo: true);
        var finalFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool completing = false;
        void Frame(TimeSpan _)
        {
            if (!observing) return;
            // A rendering opportunity with the scene's feedback predicate true, NOT compositor/presentation latency.
            if (firstAck == null && ack()) firstAck = Now;
            if (completing) finalFrame.TrySetResult();
            else window.RequestAnimationFrame(Frame);
        }
        window.RequestAnimationFrame(Frame);
        string? failure = null;
        try
        {
            await input();
            await settle();
            completing = true;
            await finalFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; throw; }
        finally
        {
            double final = Now;
            observing = false;
            window.RemoveHandler(InputElement.GotFocusEvent, Focused);
            heartbeat.Stop();
            _gestureDepth--;
            Capture(window, name, new
            {
                schema_version = 1, kind = "gesture", gesture = name, clock = "monotonic-ms-process-epoch",
                t_input = started, t_first_visual_ack = firstAck, t_final = final,
                ack_observation = "scene-visible-feedback predicate at Avalonia animation-frame callback; not native presentation",
                ack_unobserved_reason = firstAck == null ? "No matching visual feedback observed before completion" : null,
                failure, heartbeat = heartbeat.Report(), focus_trace = focus,
                memory_high_water_bytes = Process.GetCurrentProcess().PeakWorkingSet64
            });
        }
    }

    private sealed class Heartbeat : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly List<double> _samples = new() { Now };
        internal Heartbeat()
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Normal, (_, _) => _samples.Add(Now));
            _timer.Start();
        }
        internal void Stop() { _timer.Stop(); _samples.Add(Now); }
        internal object Report() => new
        {
            interval_ms = 25, stall_threshold_ms = 100, samples = _samples,
            stalls = _samples.Zip(_samples.Skip(1), (a, b) => new { t_start = a, t_end = b, gap_ms = b - a }).Where(g => g.gap_ms > 100).ToArray(),
            coverage = "gesture input through completion; screenshot/tree/JSON writing excluded"
        };
        public void Dispose() => _timer.Stop();
    }
}
