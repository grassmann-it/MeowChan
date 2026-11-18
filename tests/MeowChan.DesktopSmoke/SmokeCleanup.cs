namespace MeowChan.DesktopSmoke;

// Harness-only: a cleanup failure must not replace the check that caused cleanup.
internal static class SmokeCleanup
{
    internal static async Task RunAsync(Exception? primaryFailure, Func<Task> cleanup,
        Action<Exception> reportFailure)
    {
        try { await cleanup(); }
        catch (Exception cleanupFailure)
        {
            try { reportFailure(cleanupFailure); }
            catch (Exception diagnosticFailure)
            {
                Console.Error.WriteLine("SMOKE cleanup diagnostic failed: " + diagnosticFailure);
            }
            // A failed cleanup is still a test failure when the check itself passed.
            if (primaryFailure == null) throw;
        }
    }

    internal static void Diagnose(Action diagnostic)
    {
        try { diagnostic(); }
        catch (Exception diagnosticFailure)
        {
            Console.Error.WriteLine("SMOKE failure diagnostic failed: " + diagnosticFailure);
        }
    }
}
