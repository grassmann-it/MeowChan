using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

// SDK identities may contain '/'. Keep source scope separate from the exact field key.
// The empty scope denotes plugin settings; source identities are nonempty by SDK contract.
internal sealed record PluginConfigurationFieldError(string Scope, FieldValidationError Error)
{
    internal static IReadOnlyList<FieldValidationError> ForScope(IEnumerable<PluginConfigurationFieldError> errors, string scope) =>
        errors.Where(error => string.Equals(error.Scope, scope, StringComparison.Ordinal)).Select(error => error.Error).ToArray();
}
