using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Recorder and index of the UI catalog run (registry smoke "ui-catalog"): pages, page modes, overlays, popovers, flyouts and context
// menus of the real main window in Light/Dark x EN/DE at one standard size (pages also narrow), plus the images of the
// dialog-gallery run (MEOWCHAN_UI_CATALOG_DIALOGS names that run's output directory; its manifest and PNGs stay where they are).
// ONE index.html groups every image by the catalog area and links it to its catalog entry. The catalog itself
// (.claude/coordination/UI-KATALOG-1.1.0.md) ships a machine-readable twin, entries.json (MEOWCHAN_UI_CATALOG_ENTRIES): per entry the id,
// area, name, how to open it and the capture keys it owns (exact name, or a prefix ending in "*"). Review evidence only; behaviour and
// geometry contracts stay with the feature smokes.
internal static class UiCatalog
{
    internal sealed record Shot(string File, string Key, string Variant, string Theme, string Language, string Size, string Scale, string Note, string Source);

    internal sealed record Gap(string Key, string Theme, string Language, string Reason);

    private sealed record Entry(string Id, string Area, string Name, string Open, string[] Captures, string? Reason);

    private static readonly List<Shot> Shots = [];
    private static readonly List<Gap> Gaps = [];
    private static readonly HashSet<string> Files = new(StringComparer.Ordinal);
    private static string _directory = "";

    internal static string Theme { get; set; } = "";

    internal static string Language => Texts.Language;

    internal static int ShotCount => Shots.Count;

    internal static int GapCount => Gaps.Count;

    internal static string OutputDirectory => _directory;

    internal static void Begin(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Shots.Clear(); Gaps.Clear(); Files.Clear();
    }

    /// <summary>Renders a window (or the top-level of an in-window control) as one image.</summary>
    internal static void Record(Control root, string key, string variant, string note = "")
    {
        Visual visual = root is Window ? root : (Visual?)TopLevel.GetTopLevel(root) ?? root;
        RenderTo(visual, key, variant, note, root);
    }

    /// <summary>Renders a popup, flyout, menu or other separate surface by itself (these are no part of the window image).</summary>
    internal static void RecordSurface(Control surface, string key, string variant, string note = "", double zoom = 1) =>
        RenderTo(surface, key, variant, note, surface, zoom);

    internal static void Skip(string key, string reason) => Gaps.Add(new Gap(key, Theme, Language, reason));

