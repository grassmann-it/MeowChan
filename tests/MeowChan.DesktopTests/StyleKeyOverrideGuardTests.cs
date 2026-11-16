using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace MeowChan.DesktopTests;

/// <summary>
/// Bug-class audit 2026-11-16, class 1 (style key mismatch): a control that derives from a templated control of the framework or of Ursa takes its template from
/// the control theme of its style key, which is its own type unless it says otherwise. A subclass without <c>StyleKeyOverride</c> finds no theme and
/// draws nothing (no template, no background, nothing to hit). Reflection over the product assembly, no Avalonia application.
/// </summary>
public sealed class StyleKeyOverrideGuardTests
{
    private static readonly BindingFlags Declared = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

    // Types that are themed on purpose by a control theme of their own (own type as key), listed as "TypeName: why". Empty while none is.
    private static readonly Dictionary<string, string> OwnTheme = new(StringComparer.Ordinal);

    [Fact]
    public void EveryConcreteSubclassOfAThemedControlOverridesItsStyleKey()
    {
        Assembly product = typeof(MeowChan.Desktop.GalleryViewer).Assembly;
        List<string> missing = [];
        foreach (Type type in product.GetTypes())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(TemplatedControl).IsAssignableFrom(type)) continue;
            // UserControl and Window are the framework's own hosts: their style key is the framework type and they are not themed by subclass.
            if (typeof(UserControl).IsAssignableFrom(type) || typeof(Window).IsAssignableFrom(type) || typeof(Avalonia.Controls.Primitives.PopupRoot).IsAssignableFrom(type)) continue;
            // Only a subclass of a type of another assembly that the theme knows can lose its theme; a base of this assembly is judged where it is declared.
            Type? baseType = type.BaseType;
            if (baseType == null || baseType.Assembly == product || baseType == typeof(TemplatedControl)) continue;
            if (HasOverride(type, product)) continue;
            if (OwnTheme.ContainsKey(type.FullName ?? type.Name)) continue;
            missing.Add(type.FullName + " : " + baseType.Name);
        }
        Assert.True(missing.Count == 0,
            "These subclasses of a themed control do not override StyleKeyOverride (they draw without a template). Return the base control's type from it:\n" + string.Join("\n", missing));
    }

    // The override is declared by the type itself or by a base class of this assembly that sits between it and the framework control.
    private static bool HasOverride(Type type, Assembly product)
    {
        for (Type? current = type; current != null && current.Assembly == product; current = current.BaseType)
            if (current.GetProperty("StyleKeyOverride", Declared) != null) return true;
        return false;
    }

    [Fact]
    public void TheGuardSeesAMissingOverride()
    {
        Assert.False(HasOverride(typeof(NoOverrideButton), typeof(NoOverrideButton).Assembly));
        Assert.True(HasOverride(typeof(OverridingButton), typeof(OverridingButton).Assembly));
        Assert.True(HasOverride(typeof(InheritingButton), typeof(InheritingButton).Assembly));
    }

    private class NoOverrideButton : Button { }
    private class OverridingButton : Button { protected override Type StyleKeyOverride => typeof(Button); }
    private sealed class InheritingButton : OverridingButton { }
}
