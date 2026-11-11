using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// UX103 P7: every production dialog keeps header, local error strip and footer outside its
// scrolling body. Each group lives in its own DialogFrameChecks.<Group>.cs with private fixtures.
internal static partial class DialogFrameChecks
{
    private static readonly List<object> Evidence = new();

    private static readonly (string Name, Func<MainWindow, Task> Run)[] Groups =
    [
        ("archive", RunArchiveAsync),
        ("transfer", RunTransferAsync),
        ("search", RunSearchAsync),
        ("collections", RunCollectionsAsync),
        ("ratings", RunRatingsAsync),
        ("plugins", RunPluginsAsync),
        ("shell", RunShellAsync),
    ];

    private static partial Task RunArchiveAsync(MainWindow owner);
    private static partial Task RunTransferAsync(MainWindow owner);
    private static partial Task RunSearchAsync(MainWindow owner);
    private static partial Task RunCollectionsAsync(MainWindow owner);
    private static partial Task RunRatingsAsync(MainWindow owner);
    private static partial Task RunPluginsAsync(MainWindow owner);
    private static partial Task RunShellAsync(MainWindow owner);

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string selected = Environment.GetEnvironmentVariable("MEOWCHAN_DIALOG_FRAME_GROUP") ?? "all";
        string[] names = selected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(names.All(name => name == "all" || Groups.Any(group => group.Name == name)), "Unknown dialog frame group: " + selected);
        foreach ((string name, Func<MainWindow, Task> run) in Groups)
        {
            if (!names.Contains("all") && !names.Contains(name)) continue;
            await run(owner);
            Console.WriteLine("SMOKE dialog frame group PASS: " + name);
        }
        string evidence = Path.Combine(EvidenceDirectory, "dialog-frame-evidence.json");
        File.WriteAllText(evidence, JsonSerializer.Serialize(Evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("SMOKE dialog frame evidence: " + evidence);
    }

    /// <summary>PNG copies land here; the runner output keeps the originals.</summary>
    internal static string EvidenceDirectory
    {
        get
        {
            string directory = Environment.GetEnvironmentVariable("MEOWCHAN_DIALOG_FRAME_EVIDENCE") is { Length: > 0 } configured
                ? configured : AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>
    /// Verifies the shared frame on a shown dialog: parts present, header/error/footer outside
    /// the scrolling body, stacked without overlap, footer inside the window, and header/footer
    /// unmoved when the body scrolls to its end. <paramref name="root"/> is the dialog window or,
    /// for in-page overlays, the card that hosts the frame.
    /// </summary>
    internal static async Task VerifyFrameAsync(Control root, string label, string? capture = null)
    {
        await Settle();
        Control header = Part<Control>(root, DialogFrame.HeaderName, label);
        ScrollViewer body = Part<ScrollViewer>(root, DialogFrame.BodyName, label);
        Control footer = Part<Control>(root, DialogFrame.FooterName, label);
        Control? strip = Find<Control>(root, DialogFrame.ErrorStripName);
        Require(!IsInside(header, body) && !IsInside(footer, body), label + ": header/footer must not scroll with the body.");
        Require(strip == null || !IsInside(strip, body), label + ": error strip must not scroll with the body.");
        Rect h = BoundsIn(header, root), b = BoundsIn(body, root), f = BoundsIn(footer, root);
        Require(header.IsEffectivelyVisible && h.Height > 0, label + ": header not visible " + h);
        Require(footer.IsEffectivelyVisible && f.Height > 0, label + ": footer not visible " + f);
        Require(h.Bottom <= b.Top + 0.5, label + ": header overlaps body " + h + " / " + b);
        Require(b.Bottom <= f.Top + 0.5, label + ": body overlaps footer " + b + " / " + f);
        Require(f.Bottom <= root.Bounds.Height + 0.5, label + ": footer clipped by dialog " + f + " / " + root.Bounds);
        if (strip is { IsEffectivelyVisible: true })
        {
            Rect s = BoundsIn(strip, root);
            Require(s.Top >= h.Bottom - 0.5 && s.Bottom <= b.Top + 0.5, label + ": error strip not between header and body " + s);
        }
        bool scrollable = body.Extent.Height > body.Viewport.Height + 0.5;
        if (scrollable)
        {
            Vector start = body.Offset;
            body.Offset = new Vector(start.X, body.Extent.Height - body.Viewport.Height);
            await Settle();
            Rect h2 = BoundsIn(header, root), f2 = BoundsIn(footer, root);
            Require(Same(h, h2) && Same(f, f2), label + ": header/footer moved while scrolling " + h + "->" + h2 + ", " + f + "->" + f2);
            body.Offset = start;
            await Settle();
        }
        if (capture != null) Capture(root, capture);
        Evidence.Add(new
        {
            label, capture, scrollable, language = Texts.Language, header = h.ToString(), body = b.ToString(), footer = f.ToString(),
            errorStrip = strip is { IsEffectivelyVisible: true } ? BoundsIn(strip, root).ToString() : null,
            root = root.Bounds.ToString(), scale = TopLevel.GetTopLevel(root)?.RenderScaling
        });
        Console.WriteLine("SMOKE dialog frame PASS: " + label + (scrollable ? " (scrolled)" : ""));
    }

    /// <summary>Requires the local error strip to be shown with <paramref name="expected"/> text between header and body.</summary>
    internal static async Task VerifyErrorStripAsync(Control root, string label, string? expected = null)
    {
        await Settle();
        Control strip = Part<Control>(root, DialogFrame.ErrorStripName, label);
        Require(strip.IsEffectivelyVisible, label + ": error strip hidden");
        if (expected != null)
        {
            string text = string.Join("\n", strip.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
            Require(text.Contains(expected, StringComparison.Ordinal), label + ": error strip text '" + text + "' lacks '" + expected + "'");
        }
        await VerifyFrameAsync(root, label);
    }

    internal static void Capture(Control root, string name)
    {
        string file = "ux103-p7-" + name + ".png";
        Window window = root as Window ?? TopLevel.GetTopLevel(root) as Window
            ?? throw new InvalidOperationException("No window for capture " + name);
        // Render straight into the evidence directory. Copying the just-written PNG back fails while
        // this process still holds the encoded file open, which used to abort the whole group.
        Program.CaptureWindow(window, file, EvidenceDirectory);
    }

    internal static async Task Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    internal static T Part<T>(Control root, string name, string label) where T : Control =>
        Find<T>(root, name) ?? throw new InvalidOperationException(label + ": missing " + name + " (" + typeof(T).Name + ")");

    internal static T? Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private static bool IsInside(Control control, Control container) =>
        control.GetVisualAncestors().Contains(container) || control.GetLogicalAncestors().Contains(container);

    private static Rect BoundsIn(Control control, Control root) =>
        control.TransformToVisual(root) is { } transform ? new Rect(control.Bounds.Size).TransformToAABB(transform)
            : throw new InvalidOperationException(control.Name + " is not attached to " + root.Name);

    private static bool Same(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) <= 0.5 && Math.Abs(a.Y - b.Y) <= 0.5 && Math.Abs(a.Width - b.Width) <= 0.5 && Math.Abs(a.Height - b.Height) <= 0.5;

    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
