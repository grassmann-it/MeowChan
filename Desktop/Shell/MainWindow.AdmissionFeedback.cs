using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private string? RefreshAdmissionRepair()
    {
        Control panel = WorkspaceControl<Control>("AdmissionRepairPanel");
        AdmissionFeedback? first = _admissionResults.FirstOrDefault(item => item.Status is not (AdmissionStatus.Added or AdmissionStatus.AlreadyPresent));
        panel.IsVisible = first != null;
        if (first == null) return null;
        string cause = first.Error ?? Texts.T(first.Status == AdmissionStatus.Invalid ? "admission.repairInvalid" : "admission.repairUnsupported");
        ProviderDescriptor[] providers = _session.GetProviderDescriptors().ToArray();
        string sources = providers.Length == 0 ? Texts.T("admission.repairNoSources") : Texts.T("admission.repairSources",
            string.Join(" · ", providers.Select(provider => provider.DisplayName + " (" + provider.BaseUri.AbsoluteUri + ")")));
        Uri.TryCreate(first.Url, UriKind.Absolute, out Uri? rejected);
        string? example = UrlList.Select(row => row.Url).Concat(BoardsList.Select(row => row.Url))
            .FirstOrDefault(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? known) &&
                (rejected == null || string.Equals(known.Host, rejected.Host, StringComparison.OrdinalIgnoreCase)));
        bool sessionExample = example != null;
        if (example == null)
        {
            // These examples were validated by the source parsers on the single capture worker.
            example = _session.Projection.AdmissionExamples
                .OrderByDescending(url => rejected != null && Uri.TryCreate(url, UriKind.Absolute, out Uri? known)
                    && string.Equals(known.Host, rejected.Host, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        // Keep the cause and repair example visible together in compact windows.
        // The rejected URLs and complete source list belong to the scrollable report.
        string repair = cause
            + (example == null ? string.Empty : Environment.NewLine + Texts.T(sessionExample ? "admission.repairExample" : "admission.repairFormat", example));
        SelectableTextBlock message = WorkspaceControl<SelectableTextBlock>("AdmissionRepairText");
        message.Text = repair;
        AutomationProperties.SetName(message, first.Url + " · " + repair);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        return sources;
    }
}
