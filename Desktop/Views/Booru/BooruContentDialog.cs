using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed class BooruContentDialog : Window
{
    internal BooruContentDialog(BooruContentProfileSnapshot snapshot, Func<BooruContentSettings, long, Task> save)
    {
        Title = Texts.T("booru.content.title");
        Width = 480;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
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
        Button confirm = new() { Content = Texts.T("booru.content.save"), IsDefault = true };
        AutomationProperties.SetAutomationId(cancel, "BooruContentCancel");
        AutomationProperties.SetAutomationId(confirm, "BooruContentConfirm");
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
        if (snapshot.State is not (BooruContentProfileState.Missing or BooruContentProfileState.Ready))
        {
            maximum.IsEnabled = blacklist.IsEnabled = confirm.IsEnabled = false;
            status.Text = Texts.T("booru.content.saveError");
        }
        cancel.Click += (_, _) => Close(false);
        confirm.Click += async (_, _) =>
        {
            if (maximum.SelectedIndex < 0) return;
            confirm.IsEnabled = cancel.IsEnabled = false;
            try
            {
                await save(snapshot.Settings with { MaximumRating = ratings[maximum.SelectedIndex], GlobalBlacklist = blacklist.Text ?? "" },
                    snapshot.Revision);
                Close(true);
            }
            catch (BooruContentRuleException error)
            {
                status.Text = Texts.T("booru.content.invalidRule", error.Rule.Line);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Serilog.Log.Warning(error, "Could not save the Booru content rules");
                status.Text = Texts.T("booru.content.saveError");
            }
            finally { confirm.IsEnabled = cancel.IsEnabled = true; }
        };
        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = Texts.T("booru.content.prompt"), TextWrapping = TextWrapping.Wrap },
                maximum,
                new TextBlock { Text = Texts.T("booru.content.blacklist") },
                blacklist,
                status,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, confirm } }
            }
        };
        DialogWindow.Frame(this, content, modal: true);
    }
}
