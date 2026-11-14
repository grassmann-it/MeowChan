using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

/// <summary>
/// The header tagline: "Open-source imageboard downloader — <i>quip</i>", where the quip rotates (<see cref="TaglinePicker"/>, setting "Header
/// tagline") and the text is never cut off. The star column of the header (<c>WorkspaceTaglineHost</c>) reports its width only when it really
/// changes; the whole sentence, the short form "— quip" or nothing is chosen from cached text widths
/// (<see cref="HeaderTaglineFit"/>, <see cref="HeaderTaglineMeasurer"/>). Nothing here runs in a layout pass.
/// </summary>
public partial class MainWindow
{
    private const string TaglineSeparator = " — ";

    private readonly HeaderTaglineMeasurer _taglineMeasurer = new();
    private IReadOnlyList<TaglineEntry> _taglinePool = [];
    private TaglineEntry? _tagline;
    private string _appliedTaglineMode = string.Empty, _appliedTaglineFixedId = string.Empty;
    private bool _taglineStarted;
    private MenuItem? _taglineAnotherItem, _taglineHideItem, _taglineSettingsItem;

    /// <summary>The form of the tagline currently shown (None while it is hidden or switched off); read by the smokes.</summary>
    internal HeaderTaglineStage TaglineStage { get; private set; } = HeaderTaglineStage.None;

