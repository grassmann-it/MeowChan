using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace MeowChan.Desktop;

// GL4 (PLAN-GALERIE-LAYOUT-BLOECKE §4.1, Tom E3): the person's layout choice of the gallery, one set per mode, saved per profile.
// The profile's settings file holds "GalleryLayout": { "view": {...}, "tag": {...} }; every member is optional and null/absent means
// "the work view's default" (GalleryLayoutChoice). Nothing here touches a control; the window loads the sets into the viewer
// (GalleryViewer.SetLayoutChoice) and saves a change the viewer reports (GalleryViewer.LayoutChoiceChanged).
//
// The older setting GalleryFilmstrip stays (a setting and an export field) and has one value with the View set's filmstrip:
// legacy == (View.Filmstrip != false). A profile without a stored layout reads the old setting (FromConfig), and ApplyTo writes both,
// so an older build and this one agree until 1.2. Where the two disagree, an older build switched the strip off (it changes only the old
// setting and keeps the layout): an "off" old setting always wins for the View set (N5). The other way round ("on" in the old setting,
// View.Filmstrip false stored) is not read as "on", because a settings file without the old key reads it as on too; a View "off" chosen
// in this build stays until it is switched on here or in the settings page.

/// <summary>The saved layout choices of the gallery: <see cref="View"/> and <see cref="Tag"/> are independent sets (null member = the view's default).</summary>
internal sealed record GalleryLayoutPreferences(GalleryLayoutChoice View, GalleryLayoutChoice Tag)
{
    /// <summary>Fixed ceiling of the members one load names in the log (a damaged file must not fill it); not a setting, the profile file's own size ceiling bounds the rest.</summary>
    internal const int LoggedDroppedMembersCeiling = 8;

    internal static GalleryLayoutPreferences Default { get; } = new(GalleryLayoutChoice.Default, GalleryLayoutChoice.Default);

    /// <summary>Nothing chosen in either mode (the file then holds no layout at all).</summary>
    internal bool IsDefault => Normalize() == Default;

    /// <summary>The choice of one mode.</summary>
    internal GalleryLayoutChoice For(GalleryChromeMode mode) => (mode == GalleryChromeMode.Tag ? Tag : View) ?? GalleryLayoutChoice.Default;

    /// <summary>The same preferences with the choice of one mode replaced; the other mode is untouched.</summary>
    internal GalleryLayoutPreferences With(GalleryChromeMode mode, GalleryLayoutChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return mode == GalleryChromeMode.Tag ? this with { Tag = choice } : this with { View = choice };
    }

    /// <summary>Both modes back to the work view's defaults (the settings page's reset); the old filmstrip setting follows in <see cref="ApplyTo"/>.</summary>
    internal GalleryLayoutPreferences Reset() => Default;

    /// <summary>
    /// The settings page's "Show the filmstrip" box: on = both modes follow the work view again, off = off in both modes. The other
    /// blocks and the filmstrip's dock stay as they are.
    /// </summary>
    internal GalleryLayoutPreferences WithFilmstripSetting(bool shown) => new(
        (View ?? GalleryLayoutChoice.Default) with { Filmstrip = shown ? null : false },
        (Tag ?? GalleryLayoutChoice.Default) with { Filmstrip = shown ? null : false });

    /// <summary>Drops values that are not valid (an undefined dock); the booleans cannot be damaged once typed.</summary>
    internal GalleryLayoutPreferences Normalize() => new(NormalizeChoice(View), NormalizeChoice(Tag));

    private static GalleryLayoutChoice NormalizeChoice(GalleryLayoutChoice? choice) =>
        choice == null ? GalleryLayoutChoice.Default
        : choice.FilmstripDock is { } dock && !Enum.IsDefined(dock) ? choice with { FilmstripDock = null } : choice;

    /// <summary>
    /// The profile's layout: the stored one, with the old GalleryFilmstrip setting as the filmstrip's choice where the profile has none
    /// (a profile that never stored a layout: both modes, as the setting held for both). With a stored layout an "off" old setting always
    /// makes the View set's filmstrip false, also over a stored true (an older build switched it off, N5); the Tag set is never touched.
    /// </summary>
    internal static GalleryLayoutPreferences FromConfig(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        GalleryLayoutPreferences? stored = config.GalleryLayout?.Normalize();
        if (stored == null)
            return config.GalleryFilmstrip ? Default : Default.WithFilmstripSetting(false);
        return !config.GalleryFilmstrip && stored.View.Filmstrip != false
            ? stored with { View = stored.View with { Filmstrip = false } }
            : stored;
    }

    /// <summary>Writes the layout into the profile's settings: GalleryLayout (null when nothing is chosen) and the old GalleryFilmstrip setting with the View set's filmstrip.</summary>
    internal void ApplyTo(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        GalleryLayoutPreferences normalized = Normalize();
        config.GalleryLayout = normalized == Default ? null : normalized;
        config.GalleryFilmstrip = normalized.View.Filmstrip != false;
    }

    // ---- JSON: tolerant, member by member (a damaged value never discards the other settings) ----

