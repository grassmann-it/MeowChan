using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed class ProfileCopyWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _cancel = new() { Content = Texts.T("profile.new.cancel") };
    private CancellationTokenSource? _running;

    internal ProfileCopyWindow(string title)
    {
        Title = title;
        DialogWindow.Configure(this, DialogSize.S, modal: false, heightToContent: true);
        AutomationProperties.SetAutomationId(_cancel, "ProfileCopyCancel");
        AutomationProperties.SetAutomationId(_status, "ProfileCopyStatus");
        _status.Text = Texts.T("profile.state.checking");
        StackPanel body = new()
        {
            Margin = new Thickness(16), Spacing = 8,
            Children = { _status, new ProgressBar { IsIndeterminate = true } }
        };
        DialogWindow.Frame(this, DialogFrame.Build(null, body, DialogFooter.Build(_cancel)), modal: false);
        DialogEscape.Attach(this);
        _cancel.Click += (_, _) => Close();
        Closing += (_, args) =>
        {
            if (_running is null) return;
            args.Cancel = true;
            _cancel.IsEnabled = false;
            _running.Cancel();
        };
    }

    internal async Task<T> RunAsync<T>(Func<Action<string, long>, CancellationToken, T> work, Func<T, ValueTask>? discardOnCancellation = null)
    {
        using CancellationTokenSource cancel = new();
        _running = cancel;
        try
        {
            T result = await Task.Run(() =>
            {
                Stopwatch refresh = Stopwatch.StartNew();
                void Report(string path, long bytes)
                {
                    if (refresh.ElapsedMilliseconds < 100) return;
                    refresh.Restart();
                    string name = Path.GetFileName(path);
                    string status = bytes == 0 ? name : string.Format(Texts.Culture, "{0} · {1:N1} MiB", name, bytes / 1048576.0);
                    Dispatcher.UIThread.Post(() => _status.Text = status);
                }
                return work(Report, cancel.Token);
            });
            if (discardOnCancellation is not null && cancel.IsCancellationRequested)
            {
                await discardOnCancellation(result);
                cancel.Token.ThrowIfCancellationRequested();
            }
            return result;
        }
        finally { _running = null; }
    }
}
