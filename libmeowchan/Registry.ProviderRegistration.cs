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
        // Restore may be waiting on an external store while holding _gate. Reject
        // configuration after the startup claim without waiting on that store.
        lock (_state) EnsureProviderRegistrationOpen();
        IImageBoard[] values = providers.ToArray();
        if (values.Any(provider => provider == null)
            || values.Select(provider => provider.Name).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Providers must have distinct source identities.", nameof(providers));
        ProviderDescriptor[] descriptors = values.Select(provider =>
            {
                IProviderDescriptorSource? descriptor = provider as IProviderDescriptorSource;
                return new ProviderDescriptor(provider.Name, provider.NiceName, provider.BaseUri, descriptor?.Capabilities,
                    Array.AsReadOnly(descriptor?.SupportedHosts.ToArray() ?? Array.Empty<string>()), descriptor?.AdapterVersion, false);
            }).ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_state)
            {
                EnsureProviderRegistrationOpen();
                _providers = values;
                _providerDescriptors = descriptors;
            }
        }
        finally { _gate.Release(); }
        SignalChanged();
    }

    // Caller holds _state. The second check also covers a startup claim that won
    // while provider descriptors were being prepared or _gate was awaited.
    private void EnsureProviderRegistrationOpen()
    {
        if (_restored || _hasMutations || _stopping || _producerStopRequested || _watches.Count != 0 || _standaloneStartup != null)
            throw new InvalidOperationException("Register startup providers before starting, restoring or admitting watches.");
    }
}
