using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Recorder of the dialog gallery (MEOWCHAN_DIALOG_GALLERY_ONLY): every dialog, modal, popup, menu, banner
// and toast the gallery scenes open is rendered once per Light/Dark x EN/DE cell into one flat directory,
// together with manifest.json and an index.html grid. Review evidence only; the geometry and behaviour
// contracts of the same surfaces stay with DialogFrameChecks and the feature smokes.
internal static class DialogGallery
{
    internal sealed record Shot(string File, string Group, string Name, string Variant, string Theme, string Language,
        string Size, string Scale, string Note);

    internal sealed record Gap(string Group, string Name, string Theme, string Language, string Reason);

    private static readonly List<Shot> Shots = [];
    private static readonly List<Gap> Gaps = [];
    private static readonly HashSet<string> Files = new(StringComparer.Ordinal);
    private static string _directory = "";

    /// <summary>True while the gallery scene runs; the frame checks then record every verified dialog.</summary>
    internal static bool Active { get; private set; }

    internal static string Group { get; set; } = "";

    internal static string Theme { get; set; } = "";

    internal static string Language => Texts.Language;

    internal static int ShotCount => Shots.Count;

    internal static string OutputDirectory => _directory;

    internal static void Begin(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        Shots.Clear(); Gaps.Clear(); Findings.Clear(); Files.Clear();
        Active = true;
    }

    internal static void End() => Active = false;

    /// <summary>Records a dialog frame check (the label carries the dialog, its language and whether it is the minimum size).</summary>
    internal static void RecordFrame(Control root, string label, string note = "")
    {
        bool minimum = Regex.IsMatch(label, @"(^|[\s/_-])min(imum)?(\b|$)", RegexOptions.IgnoreCase);
        string name = Regex.Replace(label, @"[\s/_-]+min(imum)?\b", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"-(en|de)(?=-|$)", "");
        name = Regex.Replace(name.Replace('/', ' '), @"\s+normal\b", "", RegexOptions.IgnoreCase).Trim();
        Record(root, name, minimum ? "minimum size" : "default size", note);
    }

    /// <summary>Renders a window (or, for an in-page overlay, its top-level window) as one gallery image.</summary>
    internal static void Record(Control root, string name, string variant, string note)
    {
        Visual visual = root is Window ? root : (Visual?)TopLevel.GetTopLevel(root) ?? root;
        TextButtonProbe.Capture(visual, name);
        RenderTo(visual, name, variant, note, root);
    }

    /// <summary>Renders a popup root, flyout or menu surface by itself (these are separate top-levels, never part of the window image).</summary>
    internal static void RecordSurface(Control surface, string name, string variant, string note) =>
        RenderTo(surface, name, variant, note, surface);

    private static readonly List<Gap> Findings = [];

    /// <summary>A frame-contract violation the gallery saw (the image is still recorded).</summary>
    internal static void Finding(string name, string detail) => Findings.Add(new Gap(Group, name, Theme, Language, detail));

    internal static void Skip(string name, string reason) => Gaps.Add(new Gap(Group, name, Theme, Language, reason));

    private static void RenderTo(Visual visual, string name, string variant, string note, Control measured)
    {
        if (!Active) return;
        if (visual is Control control) control.UpdateLayout();
        TopLevel? top = TopLevel.GetTopLevel(visual as Control);
        double scale = top?.RenderScaling ?? 1;
        double width = visual.Bounds.Width, height = visual.Bounds.Height;
        if (width < 1 || height < 1)
            throw new InvalidOperationException("Gallery capture target has no size: " + name);
        string stem = Slug(name) + "__" + Slug(variant) + "__" + Theme + "-" + Language;
        string file = stem + ".png";
        for (int i = 2; !Files.Add(file); i++) file = stem + "-" + i + ".png";
        using RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(_directory, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Shots.Add(new Shot(file, Group, name, variant, Theme, Language,
            Math.Round(measured is Window w ? w.ClientSize.Width : width) + " x " + Math.Round(measured is Window v ? v.ClientSize.Height : height) + " DIP",
            scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), note));
        Console.WriteLine("SMOKE dialog gallery: " + file);
    }

