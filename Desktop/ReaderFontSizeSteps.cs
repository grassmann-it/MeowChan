using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's font size as steps (audit V4: Ctrl+wheel, Ctrl+Plus / Ctrl+Minus and Ctrl+0). The setting is a choice, not a number; its values and their
/// order are those of the reader settings popover (Small, Normal, Large), so a step never leaves the setting's own range: the ends stay where they are.
/// </summary>
internal static class ReaderFontSizeSteps
{
    /// <summary>The value Ctrl+0 sets (the default of <c>Config.ReadingFontSize</c>).</summary>
    internal const string Default = "Normal";

    /// <summary>The setting's values from the smallest to the largest, read from the same definition the popover and the settings page use.</summary>
    internal static IReadOnlyList<string> Order { get; } = ReaderQuickSettings.Fields.OfType<QuickChoiceField>()
        .Single(field => string.Equals(field.Id, SettingsFieldId.ReadingFontSize, StringComparison.Ordinal))
        .Choices.Select(choice => choice.Value).ToArray();

    /// <summary>The setting's field of the popover, the one every change goes through (its change is the saved, validated one).</summary>
    internal static QuickSettingField Field { get; } = ReaderQuickSettings.Fields
        .Single(field => string.Equals(field.Id, SettingsFieldId.ReadingFontSize, StringComparison.Ordinal));

    /// <summary>
    /// The value one step larger (<paramref name="steps"/> &gt; 0) or smaller than <paramref name="current"/>. Several notches at once still move one step;
    /// at the ends, and for no step, the value stays. A value that is not one of the three counts as <see cref="Default"/>.
    /// </summary>
    internal static string Step(string? current, int steps)
    {
        int index = IndexOf(current);
        return Order[Math.Clamp(index + Math.Sign(steps), 0, Order.Count - 1)];
    }

    private static int IndexOf(string? value)
    {
        for (int index = 0; index < Order.Count; index++)
            if (string.Equals(Order[index], value?.Trim(), StringComparison.OrdinalIgnoreCase)) return index;
        for (int index = 0; index < Order.Count; index++)
            if (Order[index] == Default) return index;
        return 0;
    }
}
