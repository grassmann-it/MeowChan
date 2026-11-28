using Avalonia.Controls;

namespace MeowChan;

public partial class ArchivePage
{
    /// <summary>The neighbouring reader box (layout regression seam).</summary>
    internal Control ReaderBox => ArchiveReaderContent;
}
