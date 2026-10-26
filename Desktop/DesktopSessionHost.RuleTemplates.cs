using System.Collections.Generic;
using System.Threading.Tasks;
using MeowChan.Rules;

namespace MeowChan.Desktop;
internal sealed partial class DesktopSessionHost
{
    private RuleTemplateStore? _ruleTemplates;
    internal Task<RuleTemplateLoadResult> LoadRuleTemplatesAsync()
        => RunInteractiveAsync(nameof(LoadRuleTemplatesAsync), "profile", () => LoadRuleTemplatesAdmittedCoreAsync(), captureAfter: false);

    private async Task<RuleTemplateLoadResult> LoadRuleTemplatesAdmittedCoreAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady(); _ruleTemplates ??= new(ProfileDirectory, _profileLease);
            return await _ruleTemplates.LoadAsync().ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
    internal Task<RuleTemplateSet> SaveRuleTemplatesAsync(IEnumerable<RuleTemplate> templates, string expectedRevision)
    {
        RuleTemplateSet frozen = new(expectedRevision, templates);
        return RunInteractiveAsync(nameof(SaveRuleTemplatesAsync), "profile", () => SaveRuleTemplatesAdmittedCoreAsync(frozen.Templates, expectedRevision));
    }

    private async Task<RuleTemplateSet> SaveRuleTemplatesAdmittedCoreAsync(IEnumerable<RuleTemplate> templates, string expectedRevision)
    {
        RuleTemplateSet captured = new(expectedRevision, templates);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (_ruleTemplates == null) throw new System.InvalidOperationException("Load rule templates first.");
            return await _ruleTemplates.SaveAsync(captured.Templates, expectedRevision).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
}
