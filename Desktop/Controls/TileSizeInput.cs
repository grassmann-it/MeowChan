using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>The saved spelling of <see cref="MediaTileSize"/> (a text of the settings file): unknown or empty text means Medium.</summary>
internal static class MediaTileSizes
{
    internal static MediaTileSize Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "small" or "s" => MediaTileSize.Small,
        "large" or "l" => MediaTileSize.Large,
        _ => MediaTileSize.Medium
    };

    /// <summary>The size one step larger (<paramref name="steps"/> &gt; 0) or smaller (&lt; 0); the ends stay where they are.</summary>
    internal static MediaTileSize Step(MediaTileSize size, int steps) =>
        (MediaTileSize)Math.Clamp((int)size + Math.Sign(steps), (int)MediaTileSize.Small, (int)MediaTileSize.Large);
}

/// <summary>The three commands a tile surface answers: one step larger, one step smaller, back to Medium (Ctrl/Cmd+Plus, Minus, 0).</summary>
internal sealed record TileSizeCommands(string Larger, string Smaller, string Reset)
{
    /// <summary>The media page and the tagging grid: one tile size (it is the media page's setting).</summary>
    internal static TileSizeCommands Media { get; } = new("media.tile-size.larger", "media.tile-size.smaller", "media.tile-size.reset");

    /// <summary>The archive's saved-media tiles: a size of their own.</summary>
    internal static TileSizeCommands Archive { get; } = new("archive.media-tile-size.larger", "archive.media-tile-size.smaller", "archive.media-tile-size.reset");
}

/// <summary>
/// The tile size input of a tile surface (the media page's grid, the tagging page's grid, the archive's media tiles): Ctrl+wheel (⌘+wheel on
/// macOS) and the keys of <see cref="TileSizeCommands"/> step the size (Small, Medium, Large; nothing else exists), the reset key sets Medium. The
/// surface says what the size is now and how a new one is applied and saved; this class only decides which gesture means which step. The
/// handlers sit on the surface's tunnel, so the wheel never scrolls it and a key never reaches a handler of another scope (the reader's font
/// size keys): a surface answers only while its own list has the focus or the pointer.
/// </summary>
internal sealed class TileSizeInput
{
    private readonly TileSizeCommands _commands;
    private readonly Func<MediaTileSize> _current;
    private readonly Func<MediaTileSize, bool> _apply;
    private readonly WheelSteps _wheel = new();

    private TileSizeInput(TileSizeCommands commands, Func<MediaTileSize> current, Func<MediaTileSize, bool> apply)
    {
        _commands = commands;
        _current = current;
        _apply = apply;
    }

    /// <summary>
    /// Installs the wheel and key handlers on <paramref name="surface"/>. <paramref name="apply"/> shows and saves the new size and says whether it
    /// did (it is only called for a size other than the current one).
    /// </summary>
    internal static TileSizeInput Install(Control surface, TileSizeCommands commands, Func<MediaTileSize> current, Func<MediaTileSize, bool> apply)
    {
        ArgumentNullException.ThrowIfNull(surface);
        TileSizeInput input = new(commands, current, apply);
        surface.AddHandler(InputElement.PointerWheelChangedEvent, input.OnWheel, RoutingStrategies.Tunnel);
        surface.AddHandler(InputElement.KeyDownEvent, input.OnKey, RoutingStrategies.Tunnel);
        return input;
    }

    /// <summary>Sets <paramref name="size"/> unless it is the current one (one save per change; nothing happens at the ends).</summary>
    internal bool Set(MediaTileSize size) => size != _current() && _apply(size);

    /// <summary>One step larger (<paramref name="steps"/> &gt; 0) or smaller; the ends stay where they are.</summary>
    internal bool StepBy(int steps) => steps != 0 && Set(MediaTileSizes.Step(_current(), steps));

    /// <summary>Back to Medium, the default.</summary>
    internal bool Reset() => Set(MediaTileSize.Medium);

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || !ItemActivation.IsZoomWheel(e.KeyModifiers)) return;
        // The wheel with Ctrl never scrolls the surface: a step changes the size, the rest of a touchpad gesture waits for the next one.
        e.Handled = true;
        int steps = _wheel.Add(e.Delta.Y, DateTime.UtcNow);
        if (steps != 0) StepBy(steps);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key)) return;
        if (CommandBindings.Matches(e, _commands.Larger)) StepBy(1);
        else if (CommandBindings.Matches(e, _commands.Smaller)) StepBy(-1);
        else if (CommandBindings.Matches(e, _commands.Reset)) Reset();
        else return;
        e.Handled = true;
    }
}
