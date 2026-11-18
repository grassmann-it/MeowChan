using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    // The favorite, thread favorite and rating lists warn in the status bar from 75 percent of their limits. Entry counts come
    // from the loaded lists and sizes from three file lengths, read on a worker; the check runs at most every ten seconds
    // (plus once after the settings changed) and never blocks the UI thread.
    private static readonly long StoreWarningIntervalTicks = Stopwatch.Frequency * 10;
    private long _storeWarningChecked;
    private bool _storeWarningRunning;

    private void RefreshStoreLimitWarning(bool force = false)
    {
        if (_isClosing || _storeWarningRunning) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && _storeWarningChecked != 0 && now - _storeWarningChecked < StoreWarningIntervalTicks) return;
        _storeWarningChecked = now;
        _storeWarningRunning = true;
        DesktopSessionHost session = _session;
        _ = Task.Run(() =>
        {
            IReadOnlyList<StoreLimitUsage.Item> near;
            try { near = session.ReadStoreLimitUsage(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Any failure only clears the warning; the guard flag below is lowered in every case so later checks still run.
                if (error is not (IOException or UnauthorizedAccessException or InvalidOperationException)) UnhandledErrors.TryRecover(error, "limits.warning");
                near = Array.Empty<StoreLimitUsage.Item>();
            }
            finally
            {
                Dispatcher.UIThread.Post(() => _storeWarningRunning = false);
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosing) return;
                _storeUsage = near;
                ShowStoreLimitWarning();
            });
        });
    }

    private IReadOnlyList<StoreLimitUsage.Item> _storeUsage = Array.Empty<StoreLimitUsage.Item>();

    /// <summary>Shows the last measurement in the current language (also called after a language change).</summary>
    private void ShowStoreLimitWarning()
    {
        (string Text, string Tooltip)? warning = StoreLimitUsage.Present(_storeUsage);
        WorkspaceStoreWarningButton.IsVisible = warning != null;
        WorkspaceStoreWarning.IsVisible = warning != null;
        WorkspaceStoreWarning.Text = warning?.Text;
        Avalonia.Controls.ToolTip.SetTip(WorkspaceStoreWarningButton, warning?.Tooltip);
        AutomationProperties.SetName(WorkspaceStoreWarningButton, warning?.Tooltip);
    }

    // The settings that raise the two limits the warning measures (the names of their inputs without the suffix: Storage and limits).
    private const string FavoriteEntryLimitField = "FavoriteEntryLimit", FavoriteFileLimitField = "FavoriteFileLimitMiB";

    /// <summary>The setting the warning names: the entries limit for a full list, the file limit for a full file (the fullest warning first).</summary>
    internal static string StoreWarningSettingField(IReadOnlyList<StoreLimitUsage.Item> near) =>
        near.Count > 0 && near[0].Kind == MeowChan.Persistence.PrivateStoreLimitKind.FavoriteFile ? FavoriteFileLimitField : FavoriteEntryLimitField;

    // Dead-affordance audit: the warning names a setting, so a click opens it (Settings, Storage and limits, the limit that is nearly reached).
    private void OnStoreWarningClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowSettingsAt(StoreWarningSettingField(_storeUsage));
}
