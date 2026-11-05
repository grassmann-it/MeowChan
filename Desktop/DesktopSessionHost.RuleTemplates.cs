using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.Desktop;
internal sealed partial class DesktopSessionHost
{
    private RuleTemplateStore? _ruleTemplates;
    internal Action<ProfileAdmissionToken>? BeforeRuleTemplateAdmittedSaveMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterRuleTemplateAdmittedSaveMutationForTests { get; set; }
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
        return RunInteractiveWithAdmissionAsync(nameof(SaveRuleTemplatesAsync), "profile",
            admission => SaveRuleTemplatesAdmittedCoreAsync(frozen.Templates, expectedRevision, admission));
    }

    private async Task<RuleTemplateSet> SaveRuleTemplatesAdmittedCoreAsync(IEnumerable<RuleTemplate> templates, string expectedRevision,
        ProfileAdmissionToken admission)
    {
        RuleTemplateSet captured = new(expectedRevision, templates);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (_ruleTemplates == null) throw new System.InvalidOperationException("Load rule templates first.");
            _ruleTemplates.BeforeAdmittedSaveMutationForTests = BeforeRuleTemplateAdmittedSaveMutationForTests;
            _ruleTemplates.AfterAdmittedSaveMutationForTests = AfterRuleTemplateAdmittedSaveMutationForTests;
            return await _ruleTemplates.SaveAdmittedAsync(captured.Templates, expectedRevision, admission).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
}
