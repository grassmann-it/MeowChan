using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly StackPanel _stampPanel = new() { Name = "TaggingStamp", Spacing = 8 };
    private readonly TextBlock _stampHeading = new() { Classes = { "section-title" } };
    private readonly WrapPanel _stampChips = new() { Name = "TaggingStampChips", ItemSpacing = 8, LineSpacing = 8 };
    private readonly TextBlock _stampEmpty = new() { Name = "TaggingStampEmpty", Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _stampInput = new() { Name = "TaggingStampInput", MinWidth = 200, MaxWidth = 320 };
    private readonly Button _stampAdd = new() { Name = "TaggingStampAdd" };
    private readonly TextBlock _stampHint = new() { Name = "TaggingStampHint", Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
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
        Grid input = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8 };
        input.Children.Add(_stampInput);
        Grid.SetColumn(_stampAdd, 1);
        input.Children.Add(_stampAdd);
        Grid.SetColumn(_stampHint, 2);
        input.Children.Add(_stampHint);
        _stampPanel.Children.Add(_stampHeading);
        _stampPanel.Children.Add(_stampChips);
        _stampPanel.Children.Add(_stampEmpty);
        _stampPanel.Children.Add(input);
        return _stampPanel;
    }

    private void InitializeStamp()
    {
        _stampAdd.Click += async (_, _) => await AddStampAsync(_stampInput.Text);
        _stampInput.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            await AddStampAsync(_stampInput.Text);
        };
    }

    private void LocalizeStamp()
    {
        _stampHeading.Text = Texts.T("tagging.page.stamp");
        _stampEmpty.Text = Texts.T("tagging.page.stamp.empty");
        _stampInput.PlaceholderText = Texts.T("tagging.page.stamp.input");
        AutomationProperties.SetName(_stampInput, Texts.T("tagging.page.stamp.input"));
        _stampAdd.Content = Texts.T("tagging.page.stamp.add");
        _stampHint.Text = Texts.T("tagging.page.stamp.hint", Shortcut("tags.quick.stamp-apply"));
    }

    private void RenderStamp()
    {
        _stampChips.Children.Clear();
        IReadOnlyList<string> names = _queueWork.StampNames;
        foreach (string name in names) _stampChips.Children.Add(StampChip(name));
        _stampChips.IsVisible = names.Count > 0;
        _stampEmpty.IsVisible = names.Count == 0;
        _stampInput.IsEnabled = CanChange;
        _stampAdd.IsEnabled = CanChange;
    }

    private Control StampChip(string name)
    {
        Button chip = new() { Content = new TextBlock { Text = name, Classes = { WorkspaceChips.Label } }, IsHitTestVisible = false, Focusable = false };
        WorkspaceChips.MarkOwn(chip);
        AutomationProperties.SetName(chip, name);
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
