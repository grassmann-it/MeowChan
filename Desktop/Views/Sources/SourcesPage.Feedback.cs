using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SourcesPage
{
    private void SetActionError(string? text)
    {
        if (_ended) return;
        this.FindControl<TextBlock>("SourcesActionError")!.Text = text;
        this.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("SourcesActionErrorBanner")!.IsVisible = !string.IsNullOrEmpty(text);
    }
    private void RenderOperationFeedback() => SourcePluginManagement.SetSourceOperationBusy(_busy);
}
