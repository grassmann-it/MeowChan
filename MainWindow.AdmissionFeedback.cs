using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private void RefreshAdmissionRepair()
    {
        Control panel = WorkspaceControl<Control>("AdmissionRepairPanel");
        AdmissionFeedback? first = _admissionResults.FirstOrDefault(item => item.Status is not (AdmissionStatus.Added or AdmissionStatus.AlreadyPresent));
        panel.IsVisible = first != null;
        if (first == null) return;
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
            // Only display a candidate accepted by a currently registered source's local URL parser.
            // Classification performs no catalog/media request and admits no watch.
            foreach (ProviderDescriptor provider in providers.OrderByDescending(provider => rejected != null && provider.SupportedHosts.Contains(rejected.Host, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (string host in provider.SupportedHosts.Prepend(provider.BaseUri.Host).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string path in new[] { "/b/thread/123456", "/b/res/123456.html", "/b/" })
                    {
                        string candidate = new UriBuilder(provider.BaseUri.Scheme, host) { Path = path }.Uri.AbsoluteUri;
                        if (_session.ClassifyAdmission(candidate) is not { } accepted) continue;
                        example = accepted.Url;
                        break;
                    }
                    if (example != null) break;
                }
                if (example != null) break;
            }
        }
        string repair = first.Url + " · " + cause + Environment.NewLine + sources
            + (example == null ? string.Empty : Environment.NewLine + Texts.T(sessionExample ? "admission.repairExample" : "admission.repairFormat", example));
        SelectableTextBlock message = WorkspaceControl<SelectableTextBlock>("AdmissionRepairText");
        message.Text = repair;
        AutomationProperties.SetName(message, repair);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
    }
}
