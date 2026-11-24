using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private async Task OpenWatcherWizardAsync(IReadOnlyList<BooruTag> tags, Func<bool> current)
    {
        Window? owner = TopLevel.GetTopLevel(_hostedGallery ?? (Control)this) as Window;
        if (_resultViewer is { IsActive: true } viewer) owner = viewer;
        if (owner == null || !current()) return;
        IInputElement? origin = owner.FocusManager?.GetFocusedElement();
        Window dialog = DialogWindow.Create(Texts.T("booru.wizard.title"), DialogSize.M, modal: true, heightToContent: true);
        dialog.Name = "BooruWatcherWizard";
        SelectableTextBlock query = new() { Name = "BooruWatcherQuery", Classes = { "tnum" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        TextBlock status = new() { Classes = { "workspace-meta" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Button create = new() { Content = Texts.T("booru.ui.watcher.create"), Classes = { "primary" } };
        Button search = new() { Content = Texts.T("booru.media.tags.useAsSearch") };
        Button cancel = new() { Content = Texts.T("action.cancel") };
        StackPanel choices = new() { Spacing = 8 };
        List<(string Name, CheckBox Chip)> terms = new();
        int preselected = 0;
        foreach (var category in tags.GroupBy(tag => tag.Category)
            .OrderBy(group => group.Key == BooruTagCategory.Artist ? 0 : group.Key == BooruTagCategory.Character ? 1 : 2))
        {
            choices.Children.Add(new TextBlock { Text = BooruText.TagCategory(category.Key), Classes = { "workspace-meta" } });
            WrapPanel chips = new() { ItemSpacing = 8, LineSpacing = 4 };
            foreach (BooruTag tag in category)
            {
                bool valid = BooruSourceTagSearch.Plan([tag.Name]).IsValid;
                bool initiallySelected = valid && category.Key is BooruTagCategory.Artist or BooruTagCategory.Character
                    && preselected < BooruSourceTagSearch.MaximumTags;
                if (initiallySelected) preselected++;
                CheckBox chip = new() { Content = new TextBlock { Text = tag.Name }, IsChecked = initiallySelected,
                    IsThreeState = true, IsEnabled = valid };
                if (!valid)
                {
                    ToolTip.SetTip(chip, Texts.T("booru.media.tags.search.unusable", tag.Name));
                    ToolTip.SetShowOnDisabled(chip, true);
                }
                chip.IsCheckedChanged += (_, _) => Refresh();
                chips.Children.Add(chip);
                terms.Add((tag.Name, chip));
            }
            choices.Children.Add(chips);
        }
        WrapPanel ratings = new() { ItemSpacing = 8, LineSpacing = 4 };
        CheckBox[] ratingOptions = new[] { "g", "s", "q", "e" }.Select(value => new CheckBox
        {
            Content = value.ToUpperInvariant(), Tag = value
        }).ToArray();
        foreach (CheckBox rating in ratingOptions)
        {
            rating.IsCheckedChanged += (_, _) => Refresh();
            ratings.Children.Add(rating);
        }
        choices.Children.Add(ratings);
        StackPanel body = new() { Spacing = 8, Margin = new Thickness(16, 0),
            Children = { new TextBlock { Text = Texts.T("booru.wizard.hint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new ScrollViewer { Content = choices, MaxHeight = 320 }, query, status } };
        DialogWindow.Frame(dialog, DialogFrame.Build(null, body, DialogFooter.Build(create, [cancel, search])), modal: true);
        DialogEscape.Attach(dialog, close: () => dialog.Close());
        cancel.Click += (_, _) => dialog.Close();
        create.Click += (_, _) => dialog.Close((Query: query.Text, Search: false));
        search.Click += (_, _) => dialog.Close((Query: query.Text, Search: true));
        dialog.Opened += (_, _) => (terms.Select(term => term.Chip).FirstOrDefault(chip => chip.IsEnabled) as Control ?? cancel).Focus();
        Refresh();
        try
        {
            var result = await dialog.ShowDialog<(string? Query, bool Search)>(owner);
            if (result.Query is not { } accepted || !current()) return;
            if (result.Search) TryShowQueryInNewTab(accepted);
            else await _model.CreateWatcherAsync(accepted, current);
        }
        finally
        {
            if (current() && origin is Control { IsEffectivelyVisible: true } control) control.Focus();
        }

        void Refresh()
        {
            string[] selected = terms.Where(term => term.Chip.IsChecked != false)
                .Select(term => term.Chip.IsChecked == true ? term.Name : "-" + term.Name).ToArray();
            string[] markedRatings = ratingOptions.Where(option => option.IsChecked == true).Select(option => (string)option.Tag!).ToArray();
            string draft = string.Join(" ", markedRatings.Length == 0 ? selected : selected.Append("rating:" + string.Join(",", markedRatings)));
            BooruParsedInput parsed = BooruInput.Parse(BooruInputMode.Tags, draft);
            query.Text = parsed.Canonical ?? draft;
            bool valid = parsed.Kind == BooruInputKind.Tags && selected.Length > 0;
            bool duplicate = valid && _model.Watchers.HasWatcherFor(parsed.Canonical!);
            string message = duplicate ? Texts.T("booru.ui.watcher.duplicate", parsed.Canonical)
                : selected.Length == 0 ? Texts.T("booru.media.tags.search.none")
                : !valid ? BooruText.Issue(parsed.Issue) : string.Empty;
            status.Text = Texts.T("booru.wizard.count", selected.Length, BooruSourceTagSearch.MaximumTags) + " " + message;
            create.IsEnabled = valid && !duplicate;
            search.IsEnabled = valid && SearchTabs.CanAdd;
        }
    }
}
