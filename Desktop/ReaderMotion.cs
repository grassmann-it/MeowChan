using System;
using MeowChan.Desktop.Motion;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's view of the app-wide <see cref="MotionPolicy"/> (D1): <see cref="Reduced"/> is the policy's answer, so the jump
/// highlight follows the environment, the "Animations" setting and the operating system like everything else. Kept as a thin shell
/// so the reader and its tests keep one name; <c>MEOWCHAN_REDUCED_MOTION</c> and <c>MEOWCHAN_MOTION=off</c> both still work.
/// </summary>
internal static class ReaderMotion
{
    internal static readonly TimeSpan Highlight = TimeSpan.FromMilliseconds(400);

    internal static bool Reduced => MotionPolicy.IsOff;

    /// <summary>True when this environment alone asks for reduced motion.</summary>
    internal static bool IsReduced(Func<string, string?> environment) => MotionPolicy.FromEnvironment(environment) == true;
}
