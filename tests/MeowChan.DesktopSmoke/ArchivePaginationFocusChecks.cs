using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Testing;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

internal static class ArchivePaginationFocusChecks
{
    internal static async Task RunAsync(MainWindow owner, Action<bool, string, object?> check)
    {
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "archive-pagination-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ArchivePage? page = null;
        Window? host = null;
        TaskCompletionSource? release = null, entered = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(directory);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
            for (int i = 0; i < 60; i++)
                await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId("focus", "x", i.ToString()),
                    "https://pagination.fixture.invalid/" + i, i.ToString("D3"), directory, true));
            page = new ArchivePage(async token =>
            {
                Task? pending = release?.Task;
                if (pending != null)
                {
                    entered!.TrySetResult();
                    await pending.WaitAsync(token);
                }
                return await ArchiveReader.OpenAsync(directory, token);
            }, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            host = new Window
            {
                Title = "Archive pagination focus — diagnostic", Width = 1100, Height = 800,
                Content = page, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            host.Show(owner);
            host.Activate();
            await Program.WaitUntilAsync(() => host.IsActive && page.IsEffectivelyVisible && page.Bounds.Width > 0);
            if (!await page.ApplySearchSpecAsync(new ArchiveSearchSpec(sort: ArchiveThreadSort.Title)))
                throw new InvalidOperationException("Pagination focus setup query failed.");
            Pagination navigation = page.FindControl<Pagination>("ArchiveResultsPagination")!;
            TextBox search = page.FindControl<TextBox>("ArchiveSearchText")!;
            await Program.WaitUntilAsync(() => navigation.IsEffectivelyVisible && navigation.CurrentPage == 1);
            await Dispatcher.UIThread.InvokeAsync(host.UpdateLayout, DispatcherPriority.Background);

            object State(string phase, int target) => new
            {
                phase, target, page.IsSearchLoading, page.IsBusy, currentPage = navigation.CurrentPage,
                paginationFocusWithin = navigation.IsKeyboardFocusWithin, searchFocused = search.IsFocused,
                searchEnabled = search.IsEffectivelyEnabled, searchText = search.Text,
                focused = host.FocusManager?.GetFocusedElement() is Control focused
                    ? new { type = focused.GetType().Name, focused.Name, page = (focused as PaginationButton)?.Page }
                    : null,
                host.IsActive
            };
            void Trace(string phase, int target) => Console.WriteLine("SMOKE pagination-focus " + JsonSerializer.Serialize(State(phase, target)));
            async Task Case(string name, string partName, int target, bool moveFocus, Key? activationKey = null, bool programmatic = false, bool holdPointer = false)
            {
                PaginationButton? action = null;
                await Program.WaitUntilAsync(() => (action = navigation.GetVisualDescendants().OfType<PaginationButton>()
                    .FirstOrDefault(button => button.Name == partName && button.IsEffectivelyVisible && button.IsEffectivelyEnabled)) != null);
                if (!action!.Focus()) throw new InvalidOperationException(name + ": native pagination action refused focus.");
                await Program.WaitUntilAsync(() => action.IsFocused && navigation.IsKeyboardFocusWithin);
                Trace(name + ": before action", target);
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                // Exercise actual activation before Ursa disables the origin.
                // The final property-set case deliberately has no input origin.
                if (programmatic) navigation.CurrentPage = target;
                else if (activationKey is { } key)
                {
                    action.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                    InputElement releaseTarget = host.FocusManager?.GetFocusedElement() as InputElement ?? host;
                    releaseTarget.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
                }
                else await PointerClick(host, action, holdPointer ? entered.Task : null, name);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (!page.IsSearchLoading) throw new InvalidOperationException(name + ": gated paging did not become busy.");
                Trace(name + ": query held", target);
                if (moveFocus)
                {
                    if (!search.Focus()) throw new InvalidOperationException(name + ": enabled search input refused focus.");
                    search.Text = "authored while pagination awaited";
                    await Program.WaitUntilAsync(() => search.IsFocused);
                    Trace(name + ": deliberate search focus", target);
                }
                release.TrySetResult();
                await Program.WaitUntilAsync(() => !page.IsBusy && navigation.CurrentPage == target);
                await Dispatcher.UIThread.InvokeAsync(host.UpdateLayout, DispatcherPriority.Background);
                Trace(name + ": after Loaded and Background", target);
                bool currentPageFocused = host.FocusManager?.GetFocusedElement() is PaginationButton selected
                    && selected.Page == target && !selected.Classes.Contains(PaginationButton.PC_Left)
                    && !selected.Classes.Contains(PaginationButton.PC_Right);
                check(programmatic ? host.FocusManager?.GetFocusedElement() == null
                    : moveFocus ? search.IsFocused && search.Text == "authored while pagination awaited" : currentPageFocused,
                    "pagination-focus-" + name, State("assertion", target));
                Program.CaptureWindow(host, "smoke-pagination-focus-" + name + ".png");
                release = null; entered = null;
            }
            await Case("retain-without-new-focus", Pagination.PART_NextButton, 2, moveFocus: false);
            await Case("respect-new-search-focus", Pagination.PART_PreviousButton, 1, moveFocus: true);
            await Case("return-retain-origin", Pagination.PART_NextButton, 2, moveFocus: false, activationKey: Key.Enter);
            await Case("return-respect-search", Pagination.PART_PreviousButton, 1, moveFocus: true, activationKey: Key.Enter);
            await Case("space-retain-origin", Pagination.PART_NextButton, 2, moveFocus: false, activationKey: Key.Space);
            await Case("space-respect-search", Pagination.PART_PreviousButton, 1, moveFocus: true, activationKey: Key.Space);
            await Case("pointer-hold-retain-origin", Pagination.PART_NextButton, 2, moveFocus: false, holdPointer: true);
            await Case("programmatic-no-focus-request", Pagination.PART_PreviousButton, 1, moveFocus: false, programmatic: true);
        }
        finally
        {
            release?.TrySetCanceled();
            page?.EndLifetime();
            host?.Close();
            owner.Activate();
            await Program.WaitUntilAsync(() => owner.IsActive);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static async Task PointerClick(Window host, Control control, Task? heldUntilActivation, string scenario)
    {
        // Focus can queue BringIntoView/layout after the earlier layout flush.
        // Wait for the same strict visible hit ownership used by the pointer
        // action, rather than assuming IsFocused also means hit-test-ready.
        (Point? Point, IInputElement? Hit, bool Ready) ProbeHit()
        {
            Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host);
            IInputElement? candidate = center.HasValue ? host.InputHitTest(center.Value) : null;
            bool ready = host.IsActive && control.IsFocused && control.IsEffectivelyVisible && control.IsEffectivelyEnabled
                && control.Bounds.Width > 0 && control.Bounds.Height > 0 && center.HasValue
                && new Rect(host.ClientSize).Contains(center.Value)
                && candidate is InputElement && candidate is Visual visual
                && visual.GetSelfAndVisualAncestors().Contains(control);
            return (center, candidate, ready);
        }
        void TraceHit(string phase)
        {
            var state = ProbeHit();
            Console.WriteLine("SMOKE pagination-hit " + JsonSerializer.Serialize(new
            {
                scenario, phase, state.Ready, center = state.Point?.ToString(),
                clientSize = host.ClientSize.ToString(), windowBounds = host.Bounds.ToString(),
                position = host.Position.ToString(), host.RenderScaling, host.IsActive,
                control = control.Name ?? control.GetType().Name, controlBounds = control.Bounds.ToString(),
                topLeft = control.TranslatePoint(default, host)?.ToString(), control.IsFocused,
                control.IsEffectivelyVisible, control.IsEffectivelyEnabled, control.IsHitTestVisible,
                hit = state.Hit is Control hitControl ? new { type = hitControl.GetType().Name, hitControl.Name }
                    : state.Hit == null ? null : new { type = state.Hit.GetType().Name, Name = (string?)null },
                hitAncestors = state.Hit is Visual hitVisual
                    ? hitVisual.GetSelfAndVisualAncestors().OfType<Control>().Select(node => node.GetType().Name + ":" + node.Name).ToArray() : [],
                sourceAncestors = control.GetSelfAndVisualAncestors().OfType<Control>().Select(node => new
                {
                    type = node.GetType().Name, node.Name, bounds = node.Bounds.ToString(), node.ClipToBounds,
                    node.IsEffectivelyVisible, node.IsHitTestVisible
                }).ToArray()
            }));
        }
        TraceHit("before readiness wait");
        try { await Program.WaitUntilAsync(() => ProbeHit().Ready); }
        catch (TimeoutException error)
        {
            TraceHit("readiness timeout");
            try { Program.CaptureWindow(host, "smoke-pagination-hitblocked-" + scenario + ".png"); }
            catch (Exception captureError) { Console.WriteLine("SMOKE pagination-hit capture failed: " + captureError); }
            throw new InvalidOperationException("Pagination action never owned its visible hit target: " + scenario, error);
        }
        var ready = ProbeHit();
        TraceHit("ready before single activation");
        if (!ready.Ready || ready.Point is not { } point || ready.Hit is not InputElement hit)
            throw new InvalidOperationException("Pagination hit ownership changed before activation: " + scenario);
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, host, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        // Wait for the actual RepeatButton timer to enter the gated query;
        // releasing first would only exercise the ordinary click again.
        if (heldUntilActivation != null) await heldUntilActivation.WaitAsync(TimeSpan.FromSeconds(5));
        hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, host, point, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
    }
}
