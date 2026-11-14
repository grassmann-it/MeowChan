using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// DS-P3: the one empty-state pattern of a list or grid surface: centred in the area, one muted sentence and at most one primary
/// next step below it (Downloads threads and boards, Archive, Media). Alignment, width, margin and spacing come from the
/// <c>Workspace.Size.EmptyStateWidth</c>, <c>Workspace.Inset.EmptyState</c> and <c>Workspace.Space.12</c> tokens through the style in
/// WorkspaceStyles.axaml; the sentence takes the <see cref="TextClass"/> class. A page keeps its own names, texts and actions and
/// decides when the state is visible; code-built pages create the control and add the sentence and the action. A short inline sentence at the top of a list (Sources, Diagnostics) stays what it was.
/// </summary>
internal sealed class WorkspaceEmptyState : StackPanel
{
    /// <summary>The style class of the sentence: muted body text, centred and wrapped.</summary>
    internal const string TextClass = "workspace-empty-text";
}