    private static readonly Version? ApplicationVersion = TaglineCatalog.ParseVersion(typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3));

    private void StartHeaderTagline()
    {
        if (_taglineStarted) return;
        _taglineStarted = true;
        WorkspaceTaglineHost.SizeChanged += OnTaglineHostSizeChanged;
        BuildTaglineMenu();
        ChooseHeaderTagline(atStart: true);
        UpdateHeaderTagline();
    }

    private void StopHeaderTagline()
    {
        if (!_taglineStarted) return;
        WorkspaceTaglineHost.SizeChanged -= OnTaglineHostSizeChanged;
        _taglineStarted = false;
    }

    // SizeChanged fires only for a real change of the column, so the text is measured (or served from the cache) once per resize step.
    private void OnTaglineHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateHeaderTagline();
    }

    /// <summary>Applies a changed "Header tagline" setting (called after settings are applied); an unchanged setting keeps the quip on screen.</summary>
    private void ApplyHeaderTaglineSetting()
    {
        if (!_taglineStarted) return;
        if (_config.HeaderTagline == _appliedTaglineMode && (_config.HeaderTagline != TaglinePicker.ModeFixed || _config.HeaderTaglineFixedId == _appliedTaglineFixedId)) return;
        ChooseHeaderTagline(atStart: false);
        UpdateHeaderTagline();
    }

    /// <summary>A language change: the same quip in the new language when its pack has it, else a new pick; also the menu texts.</summary>
    private void LocalizeHeaderTagline()
    {
        if (!_taglineStarted) return;
        if (_config.HeaderTagline != TaglinePicker.ModeOff)
        {
            _taglinePool = TaglineCatalog.Available();
            TaglineEntry? same = _tagline == null ? null : _taglinePool.FirstOrDefault(entry => entry.Id == _tagline.Id);
            if (same != null) _tagline = same;
            else if (_tagline != null) ChooseHeaderTagline(atStart: false);
        }
        LocalizeTaglineMenu();
        UpdateHeaderTagline();
    }

    private void ChooseHeaderTagline(bool atStart)
    {
        string mode = _config.HeaderTagline;
        _appliedTaglineMode = mode;
        _appliedTaglineFixedId = _config.HeaderTaglineFixedId;
        // Off costs nothing: no pool, no measurement.
        _taglinePool = mode == TaglinePicker.ModeOff ? [] : TaglineCatalog.Available();
        TaglinePick pick = TaglinePicker.Pick(mode, _config.HeaderTaglineFixedId, _taglinePool, DateOnly.FromDateTime(DateTime.Now),
            ApplicationVersion, TaglineCatalog.ParseVersion(_config.LastTaglineVersion), atStart, Random.Shared);
        _tagline = pick.Entry;
        // The release quip is shown once: from now on this version counts as seen (Off does not use up the chance).
        if (atStart && mode != TaglinePicker.ModeOff && ApplicationVersion is { } current && _config.LastTaglineVersion != current.ToString(3))
            RememberTaglineVersion(current.ToString(3));
    }

    private async void RememberTaglineVersion(string version)
    {
        _config.LastTaglineVersion = version;
        try { await _session.SaveViewPreferencesAsync(config => config.LastTaglineVersion = version); }
        catch (Exception error) { Log.Warning(error, "Could not save the last seen tagline version."); }
    }

    /// <summary>Chooses the form from the column width and the cached text widths and shows it. Never measures while the tagline is off or the column has no width yet.</summary>
    private void UpdateHeaderTagline()
    {
        if (!_taglineStarted) return;
        HeaderTaglineStage stage = HeaderTaglineStage.None;
        string full = string.Empty, quipForm = string.Empty;
        if (_tagline != null)
        {
            full = Texts.T("shell.tagline.lead") + TaglineSeparator + _tagline.Text;
            quipForm = "— " + _tagline.Text;
            double available = WorkspaceTaglineHost.Bounds.Width;
            if (available > 0)
                stage = HeaderTaglineFit.Choose(available, _taglineMeasurer.Width(full, WorkspaceSubtitle), _taglineMeasurer.Width(quipForm, WorkspaceSubtitle));
        }
        TaglineStage = stage;
        if (stage == HeaderTaglineStage.None)
        {
            if (WorkspaceSubtitle.IsVisible) WorkspaceSubtitle.IsVisible = false;
            ToolTip.SetTip(WorkspaceSubtitle, null);
            return;
        }
        string shown = stage == HeaderTaglineStage.Full ? full : quipForm;
        if (WorkspaceSubtitle.Text != shown) WorkspaceSubtitle.Text = shown;
        AutomationProperties.SetName(WorkspaceSubtitle, shown);
        // The whole sentence is on screen in the Full form; the short form carries it as a tooltip.
        ToolTip.SetTip(WorkspaceSubtitle, stage == HeaderTaglineStage.Quip ? full : null);
        if (!WorkspaceSubtitle.IsVisible) WorkspaceSubtitle.IsVisible = true;
    }

    private void BuildTaglineMenu()
    {
        _taglineAnotherItem = new MenuItem();
        _taglineAnotherItem.Click += OnTaglineAnotherClick;
        _taglineHideItem = new MenuItem();
        _taglineHideItem.Click += OnTaglineHideClick;
        _taglineSettingsItem = new MenuItem();
        _taglineSettingsItem.Click += (_, _) => NavigateWorkspace(WorkspacePage.Settings);
        WorkspaceSubtitle.ContextMenu = new ContextMenu { ItemsSource = new Control[] { _taglineAnotherItem, _taglineHideItem, new Separator(), _taglineSettingsItem } };
        LocalizeTaglineMenu();
    }

    private void LocalizeTaglineMenu()
    {
        if (_taglineAnotherItem == null || _taglineHideItem == null || _taglineSettingsItem == null) return;
        _taglineAnotherItem.Header = Texts.T("shell.tagline.menuAnother");
        _taglineAnotherItem.IsEnabled = _taglinePool.Count > 1;
        _taglineHideItem.Header = Texts.T("shell.tagline.menuHide");
        _taglineSettingsItem.Header = CommandCatalog.Label("navigation.settings");
    }

    // Only for this session: the setting and the stored quip stay as they are.
    private void OnTaglineAnotherClick(object? sender, RoutedEventArgs e)
    {
        _tagline = TaglinePicker.PickAnother(_taglinePool, _tagline?.Id, Random.Shared) ?? _tagline;
        UpdateHeaderTagline();
    }

    private async void OnTaglineHideClick(object? sender, RoutedEventArgs e)
    {
        string previous = _config.HeaderTagline;
        _config.HeaderTagline = TaglinePicker.ModeOff;
        ApplyHeaderTaglineSetting();
        try { await _session.SaveViewPreferencesAsync(config => config.HeaderTagline = TaglinePicker.ModeOff); }
        catch (Exception error)
        {
            _config.HeaderTagline = previous;
            ApplyHeaderTaglineSetting();
            Log.Warning(error, "Could not save hiding the header tagline.");
        }
    }
}
