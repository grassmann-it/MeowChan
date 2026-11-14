using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// "Show full history…": a modal list of every remembered thread, newest first. A click (or Enter) on a row returns its place; the
/// caller opens it. Every row carries the icon of the one "continue reading" command. The title stands in the title bar only, and a
/// lone Close button is left out: × and Escape close the window (the dialog rules).
/// </summary>
internal static class ReadingHistoryWindow
{
    internal const string ListName = "ReadingHistoryList";

    /// <summary>Shows the window over <paramref name="owner"/>; null when it closes without a choice.</summary>
    internal static async Task<LocalReadingPosition?> ShowAsync(Window owner, IReadOnlyList<LocalReadingHistoryEntry> entries, DateTimeOffset now)
    {
        Window dialog = DialogWindow.Create(Texts.T("resume.history.menu"), DialogSize.M, modal: true);
        dialog.Name = "ReadingHistoryWindow";
        LocalReadingPosition? chosen = null;
        void Choose(LocalReadingPosition place) { chosen = place; dialog.Close(); }
        ListBox list = new() { Name = ListName, ItemsSource = entries, SelectionMode = SelectionMode.Single };
        AutomationProperties.SetName(list, Texts.T("resume.history.menu"));
        list.ItemTemplate = new FuncDataTemplate<LocalReadingHistoryEntry>((entry, _) =>
        {
            if (entry == null) return new TextBlock();
            (string title, string detail) = ReadingHistoryMenu.Row(entry, now);
            TextBlock titleText = new() { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            titleText.Classes.Add("section-title");
            TextBlock detailText = new() { Text = detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            detailText.Classes.Add("workspace-meta");
            Control icon = CommandBindings.Icon(ReadingHistoryMenu.ContinueCommandId);
            icon.VerticalAlignment = VerticalAlignment.Center;
            StackPanel lines = new() { Spacing = 2, Children = { titleText, detailText } };
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(4, 6), Children = { icon, lines } };
            AutomationProperties.SetName(row, title + ", " + detail);
            ToolTip.SetTip(row, ReadingHistoryMenu.EntryTip(entry.Position));
            return row;
        });
        // A click on a row opens it; the arrow keys only move, Enter opens the selected row.
        list.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is LocalReadingHistoryEntry entry) Choose(entry.Position);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        list.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return) || list.SelectedItem is not LocalReadingHistoryEntry entry) return;
            e.Handled = true;
            Choose(entry.Position);
        };
        TextBlock hint = new() { Text = Texts.T("resume.history.dialogHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        hint.Classes.Add("workspace-help");
        Grid layout = DialogFrame.Build(hint, list, footer: null, fillBody: true);
        DialogWindow.Frame(dialog, layout, modal: true);
        DialogEscape.Attach(dialog);
        dialog.Opened += (_, _) =>
        {
            if (entries.Count > 0) list.SelectedIndex = 0;
            list.Focus();
        };
        await dialog.ShowDialog(owner);
        return chosen;
    }
}