    private static string Slug(string text) => Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    /// <summary>Writes manifest.json and index.html; returns the number of images.</summary>
    internal static int Finish(string runLabel)
    {
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), JsonSerializer.Serialize(new { run = runLabel, shots = Shots, gaps = Gaps, layoutFindings = Findings },
            new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_directory, "index.html"), Html(runLabel));
        TextButtonProbe.Write(_directory);
        Console.WriteLine("SMOKE dialog gallery: " + Shots.Count + " images, " + Gaps.Count + " gaps, " + Findings.Count + " layout findings in " + _directory);
        return Shots.Count;
    }

    private static string Html(string runLabel)
    {
        StringBuilder html = new();
        string E(string text) => WebUtility.HtmlEncode(text);
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>MeowChan dialog gallery</title>");
        html.AppendLine("<style>body{font:14px system-ui,sans-serif;margin:24px;background:#f4f4f5;color:#18181b}h1{margin:0 0 4px}h2{margin:32px 0 8px;border-bottom:1px solid #ccc;padding-bottom:4px}"
            + "h3{margin:20px 0 6px;font-size:15px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:12px}"
            + "figure{margin:0;background:#fff;border:1px solid #d4d4d8;border-radius:6px;padding:8px}figure.dark{background:#27272a;color:#e4e4e7;border-color:#3f3f46}"
            + "img{max-width:100%;height:auto;display:block;margin:0 auto;border:1px solid #8883}figcaption{font-size:12px;margin-top:6px;line-height:1.4}"
            + "code{font-size:11px}.gap{color:#b91c1c}nav a{margin-right:10px}.filters{margin:12px 0}</style></head><body>");
        html.AppendLine("<h1>MeowChan dialog gallery</h1><p>" + E(runLabel) + " &mdash; " + Shots.Count + " images, Light/Dark x EN/DE, fixture profiles only. "
            + "Click an image for the original.</p>");
        html.AppendLine("<div class=\"filters\">Show: <select id=\"theme\"><option value=\"\">all themes</option><option>Light</option><option>Dark</option></select> "
            + "<select id=\"lang\"><option value=\"\">all languages</option><option>en</option><option>de</option></select> "
            + "<select id=\"size\"><option value=\"\">all sizes</option><option>default size</option><option>minimum size</option></select></div>");
        string[] groups = Shots.Select(shot => shot.Group).Distinct().ToArray();
        html.AppendLine("<nav>" + string.Join("", groups.Select(group => "<a href=\"#g-" + Slug(group) + "\">" + E(group) + "</a>")) + "</nav>");
        foreach (string group in groups)
        {
            html.AppendLine("<h2 id=\"g-" + Slug(group) + "\">" + E(group) + "</h2>");
            foreach (var surface in Shots.Where(shot => shot.Group == group).GroupBy(shot => shot.Name))
            {
                html.AppendLine("<h3>" + E(surface.Key) + "</h3><div class=\"grid\">");
                foreach (Shot shot in surface.OrderBy(s => s.Variant).ThenBy(s => s.Theme).ThenBy(s => s.Language))
                    html.AppendLine("<figure class=\"" + (shot.Theme == "Dark" ? "dark" : "") + "\" data-theme=\"" + shot.Theme + "\" data-lang=\"" + shot.Language
                        + "\" data-size=\"" + E(shot.Variant) + "\"><a href=\"" + E(shot.File) + "\"><img loading=\"lazy\" src=\"" + E(shot.File) + "\" alt=\"" + E(shot.Name) + "\"></a>"
                        + "<figcaption><b>" + E(shot.Name) + "</b><br>" + shot.Theme + " &middot; " + shot.Language.ToUpperInvariant() + " &middot; " + E(shot.Variant)
                        + " &middot; " + E(shot.Size) + " @" + shot.Scale + "x" + (shot.Note.Length > 0 ? "<br>" + E(shot.Note) : "") + "</figcaption></figure>");
                html.AppendLine("</div>");
            }
        }
        if (Findings.Count > 0)
        {
            html.AppendLine("<h2 id=\"g-findings\">Layout findings (frame contract violated; image still shown)</h2><ul>");
            foreach (var finding in Findings)
                html.AppendLine("<li class=\"gap\"><b>" + E(finding.Name) + "</b> (" + finding.Theme + ", " + finding.Language + "): " + E(finding.Reason) + "</li>");
            html.AppendLine("</ul>");
        }
        if (Gaps.Count > 0)
        {
            html.AppendLine("<h2 id=\"g-gaps\">Not captured</h2><ul>");
            foreach (var gap in Gaps.GroupBy(gap => (gap.Group, gap.Name, gap.Reason)))
                html.AppendLine("<li class=\"gap\"><b>" + E(gap.Key.Name) + "</b> (" + E(gap.Key.Group) + ", " + gap.Count() + " cells): " + E(gap.Key.Reason) + "</li>");
            html.AppendLine("</ul>");
        }
        html.AppendLine("<script>const s=['theme','lang','size'].map(i=>document.getElementById(i));function f(){document.querySelectorAll('figure').forEach(g=>{"
            + "g.style.display=(!s[0].value||g.dataset.theme==s[0].value)&&(!s[1].value||g.dataset.lang==s[1].value)&&(!s[2].value||g.dataset.size==s[2].value)?'':'none'})}"
            + "s.forEach(x=>x.onchange=f)</script></body></html>");
        return html.ToString();
    }
}
