using System.Linq;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

public partial class ArchivePage
{
    /// <summary>The neighbouring reader box (layout regression seam).</summary>
    internal Control ReaderBox => ArchiveReaderContent;

    // Header rows stay above the posts column only, on either sidebar side.
    private StackPanel? ReaderHeaderRows => ArchiveReaderPane.Children.OfType<StackPanel>().FirstOrDefault();
}
