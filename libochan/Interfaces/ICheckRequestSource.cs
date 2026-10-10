namespace oChan.Interfaces;

/// <summary>Optional provider support for waking its owned, serialized polling lifetime.</summary>
public interface ICheckRequestSource
{
    /// <summary>Returns promptly after signalling; performs no provider work on the calling thread.</summary>
    CheckRequestStatus RequestCheck();
}

public enum CheckRequestStatus { Scheduled, AlreadyScheduled, Paused, Stale, Unsupported, Stopping }
