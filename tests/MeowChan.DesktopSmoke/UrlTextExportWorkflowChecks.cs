using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// Root invokes after Ready in an isolated native smoke process. No runner hook.
internal static class UrlTextExportWorkflowChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible, "Visible native owner required.");
        string language = Texts.Language;
        // Runtime fixture only; retained for Root inspection, never a profile.
        string root = Path.Combine(Path.GetTempPath(), "meowchan-urltext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int readers = 0, choosers = 0, writes = 0, shows = 0;
        var page = new ArchivePage(_ =>
        {
            readers++;
            throw new InvalidOperationException("No archive reader allowed in synthetic URLtext smoke.");
        }, (_, _) => throw new InvalidOperationException("No capture mutation allowed."),
            _ => throw new InvalidOperationException("No folder launch allowed.")) { IsAvailable = true };
        var nativeWindow = new Window
        {
            Title = "URLtext synthetic offline workflow", Width = 1000, Height = 750, Content = page
        };
        try
        {
            nativeWindow.Show(owner);
            await WaitAsync(() => nativeWindow.IsVisible, "native fixture host");
            foreach (var fixture in new[]
            {
                (Id: new ResourceId("4chan", "g", "123"), Url: "https://boards.4chan.org/g/thread/123"),
                (Id: new ResourceId("8kun", "tech", "456"), Url: "https://8kun.top/tech/res/456.html")
            })
            {
                var summary = new ArchiveThreadSummary(fixture.Id, fixture.Url, "Synthetic offline summary",
                    "", ThreadSourceState.Archived, false, DateTimeOffset.UnixEpoch, null, 1, 1);
                page.UrlTextSummaryForTests = (id, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Require(id == fixture.Id, "Selected summary identity changed.");
                    return Task.FromResult<ArchiveThreadSummary?>(summary);
                };
                byte[] expected = new UTF8Encoding(false, true).GetBytes(fixture.Url + "\n");
                string target = Path.Combine(root, fixture.Id.Source + "-tmp.txt");
                Require(Path.IsPathFullyQualified(target) && !File.Exists(target), "New absolute target required.");
                page.UrlTextChooseForTests = () => { choosers++; return Task.FromResult<string?>(target); };
                page.UrlTextWriteForTests = async (plan, path, token) =>
                {
                    writes++;
                    Require(plan.Id == fixture.Id && plan.Url == fixture.Url && plan.CopyUtf8().SequenceEqual(expected),
                        "Writer plan differs from visible consent.");
                    Require(path == target, "Chooser target changed.");
                    return await new UrlTextExportFileWriter().WriteAsync(plan, path, token);
                };
                foreach (bool confirm in new[] { false, true })
                {
                    int beforeChoose = choosers, beforeWrite = writes, beforeShow = shows;
                    Exception? driverFailure = null;
                    page.UrlTextShowForTests = async (dialog, actualOwner) =>
                    {
                        shows++;
                        Task? shown = null;
                        try
                        {
                            Require(ReferenceEquals(actualOwner, nativeWindow), "Dialog attached to wrong host.");
                            shown = dialog.ShowDialog(actualOwner);
                            await WaitAsync(() => dialog.IsVisible && actualOwner.OwnedWindows.Contains(dialog), "real ShowDialog");
                            Button save = Named<Button>(dialog, "UrlTextExportSave");
                            Button close = Named<Button>(dialog, "UrlTextExportClose");
                            TextBlock[] identities = new[] { "Source", "Board", "Thread", "Url" }
                                .Select(part => Named<TextBlock>(dialog, "UrlTextExport" + part)).ToArray();
                            // Observe the real native client at both the normal and minimum scene.
                            // Never measure a clamped/larger host as a successful 360-DIP layout.
                            foreach (var size in new[] { (Width: 640d, Height: 600d), (Width: 360d, Height: 360d) })
                            {
                                dialog.Width = size.Width;
                                dialog.Height = size.Height;
                                await WaitAsync(() => Math.Abs(dialog.ClientSize.Width - size.Width) <= 1
                                    && Math.Abs(dialog.ClientSize.Height - size.Height) <= 1,
                                    $"native client {size.Width}x{size.Height}; clamping is not a layout PASS");
                                foreach (string code in new[] { "es", "fr", "ru" })
                                {
                                    Require(Texts.Languages.Any(item => item.Code == code), "Missing language: " + code);
                                    Texts.SetLanguage(code);
                                    await WaitAsync(() => dialog.Title == Texts.T("urltext.export.title")
                                        && Equals(save.Content, Texts.T("urltext.export.save")), "live " + code);
                                    await SettleAsync(dialog);
                                    string[] consent = Named<StackPanel>(dialog, "UrlTextExportConsent").Children
                                        .OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
                                    string[] expectedConsent =
                                    [
                                        Texts.T("urltext.export.source", fixture.Id.Source),
                                        Texts.T("urltext.export.board", fixture.Id.Board),
                                        Texts.T("urltext.export.thread", fixture.Id.Thread),
                                        Texts.T("urltext.export.url", fixture.Url),
                                        Texts.T("urltext.export.scope"), Texts.T("urltext.export.excluded"),
                                        Texts.T("urltext.export.privacy"), Texts.T("urltext.export.noClient"),
                                        Texts.T("urltext.export.availability")
                                    ];
                                    Require(new[] { "Source", "Board", "Thread", "Url" }
                                        .Select(part => Named<TextBlock>(dialog, "UrlTextExport" + part))
                                        .Zip(identities).All(pair => ReferenceEquals(pair.First, pair.Second)),
                                        "Live refresh replaced identity controls.");
                                    Require(consent.SequenceEqual(expectedConsent), "Live consent or frozen identity changed: " + code);
                                    Require(Equals(close.Content, Texts.T("urltext.export.cancel"))
                                        && AutomationProperties.GetName(save) == Texts.T("urltext.export.save")
                                        && !dialog.Operation.Started && choosers == beforeChoose && writes == beforeWrite,
                                        "Language refresh authorized an operation.");
                                    CheckGeometry(dialog, save, close, "urltext.export.cancel", new Size(size.Width, size.Height),
                                        $"{fixture.Id.Source}/{(confirm ? "save" : "cancel")}/{size.Width}x{size.Height}/{code}/before-confirm");
                                }
                            }
                            if (confirm)
                            {
                                Require(save.IsEffectivelyVisible && save.IsEffectivelyEnabled, "Save not actionable.");
                                SmokeActionClick.Execute(save);
                                await dialog.Operation.Completion.WaitAsync(TimeSpan.FromSeconds(20));
                                Require(dialog.Operation.Result is { Status: UrlTextExportFileWriteStatus.Saved,
                                    Error: UrlTextExportFileWriteError.None }, "Real writer did not save.");
                                Require(File.ReadAllBytes(target).SequenceEqual(expected), "Final bytes are not exactly confirmed URL + LF.");
                                // The real Close label exists only after confirmation; inspect it too,
                                // including the now-disabled Save text, without enabling either artificially.
                                foreach (string code in new[] { "es", "fr", "ru" })
                                {
                                    Texts.SetLanguage(code);
                                    await WaitAsync(() => Equals(close.Content, Texts.T("urltext.export.close"))
                                        && Equals(save.Content, Texts.T("urltext.export.save")), "saved live " + code);
                                    await SettleAsync(dialog);
                                    Require(!save.IsEnabled && close.IsEffectivelyEnabled
                                        && Named<TextBlock>(dialog, "UrlTextExportStatus").Text == Texts.T("urltext.export.saved")
                                        && choosers == beforeChoose + 1 && writes == beforeWrite + 1,
                                        "Saved language refresh changed the result or authorized another write.");
                                    CheckGeometry(dialog, save, close, "urltext.export.close", new Size(360, 360),
                                        $"{fixture.Id.Source}/360x360/{code}/saved");
                                }
                                Require(File.ReadAllBytes(target).SequenceEqual(expected), "Saved live refresh changed final bytes.");
                            }
                            SmokeActionClick.Execute(close);
                            await shown.WaitAsync(TimeSpan.FromSeconds(20));
                        }
                        catch (Exception error) { driverFailure = error; throw; }
                        finally
                        {
                            // Drain owned operation before releasing the native window; never abandon a writer.
                            try
                            {
                                await dialog.TerminateAsync();
                                if (dialog.IsVisible) dialog.Close();
                                if (shown != null) await shown;
                            }
                            catch (Exception error)
                            {
                                driverFailure = driverFailure == null ? error : new AggregateException(driverFailure, error);
                                throw;
                            }
                        }
                    };
                    await page.OpenUrlTextExportForThreadAsync(fixture.Id, null);
                    // Production intentionally translates exceptions to UI messages: preserve driver failures explicitly.
                    if (driverFailure != null) throw new InvalidOperationException("URLtext native driver failed.", driverFailure);
                    Require(shows == beforeShow + 1, "Workflow did not show an actual dialog.");
                    Require(choosers == beforeChoose + (confirm ? 1 : 0)
                        && writes == beforeWrite + (confirm ? 1 : 0), "Chooser/write confirmation boundary violated.");
                    Require(confirm || !File.Exists(target), "Pre-confirm cancellation created output.");
                    Console.WriteLine("SMOKE URLTEXT_NATIVE_GP53 " + fixture.Id.Source + " "
                        + (confirm ? "SAVE_REAL_WRITER_BYTES_OK" : "CANCEL_CHOOSER0_WRITE0") + " LIVE_ES_FR_RU_OK GEOMETRY_CHECKED");
                }
            }
            Require(readers == 0 && shows == 4 && choosers == 2 && writes == 2, "Unexpected boundary totals.");
            Console.WriteLine("SMOKE URLTEXT_NATIVE_GP53 COMPLETE injected-chooser; NOT OS-savepicker; retained=" + root);
        }
        finally
        {
            page.EndLifetime();
            await page.UrlTextExportTask;
            nativeWindow.Close();
            Texts.SetLanguage(language);
        }
    }

    // Read realized production templates only: do not replace button content, set wrapping,
    // widen a control or fake a label. Row/glyph wrapping is reported as actually observed.
    private static void CheckGeometry(UrlTextExportDialog dialog, Button save, Button close,
        string closeKey, Size requestedClient, string scene)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(dialog.IsVisible && Math.Abs(dialog.ClientSize.Width - requestedClient.Width) <= 1
            && Math.Abs(dialog.ClientSize.Height - requestedClient.Height) <= 1,
            scene + ": actual native client differs from requested scene; no layout PASS");
        WindowChromeBar chrome = Named<WindowChromeBar>(dialog, "UrlTextExportChrome");
        Require(dialog.WindowDecorations == WindowDecorations.BorderOnly && dialog.ExtendClientAreaToDecorationsHint
            && Math.Abs(dialog.ExtendClientAreaTitleBarHeightHint - 48) <= 0.01,
            scene + ": own 48-DIP chrome policy changed");
        Require(chrome.IsEffectivelyVisible && TopLevel.GetTopLevel(chrome) == dialog
            && Math.Abs(chrome.Bounds.Height - 48) <= 1 && Fits(chrome, dialog, default),
            scene + ": own chrome is detached, hidden or clipped");
        Point chromeOrigin = chrome.TranslatePoint(default, dialog)
            ?? throw new InvalidOperationException(scene + ": detached chrome");
        Require(Math.Abs(chromeOrigin.Y) <= 1 && Math.Abs(chrome.Bounds.Width - dialog.ClientSize.Width) <= 1,
            scene + ": chrome is not the visible full-width first row");
        TextBlock caption = Named<TextBlock>(dialog, "WindowCaptionTitle");
        Require(caption.GetVisualAncestors().Contains(chrome) && caption.IsEffectivelyVisible
            && caption.Text == dialog.Title && Fits(caption, chrome, default), scene + ": own title is not realized/current");
        foreach (string name in new[] { "WindowMinimizeButton", "WindowMaximizeButton", "WindowCloseButton" })
        {
            Button button = Named<Button>(dialog, name);
            bool belongsToChrome = button.GetVisualAncestors().Contains(chrome);
            bool visible = button.IsEffectivelyVisible;
            bool fitsChrome = Fits(button, chrome, default);
            var probe = ObserveCenterHit(dialog, button);
            bool centerHit = probe.InClient && probe.IsControlOrDescendant;
            // Emit BEFORE the compound assertion, including its exact evaluated predicates.
            // Disabled/hidden captions are observations, never an automatic skip or exemption.
            Console.WriteLine("SMOKE URLTEXT_NATIVE_GP53 CAPTION_PROBE " + JsonSerializer.Serialize(new
            {
                scene, name, belongsToChrome, visible, fitsChrome, centerHit,
                failedPredicates = new[]
                {
                    belongsToChrome ? null : "belongsToChrome", visible ? null : "visible",
                    fitsChrome ? null : "fitsChrome", centerHit ? null : "centerHit"
                }.Where(value => value != null).ToArray(),
                window = new
                {
                    bounds = dialog.Bounds.ToString(), client = dialog.ClientSize.ToString(),
                    frame = dialog.FrameSize?.ToString(), state = dialog.WindowState.ToString(),
                    dialog.IsVisible, dialog.IsEnabled, dialog.IsEffectivelyEnabled,
                    dialog.CanMinimize, dialog.CanMaximize, dialog.CanResize, dialog.RenderScaling
                },
                chrome = DescribeVisual(chrome), button = DescribeVisual(button),
                buttonOriginInWindow = button.TranslatePoint(default, dialog)?.ToString(),
                buttonOriginInChrome = button.TranslatePoint(default, chrome)?.ToString(),
                centerInWindow = probe.Center?.ToString(), centerInClient = probe.InClient,
                hitType = probe.Hit?.GetType().FullName, hitIsControlOrDescendant = probe.IsControlOrDescendant,
                actualHitPath = probe.Hit is Visual hit
                    ? hit.GetVisualAncestors().Prepend(hit).Select(DescribeVisual).ToArray() : Array.Empty<object>(),
                buttonPath = button.GetVisualAncestors().Prepend(button).Select(DescribeVisual).ToArray()
            }));
            Require(belongsToChrome && visible && fitsChrome && centerHit,
                scene + ": invisible/covered caption " + name);
        }
        Require(AutomationProperties.GetName(Named<Button>(dialog, "WindowCloseButton")) == Texts.T("action.close"),
            scene + ": own chrome did not refresh its close name");

        WrapPanel actions = save.GetVisualAncestors().OfType<WrapPanel>().FirstOrDefault()
            ?? throw new InvalidOperationException(scene + ": production action WrapPanel missing");
        Require(actions.Children.Count == 2 && ReferenceEquals(actions.Children[0], save)
            && ReferenceEquals(actions.Children[1], close) && close.GetVisualAncestors().Contains(actions),
            scene + ": Save/Close are no longer the real action pair");
        Border footer = actions.GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("workspace-dialog-footer"));
        Require(Fits(actions, footer, footer.Padding) && Fits(footer, dialog, default)
            && Fits(save, actions, default) && Fits(close, actions, default), scene + ": action/footer clipped");
        Point footerOrigin = footer.TranslatePoint(default, dialog)!.Value;
        Require(footerOrigin.Y >= chromeOrigin.Y + chrome.Bounds.Height - 1, scene + ": footer overlaps chrome");
        Point saveOrigin = save.TranslatePoint(default, actions)!.Value;
        Point closeOrigin = close.TranslatePoint(default, actions)!.Value;
        bool nextRow = closeOrigin.Y >= saveOrigin.Y + save.Bounds.Height + actions.LineSpacing - 1;
        bool sameRow = Math.Min(closeOrigin.Y + close.Bounds.Height, saveOrigin.Y + save.Bounds.Height)
                > Math.Max(closeOrigin.Y, saveOrigin.Y)
            && closeOrigin.X >= saveOrigin.X + save.Bounds.Width + actions.ItemSpacing - 1;
        Require(nextRow || sameRow, scene + ": actions overlap or lost wrap spacing");
        // Only require a row break if the actual realized pair cannot fit side by side.
        // Short translations may fit at the minimum width: that is not a wrapped-row claim.
        if (save.Bounds.Width + close.Bounds.Width + actions.ItemSpacing > actions.Bounds.Width + 1)
            Require(nextRow, scene + ": action pair requires wrapping but stayed on one row");
        int saveLines = CheckButtonText(dialog, save, Texts.T("urltext.export.save"), scene);
        int closeLines = CheckButtonText(dialog, close, Texts.T(closeKey), scene);
        Require(close.IsEffectivelyEnabled && HitAtCenter(dialog, close), scene + ": Close/Cancel unreachable");
        if (save.IsEffectivelyEnabled) Require(HitAtCenter(dialog, save), scene + ": Save covered");
        Console.WriteLine("SMOKE URLTEXT_NATIVE_GP53 GEOMETRY " + JsonSerializer.Serialize(new
        {
            scene, width = dialog.ClientSize.Width, height = dialog.ClientSize.Height, scale = dialog.RenderScaling,
            chrome = chrome.Bounds.ToString(), save = save.Bounds.ToString(), close = close.Bounds.ToString(),
            actions = actions.Bounds.ToString(), actionRows = nextRow ? 2 : 1, saveLines, closeLines
        }));
    }

    private static int CheckButtonText(Window dialog, Button button, string expected, string scene)
    {
        TextBlock[] matches = button.GetVisualDescendants().OfType<TextBlock>()
            .Where(label => label.Text == expected).ToArray();
        Require(matches.Length == 1 && Equals(button.Content, expected)
            && AutomationProperties.GetName(button) == expected, scene + ": complete button label/name missing: " + button.Name);
        TextBlock label = matches.Single();
        ContentPresenter presenter = label.GetVisualAncestors().OfType<ContentPresenter>().FirstOrDefault()
            ?? throw new InvalidOperationException(scene + ": button presenter missing");
        Require(presenter.GetVisualAncestors().Contains(button) && button.IsEffectivelyVisible && label.IsEffectivelyVisible,
            scene + ": detached/hidden button text");
        var layout = label.TextLayout;
        string rendered = string.Concat(layout.TextLines.SelectMany(line => line.TextRuns)
            .Where(run => run is not TextEndOfLine).Select(run => run.Text.ToString()));
        Require(rendered == expected && layout.TextLines.All(line => !line.HasCollapsed),
            scene + ": shortened/collapsed button glyphs: " + button.Name);
        Require(layout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1 && layout.Height <= label.Bounds.Height + 1,
            scene + ": text layout exceeds label: " + button.Name);
        Require(Fits(label, presenter, presenter.Padding) && Fits(label, button, button.Padding) && Fits(button, dialog, default),
            scene + ": text/button outside actual content/client bounds: " + button.Name);
        foreach (Control ancestor in label.GetVisualAncestors().OfType<Control>())
            if (ancestor.ClipToBounds || ancestor is ScrollViewer)
                Require(Fits(label, ancestor, default), scene + ": button label clipped by " + ancestor.GetType().Name);
        Console.WriteLine("SMOKE URLTEXT_NATIVE_GP53 BUTTON_TEXT " + JsonSerializer.Serialize(new
        {
            scene, name = button.Name, expected, rendered, label = label.Bounds.ToString(),
            presenter = presenter.Bounds.ToString(), layout.WidthIncludingTrailingWhitespace, layout.Height,
            wrapping = label.TextWrapping.ToString(), trimming = label.TextTrimming.ToString(),
            lines = layout.TextLines.Count()
        }));
        return layout.TextLines.Count();
    }

    private static bool HitAtCenter(Window window, Control control)
    {
        var probe = ObserveCenterHit(window, control);
        return probe.InClient && probe.IsControlOrDescendant;
    }

    private static (Point? Center, bool InClient, object? Hit, bool IsControlOrDescendant)
        ObserveCenterHit(Window window, Control control)
    {
        Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        bool inClient = center is Point point && point.X >= 0 && point.Y >= 0
            && point.X < window.ClientSize.Width && point.Y < window.ClientSize.Height;
        // Also observe an out-of-client point for diagnosis, but it can NEVER pass.
        object? hit = center.HasValue ? window.InputHitTest(center.Value) : null;
        bool matches = hit is Visual visual
            && (ReferenceEquals(visual, control) || visual.GetVisualAncestors().Contains(control));
        return (center, inClient, hit, matches);
    }

    private static object DescribeVisual(Visual visual) => new
    {
        type = visual.GetType().FullName, name = (visual as Control)?.Name,
        bounds = visual.Bounds.ToString(), visual.IsVisible,
        effectivelyVisible = (visual as Control)?.IsEffectivelyVisible,
        enabled = (visual as InputElement)?.IsEnabled,
        effectivelyEnabled = (visual as InputElement)?.IsEffectivelyEnabled,
        hitTestVisible = (visual as InputElement)?.IsHitTestVisible,
        visual.ClipToBounds
    };

    private static bool Fits(Control child, Control container, Thickness inset)
    {
        Point? origin = child.TranslatePoint(default, container);
        return origin.HasValue && child.Bounds.Width > 0 && child.Bounds.Height > 0
            && origin.Value.X >= inset.Left - 1 && origin.Value.Y >= inset.Top - 1
            && origin.Value.X + child.Bounds.Width <= container.Bounds.Width - inset.Right + 1
            && origin.Value.Y + child.Bounds.Height <= container.Bounds.Height - inset.Bottom + 1;
    }

    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        // UpdateLayout is not a rendered-frame fence. In particular, InvokeAsync
        // may run inline on this thread while the compositor hit tree still uses
        // pre-resize bounds. Observe two actual frame callbacks before hit-testing:
        // the second is after the preceding frame's scene submission. No predicate
        // is retried until it passes and no geometry/input assertion is relaxed.
        for (int frame = 0; frame < 2; frame++)
        {
            var rendered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RequestAnimationFrame(time => rendered.TrySetResult(time));
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static T Named<T>(Window dialog, string name) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static async Task WaitAsync(Func<bool> ready, string phase)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("URLtext smoke: " + phase);
            await Task.Delay(20);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
