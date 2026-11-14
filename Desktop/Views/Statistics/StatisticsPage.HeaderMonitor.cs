using System;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The statistics page's checkbox "Show network and disk in the header": the way back after the setting "Header graph" went Off, next
// to the settings row and the tray menu. It is the same setting; the window owns it and this page only reports the click.
public partial class StatisticsPage
{
    /// <summary>Raised when the user ticks or unticks the checkbox (true: show the display in the header).</summary>
    internal event Action<bool>? HeaderMonitorToggled;

    /// <summary>The checkbox; read by the tests and smokes.</summary>
    internal Avalonia.Controls.CheckBox HeaderMonitorBox => HeaderMonitorCheck;

    /// <summary>Shows the current state of the setting without raising <see cref="HeaderMonitorToggled"/>.</summary>
    internal void SetHeaderMonitorShown(bool shown) => HeaderMonitorCheck.IsChecked = shown;

    private void ApplyHeaderMonitorLanguage() => HeaderMonitorCheck.Content = Texts.T("stats.headerMonitor.show");

    private void OnHeaderMonitorClick(object? sender, RoutedEventArgs e) => HeaderMonitorToggled?.Invoke(HeaderMonitorCheck.IsChecked == true);
}
