using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SourcesPage
{
    private void SetActionError(string? text)
    {
        if (_ended) return;
        this.FindControl<TextBlock>("SourcesActionError")!.Text = text;
        this.FindControl<Ursa.Controls.Banner>("SourcesActionErrorBanner")!.IsVisible = !string.IsNullOrEmpty(text);
    }
    private void RenderOperationFeedback()
    {
        Ursa.Controls.LoadingIcon loading = this.FindControl<Ursa.Controls.LoadingIcon>("SourcesOperationLoading")!;
        loading.IsLoading = loading.IsVisible = _busy;
        this.FindControl<TextBlock>("SourcesOperationStatus")!.Text = _busy ? Texts.T("feedback.working") : string.Empty;
    }
}
