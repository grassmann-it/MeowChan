using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The shell's half of the media page's focus mode (M4, MediaPage.Focus.cs): while the mode is on, the Media page is the selected page and
// no gallery or large reader covers it, the window gives the grid the whole client area. The footer (status strip and page navigation) is
// always hidden then; the application header (title bar with the I/O monitor, the header buttons and the window controls) is hidden when the
// setting MediaFocusHidesHeader asks for it (default on). The header is not removed: it floats over the top of the page and is shown again
// while the pointer rests on a thin strip at the top edge (MediaFocusHeaderStrip), so the window controls stay reachable. Leaving the mode
// (Esc, "Exit focus ×", Shift+F), leaving the page, opening a gallery or switching the setting off restores every row at once. The OS window
// fullscreen (F11) and the gallery's own fullscreen are separate and untouched: the gallery's immersive title bar rule is folded in here.
public partial class MainWindow
{
    private const double StatusStripHeight = 26, NavigationHeight = 52;

    // What the media page last reported; the shell does not read the page's own state.
    private bool _mediaFocusActive;
    // Whether the chrome is currently hidden, and the title bar floats (hidden or shown by the strip).
    private bool _mediaFocusFooterHidden, _mediaFocusHeaderHidden, _mediaFocusHeaderRevealed;
    // The gallery's own fullscreen hides the title bar too; both reasons are applied together (UpdateGalleryFullscreenPresentation).
    private bool _galleryImmersive;
    private MediaFocusChromeTextBinding? _mediaFocusChromeTexts;

    /// <summary>The footer (status strip and navigation) is hidden by the media focus mode (smokes).</summary>
    internal bool MediaFocusFooterHidden => _mediaFocusFooterHidden;
    /// <summary>The title bar is hidden by the media focus mode and floats over the page when shown (smokes).</summary>
    internal bool MediaFocusHeaderHidden => _mediaFocusHeaderHidden;
    /// <summary>The hover strip has shown the floating title bar (smokes).</summary>
    internal bool MediaFocusHeaderRevealed => _mediaFocusHeaderRevealed;

    private void InitializeMediaFocusChrome()
    {
        Border strip = MediaFocusHeaderStrip;
        _mediaFocusChromeTexts?.Dispose();
        _mediaFocusChromeTexts = new MediaFocusChromeTextBinding(strip);
        strip.PointerEntered += (_, _) => { if (TransientHover.WindowUsable(strip)) RevealMediaFocusHeader(true); };
        // Observe the stable window, not the reveal strip which hides during reveal.
        TransientHover.Attach(this, over =>
        {
            if (over) return;
            if (!IsActive || !IsVisible || WindowState == WindowState.Minimized) RevealMediaFocusHeader(false);
            else HideRevealedMediaFocusHeader();
        });
        // The floating title bar goes away when the pointer leaves it, the keyboard focus has left it and no menu of it is open.
        WorkspaceTitleBarFrame.PointerExited += (_, _) => HideRevealedMediaFocusHeader();
        WorkspaceTitleBarFrame.LostFocus += (_, _) => HideRevealedMediaFocusHeader();
        WorkspaceMenuItem.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.MenuItem.IsSubMenuOpenProperty) HideRevealedMediaFocusHeader();
        };
    }

    private void DisposeMediaFocusChrome()
    {
        _mediaFocusChromeTexts?.Dispose();
        _mediaFocusChromeTexts = null;
    }

    // Texts is process-wide. Its handler must end with the shell, not with GC: retaining the strip also retains
    // its pointer handler's MainWindow, including the closed window's native menu and shortcut presentations.
    internal sealed class MediaFocusChromeTextBinding : IDisposable
    {
        private Border? _strip;

        internal MediaFocusChromeTextBinding(Border strip)
        {
            ArgumentNullException.ThrowIfNull(strip);
            _strip = strip;
            RefreshTexts(null, EventArgs.Empty);
            Texts.Changed += RefreshTexts;
        }

        private void RefreshTexts(object? sender, EventArgs e)
        {
            if (_strip is not { } strip) return;
            AutomationProperties.SetName(strip, Texts.T("media.focus.headerStrip"));
            ToolTip.SetTip(strip, Texts.T("media.focus.headerStrip"));
        }

        public void Dispose()
        {
            if (_strip == null) return;
            Texts.Changed -= RefreshTexts;
            _strip = null;
        }
    }

    /// <summary>Applies what the media focus mode, the selected page, the open gallery and the setting say about the shell's chrome.</summary>
    private void UpdateMediaFocusChrome()
    {
        if (!IsInitialized) return;
        bool hide = _mediaFocusActive && !_isClosing && _selectedPage == WorkspacePage.Media && !_largeReaderOpen && _gallery == null;
        _mediaFocusFooterHidden = hide;
        _mediaFocusHeaderHidden = hide && _config.MediaFocusHidesHeader;
        WorkspaceStatusBar.IsVisible = WorkspaceNavigation.IsVisible = !hide;
        WorkspaceRoot.RowDefinitions[2].Height = new GridLength(hide ? 0 : StatusStripHeight);
        WorkspaceRoot.RowDefinitions[3].Height = new GridLength(hide ? 0 : NavigationHeight);
        ApplyTitleBarPresentation();
    }

    // The one place that decides how the title bar sits: in its own row (normal), or floating over the page and shown by the strip.
    private void ApplyTitleBarPresentation()
    {
        bool floating = _mediaFocusHeaderHidden;
        if (!floating) _mediaFocusHeaderRevealed = false;
        Border frame = WorkspaceTitleBarFrame;
        WorkspaceRoot.RowDefinitions[0].Height = floating ? new GridLength(0) : WorkspaceMetrics.TitleBarRow;
        Grid.SetRowSpan(frame, floating ? 2 : 1);
        frame.VerticalAlignment = floating ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        frame.Height = floating ? WorkspaceMetrics.TitleBarHeight : double.NaN;
        frame.ZIndex = floating ? 120 : 0;
        frame.IsVisible = !_galleryImmersive && (!floating || _mediaFocusHeaderRevealed);
        MediaFocusHeaderStrip.IsVisible = floating && !_mediaFocusHeaderRevealed && !_galleryImmersive;
    }

    private void RevealMediaFocusHeader(bool reveal)
    {
        if (!_mediaFocusHeaderHidden || _mediaFocusHeaderRevealed == reveal) return;
        _mediaFocusHeaderRevealed = reveal;
        ApplyTitleBarPresentation();
    }

    private void HideRevealedMediaFocusHeader()
    {
        if (!_mediaFocusHeaderRevealed) return;
        if (WorkspaceTitleBarFrame.IsPointerOver || WorkspaceMenuItem.IsSubMenuOpen) return;
        bool hadFocus = WorkspaceTitleBarFrame.IsKeyboardFocusWithin;
        if (hadFocus) return; // the keyboard user is still in it; its own LostFocus ends the reveal
        RevealMediaFocusHeader(false);
    }
}