    // zoom > 1 renders a small control (an icon button and its states) enlarged, so optical details can be judged.
    private static void RenderTo(Visual visual, string key, string variant, string note, Control measured, double zoom = 1)
    {
        if (visual is Control control) control.UpdateLayout();
        TopLevel? top = TopLevel.GetTopLevel(visual as Control);
        double scale = (top?.RenderScaling ?? 1) * zoom;
        double width = visual.Bounds.Width, height = visual.Bounds.Height;
        if (width < 1 || height < 1) throw new InvalidOperationException("Capture target has no size: " + key);
        string stem = Slug(key) + "__" + Slug(variant) + "__" + Theme + "-" + Language;
        string file = stem + ".png";
        for (int i = 2; !Files.Add(file); i++) file = stem + "-" + i + ".png";
        using RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(_directory, file), PngBitmapEncoderOptions.Default);
        Shots.Add(new Shot(file, key, variant, Theme, Language,
            Math.Round(measured is Window w ? w.ClientSize.Width : width) + " x " + Math.Round(measured is Window v ? v.ClientSize.Height : height) + " DIP",
            scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), note, ""));
        Console.WriteLine("SMOKE ui catalog: " + file);
    }

    private static string Slug(string text) => Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    /// <summary>Writes manifest.json and index.html; returns the number of images of this run (without the dialog gallery's).</summary>
    internal static int Finish(string runLabel)
    {
        string? dialogs = Environment.GetEnvironmentVariable("MEOWCHAN_UI_CATALOG_DIALOGS");
        List<Shot> all = [.. Shots];
        List<Gap> gaps = [.. Gaps];
        int imported = 0;
        if (!string.IsNullOrWhiteSpace(dialogs) && File.Exists(Path.Combine(dialogs, "manifest.json")))
        {
            string relative = Path.GetRelativePath(_directory, dialogs).Replace('\\', '/');
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dialogs, "manifest.json")));
            foreach (JsonElement shot in manifest.RootElement.GetProperty("shots").EnumerateArray())
            {
                string S(string name) => shot.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";
                all.Add(new Shot(relative + "/" + S("File"), S("Name"), S("Variant"), S("Theme"), S("Language"), S("Size"), S("Scale"), S("Note"), "dialog-gallery"));
                imported++;
            }
            foreach (JsonElement gap in manifest.RootElement.GetProperty("gaps").EnumerateArray())
                gaps.Add(new Gap(gap.GetProperty("Name").GetString() ?? "", gap.GetProperty("Theme").GetString() ?? "", gap.GetProperty("Language").GetString() ?? "",
                    gap.GetProperty("Reason").GetString() ?? ""));
        }
        List<Entry> entries = ReadEntries();
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), JsonSerializer.Serialize(new { run = runLabel, shots = all, gaps }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_directory, "index.html"), Html(runLabel, entries, all, gaps));
        Console.WriteLine("SMOKE ui catalog: " + Shots.Count + " images (+" + imported + " from the dialog gallery), " + Gaps.Count + " gaps in " + _directory);
        return Shots.Count;
    }

    private static List<Entry> ReadEntries()
    {
        string? path = Environment.GetEnvironmentVariable("MEOWCHAN_UI_CATALOG_ENTRIES");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return [];
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        List<Entry> entries = [];
        foreach (JsonElement item in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            string S(string name) => item.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";
            string[] captures = item.TryGetProperty("captures", out JsonElement list) ? list.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];
            entries.Add(new Entry(S("id"), S("area"), S("name"), S("open"), captures, item.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() : null));
        }
        return entries;
    }

    private static bool Owns(Entry entry, string key) =>
        entry.Captures.Any(pattern => pattern.EndsWith('*') ? key.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(pattern, key, StringComparison.OrdinalIgnoreCase));

    private static string Html(string runLabel, List<Entry> entries, List<Shot> shots, List<Gap> gaps)
    {
        StringBuilder html = new();
        static string E(string text) => WebUtility.HtmlEncode(text);
        string mdLink = Environment.GetEnvironmentVariable("MEOWCHAN_UI_CATALOG_MD") ?? "UI-KATALOG-1.1.0.md";
        html.AppendLine("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><title>MeowChan UI-Katalog</title>");
        html.AppendLine("<style>body{font:14px system-ui,sans-serif;margin:24px;background:#f4f4f5;color:#18181b}h1{margin:0 0 4px}h2{margin:36px 0 8px;border-bottom:2px solid #999;padding-bottom:4px}"
            + "h3{margin:22px 0 2px;font-size:15px}.meta{font-size:12px;color:#52525b;margin:0 0 8px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:12px}"
            + "figure{margin:0;background:#fff;border:1px solid #d4d4d8;border-radius:6px;padding:8px}figure.dark{background:#27272a;color:#e4e4e7;border-color:#3f3f46}"
            + "img{max-width:100%;height:auto;display:block;margin:0 auto;border:1px solid #8883}figcaption{font-size:12px;margin-top:6px;line-height:1.4}"
            + ".gap{color:#b91c1c}nav a{margin-right:10px;white-space:nowrap}.filters{margin:12px 0;position:sticky;top:0;background:#f4f4f5;padding:6px 0;z-index:2}"
            + ".id{font:600 12px ui-monospace,monospace;background:#e4e4e7;border-radius:4px;padding:1px 6px;margin-right:6px}code{font-size:11px}</style></head><body>");
        html.AppendLine("<h1>MeowChan UI-Katalog: Bilder</h1><p>" + E(runLabel) + " &mdash; " + shots.Count + " Bilder, Light/Dark x EN/DE. Eintr&auml;ge: <a href=\"" + E(mdLink) + "\">"
            + E(mdLink) + "</a> (jede Gruppe nennt die ID ihres Katalogeintrags). Klick auf ein Bild: Original.</p>");
        html.AppendLine("<div class=\"filters\">Zeigen: <select id=\"theme\"><option value=\"\">alle Themes</option><option>Light</option><option>Dark</option></select> "
            + "<select id=\"lang\"><option value=\"\">alle Sprachen</option><option>en</option><option>de</option></select> "
            + "<select id=\"size\"><option value=\"\">alle Gr&ouml;&szlig;en</option>"
            + string.Join("", shots.Select(shot => shot.Variant).Distinct().OrderBy(v => v).Select(v => "<option>" + E(v) + "</option>")) + "</select></div>");

        List<Shot> unassigned = [.. shots];
        string[] areas = entries.Select(entry => entry.Area).Distinct().ToArray();
        html.AppendLine("<nav>" + string.Join("", areas.Select(area => "<a href=\"#a-" + Slug(area) + "\">" + E(area) + "</a>")) + "<a href=\"#a-unassigned\">Nicht zugeordnet</a><a href=\"#a-gaps\">Nicht erfasst</a></nav>");
        List<Entry> withoutShots = [];
        foreach (string area in areas)
        {
            html.AppendLine("<h2 id=\"a-" + Slug(area) + "\">" + E(area) + "</h2>");
            foreach (Entry entry in entries.Where(candidate => candidate.Area == area))
            {
                List<Shot> own = shots.Where(shot => Owns(entry, shot.Key)).ToList();
                if (own.Count == 0) { withoutShots.Add(entry); continue; }
                unassigned.RemoveAll(shot => own.Contains(shot));
                html.AppendLine("<h3 id=\"e-" + E(entry.Id) + "\"><span class=\"id\">" + E(entry.Id) + "</span>" + E(entry.Name) + "</h3><p class=\"meta\">&Ouml;ffnen: " + E(entry.Open) + "</p>");
                WriteGroups(html, own);
            }
        }
        if (unassigned.Count > 0)
        {
            html.AppendLine("<h2 id=\"a-unassigned\">Nicht zugeordnet (kein Katalogeintrag nennt diesen Capture-Schl&uuml;ssel)</h2>");
            WriteGroups(html, unassigned);
        }
        html.AppendLine("<h2 id=\"a-gaps\">Nicht erfasst</h2><p class=\"meta\">Katalogeintr&auml;ge ohne Bild und Szenen, die in dieser Zelle scheiterten.</p><ul>");
        foreach (Entry entry in withoutShots)
        {
            string reason = gaps.Where(gap => Owns(entry, gap.Key)).Select(gap => gap.Reason).FirstOrDefault() ?? entry.Reason ?? "keine Szene im Smoke";
            html.AppendLine("<li class=\"gap\"><span class=\"id\">" + E(entry.Id) + "</span><b>" + E(entry.Name) + "</b>: " + E(reason) + "</li>");
        }
        foreach (var gap in gaps.GroupBy(gap => (gap.Key, gap.Reason)))
            html.AppendLine("<li class=\"gap\"><b>" + E(gap.Key.Key) + "</b> (Szene, " + gap.Count() + " Zellen): " + E(gap.Key.Reason) + "</li>");
        html.AppendLine("</ul>");
        html.AppendLine("<script>const s=['theme','lang','size'].map(i=>document.getElementById(i));function f(){document.querySelectorAll('figure').forEach(g=>{"
            + "g.style.display=(!s[0].value||g.dataset.theme==s[0].value)&&(!s[1].value||g.dataset.lang==s[1].value)&&(!s[2].value||g.dataset.size==s[2].value)?'':'none'})}"
            + "s.forEach(x=>x.onchange=f)</script></body></html>");
        return html.ToString();
    }

    private static void WriteGroups(StringBuilder html, List<Shot> shots)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        foreach (var surface in shots.GroupBy(shot => shot.Key))
        {
            html.AppendLine("<p class=\"meta\"><code>" + E(surface.Key) + "</code></p><div class=\"grid\">");
            foreach (Shot shot in surface.OrderBy(s => s.Variant).ThenBy(s => s.Theme).ThenBy(s => s.Language))
                html.AppendLine("<figure class=\"" + (shot.Theme == "Dark" ? "dark" : "") + "\" data-theme=\"" + shot.Theme + "\" data-lang=\"" + shot.Language + "\" data-size=\"" + E(shot.Variant)
                    + "\"><a href=\"" + E(shot.File) + "\"><img loading=\"lazy\" src=\"" + E(shot.File) + "\" alt=\"" + E(shot.Key) + "\"></a><figcaption><b>" + E(shot.Key) + "</b><br>"
                    + shot.Theme + " &middot; " + shot.Language.ToUpperInvariant() + " &middot; " + E(shot.Variant) + " &middot; " + E(shot.Size) + " @" + shot.Scale + "x"
                    + (shot.Note.Length > 0 ? "<br>" + E(shot.Note) : "") + "</figcaption></figure>");
            html.AppendLine("</div>");
        }
    }
}
