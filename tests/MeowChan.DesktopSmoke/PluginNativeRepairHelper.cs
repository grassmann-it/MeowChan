using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

/// <summary>Native repair-dialog input on the runner's private Xvfb, never a personal clipboard/display.</summary>
internal static class PluginNativeRepairHelper
{
    internal static IDisposable IsolateClipboard()
    {
        RequirePrivateXvfb();
        // Program installs ClipboardChecks' write-only fixture globally. Scope a read/write
        // fixture for genuine TextBox paste/copy; never bypass isolation with a system clipboard.
        IDisposable scope = AvaloniaLocator.EnterScope();
        try
        {
            AvaloniaLocator.CurrentMutable.Bind<IClipboard>().ToConstant(new RepairClipboard());
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private static IClipboard ClipboardFor(Window modal) => modal.Clipboard is RepairClipboard clipboard ? clipboard :
        throw new InvalidOperationException("Repair modal is not using its scoped fixture clipboard.");

    internal static ToggleButton ExpanderHeader(Expander expander)
    {
        expander.ApplyTemplate();
        // The expanded configuration contains TextBox template toggles as well. Their
        // nearest Expander is the same, but they are not this Expander's header. Match
        // the actual Semi header part and owner; retain Single so ambiguity stays a failure.
        ToggleButton header = expander.GetVisualDescendants().OfType<ToggleButton>()
            .Single(toggle => toggle.Name == "ExpanderHeader" &&
                ReferenceEquals(toggle.GetVisualAncestors().OfType<Expander>().FirstOrDefault(), expander));
        Console.WriteLine("PLUGIN_REPAIR_HEADER " + JsonSerializer.Serialize(new {
            id = AutomationProperties.GetAutomationId(expander), header.Name,
            templatedParent = header.TemplatedParent?.GetType().Name,
            directTemplatedParent = ReferenceEquals(header.TemplatedParent, expander),
            nearestExpander = true }));
        return header;
    }

    internal static async Task ClickAsync(Window window, MediaNativeInput input, Control control)
    {
        await Program.WaitUntilAsync(() => {
            window.UpdateLayout(); control.BringIntoView(); window.UpdateLayout();
            Point? point = control.TranslatePoint(default, window);
            return control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled &&
                ReferenceEquals(TopLevel.GetTopLevel(control), window) && point is { } p &&
                p.X >= 0 && p.Y >= 0 && p.X + control.Bounds.Width <= window.ClientSize.Width + 1 &&
                p.Y + control.Bounds.Height <= window.ClientSize.Height + 1 && control.Bounds.Width > 0 && control.Bounds.Height > 0;
        }, () => "Native repair target is not reachable/enabled: " + control.GetType().Name + ":" +
            AutomationProperties.GetAutomationId(control) + " bounds=" + control.Bounds +
            " origin=" + control.TranslatePoint(default, window) + " enabled=" + control.IsEffectivelyEnabled);
        await input.ClickAsync(control);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    internal static async Task ReplaceTextAsync(Window modal, MediaNativeInput input, TextBox field, string text)
    {
        RequirePrivateXvfb();
        Require(!field.IsReadOnly, "Native repair field must be editable.");
        await ClickAsync(modal, input, field);
        Require(field.IsFocused, "Native click did not focus the repair field.");
        // Paste through actual OS shortcuts, including Unicode, instead of assigning Text/Draft
        // or invoking a private UI method. The scoped clipboard never delegates to the OS.
        await ClipboardFor(modal).SetTextAsync(text);
        await ControlKeyAsync(modal, input, 'a');
        await ControlKeyAsync(modal, input, 'v');
        await Program.WaitUntilAsync(() => field.Text == text,
            () => "Native repair paste did not replace the focused field: " + AutomationProperties.GetAutomationId(field));
    }

    internal static async Task VerifyRawConfigurationCopyAsync(Window modal, MediaNativeInput input, TextBox field, string expected)
    {
        RequirePrivateXvfb();
        Require(field.IsReadOnly, "Configuration evidence must remain read-only.");
        await ClickAsync(modal, input, field);
        Require(field.IsFocused, "Read-only configuration is not keyboard reachable.");
        IClipboard clipboard = ClipboardFor(modal);
        await clipboard.SetTextAsync("not the configuration");
        await ControlKeyAsync(modal, input, 'a');
        await ControlKeyAsync(modal, input, 'c');
        string? copied = await clipboard.TryGetTextAsync();
        Require(copied == expected && field.Text == expected, "Native copy changed, truncated or prefixed the raw configuration JSON.");
        using JsonDocument document = JsonDocument.Parse(copied!);
        Require(document.RootElement.ValueKind == JsonValueKind.Object, "Copied configuration is not a JSON object.");
        Console.WriteLine("PLUGIN_REPAIR_RAW_COPY " + JsonSerializer.Serialize(new { result = "pass", characters = copied!.Length,
            nativeSelectAllAndCopy = true, clipboard = "scoped fixture, not OS", exactUnicodeAndWhitespace = true, noPrefix = true }));
    }

    private sealed class RepairClipboard : IClipboard
    {
        private string? _text;
        public Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            if (_text is null) return Task.FromResult<IAsyncDataTransfer?>(null);
            DataTransfer data = new();
            data.Add(DataTransferItem.Create(DataFormat.Text, _text));
            return Task.FromResult<IAsyncDataTransfer?>(data);
        }
        public async Task SetDataAsync(IAsyncDataTransfer? dataTransfer)
        {
            try { _text = dataTransfer is null ? null : await dataTransfer.TryGetTextAsync(); }
            finally { dataTransfer?.Dispose(); }
        }
        public Task ClearAsync() { _text = null; return Task.CompletedTask; }
        public Task FlushAsync() => Task.CompletedTask;
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => TryGetDataAsync();
    }

    // MediaNativeInput has Ctrl+Shift chords for media commands, not ordinary Ctrl+A/C/V.
    // Supply only the held modifier here; key press/release and pointer safety stay in that helper.
    private static async Task ControlKeyAsync(Window modal, MediaNativeInput input, char key)
    {
        RequirePrivateXvfb();
        Require(modal.IsActive && modal.IsVisible, "Native shortcut lost its owned modal.");
        nint display = XOpenDisplay(0);
        Require(display != 0, "Cannot open the private repair display.");
        byte control = XKeysymToKeycode(display, (nuint)MediaNativeInput.ControlLeft);
        try
        {
            Require(control != 0 && XTestFakeKeyEvent(display, control, 1, 0) != 0, "Native Ctrl press was rejected.");
            XSync(display, 0);
            await input.KeyAsync(key);
        }
        finally
        {
            if (control != 0) { XTestFakeKeyEvent(display, control, 0, 0); XSync(display, 0); }
            XCloseDisplay(display);
        }
    }
    private static void RequirePrivateXvfb() => Require(OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1" &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")),
        "Repair clipboard/input checks require the isolated Linux Xvfb runner; no personal-display or synthetic fallback.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(nint display, nuint symbol);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, int discard);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(nint display, uint key, int down, nuint delay);
}
