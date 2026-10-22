using MeowChan.PluginFilters.Tests;
if (args.Length == 2 && args[0].StartsWith("crash-", StringComparison.Ordinal)) await OriginalFilterTests.CrashProbeAsync(args[0], args[1]);
