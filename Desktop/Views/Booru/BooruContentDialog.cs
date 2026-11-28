using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed class BooruContentDialog : Window
{
    private bool _busy;

    /// <summary>Edits the shown document. <paramref name="save"/> receives only the changed fields; the store merges them.</summary>
    internal BooruContentDialog(BooruContentProfileSnapshot snapshot, Func<BooruContentEdit, Task> save)
    {
        Title = Texts.T("booru.content.title");
        DialogWindow.Configure(this, DialogSize.M, modal: true, heightToContent: true);
        bool readOnly = snapshot.State is BooruContentProfileState.Unreadable or BooruContentProfileState.Unsupported;
        TextBlock header = new()
        {
            Text = readOnly ? Texts.T("booru.content.readError", BooruContentProfileStore.FileName) : Texts.T("booru.content.prompt"),
            TextWrapping = TextWrapping.Wrap
        };
        BooruPostClaimRating[] ratings =
            [BooruPostClaimRating.General, BooruPostClaimRating.Sensitive, BooruPostClaimRating.Questionable, BooruPostClaimRating.Explicit];
        int MaximumIndex(BooruContentProfileSnapshot rules) =>
            Array.IndexOf(ratings, rules.Settings.MaximumRating ?? BooruPostClaimRating.Sensitive);
        ComboBox maximum = new()
        {
            ItemsSource = new[] { Texts.T("booru.content.general"), Texts.T("booru.content.sensitive"),
                Texts.T("booru.content.questionable"), Texts.T("booru.content.explicit") },
            SelectedIndex = MaximumIndex(snapshot)
        };
        AutomationProperties.SetAutomationId(maximum, "BooruContentMaximum");
        AutomationProperties.SetLabeledBy(maximum, header);
        TextBox blacklist = new()
        {
            Text = snapshot.Settings.GlobalBlacklist,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80
        };
        AutomationProperties.SetAutomationId(blacklist, "BooruContentBlacklist");
        AutomationProperties.SetName(blacklist, Texts.T("booru.content.blacklist"));
        Button cancel = new() { Content = Texts.T("booru.ui.cancel"), IsCancel = true };
        // No IsDefault: Enter in the multi-line blacklist or on the pre-selected rating must not confirm unnoticed.
        Button confirm = new() { Content = Texts.T("booru.content.save") };
        confirm.Classes.Add("primary");
        AutomationProperties.SetAutomationId(cancel, "BooruContentCancel");
        AutomationProperties.SetAutomationId(confirm, "BooruContentConfirm");
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        status.Classes.Add("workspace-error");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
        void SetStatus(string text) { status.Text = text; status.IsVisible = !string.IsNullOrEmpty(text); }
        if (readOnly)
        {
            maximum.IsEnabled = blacklist.IsEnabled = false;
            confirm.IsVisible = false;
            cancel.Classes.Add("primary");
        }
        cancel.Click += (_, _) => Close(false);
        confirm.Click += async (_, _) =>
        {
            if (readOnly || _busy || maximum.SelectedIndex < 0) return;
            _busy = true;
            maximum.IsEnabled = blacklist.IsEnabled = confirm.IsEnabled = cancel.IsEnabled = false;
            bool saved = false;
            try
            {
                BooruPostClaimRating rating = ratings[maximum.SelectedIndex];
                string text = blacklist.Text ?? "";
                // A first choice always writes the rating; every untouched field keeps the value of the document at save time.
                await save(new BooruContentEdit(snapshot,
                    rating == snapshot.Settings.MaximumRating ? null : rating,
                    string.Equals(text, snapshot.Settings.GlobalBlacklist, StringComparison.Ordinal) ? null : text));
                saved = true;
            }
            catch (BooruContentRuleException error)
            {
                SetStatus(Texts.T("booru.content.invalidRule", error.Rule.Line));
            }
            catch (BooruContentRevisionException error)
            {
                // Keep local edits and refresh the untouched inputs before retrying against the current revision.
                if (maximum.SelectedIndex == MaximumIndex(snapshot))
                    maximum.SelectedIndex = MaximumIndex(error.Current);
                if (string.Equals(blacklist.Text ?? "", snapshot.Settings.GlobalBlacklist, StringComparison.Ordinal))
                    blacklist.Text = error.Current.Settings.GlobalBlacklist;
                snapshot = error.Current;
                SetStatus(Texts.T("booru.content.saveError"));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Serilog.Log.Warning(error, "Could not save the Booru content rules");
                SetStatus(Texts.T("booru.content.saveError"));
            }
            finally
            {
                _busy = false;
                maximum.IsEnabled = blacklist.IsEnabled = confirm.IsEnabled = cancel.IsEnabled = true;
            }
            if (saved) Close(true);
        };
        Closing += (_, e) => e.Cancel = _busy;
        DialogEscape.Attach(this, isBusy: () => _busy, close: () => Close(false));
        Opened += (_, _) => { if (readOnly) cancel.Focus(); else maximum.Focus(); };
        StackPanel body = new()
        {
            IsVisible = !readOnly,
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
            Children = { maximum, new TextBlock { Text = Texts.T("booru.content.blacklist") }, blacklist }
        };
        DialogWindow.Frame(this, DialogFrame.Build(header,
            body, DialogFooter.Build(confirm, new Control[] { cancel }), DialogFrame.ErrorStrip(status)), modal: true);
    }
}