    /// <summary>
    /// Reads the stored object. Unknown members are dropped and named in <paramref name="dropped"/>, a member of the wrong type or an unknown
    /// dock is dropped too (it reads as "the view's default"); everything valid beside it survives. A value that is no object gives null.
    /// </summary>
    internal static GalleryLayoutPreferences? Read(JsonElement element, ICollection<string>? dropped = null)
    {
        if (element.ValueKind != JsonValueKind.Object) { dropped?.Add("(not an object)"); return null; }
        GalleryLayoutChoice view = GalleryLayoutChoice.Default, tag = GalleryLayoutChoice.Default;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals("view", StringComparison.OrdinalIgnoreCase))
                view = ReadChoice(property.Value, "view", dropped);
            else if (property.Name.Equals("tag", StringComparison.OrdinalIgnoreCase))
                tag = ReadChoice(property.Value, "tag", dropped);
            else dropped?.Add(property.Name);
        }
        return new GalleryLayoutPreferences(view, tag);
    }

    private static GalleryLayoutChoice ReadChoice(JsonElement element, string mode, ICollection<string>? dropped)
    {
        if (element.ValueKind == JsonValueKind.Null) return GalleryLayoutChoice.Default;
        if (element.ValueKind != JsonValueKind.Object) { dropped?.Add(mode + " (not an object)"); return GalleryLayoutChoice.Default; }
        bool? filmstrip = null, tools = null, info = null, hints = null;
        GalleryFilmstripDock? dock = null;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name = property.Name;
            if (name.Equals("filmstrip", StringComparison.OrdinalIgnoreCase)) filmstrip = ReadBool(property.Value, mode, name, dropped);
            else if (name.Equals("tools", StringComparison.OrdinalIgnoreCase)) tools = ReadBool(property.Value, mode, name, dropped);
            else if (name.Equals("info", StringComparison.OrdinalIgnoreCase)) info = ReadBool(property.Value, mode, name, dropped);
            else if (name.Equals("hints", StringComparison.OrdinalIgnoreCase)) hints = ReadBool(property.Value, mode, name, dropped);
            else if (name.Equals("filmstripDock", StringComparison.OrdinalIgnoreCase)) dock = ReadDock(property.Value, mode, name, dropped);
            else dropped?.Add(mode + "." + name);
        }
        return new GalleryLayoutChoice(filmstrip, dock, tools, info, hints);
    }

    private static bool? ReadBool(JsonElement value, string mode, string name, ICollection<string>? dropped)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind != JsonValueKind.Null) dropped?.Add(mode + "." + name + " (invalid value)");
        return null;
    }

    private static GalleryFilmstripDock? ReadDock(JsonElement value, string mode, string name, ICollection<string>? dropped)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (string.Equals(text, "top", StringComparison.OrdinalIgnoreCase)) return GalleryFilmstripDock.Top;
            if (string.Equals(text, "bottom", StringComparison.OrdinalIgnoreCase)) return GalleryFilmstripDock.Bottom;
        }
        if (value.ValueKind != JsonValueKind.Null) dropped?.Add(mode + "." + name + " (invalid value)");
        return null;
    }

    /// <summary>Writes only the chosen members (a null member is absent), view first.</summary>
    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteChoice(writer, "view", View);
        WriteChoice(writer, "tag", Tag);
        writer.WriteEndObject();
    }

    private static void WriteChoice(Utf8JsonWriter writer, string mode, GalleryLayoutChoice? choice)
    {
        writer.WriteStartObject(mode);
        if (choice != null)
        {
            if (choice.Filmstrip is { } filmstrip) writer.WriteBoolean("filmstrip", filmstrip);
            if (choice.FilmstripDock is { } dock && Enum.IsDefined(dock)) writer.WriteString("filmstripDock", dock == GalleryFilmstripDock.Top ? "top" : "bottom");
            if (choice.Tools is { } tools) writer.WriteBoolean("tools", tools);
            if (choice.Info is { } info) writer.WriteBoolean("info", info);
            if (choice.Hints is { } hints) writer.WriteBoolean("hints", hints);
        }
        writer.WriteEndObject();
    }

    // ---- the settings file's JSON node (Config.GalleryLayoutJson) ----

    /// <summary>
    /// The stored node as preferences: not an object (or null) gives null (nothing chosen); unknown or damaged members are dropped and
    /// logged once, never failing the load.
    /// </summary>
    internal static GalleryLayoutPreferences? FromJsonNode(JsonNode? node)
    {
        if (node == null) return null;
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        List<string> dropped = new();
        GalleryLayoutPreferences? read = Read(document.RootElement, dropped);
        if (dropped.Count > 0)
        {
            int shown = Math.Min(dropped.Count, LoggedDroppedMembersCeiling);
            Log.Warning("The stored gallery layout had {Count} unknown or invalid member(s) that were dropped: {Members}",
                dropped.Count, string.Join(", ", dropped.GetRange(0, shown)) + (shown < dropped.Count ? ", ..." : string.Empty));
        }
        return read?.Normalize() is { IsDefault: false } layout ? layout : null;
    }

    /// <summary>The preferences as the saved JSON object (only the chosen members).</summary>
    internal JsonNode ToJsonNode()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream)) Write(writer);
        return JsonNode.Parse(stream.ToArray())!;
    }
}
