using System;
using System.Linq;
using MeowChan.Interfaces;

namespace MeowChan;

public sealed partial class Registry
{
    internal ReadOnlySpan<IImageBoard> AdmissionProviders => _providers;
    internal IImageBoard? FindAdmissionProvider(Uri uri) => _providers.FirstOrDefault(provider => provider.CanHandle(uri));
}
