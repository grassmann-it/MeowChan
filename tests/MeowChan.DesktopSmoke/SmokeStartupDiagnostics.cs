using System.Collections.Generic;

namespace MeowChan.DesktopSmoke;

/// <summary>Turns the otherwise opaque command-registration startup failure into a repair hint.</summary>
internal static class SmokeStartupDiagnostics
{
    private const string UnknownCommandPrefix = "Unknown command: ";

    internal static bool TryGetUnknownCommand(string message, out string commandId)
    {
        int start = message.IndexOf(UnknownCommandPrefix, StringComparison.Ordinal);
        if (start >= 0)
        {
            commandId = message[(start + UnknownCommandPrefix.Length)..].Split(['\r', '\n'], 2, StringSplitOptions.None)[0].Trim();
            return commandId.Length != 0;
        }

        commandId = string.Empty;
        return false;
    }

    internal static bool TryGetUnknownCommand(Exception exception, out string commandId)
    {
        Stack<Exception> pending = new();
        pending.Push(exception);
        while (pending.Count != 0)
        {
            Exception current = pending.Pop();
            if (current is KeyNotFoundException
                && current.Message.StartsWith(UnknownCommandPrefix, StringComparison.Ordinal))
            {
                commandId = current.Message[UnknownCommandPrefix.Length..];
                return commandId.Length != 0;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        commandId = string.Empty;
        return false;
    }

    internal static string UnknownCommandRegistration(string commandId) =>
        $"unknown command registration '{commandId}' during startup. "
        + $"Add '{commandId}' to Desktop/Commands/CommandCatalog.cs or correct the registration; "
        + "do not weaken the smoke assertion to hide the startup failure.";
}
