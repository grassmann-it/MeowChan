using System.Collections.Generic;
using System.Threading.Tasks;
using MeowChan.Rules;

namespace MeowChan.Desktop;
internal sealed partial class DesktopSessionHost
{
    private RuleTemplateStore? _ruleTemplates;
    internal async Task<RuleTemplateLoadResult> LoadRuleTemplatesAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady(); _ruleTemplates ??= new(ProfileDirectory, _profileLease);
            return await _ruleTemplates.LoadAsync().ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
    internal async Task<RuleTemplateSet> SaveRuleTemplatesAsync(IEnumerable<RuleTemplate> templates, string expectedRevision)
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
