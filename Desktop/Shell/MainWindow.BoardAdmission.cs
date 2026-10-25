using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    private bool _admissionBatchCanceled;
    private async Task<BoardAdmissionSelection?> SelectBoardAdmissionsAsync(IReadOnlyList<string> urls, bool forceWizard = false)
    {
        if (urls.Count == 0) return new(new Dictionary<string, BoardAdmissionPolicy>(), !_config.ShowBoardAdmissionWizard);
        if (!_config.ShowBoardAdmissionWizard && !forceWizard)
            return DirectBoardAdmissions(urls, _session.GetBoardAdmissionPolicy);
        var dialog = new BoardAdmissionWindow(urls, _session.GetBoardAdmissionPolicy, _session.PreviewBoardAdmissionAsync, _config.ShowBoardAdmissionWizard);
        return await dialog.ShowDialog<BoardAdmissionSelection?>(this);
    }

    internal static BoardAdmissionSelection DirectBoardAdmissions(IEnumerable<string> urls, Func<string, BoardAdmissionPolicy> readPolicy)
        => new(urls.Distinct(StringComparer.Ordinal).ToDictionary(url => url,
            url => new BoardAdmissionPolicy(Array.Empty<ThreadTextRule>(), readPolicy(url).Exceptions), StringComparer.Ordinal), true);

    private async Task<bool> SubmitAdmissionBatchAsync(string? input, Action<Window>? observeExperimentalModal = null)
    {
        _admissionBatchCanceled = false;
        var entries = new List<string>();
        await AddInput.SubmitAsync(input, ReadClipboardAsync, url => { entries.Add(url); return Task.CompletedTask; });
        IReadOnlyDictionary<int, string> skipped = await FilterExperimentalAdmissionsAsync(entries, observeExperimentalModal);
        var boards = entries.Where((_, index) => !skipped.ContainsKey(index)).Select(url => _session.ClassifyAdmission(url))
            .Where(target => target is { IsBoard: true, AlreadyWatching: false })
            .Select(target => target!.Url).Distinct(StringComparer.Ordinal).ToArray();
        BoardAdmissionSelection? selection = await SelectBoardAdmissionsAsync(boards);
        if (selection == null || _isClosing)
        {
            foreach (var item in skipped.OrderBy(item => item.Key))
                _admissionResults.Add(new AdmissionFeedback(entries[item.Key], null, item.Value, Skipped: true));
            _admissionBatchCanceled = true;
            return false;
        }
        if (boards.Length > 0 && _config.ShowBoardAdmissionWizard == selection.DirectFuture)
        {
            Config candidate = _config.Clone(); candidate.ShowBoardAdmissionWizard = !selection.DirectFuture;
            await _session.ApplySettingsAsync(candidate);
        }
        bool allAccepted = skipped.Count == 0;
        for (int index = 0; index < entries.Count; index++)
        {
            string url = entries[index];
            if (_isClosing) throw new OperationCanceledException();
            if (skipped.TryGetValue(index, out string? reason))
            {
                _admissionResults.Add(new AdmissionFeedback(url, null, reason, Skipped: true));
                _admissionBatchCanceled = true;
                continue;
            }
            DesktopAdmissionResult? result;
            BoardAdmissionTarget? target = _session.ClassifyAdmission(url);
            if (target is { IsBoard: true, AlreadyWatching: false } && selection.Policies.TryGetValue(target.Url, out BoardAdmissionPolicy? policy))
            {
                try
                {
                    result = await _session.ApplyBoardAdmissionAsync(target.Url, policy, selection.Reactivations?.GetValueOrDefault(target.Url));
                    _admissionResults.Add(new AdmissionFeedback(url, result.Status, null));
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "Could not apply board admission for {Url}", url);
                    _admissionResults.Add(new AdmissionFeedback(url, null, exception.Message)); result = null;
                }
            }
            else result = await AddUrlAsync(url);
            allAccepted &= result?.Status is AdmissionStatus.Added or AdmissionStatus.AlreadyPresent;
            if (_admissionResults.Count % 32 == 0)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        return entries.Count != 0 && allAccepted;
    }

    private async Task OpenBoardAdmissionAsync(BoardRow board)
    {
        if (!_restoreCompleted || _isClosing || _importing || _applyingPreferences) return;
        _importing = true; UpdateInteractionState();
        try
        {
            var selection = await SelectBoardAdmissionsAsync(new[] { board.Url }, forceWizard: true);
            if (selection == null || _isClosing) return;
            await _session.ApplyBoardAdmissionAsync(board.Url, selection.Policies[board.Url], selection.Reactivations?.GetValueOrDefault(board.Url));
            if (_config.ShowBoardAdmissionWizard == selection.DirectFuture)
            {
                Config candidate = _config.Clone(); candidate.ShowBoardAdmissionWizard = !selection.DirectFuture;
                await _session.ApplySettingsAsync(candidate);
            }
        }
        catch (Exception exception)
        {
            ShowNotification(Texts.T("notification.error"), exception.Message, NotificationType.Error, context: board.Url, operation: "board.admission");
        }
        finally { _importing = false; UpdateInteractionState(); }
    }
}
