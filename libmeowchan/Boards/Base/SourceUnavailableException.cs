using System;

namespace MeowChan.Boards.Base;

/// <summary>The thread's source is switched off or gone (for example a disabled source plugin). This is an expected
/// state, not a failed observation: the monitor shows a neutral activity, stops polling and resumes when the source
/// reports availability again. It is never logged with a stack trace and never recorded as a check failure.</summary>
internal sealed class SourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
