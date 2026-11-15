using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The reader's settings popover (R7, RL1): the gear "Settings" of the reader toolbar opens one popover with the view switches of this
// session (the former View menu), the display options readers reach for most often, and "All reader settings" at the bottom. The fields
// are those of ReaderQuickSettings (the same settings as the settings page); the shell supplies how a change is saved
// (QuickSettingsHost), this page supplies the session rows (QuickSettingsSession). The popover exists from the first use on.
public partial class ArchivePage
{
    private QuickSettingsPopover? _readerQuickSettings;
    private QuickSettingsSession? _readerSettingsSession;
    private readonly ReaderSettingsDot _readerSettingsDot = new();

    /// <summary>How the popover reads and saves the settings; set by the shell. Without it the gear does nothing.</summary>
    internal QuickSettingsHost? QuickSettingsHost { get; set; }

    internal bool IsQuickSettingsOpen => _readerQuickSettings?.IsOpen == true;
    internal QuickSettingsPopover? ReaderQuickSettings => _readerQuickSettings;
    /// <summary>The session rows the popover shows (what a bare page or the post filters being absent leaves out is not in the list).</summary>
    internal QuickSettingsSession ReaderSettingsSession => _readerSettingsSession ??= new QuickSettingsSession { Items = ReaderSettingsSessionItems };
    /// <summary>The gear is marked: a switch of the session's view is on (smoke and test seam).</summary>
    internal bool IsReaderSettingsMarked => _readerSettingsDot.Active;

    /// <summary>Opens (or, when open, closes) the reader's settings under the gear; under "More" when the gear is folded away.</summary>
    internal void OpenReaderQuickSettings()
    {
        if (_ended || IsReaderDialogOpen || QuickSettingsHost is not { } host) return;
        if (_readerQuickSettings is { IsOpen: true } open) { open.Hide(); return; }
        _readerQuickSettings ??= new QuickSettingsPopover(QuickSettingsSurfaces.Get(QuickSettingsSurfaces.Reader), host, "ReaderQuickSettings", ReaderSettingsSession);
        _readerQuickSettings.Show(QuickSettingsAnchor());
    }

    private void OnReaderSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenReaderQuickSettings();

    // The gear, or what stands in for it when the toolbar folded it into "More" (its parent is then the closed overflow).
    private Avalonia.Controls.Control QuickSettingsAnchor() =>
        ReaderSettingsOpen.IsEffectivelyVisible ? ReaderSettingsOpen
        : ReaderToolbar.More is { IsEffectivelyVisible: true } more ? more
        : ReaderToolbar;

    private void CloseQuickSettings()
    {
        _readerQuickSettings?.Dispose();
        _readerQuickSettings = null;
    }

    // The former View menu's entries as the popover's session rows: the same options (and the same shortcuts), enabled by the same rule.
    private System.Collections.Generic.IReadOnlyList<QuickSettingsSessionItem> ReaderSettingsSessionItems() =>
        OrderedViewOptions().Select(option => new QuickSettingsSessionItem(option.CommandId, option.IsChecked,
            () => { if (!_ended) option.Toggle(); },
            () => !_ended && !IsFilenameSearchOpen && (option.IsEnabled?.Invoke() ?? true),
            option.Note)).ToArray();

    /// <summary>The popover and the gear read the session's state again (a switch changed, a thread was opened).</summary>
    private void RefreshReaderSettings()
    {
        if (_ended) return;
        _readerSettingsSession?.RaiseChanged();
        UpdateReaderSettingsButton();
    }

    // The gear carries a dot while a view filter of the session is on, so an active filter stays visible with the popover closed (R6).
    private void UpdateReaderSettingsButton()
    {
        bool marked = !_readerView.IsDefault;
        string label = Texts.T("reader.settings.button");
        string tip = Texts.T("quicksettings.reader.title");
        if (marked) tip += Environment.NewLine + Texts.T("reader.settings.active");
        Button button = ReaderSettingsOpen;
        if (!Equals(button.Content, label)) button.Content = label;
        if (!Equals(ToolTip.GetTip(button), tip)) ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, marked ? label + ", " + Texts.T("reader.settings.active") : label);
        Avalonia.Automation.AutomationProperties.SetHelpText(button, tip);
        _readerSettingsDot.Active = marked;
    }

    // The gear's content: the catalog icon with the accent dot over its corner, then the text. The template keeps the label a plain
    // string (what sets or reads Content is unchanged), as the other toolbar buttons do.
    private void InitializeReaderSettingsButton()
    {
        Button button = ReaderSettingsOpen;
        const string icon = "settings";
        MeowChan.Desktop.Controls.IconButtons.Record(button, icon);
        button.ContentTemplate = new FuncDataTemplate<object>((content, _) =>
        {
            TextBlock text = new() { Text = content?.ToString(), VerticalAlignment = VerticalAlignment.Center };
            Ellipse dot = new()
            {
                Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = new TranslateTransform(4, -2), IsHitTestVisible = false, // over the gear's corner without a negative margin Name = "ReaderSettingsDot"
            };
            dot[!Shape.FillProperty] = new DynamicResourceExtension("Workspace.Accent");
            dot.Bind(Visual.IsVisibleProperty, new Binding(nameof(ReaderSettingsDot.Active)) { Source = _readerSettingsDot });
            Grid mark = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            mark.Children.Add(CommandBindings.ButtonIcon(icon, 16, text, byKey: true));
            mark.Children.Add(dot);
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
                Children = { mark, text }
            };
        });
    }

    /// <summary>The bindable state of the gear's dot.</summary>
    internal sealed class ReaderSettingsDot : INotifyPropertyChanged
    {
        private bool _active;
        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
