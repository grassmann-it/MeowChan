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
        ListBox list = BuildList(entries, now, place => Choose(place));
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

    /// <summary>The list of the window (a seam for tests: no window needed). <paramref name="choose"/> receives the place of the row that was clicked or entered.</summary>
    internal static ListBox BuildList(IReadOnlyList<LocalReadingHistoryEntry> entries, DateTimeOffset now, Action<LocalReadingPosition> choose)
    {
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
            // A grid, not a horizontal stack: a stack measures its children without a width limit, so a long thread title never wrapped
            // and ran past the list edge at the window's minimum width (narrow-window sweep, 16.11.2026).
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(4, 8), Children = { icon, lines } };
            Grid.SetColumn(lines, 1);
            AutomationProperties.SetName(row, title + ", " + detail);
            ToolTip.SetTip(row, ReadingHistoryMenu.EntryTip(entry.Position));
            return row;
        });
        // A click on a row opens it; the arrow keys only move.
        list.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is LocalReadingHistoryEntry entry) choose(entry.Position);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // Enter opens the row that has the focus. A focused ListBoxItem consumes Enter before a bubble handler of the list sees it (Avalonia 12), so the
        // key is taken in the tunnel phase; with the list itself focused, the selected row counts. The rows hold no interactive parts.
        list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!ItemActivation.IsPrimaryKey(e)) return;
            LocalReadingHistoryEntry? entry = ItemActivation.ItemAt<LocalReadingHistoryEntry>(e.Source, list)
                ?? (ReferenceEquals(e.Source, list) ? list.SelectedItem as LocalReadingHistoryEntry : null);
            if (entry == null) return;
            e.Handled = true;
            choose(entry.Position);
        }, RoutingStrategies.Tunnel);
        return list;
    }
}
