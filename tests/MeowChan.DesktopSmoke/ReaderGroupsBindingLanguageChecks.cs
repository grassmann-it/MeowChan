using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// The identical bound-row assertions previously made by a detached xUnit test
// belong on Avalonia's actual UI thread. No dispatcher reset/pumping or skips.
internal static class ReaderGroupsBindingLanguageChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string previous = Texts.Language;
        Texts.SetLanguage("en");
        var cases = new[]
        {
            (Local: true, Watched: false, Key: "readerGroups.localPosts"),
            (Local: false, Watched: true, Key: "readerGroups.watchedNoPosts"),
            (Local: false, Watched: false, Key: "readerGroups.unavailableLocally")
        };
        var items = cases.Select(c => new ReaderGroupCandidate(new ResourceId("raw-source", "b", "123"),
            "readerGroups.saved {0} / 日本語", c.Local, c.Watched)).ToArray();
        var rows = items.Select(item => new ReaderGroupsDialog.MemberRow(item)).ToArray();
        foreach (var row in rows) row.Refresh();
        var blocks = rows.Select(ReaderGroupsDialog.RowText).ToArray();
        var counts = new int[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int index = i;
            rows[i].PropertyChanged += (_, e) =>
            {
                Require(e.PropertyName == "Display", "Unexpected row notification.");
                ++counts[index];
            };
        }
        StackPanel content = new();
        foreach (TextBlock block in blocks) content.Children.Add(block);
        Window window = new() { Width = 700, Height = 360, Content = content, Title = "Synthetic row binding regression" };
        try
        {
            window.Show(owner);
            await Program.WaitUntilAsync(() => window.IsVisible && blocks.Select((b, i) => b.Text == rows[i].Display).All(x => x));
            Require(window.TryGetPlatformHandle()?.Handle is nint handle && handle != 0, "No real native window handle.");
            string[] english = rows.Select(row => row.Display).ToArray();
            foreach (string language in new[] { "es", "fr", "ru", "de", "en" })
            {
                Texts.SetLanguage(language);
                foreach (var row in rows) row.Refresh();
                await Program.WaitUntilAsync(() => blocks.Select((b, i) => b.Text == rows[i].Display).All(x => x));
                for (int i = 0; i < rows.Length; i++)
                {
                    string expected = $"raw-source/b/123 · {items[i].Title} · {Texts.T(cases[i].Key)}";
                    Require(ReferenceEquals(items[i], rows[i].Item), "Row identity changed.");
                    Require(rows[i].Display == expected && blocks[i].Text == expected && rows[i].ToString() == expected,
                        "Bound row did not refresh exact identity/title/status in " + language + ".");
                    if (language != "en") Require(rows[i].Display != english[i], "The translated row stayed English.");
                }
            }
            for (int i = 0; i < rows.Length; i++)
            {
                Require(counts[i] == 5, "Row notification count changed.");
                rows[i].Refresh();
                Require(counts[i] == 5, "An unchanged row churned its binding.");
            }
            Console.WriteLine("SMOKE reader-group bound rows: 3 presence states × EN→ES→FR→RU→DE→EN, exact raw identity/title, same rows/controls and 5 notifications; actual native UI thread, no profile/network.");
        }
        finally { window.Close(); Texts.SetLanguage(previous); }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
