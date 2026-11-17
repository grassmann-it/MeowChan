using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// The stamp set editor of the tagging page (PT2 step 3, PT5's stamp). wave3: it edits the window's one stamp (Stamp, saved in
// Config.TagStamp by the window), the same the gallery panel and the popovers show; the stamp field of tag-queue.json stays in the
// format but unused. A name that no tag carries yet is kept and is simply not offered by a tag surface until such a tag exists.
// The size limit is the stamp size setting (TagStampSize, PT7).
public sealed partial class TaggingPage
{
    // Head and body share one line while they fit (a short stamp on a wide page); a long stamp or a narrow page puts the body below the head.
    private readonly WrapPanel _stampPanel = new() { Name = "TaggingStamp", [!WrapPanel.ItemSpacingProperty] = new DynamicResourceExtension("Workspace.Space.12"),
        [!WrapPanel.LineSpacingProperty] = new DynamicResourceExtension("Workspace.Space.12") };
    // The block's head: a quiet disclosure (turning arrow, title with the number of stamp tags) and, right after it, the help of the
    // block; the body holds the chips, the field, the add button and the hint in one wrapping row.
    private readonly ToggleButton _stampToggle = new() { Name = "TaggingStampToggle", Classes = { "disclosure" }, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stampHeading = new() { Name = "TaggingStampHeading", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("Workspace.Text.Body"),
        FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _stampArrow = new() { VerticalAlignment = VerticalAlignment.Center, Focusable = false, RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
    private readonly HelpButton _stampHelp = new() { Name = "TaggingStampHelp", HelpKey = "help.taggingStamp" };
    private readonly WrapPanel _stampBody = new() { Name = "TaggingStampBody", ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _stampChips = new() { Name = "TaggingStampChips", ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stampEmpty = new() { Name = "TaggingStampEmpty", Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = HelpPopover.MaxContentWidth }; // Workspace.Size.ReadingWidth: the one reading column
    private readonly TextBox _stampInput = new() { Name = "TaggingStampInput", Width = 240, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _stampAdd = new() { Name = "TaggingStampAdd", VerticalAlignment = VerticalAlignment.Center };
    private TaggingStampBlockState _stampBlock = TaggingStampBlockState.Auto;
    private int _stampCapacity = QuickTagOptions.DefaultStampSize;

    /// <summary>The most tags the stamp holds (the stamp size setting); clamped to its range. A lower value never cuts a saved stamp here.</summary>
    internal int StampCapacity
    {
        get => _stampCapacity;
        set => _stampCapacity = Math.Clamp(value, QuickTagOptions.MinimumStampSize, QuickTagOptions.MaximumStampSize);
    }

    private QuickTagStamp? _sharedStamp;

    /// <summary>The window's shared stamp; set by the shell. Its changes (from any surface) redraw the editor.</summary>
    internal QuickTagStamp? Stamp
    {
        get => _sharedStamp;
        set
        {
            if (_sharedStamp != null) _sharedStamp.Changed -= OnSharedStampChanged;
            _sharedStamp = value;
            _queueWork.SharedStamp = value;
            if (value != null) { value.Changed += OnSharedStampChanged; _stampCapacity = value.Capacity; }
        }
    }

    private void OnSharedStampChanged() { if (!_queueWork.Ended) RenderStamp(); }

    internal WrapPanel StampChips => _stampChips;
    internal TextBox StampInput => _stampInput;

    private Control BuildStamp()
    {
        WorkViewElement.SetId(_stampPanel, "tagging.stamp");
        _stampToggle.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _stampArrow, _stampHeading } };
        // The help stands directly after the title it explains, not at the far edge of the page.
        Grid head = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(_stampToggle);
        Grid.SetColumn(_stampHelp, 1);
        head.Children.Add(_stampHelp);
        _stampBody.Children.Add(_stampChips);
        _stampBody.Children.Add(_stampInput);
        _stampBody.Children.Add(_stampAdd);
        _stampBody.Children.Add(_stampEmpty);
        _stampPanel.Children.Add(head);
        _stampPanel.Children.Add(_stampBody);
        return _stampPanel;
    }

    /// <summary>True while the stamp block is open.</summary>
    internal bool StampOpen => _stampBody.IsVisible;
    internal ToggleButton StampToggle => _stampToggle;
    internal HelpButton StampHelp => _stampHelp;
    internal TextBlock StampEmptyText => _stampEmpty;
    internal TaggingStampBlockState StampBlockState => _stampBlock;
    /// <summary>The person chose the block open or closed: the shell saves it for the profile.</summary>
    internal Action<TaggingStampBlockState>? SaveStampBlock { get; set; }

    /// <summary>Restores the saved state of the block (the shell, when the page is built).</summary>
    internal void RestoreStampBlock(TaggingStampBlockState state)
    {
        _stampBlock = state;
        ShowStampBlock();
    }

    private void ShowStampBlock()
    {
        bool open = TaggingStampBlocks.IsOpen(_stampBlock, WorkViewDensity.Compact(_workView));
        _stampBody.IsVisible = open;
        _stampToggle.IsChecked = open;
        _stampArrow.RenderTransform = open ? new RotateTransform(90) : null;
        if (_stampArrow.Content == null) _stampArrow.Content = CommandBindings.IconFromKey("chevron-right", 14);
        AutomationProperties.SetName(_stampToggle, StampTitle());
        ToolTip.SetTip(_stampToggle, Texts.T(open ? "tagging.page.stamp.collapse" : "tagging.page.stamp.expand"));
    }

    private string StampTitle() => _queueWork.ActiveGroup is { } group
        ? Texts.T("tagging.page.stamp.groupTitle", group.Name, _queueWork.StampNames.Count)
        : Texts.T("tagging.page.stamp.title", _queueWork.StampNames.Count);

    private void InitializeStamp()
    {
        _stampAdd.Click += async (_, _) => await AddStampAsync(_stampInput.Text);
        _stampToggle.Click += (_, _) =>
        {
            _stampBlock = _stampToggle.IsChecked == true ? TaggingStampBlockState.Open : TaggingStampBlockState.Closed;
            ShowStampBlock();
            SaveStampBlock?.Invoke(_stampBlock);
        };
        TagSuggestionInput.AttachPaste(_stampInput); // pasted lines stay: they become a comma list the stamp takes together
        _stampInput.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            await AddStampAsync(_stampInput.Text);
        };
    }

    private void LocalizeStamp()
    {
        _stampEmpty.Text = Texts.T("tagging.page.stamp.empty");
        _stampHelp.Refresh();
        _stampInput.PlaceholderText = Texts.T("tags.input.several");
        AutomationProperties.SetName(_stampInput, Texts.T("tagging.page.stamp.input"));
        _stampAdd.Content = Texts.T("tagging.page.stamp.add");
    }

    private void RenderStamp()
    {
        _stampChips.Children.Clear();
        IReadOnlyList<string> names = _queueWork.StampNames;
        foreach (string name in names) _stampChips.Children.Add(StampChip(name));
        _stampChips.IsVisible = names.Count > 0;
        _stampEmpty.IsVisible = names.Count == 0;
        _stampHeading.Text = StampTitle();
        ShowStampBlock();
        _stampInput.IsEnabled = CanChange;
        _stampAdd.IsEnabled = CanChange;
    }

    private Control StampChip(string name)
    {
        // Dead-affordance audit: the name is a static fact (a read-only badge); the cross beside it is what removes it. It used to be a pressable
        // pill with its hit test switched off, which looked like a button and did nothing.
        Border chip = WorkspaceChips.MarkReadOnly(WorkspaceChips.CreateBadge("TaggingStampChip", name));
        chip.VerticalAlignment = VerticalAlignment.Center;
        string removeName = Texts.T("tagging.page.stamp.remove", name);
        Button remove = new() { Content = WorkspaceIcons.Create(WorkspaceIcons.CloseSmall), IsEnabled = CanChange };
        remove.Classes.Add(WorkspaceIcons.SquareClass);
        remove.Classes.Add("subtle");
        AutomationProperties.SetName(remove, removeName);
        ToolTip.SetTip(remove, removeName);
        remove.Click += async (_, _) => await RemoveStampAsync(name);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { chip, remove } };
    }

    /// <summary>Appends a name to the stamp (refused when invalid, already there or the stamp is full; the status line says why).</summary>
    internal async Task AddStampAsync(string? text)
    {
        Task<bool> added = _queueWork.AddStampAsync(text, _sharedStamp?.Capacity ?? _stampCapacity);
        UpdateActions();
        bool done = await added;
        if (_queueWork.Ended) return;
        Show();
        ShowStatus(_queueWork.Status);
        if (!done) return;
        _stampInput.Text = string.Empty;
        _stampInput.Focus();
    }

    internal async Task RemoveStampAsync(string name)
    {
        bool done = await _queueWork.RemoveStampAsync(name);
        if (_queueWork.Ended) return;
        Show();
        ShowStatus(_queueWork.Status);
        if (done && _stampChips.Children.Count == 0) _stampInput.Focus();
    }
}
