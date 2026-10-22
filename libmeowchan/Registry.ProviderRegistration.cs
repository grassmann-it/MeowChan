using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Interfaces;

namespace MeowChan;

public sealed partial class Registry
{
    // Plugin discovery runs before this method; no IPC occurs while the registry gate is held.
    internal async Task RegisterProvidersBeforeRestoreAsync(IEnumerable<IImageBoard> providers,
        CancellationToken cancellationToken = default)
    {
        IImageBoard[] values = providers.ToArray();
        if (values.Any(provider => provider == null)
            || values.Select(provider => provider.Name).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Providers must have distinct source identities.", nameof(providers));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_restored || _hasMutations || _stopping || _watches.Count != 0)
                throw new InvalidOperationException("Register startup providers before restoring or admitting watches.");
            _providers = values;
            _providerDescriptors = values.Select(provider =>
            {
                IProviderDescriptorSource? descriptor = provider as IProviderDescriptorSource;
                return new ProviderDescriptor(provider.Name, provider.NiceName, provider.BaseUri, descriptor?.Capabilities,
                    Array.AsReadOnly(descriptor?.SupportedHosts.ToArray() ?? Array.Empty<string>()), descriptor?.AdapterVersion, false);
            }).ToArray();
        }
        finally { _gate.Release(); }
        SignalChanged();
    }
}
