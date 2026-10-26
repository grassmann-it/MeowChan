using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Diagnostics;

namespace MeowChan.DesktopSmoke;

/// <summary>The exact same measurement sources compile on release HEAD and on the projection candidate.
/// Projection is optional and discovered without a compile-time reference. Fallback state captures run off UI;
/// only ReadPausedOnUi deliberately exercises the release shell's legacy synchronous UI read.</summary>
internal sealed class ShellLoadSessionObservation
{
    private readonly DesktopSessionHost _host;
    private readonly PropertyInfo? _projection;
    private readonly long _fallbackEpoch = PerformanceDiagnostics.NextId();
    private const BindingFlags Properties = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal ShellLoadSessionObservation(DesktopSessionHost host)
    {
        _host = host;
        _projection = host.GetType().GetProperty("Projection", Properties);
    }
    internal bool HasProjection => _projection != null;
    private object? Projection => _projection?.GetValue(_host);
    private static object? Property(object instance, string name) => instance.GetType().GetProperty(name, Properties)?.GetValue(instance);
    internal long Epoch => Projection is { } projection && Property(projection, "Epoch") is long epoch ? epoch : _fallbackEpoch;
    internal bool IsStale => Projection is { } projection && Property(projection, "IsStale") is true;
    internal bool ReadPausedOnUi()
    {
        if (Projection is { } projection && Property(projection, "IsPaused") is bool paused) return paused;
        PerformanceDiagnostics.Record("harness.session-read", "legacy-ui-IsPaused", epoch: _fallbackEpoch);
        return _host.IsPaused;
    }
    internal Task<DesktopSessionView> ReadViewAsync() => Projection is { } projection && Property(projection, "View") is DesktopSessionView view
        ? Task.FromResult(view) : Task.Run(_host.GetView);
}
