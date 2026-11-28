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

    internal BooruContentDialog(BooruContentProfileSnapshot snapshot, Func<BooruContentSettings, long, Task> save)
    {
        Title = Texts.T("booru.content.title");
        DialogWindow.Configure(this, DialogSize.M, modal: true, heightToContent: true);
        BooruPostClaimRating[] ratings =
            [BooruPostClaimRating.General, BooruPostClaimRating.Sensitive, BooruPostClaimRating.Questionable, BooruPostClaimRating.Explicit];
        ComboBox maximum = new()
        {
            ItemsSource = new[] { Texts.T("booru.content.general"), Texts.T("booru.content.sensitive"),
                Texts.T("booru.content.questionable"), Texts.T("booru.content.explicit") },
            SelectedIndex = Array.IndexOf(ratings, snapshot.Settings.MaximumRating ?? BooruPostClaimRating.Sensitive)
        };
        AutomationProperties.SetAutomationId(maximum, "BooruContentMaximum");
        AutomationProperties.SetName(maximum, Texts.T("booru.content.prompt"));
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
        long revision = snapshot.Revision;
        bool readOnly = snapshot.State is not (BooruContentProfileState.Missing or BooruContentProfileState.Ready);
        if (readOnly)
        {
            // An unreadable or incompatible document can never be saved: no dead "save" action, only the message and a way out.
            maximum.IsEnabled = blacklist.IsEnabled = false;
            confirm.IsVisible = false;
            cancel.Classes.Add("primary");
            SetStatus(Texts.T("booru.content.saveError"));
        }
        cancel.Click += (_, _) => Close(false);
        confirm.Click += async (_, _) =>
        {
            if (_busy || maximum.SelectedIndex < 0) return;
            _busy = true;
            maximum.IsEnabled = blacklist.IsEnabled = confirm.IsEnabled = cancel.IsEnabled = false;
            bool saved = false;
            try
            {
                await save(snapshot.Settings with { MaximumRating = ratings[maximum.SelectedIndex], GlobalBlacklist = blacklist.Text ?? "" },
                    revision);
                saved = true;
            }
            catch (BooruContentRuleException error)
            {
                SetStatus(Texts.T("booru.content.invalidRule", error.Rule.Line));
            }
            catch (BooruContentRevisionException error)
            {
                // The document changed on disk: the next explicit confirmation saves the shown values on top of it.
                revision = error.CurrentRevision;
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
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
            Children = { maximum, new TextBlock { Text = Texts.T("booru.content.blacklist") }, blacklist }
        };
        DialogWindow.Frame(this, DialogFrame.Build(new TextBlock { Text = Texts.T("booru.content.prompt"), TextWrapping = TextWrapping.Wrap },
            body, DialogFooter.Build(confirm, new Control[] { cancel }), DialogFrame.ErrorStrip(status)), modal: true);
    }
}
