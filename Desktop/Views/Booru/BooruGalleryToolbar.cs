using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed class BooruGalleryToolbar : Border
{
    private readonly Button _openPost;
    private readonly Button _openWindow;
    private readonly Button _close;
    private readonly Button _fullscreen;
    private readonly TextBlock _position;
    private readonly TextBlock _selectionLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _selectionKey = new();
    private GalleryToolbarActionContent? _downloadContent;
    private string _downloadLabel = string.Empty;

    internal BooruGalleryToolbar(string prefix, bool popout = false)
    {
        Name = prefix + "Bar";
        Previous = new() { Name = prefix + "Previous" };
        Next = new() { Name = prefix + "Next" };
        _position = new() { Name = prefix + "Position", Classes = { "workspace-meta", "tnum" }, VerticalAlignment = VerticalAlignment.Center };
        Select = new() { Name = prefix + "Select", [!MinHeightProperty] = new DynamicResourceExtension(WorkspaceTokens.SizeControl) };
        Download = new() { Name = prefix + "Download", Classes = { "primary" } };
        _openPost = new() { Name = prefix + "OpenPost" };
        _openWindow = new() { Name = prefix + "OpenWindow", IsVisible = !popout };
        _close = new() { Name = prefix + "Close", IsVisible = popout };
        _fullscreen = new() { Name = prefix + "Fullscreen" };

        Border keycap = new() { Classes = { KeyCapText.CapClass }, Child = _selectionKey, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(keycap, AccessibilityView.Raw);
        Select.Content = Group(_selectionLabel, keycap);
        Select.VerticalAlignment = VerticalAlignment.Center;

        Child = new GalleryToolbarPanel
        {
            Children = { Group(_close, Select), Group(Previous, _position, Next), Group(Download, _openPost, _openWindow, _fullscreen) }
        };
        Previous.Click += (_, _) => Move?.Invoke(-1);
        Next.Click += (_, _) => Move?.Invoke(1);
        Select.Click += (_, _) => ToggleSelection?.Invoke();
        Download.Click += (_, _) => DownloadOriginal();
        _openPost.Click += (_, _) => OpenPost?.Invoke();
        _openWindow.Click += (_, _) => OpenWindow?.Invoke();
        _close.Click += (_, _) => Close?.Invoke();
        _fullscreen.Click += (_, _) => Fullscreen?.Invoke();
        ToolTip.SetShowOnDisabled(Previous, true);
        ToolTip.SetShowOnDisabled(Next, true);
        ToolTip.SetShowOnDisabled(Download, true);
        Download.AttachedToVisualTree += (_, _) =>
        {
            if (_downloadContent is not null) return;
            _downloadContent = new GalleryToolbarActionContent(CommandBindings.NamedIcon("download-cloud"), _downloadLabel);
            Download.Content = _downloadContent;
        };
    }

    internal Button Previous { get; }
    internal Button Next { get; }
    internal Button Download { get; }
    internal CheckBox Select { get; }
    internal Action<int>? Move { get; set; }
    internal Action<bool>? MoveToBoundary { get; set; }
    internal Action? ToggleSelection { get; set; }
    internal Action? Load { get; set; }
    internal Action? OpenPost { get; set; }
    internal Action? OpenWindow { get; set; }
    internal Action? Close { get; set; }
    internal Action? Fullscreen { get; set; }

    private static StackPanel Group(params Control[] controls)
    {
        StackPanel group = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        group.Children.AddRange(controls);
        return group;
    }

    internal void Present(BooruResultRowView row, int index, int count, bool selected)
    {
        PresentCommand(Previous, "gallery.previous");
        PresentCommand(Next, "gallery.next");
        PresentCommand(_fullscreen, "window.fullscreen");
        PresentCommand(_close, "gallery.close");
        Previous.IsEnabled = index > 0;
        Next.IsEnabled = index < count - 1;
        _position.Text = Texts.T("booru.gallery.position", index + 1, count);

        string title = Texts.T("booru.ui.post.title", row.Post.PostId);
        string selectionLabel = Texts.T("media.tile.selected");
        string selectionKey = Texts.T("key.space");
        string selectionKeys = selectionKey + " / " + Texts.T("key.enter");
        _selectionLabel.Text = selectionLabel;
        _selectionKey.Text = selectionKey;
        Select.IsChecked = selected;
        ToolTip.SetTip(Select, selectionLabel + " · " + selectionKeys);
        AutomationProperties.SetName(Select, selectionLabel + ": " + title);
        AutomationProperties.SetAcceleratorKey(Select, selectionKey);
        AutomationProperties.SetHelpText(Select, selectionKeys);

        _downloadLabel = row.CanRetry ? BooruText.RetryOriginal(row) : Texts.T("booru.ui.load");
        if (_downloadContent is { } content) content.Text = _downloadLabel;
        else Download.Content = _downloadLabel;
        Download.IsEnabled = row.CanLoad || row.CanRetry;
        string action = row.CanRetry ? _downloadLabel + ": " + title : Texts.T("booru.ui.loadFor", row.Post.PostId);
        string? reason = BooruText.Load(row) ?? BooruText.Original(row.Post.OriginalAccess, row.Post.IsDeleted || row.Post.IsBanned);
        string? shortcut = CommandCatalog.ShortcutTooltipDisplay("media.export-original", "gallery");
        string tip = (shortcut is null ? action : action + " · " + shortcut) + Environment.NewLine + reason;
        ToolTip.SetTip(Download, tip);
        AutomationProperties.SetName(Download, action);
        AutomationProperties.SetHelpText(Download, tip);

        _openPost.IsVisible = BooruPostLinks.TryCanonical(row.Post, out string url);
        IconButtons.Apply(_openPost, "external-link", Texts.T("booru.ui.post.openFor", row.Post.PostId));
        if (_openPost.IsVisible) ToolTip.SetTip(_openPost, Texts.T("booru.ui.post.openTip", url));
        IconButtons.Apply(_openWindow, "maximize", Texts.T("booru.gallery.openWindow"));
    }

    private static void PresentCommand(Button button, string id)
    {
        IconButtons.Apply(button, CommandCatalog.Get(id).Icon, CommandCatalog.Label(id));
        string? shortcut = CommandCatalog.ShortcutTooltipDisplay(id, "gallery");
        if (id == "gallery.close")
            shortcut = shortcut is null ? Texts.T("key.escape") : Texts.T("key.escape") + " / " + shortcut;
        ToolTip.SetTip(button, shortcut is null ? CommandCatalog.Label(id) : CommandCatalog.Label(id) + " · " + shortcut);
        AutomationProperties.SetAcceleratorKey(button, shortcut);
    }

    private void DownloadOriginal()
    {
        if (Download.IsEnabled) Load?.Invoke();
    }

    internal void RouteKey(KeyEventArgs e, Control stage)
    {
        if (e.Handled || ShortcutService.IsCapturing || e.Source is Visual source
            && source.GetSelfAndVisualAncestors().Any(PointerFocusReclaim.IsTextInput)) return;

        using var context = EffectiveShortcutBindings.EnterKeyContext("gallery");
        EffectiveShortcutBindings bindings = ShortcutService.Current;
        bool plain = e.KeyModifiers == KeyModifiers.None;
        // The surrounding search TabControl normally owns the arrows. In Gallery the active stage owns them.
        if (bindings.Matches(e, "gallery.previous")) Browse(-1);
        else if (bindings.Matches(e, "gallery.next")) Browse(1);
        // Escape is fixed navigation in the shell and is kept out of the editable shortcuts.
        else if (plain && e.Key == Key.Escape || bindings.Matches(e, "gallery.close")) Close?.Invoke();
        else if (CommandBindings.Matches(e, "window.fullscreen")) Fullscreen?.Invoke();
        else if (CommandBindings.Matches(e, "media.export-original")) DownloadOriginal();
        else if (plain && e.Key is Key.Up or Key.Down) Browse(e.Key == Key.Up ? -1 : 1);
        else if (plain && e.Key is Key.Home or Key.End)
        {
            MoveToBoundary?.Invoke(e.Key == Key.End);
            stage.Focus(NavigationMethod.Directional);
        }
        else if (plain && e.Key is Key.Space or Key.Enter && IsPictureActivation(e, stage))
            ToggleSelection?.Invoke();
        else return;
        e.Handled = true;

        void Browse(int delta)
        {
            Move?.Invoke(delta);
            if (!stage.IsKeyboardFocusWithin) stage.Focus(NavigationMethod.Directional);
        }
    }

    private static bool IsPictureActivation(KeyEventArgs e, Control stage) =>
        e.Source is Window window && GalleryViewer.IsUnfocusedWindowKey(e, window, window.FocusManager?.GetFocusedElement())
        || e.Source is Visual origin && origin.GetSelfAndVisualAncestors().Contains(stage)
            && !ItemActivation.IsOwnedByInteractivePart(origin, stage);
}
