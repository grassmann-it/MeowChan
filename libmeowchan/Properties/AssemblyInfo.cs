using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeowChan.Tests")]
[assembly: InternalsVisibleTo("MeowChan.PluginHost")]
[assembly: InternalsVisibleTo("MeowChan.PluginFilters.Tests")]

// URSA-12: desktop-owned group store and passive metadata queries stay outside the public library API.
[assembly: InternalsVisibleTo("MeowChan")]
[assembly: InternalsVisibleTo("MeowChan.DesktopTests")]
[assembly: InternalsVisibleTo("MeowChan.DesktopSmoke")]
