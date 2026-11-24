using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Window fullscreen hides the footer and title bar. Media focus mode also hides
// the footer and can opt into the same floating title bar.
public partial class MainWindow
{
    private const double StatusStripHeight = 26, NavigationHeight = 52;

    // What the media page last reported; the shell does not read the page's own state.
    private bool _mediaFocusActive;
    private bool _mediaFocusFooterHidden, _mediaFocusHeaderHidden, _titleBarRevealed;
    private Control? _titleBarReturnFocus;
    private MediaFocusChromeTextBinding? _mediaFocusChromeTexts;
    private bool TitleBarFloats => _mediaFocusHeaderHidden || WindowState == WindowState.FullScreen;

    /// <summary>The footer (status strip and navigation) is hidden by the media focus mode (smokes).</summary>
    internal bool MediaFocusFooterHidden => _mediaFocusFooterHidden;
    /// <summary>The title bar is hidden by the media focus mode and floats over the page when shown (smokes).</summary>
    internal bool MediaFocusHeaderHidden => _mediaFocusHeaderHidden;
    /// <summary>The hover strip has shown the floating title bar (smokes).</summary>
    internal bool MediaFocusHeaderRevealed => _mediaFocusHeaderHidden && _titleBarRevealed;

    private void InitializeMediaFocusChrome()
    {
        Border strip = MediaFocusHeaderStrip;
        _mediaFocusChromeTexts?.Dispose();
        _mediaFocusChromeTexts = new MediaFocusChromeTextBinding(strip);
        strip.PointerEntered += OnTitleBarStripPointer;
        strip.PointerMoved += OnTitleBarStripPointer;
        // Observe the stable window, not the reveal strip which hides during reveal.
        TransientHover.Attach(this, over =>
        {
            if (over) return;
            HideRevealedTitleBar();
        });
        // The floating title bar goes away when the pointer leaves it, the keyboard focus has left it and no menu of it is open.
        WorkspaceTitleBarFrame.PointerExited += (_, _) => HideRevealedTitleBar();
        PointerMoved += (_, e) =>
        {
            // A fast move can go straight from the strip to the page without entering the revealed frame.
            if (_titleBarRevealed && e.GetPosition(WorkspaceRoot).Y >= WorkspaceMetrics.TitleBarHeight)
                HideRevealedTitleBar();
        };
        // Wait for focus to reach its next target. Tabbing within the title bar also raises LostFocus.
        WorkspaceTitleBarFrame.LostFocus += (_, _) => Dispatcher.UIThread.Post(HideRevealedTitleBar);
        WorkspaceMenuItem.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.MenuItem.IsSubMenuOpenProperty) HideRevealedTitleBar();
        };
    }

    private void OnTitleBarStripPointer(object? sender, PointerEventArgs e)
    {
        if (TransientHover.WindowUsable(MediaFocusHeaderStrip)) RevealTitleBar();
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

    /// <summary>Applies the media focus rules for the selected page.</summary>
    private void UpdateMediaFocusChrome()
    {
        if (!IsInitialized) return;
        bool hide = _mediaFocusActive && !_isClosing && _selectedPage == WorkspacePage.Media && !_largeReaderOpen && _gallery == null;
        _mediaFocusFooterHidden = hide;
        _mediaFocusHeaderHidden = hide && _config.MediaFocusHidesHeader;
        ApplyShellChromePresentation();
    }

    private void ApplyShellChromePresentation()
    {
        bool hideFooter = _mediaFocusFooterHidden || WindowState == WindowState.FullScreen;
        bool moveFooterFocus = hideFooter && (WorkspaceStatusBar.IsKeyboardFocusWithin || WorkspaceNavigation.IsKeyboardFocusWithin);
        if (hideFooter && WorkspaceNavigation.IsVisible)
        {
            bool popupOpen = CloseFooterDisplayMenus() || WorkspaceModeComboBox.IsDropDownOpen || ToolsNavigationButton.Flyout?.IsOpen == true;
            WorkspaceModeComboBox.IsDropDownOpen = false;
            ToolsNavigationButton.Flyout?.Hide();
            // Closing the footer popup can return focus to its anchor or clear it.
            moveFooterFocus |= WorkspaceNavigation.IsKeyboardFocusWithin || popupOpen && FocusManager?.GetFocusedElement() == null;
        }
        WorkspaceNavigation.IsVisible = !hideFooter;
        UpdateFooterStatusStrip();
        WorkspaceRoot.RowDefinitions[3].Height = new GridLength(hideFooter ? 0 : NavigationHeight);
        ApplyTitleBarPresentation();
        if (moveFooterFocus) FocusWorkspaceContent();
    }

    // The one place that decides how the title bar sits: in its own row (normal), or floating over the page and shown by the strip.
    private void ApplyTitleBarPresentation()
    {
        bool floating = TitleBarFloats;
        Border frame = WorkspaceTitleBarFrame;
        bool visible = !floating || _titleBarRevealed;
        bool returnFocus = frame.IsKeyboardFocusWithin && (!visible || !floating && _titleBarRevealed);
        if (!floating) _titleBarRevealed = false;
        WorkspaceRoot.RowDefinitions[0].Height = floating ? new GridLength(0) : WorkspaceMetrics.TitleBarRow;
        Grid.SetRowSpan(frame, floating ? 2 : 1);
        frame.VerticalAlignment = floating ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        frame.Height = floating ? WorkspaceMetrics.TitleBarHeight : double.NaN;
        frame.ZIndex = floating ? 120 : 0;
        if (returnFocus) RestoreTitleBarReturnFocus();
        if (!_titleBarRevealed) _titleBarReturnFocus = null;
        frame.IsVisible = frame.IsHitTestVisible = visible;
        MediaFocusHeaderStrip.IsVisible = !visible;
    }

    private void RevealTitleBar()
    {
        if (!TitleBarFloats || _titleBarRevealed) return;
        _titleBarReturnFocus = FocusManager?.GetFocusedElement() as Control;
        _titleBarRevealed = true;
        ApplyTitleBarPresentation();
    }

    private void HideRevealedTitleBar()
    {
        if (!_titleBarRevealed || WorkspaceTitleBarFrame.IsKeyboardFocusWithin || WorkspaceMenuItem.IsSubMenuOpen) return;
        if (IsActive && IsVisible && WindowState != WindowState.Minimized && WorkspaceTitleBarFrame.IsPointerOver) return;
        _titleBarRevealed = false;
        ApplyTitleBarPresentation();
    }

    private bool ToggleTitleBarFocus()
    {
        if (!TitleBarFloats) return false;
        if (WorkspaceTitleBarFrame.IsKeyboardFocusWithin)
        {
            RestoreTitleBarReturnFocus();
            HideRevealedTitleBar();
        }
        else
        {
            RevealTitleBar();
            _titleBarReturnFocus = FocusManager?.GetFocusedElement() as Control;
            BrandAboutButton.Focus(NavigationMethod.Tab);
        }
        return true;
    }

    private void RestoreTitleBarReturnFocus()
    {
        if (_titleBarReturnFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } origin
            && TopLevel.GetTopLevel(origin) == this && origin.Focus(NavigationMethod.Tab)) return;
        FocusWorkspaceContent();
    }

    private void FocusWorkspaceContent()
    {
        bool galleryActive = _gallery != null && !_gallerySuspended;
        Control? content = galleryActive ? _gallery
            : _largeReaderOpen || _selectedPage == WorkspacePage.Archive
                ? _archivePage?.FindControl<ListBox>("ArchivePosts") : null;
        if (content?.Focus(NavigationMethod.Tab) == true) return;
        Control scope = galleryActive ? GalleryHost : _largeReaderOpen ? ReaderHost : WorkspacePages;
        // A fallback outside text input keeps the shell's page shortcuts available.
        Control? first = scope.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
            control is { Focusable: true, IsTabStop: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
            && !PointerFocusReclaim.IsTextInput(control));
        first?.Focus(NavigationMethod.Tab);
    }
}
