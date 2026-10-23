using Avalonia.Controls;
using Avalonia.Threading;

namespace MeowChan;

public partial class RulesPage
{
    private void FocusFilterSettingError()
    {
        RuleDraft draft = _draft;
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || _busy || !ReferenceEquals(_draft, draft) || TopLevel.GetTopLevel(this) == null) return;
            if (_filterEditor?.TryFocusFirstError() == true) return;
            TextBlock? summary = this.FindControl<TextBlock>(HasError ? "RuleActionError" : "RuleValidationSummary");
            if (summary?.IsEffectivelyVisible != true) return;
            summary.BringIntoView();
            summary.Focus();
        }, DispatcherPriority.Background);
    }
}
