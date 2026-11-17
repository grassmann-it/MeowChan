using System;
using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

public partial class ArchivePage
{
    // Fault boundary after each synchronous hosting mutation; absent in production.
    internal Action<string>? ReaderHostingStepCompleted { get; set; }
    private void ReaderHostingStep(string step) => ReaderHostingStepCompleted?.Invoke(step);

    // Includes shell attachment: a frame returned by DetachReader is not yet
    // owned by the window. Presentation/scope failures must undo that move too.
    internal void HostLargeReader(ContentControl host, Action present, Action scope)
    {
        Action rollback = CaptureReaderHostingRollback();
        bool detached = false;
        try
        {
            Control content = DetachReader();
            detached = true;
            host.Content = content;
            ReaderHostingStep("shell.content");
            host.IsVisible = true;
            ReaderHostingStep("shell.visible");
            present();
            ReaderHostingStep("shell.presentation");
            scope();
            ReaderHostingStep("shell.scope");
        }
        catch (Exception error)
        {
            Exception failure = RollBackReaderHosting(error,
                () => host.Content = null,
                () => host.IsVisible = false,
                () => { if (detached) rollback(); });
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }

    // Capture before removing anything. Unlike AttachReader this does not assume
    // DetachReader reached WrapLargeReader (or even removed the export dialog).
    internal Action CaptureReaderHostingRollback()
    {
        Grid pane = ArchiveReaderPane;
        Border export = ArchiveExportDialog;
        int paneIndex = ArchiveSplit.Children.IndexOf(pane);
        int exportIndex = ArchiveRoot.Children.IndexOf(export);
        int column = Grid.GetColumn(pane);
        var margin = pane.Margin;
        bool closeVisible = ArchiveCloseReader.IsVisible, readVisible = ArchiveReadPosts.IsVisible;
        var threads = _readerThreads;
        return () =>
        {
            List<Exception> failures = new();
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception error) { failures.Add(error); }
            }
            // Restore ownership first, before rendering, bindings or sidebar layout
            // can fail again. Each repair runs even if another repair fails.
            _largeReader = false;
            _movingMediaSidebar = true;
            Restore(() => Reparent(pane, ArchiveSplit, paneIndex));
            Restore(() => Grid.SetColumn(pane, column));
            Restore(() => Reparent(export, ArchiveRoot, exportIndex));
            Restore(() =>
            {
                if (_mediaSidebar != null) Reparent(_mediaSidebar, pane);
            });
            Restore(() =>
            {
                if (_mediaSidebarSplitter != null) Reparent(_mediaSidebarSplitter, pane);
            });
            Restore(() =>
            {
                if (_mediaSidebar != null)
                {
                    Grid.SetRow(_mediaSidebar, 1);
                    ApplyMediaSidebarInsets(_mediaSidebar, archiveView: true);
                    _mediaSidebar.BorderThickness = default;
                }
                if (_mediaSidebarSplitter != null) Grid.SetRow(_mediaSidebarSplitter, 1);
            });
            _mediaSidebarHost = _mediaSidebar == null ? null : pane;
            _movingMediaSidebar = false;
            ++_readerScopeRequest; // retire any freeze started before the shell failed
            _readerThreads = threads;
            Restore(() => _readerTabScope?.Dispose());
            _readerTabScope = null;
            Restore(() => _frameTabScope?.Dispose());
            _frameTabScope = null;
            _tabScopeOnFrame = false;
            Restore(() => pane.ClearValue(ReaderPostLayout.LargeReaderProperty));
            Restore(() => UnwireReaderSurface(pane));
            Restore(() => pane.Margin = margin);
            Restore(() => ArchiveCloseReader.IsVisible = closeVisible);
            Restore(() => ArchiveReadPosts.IsVisible = readVisible);
            Restore(() => UpdateReaderNavigation());
            Restore(() => UpdateLayoutMode());
            Restore(() => LayoutMediaSidebar(force: true));
            Restore(() => { if (_thread != null) RenderThread(); });
            if (failures.Count > 0) throw new AggregateException("Reader hosting rollback failed.", failures);
        };
    }

    private static void Reparent(Control control, Panel parent, int index = -1)
    {
        if (ReferenceEquals(control.Parent, parent)) return;
        if (control.Parent is Panel previous) previous.Children.Remove(control);
        else if (control.Parent is ContentControl host) host.Content = null;
        parent.Children.Insert(index < 0 ? parent.Children.Count : Math.Min(index, parent.Children.Count), control);
    }

    internal static bool IsFatalReaderHostingFailure(Exception error)
    {
        if (error is OutOfMemoryException) return true;
        if (error is AggregateException aggregate)
            foreach (Exception inner in aggregate.InnerExceptions)
                if (IsFatalReaderHostingFailure(inner)) return true;
        return false;
    }

    internal static Exception RollBackReaderHosting(Exception error, params Action[] repairs)
    {
        List<Exception> failures = new() { error };
        foreach (Action repair in repairs)
        {
            try { repair(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        return failures.Count == 1 ? error : new AggregateException("Reader hosting failed and recovery reported errors.", failures);
    }
}
