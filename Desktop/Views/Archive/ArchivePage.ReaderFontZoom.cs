using System;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Audit V4: in the reader (embedded and large) Ctrl+wheel (Cmd+wheel on macOS) and Ctrl+Plus / Ctrl+Minus step the reader's font size, Ctrl+0 sets it back
// to Normal. The size is the setting "Reading font size" (Small, Normal, Large): a step never leaves its range, and it is saved and applied exactly like the
// reader settings popover does it (QuickSettingsHost). The wheel with Ctrl never scrolls the reader. Kept apart from the reader's scroll code on purpose.
public partial class ArchivePage
{
    private readonly WheelSteps _readerFontWheel = new();
    private QuickSettingsApplier? _readerFontApplier;
    // The value asked for and not yet confirmed: the next step starts from it, so quick steps add up before the first save has finished.
    private string? _readerFontTarget;

    internal const string ReaderFontLargerCommandId = "reader.font-size.larger";
    internal const string ReaderFontSmallerCommandId = "reader.font-size.smaller";
    internal const string ReaderFontResetCommandId = "reader.font-size.reset";

    private void AttachReaderFontZoom()
    {
        ArchiveReaderPane.AddHandler(PointerWheelChangedEvent, OnReaderFontWheel, RoutingStrategies.Tunnel);
        ArchiveReaderPane.AddHandler(KeyDownEvent, OnReaderFontKeyDown, RoutingStrategies.Bubble);
    }

    private void OnReaderFontWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || !ItemActivation.IsZoomWheel(e.KeyModifiers) || _ended || IsReaderDialogOpen) return;
        // Ctrl with the wheel is never a scroll: a step changes the size, the rest of a touchpad gesture waits for the next one.
        e.Handled = true;
        int steps = _readerFontWheel.Add(e.Delta.Y, DateTime.UtcNow);
        if (steps != 0) StepReaderFontSize(steps);
    }

    private void OnReaderFontKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _ended || ShortcutService.IsCapturing || IsReaderDialogOpen || CommandRouter.IsNativeInput(e.Source, e.Key)) return;
        if (CommandBindings.Matches(e, ReaderFontLargerCommandId)) { e.Handled = true; StepReaderFontSize(1); }
        else if (CommandBindings.Matches(e, ReaderFontSmallerCommandId)) { e.Handled = true; StepReaderFontSize(-1); }
        else if (CommandBindings.Matches(e, ReaderFontResetCommandId)) { e.Handled = true; ResetReaderFontSize(); }
    }

    /// <summary>One step larger (positive) or smaller; false at the end of the range, without a host that saves the setting, or after the page ended.</summary>
    internal bool StepReaderFontSize(int steps) => steps != 0 && ApplyReaderFontSize(ReaderFontSizeSteps.Step(CurrentReaderFontSize(), steps));

    /// <summary>Back to Normal; false when it already is.</summary>
    internal bool ResetReaderFontSize() => ApplyReaderFontSize(ReaderFontSizeSteps.Default);

    private string CurrentReaderFontSize() => _readerFontTarget ?? QuickSettingsHost?.CurrentConfig().ReadingFontSize ?? ReaderFontSizeSteps.Default;

    private bool ApplyReaderFontSize(string next)
    {
        if (_ended || QuickSettingsHost is not { } host || string.Equals(next, CurrentReaderFontSize(), StringComparison.Ordinal)) return false;
        QuickSettingsApplier applier = _readerFontApplier ??= CreateReaderFontApplier(host);
        if (!applier.Set(ReaderFontSizeSteps.Field, next)) return false;
        _readerFontTarget = next;
        return true;
    }

    private QuickSettingsApplier CreateReaderFontApplier(QuickSettingsHost host)
    {
        QuickSettingsApplier applier = new(host.SaveAsync, host.Debounce);
        applier.Applied += ids =>
        {
            if (applier.PendingCount == 0) _readerFontTarget = null;
            if (!_ended) host.Applied?.Invoke(ids);
        };
        applier.Failed += (_, error) =>
        {
            _readerFontTarget = null;
            if (!_ended) Message(Texts.T("quicksettings.failed", UserFacingError.Report(error, "Applying a quick setting")), true);
        };
        return applier;
    }
}
