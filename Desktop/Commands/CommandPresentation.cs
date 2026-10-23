using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop.Commands;

/// <summary>Adds a command identity to an existing parameterized control without replacing its handler or label.</summary>
public sealed class CommandPresentation : AvaloniaObject
{
    public static readonly AttachedProperty<string?> IdProperty =
        AvaloniaProperty.RegisterAttached<CommandPresentation, Control, string?>("Id");
    static CommandPresentation()
    {
        IdProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (control.GetValue(IdProperty) is string id) CommandBindings.BindExisting(control, id);
        });
    }
    public static string? GetId(Control control) => control.GetValue(IdProperty);
    public static void SetId(Control control, string? value) => control.SetValue(IdProperty, value);
}
